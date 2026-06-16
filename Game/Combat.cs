using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Combate cuerpo a cuerpo. Porta UsuarioAtaca + CalcularDanio + UserdañoNpc
/// (SistemaCombate.bas), versión núcleo: golpe al tile de enfrente, daño con el arma
/// equipada (MinHIT/MaxHIT de obj.dat) o stats del usuario, aplica al NPC y maneja muerte.
///
/// Falta al portar el resto: PVP (atacar jugadores), evasión/probabilidad de golpe por
/// skills, apuñalar, exp/oro al matar, respawn de NPCs, contraataque (IA).
/// </summary>
public static class Combat
{
    private static readonly Random _rng = new();

    // Hechizos de leveo con curva de daño lineal propia por nivel (NO usan el escalado global
    // EscalaMagia×ELV). Clave = índice de hechizo, valor = crecimiento de daño por nivel sobre
    // el MinHP/MaxHP base (nivel 15). La diferencia min↔max se mantiene (mismo crecimiento en ambos).
    //   121 Dardo Arcano:   26-34 (n15) → 262-270 (n50)  → (270-34)/(50-15) ≈ 6.7428571
    //   122 Centella Menor:  18-24 (n15) → 174-180 (n50)  → (180-24)/(50-15) ≈ 4.4571428
    private static readonly Dictionary<int, double> LevelingSpellGrowth = new()
    {
        [121] = 236.0 / 35.0, // ≈ 6.7428571
        [122] = 156.0 / 35.0, // ≈ 4.4571428
    };

    public static void UsuarioAtaca(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.flags.Muerto == 1) return;
        if (u.flags.Meditando) return;
        if (u.flags.Paralizado == 1) return;
        if (u.flags.Maldecido == 1)
        { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "¡Estás maldecido! No puedes atacar.", 1); return; }

        // VB6 HandleAttack (Protocol.bas:2267): con un arma a distancia equipada (proyectil > 0:
        // arco=1 o arrojadiza/arpón=2) NO se puede golpear cuerpo a cuerpo. Hay que usar el disparo.
        if (u.Invent.WeaponEqpObjIndex > 0 && ObjData.Get(u.Invent.WeaponEqpObjIndex).Proyectil > 0)
        { if (u.Conn != null) ServerPackets.LocaleMsg(u.Conn, 127); return; }

        // Intervalos de ataque cuerpo a cuerpo (UsuarioAtaca, modNuevoTimer 1:1):
        //  1) arco read-only (no se puede golpear con el cooldown de arco activo, sin consumirlo),
        //  2) MagiaGolpe (¿pasó el tiempo desde el último casteo?); si no, cae al cooldown de ataque normal.
        if (!Intervals.PuedeUsarArco(u, actualizar: false)) return;
        if (!Intervals.PuedeMagiaGolpe(u))
            if (!Intervals.PuedeAtacar(u)) return;

        // Energía por golpe (VB6 UsuarioAtaca, SistemaCombate.bas:1308-1323): nudillos o arma →
        // StaRequerido del objeto (default 10, FileIO.bas:1134); a mano limpia → Random(1,10).
        // Sin energía suficiente no se ataca.
        int energiaGolpe;
        if (u.Invent.NudiEqpObjIndex > 0)
            energiaGolpe = ObjData.Get(u.Invent.NudiEqpObjIndex).StaRequerido;
        else if (u.Invent.WeaponEqpObjIndex > 0)
            energiaGolpe = ObjData.Get(u.Invent.WeaponEqpObjIndex).StaRequerido;
        else
            energiaGolpe = Random.Shared.Next(1, 11);
        if (u.Stats.MinSta < energiaGolpe)
        { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "Estás muy cansado para luchar.", 1); return; }
        u.Stats.MinSta = (short)(u.Stats.MinSta - energiaGolpe);
        if (u.Conn != null) ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);

        // VB6: al atacar se revela el ocultamiento/invisibilidad.
        RevelarOculto(u);
        // (Sacrificio Impío se consume dentro de UsuarioImpacto/UserImpactoNpc al calcular el impacto.)

        // Tile de enfrente, según el heading.
        WorldPos atk = u.Pos;
        Movement.HeadtoPos(u.Char.heading, ref atk);
        if (atk.X < 1 || atk.X > 100 || atk.Y < 1 || atk.Y > 100) return;

        // VB6 UsuarioAtaca: busca primero usuario en tile, luego NPC
        // Buscar usuario en el tile
        int targetUserIdx = -1;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (i != userIndex && t.flags.UserLogged && t.flags.Muerto == 0
                && t.Pos.Map == atk.Map && t.Pos.X == atk.X && t.Pos.Y == atk.Y)
            {
                targetUserIdx = i;
                break;
            }
        }

        if (targetUserIdx > 0)
        {
            // VB6 UsuarioAtacaUsuario 1:1
            UsuarioAtacaUsuario(userIndex, targetUserIdx, u);
            return;
        }

        // Buscar NPC en ese tile
        var npc = NpcManager.NpcAt(u.Pos.Map, atk.X, atk.Y);
        if (npc == null)
        {
            // VB6: golpe al aire → PrepareMessagePlayWave(SND_SWING=2, X, Y) al área
            const short SND_SWING = 2;
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var o = UserListManager.UserList[i];
                if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                    ServerPackets.PlayWave(o.Conn, SND_SWING, (byte)u.Pos.X, (byte)u.Pos.Y);
            }
            return;
        }

        // VB6 PuedeAtacarNPC: NPCs no atacables (Attackable=0: mercaderes, sacerdotes, etc.) intocables;
        // guardias: Rinkel intocable para todos, en el resto solo guardias enemigos de la facción.
        if (!NpcManager.UsuarioPuedeAtacarNpc(u, npc, out string motGuardia))
        { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, motGuardia, 1); return; }

        // VB6 UserImpactoNpc: chance de impacto (poder de ataque vs evasión del NPC). Falla → swing al aire.
        if (!UserImpactoNpc(userIndex, npc))
        {
            const short SND_SWING2 = 2;
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var o = UserListManager.UserList[i];
                if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                    ServerPackets.PlayWave(o.Conn, SND_SWING2, (byte)u.Pos.X, (byte)u.Pos.Y);
            }
            FalloPropio(u);   // "¡Fallas!" sobre la cabeza del atacante (VB6 WriteChatOverHeadLocale ...,0)
            BroadcastFX(npc.Map, npc.CharIndex, FX_GOLPE_FALLO, 0);  // FX de fallo sobre el NPC (todos lo ven)
            return;
        }

        BroadcastFX(npc.Map, npc.CharIndex, FX_GOLPE_ACIERTO, 0);    // FX de acierto sobre el NPC (todos lo ven)
        int dano = CalcularDanio(u, npc); // PvE (usa MinHIT/MaxHITPVE + especiales)

        // VB6: sistema de apuñalamiento (Asesino/Ladrón con daga, 20% prob)
        if (PuedeApunalar(u))
        {
            dano = DanoApunalamiento(u, dano);
            ServerPackets.ConsoleMsg(u.Conn, $"¡Has apuñalado a la criatura por {dano}!", 2); // font 2 = rojo + tab Combate
            BroadcastFX(npc.Map, npc.CharIndex, FX_APUNALAR, 0);  // FX/logo de daga sobre el objetivo
        }
        // Número de daño azul sobre el NPC + "Golpeás por X" en consola (lo arma el cliente).
        DanoInfligido(u, npc.CharIndex, dano);
        // Sonido de impacto cuerpo a cuerpo (SistemaCombate.bas UsuarioAtacaNpc: SND_IMPACTO=86).
        BroadcastWaveArea(npc.Map, npc.X, npc.Y, Sounds.IMPACTO);
        // Espada Mata Dragones golpeando a un dragón: sonido especial (149).
        {
            int armaIdx = u.Invent.WeaponEqpObjIndex > 0 ? u.Invent.WeaponEqpObjIndex
                        : (u.Invent.NudiEqpObjIndex > 0 ? u.Invent.NudiEqpObjIndex : 0);
            if (armaIdx == ESPADA_MATADRAGONES && npc.NpcType == NPCTYPE_DRAGON)
                BroadcastWaveArea(npc.Map, npc.X, npc.Y, Sounds.DRAGON_ESPADA);
        }
        GolpeParalizaNpc(u, npc);         // parálisis de artes marciales (Gladiador/Bardo con nudillos/manos)
        GolpeOrbeNpc(u, npc);             // Orbe Acuática/espadas con Paraliza(11): 60% de paralizar 60s
        NpcManager.ProvocarNpc(npc, u);   // aggro: el NPC se vuelve hostil/persigue y registra atacante

        // EXP proporcional al daño (CalcularDarExp), antes de restar HP (cap interno a MinHP).
        CalcularDarExp(userIndex, npc, dano);
        npc.MinHP -= dano;

        if (npc.MinHP > 0) return;

        MatarNpc(u, npc);
    }

    /// <summary>
    /// Ataque a distancia (HandleWorkLeftClick: Proyectiles/ArmasArrojadizas, Protocol.bas:3363/3460).
    /// arrojadiza=true: daga/shuriken (arma SubTipo 5, se consume el arma). false: arco+flecha
    /// (Proyectil=1 + munición Flechas, se consume la flecha). Apunta al tile (x,y); pega a user o NPC.
    /// </summary>
    public static void AtaqueADistancia(int userIndex, byte x, byte y, bool arrojadiza)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.flags.Muerto == 1 || u.flags.Meditando || u.flags.Paralizado == 1) return;
        if (u.flags.Maldecido == 1)
        { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "¡Estás maldecido! No puedes atacar.", 1); return; }

        // Intervalos del ataque a distancia (Protocol.bas:3366 1:1): Atacar y LanzarSpell read-only
        // (deben haber pasado, pero no se consumen) y recién ahí se consume el de arco/proyectil.
        if (!Intervals.PuedeAtacar(u, actualizar: false)) return;
        if (!Intervals.PuedeLanzarSpell(u, actualizar: false)) return;
        if (!Intervals.PuedeUsarArco(u)) return;

        var inv = u.Invent;
        bool usaMunicion = false; // arco: consume flecha. arpón/lanza/daga: no.
        if (arrojadiza)
        {
            // eSkill.ArmasArrojadizas (Protocol.bas:3363): arma arrojadiza equipada con stock (Amount≥1).
            // Se arroja y se consume del stack (daga/shuriken/hacha arrojadiza).
            if (inv.WeaponEqpObjIndex == 0
                || inv.WeaponEqpSlot < 1 || inv.WeaponEqpSlot > Constants.MAX_INVENTORY_SLOTS
                || inv.Object[inv.WeaponEqpSlot].Amount < 1)
            { ServerPackets.ConsoleMsg(u.Conn, "No tienes nada para arrojar.", 1); return; }
        }
        else
        {
            // eSkill.Proyectiles (Protocol.bas:3460): arco (Municiones=1 → necesita flechas) o arma de
            // proyectil sin munición (arpón/lanza/jabalina: Proyectil≥1, Municiones=0 → se arroja sola).
            if (inv.WeaponEqpObjIndex == 0)
            { ServerPackets.ConsoleMsg(u.Conn, "No tienes un arma de proyectiles equipada.", 1); return; }
            var w = ObjData.Get(inv.WeaponEqpObjIndex);
            if (w.Municion == 1)
            {
                bool ammoOk = inv.MunicionEqpObjIndex > 0
                              && inv.MunicionEqpSlot >= 1 && inv.MunicionEqpSlot <= Constants.MAX_INVENTORY_SLOTS
                              && ObjData.Get(inv.MunicionEqpObjIndex).Type == ObjType.Flechas
                              && inv.Object[inv.MunicionEqpSlot].Amount >= 1;
                if (!ammoOk) { ServerPackets.ConsoleMsg(u.Conn, "No tienes munición equipada.", 1); return; }
                usaMunicion = true;
            }
            else if (w.Proyectil < 1)
            { ServerPackets.ConsoleMsg(u.Conn, "Eso no es un arma de proyectiles.", 1); return; }
            // else: arpón/lanza (Proyectil≥1, sin munición) → se arroja sin consumir nada.
        }

        // Stamina (VB6: ≥10 → quita Random(1,10); si no, no puede).
        if (u.Stats.MinSta < 10) { ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado cansado.", 1); return; }
        u.Stats.MinSta = (short)Math.Max(0, u.Stats.MinSta - Random.Shared.Next(1, 11));
        ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);

        RevelarOculto(u);

        // Apuntar al tile clickeado (LookatTile fija TargetUser/NPC).
        Commerce.LeftClick(userIndex, x, y);

        bool atacado = false;
        if (u.TargetUserCharIndex > 0)
        {
            int vic = FindUserByCharIndex(u.TargetUserCharIndex);
            if (vic > 0 && vic != userIndex)
            {
                var v = UserListManager.UserList[vic];
                if (Math.Abs(v.Pos.Y - u.Pos.Y) > RANGO_VISION_Y_DIST)
                    ServerPackets.ConsoleMsg(u.Conn, "Está demasiado lejos.", 1);
                else if (PuedeAtacar(userIndex, vic))
                { UsuarioAtacaUsuario(userIndex, vic, u); atacado = true; }
            }
        }
        else if (u.TargetNpcCharIndex > 0)
        {
            var npc = NpcManager.NpcByCharIndex(u.Pos.Map, u.TargetNpcCharIndex);
            if (npc != null && !npc.Dead)
            {
                GolpearNpcADistancia(userIndex, u, npc);
                atacado = true;
            }
        }

        // Consumir (Protocol.bas:3578 1:1): arrojadiza SIEMPRE consume el arma (aun al aire); arco consume
        // la flecha sólo si atacó; arpón/lanza (proyectil sin munición) consume el arma al lanzarla.
        if (arrojadiza)
        {
            // Capturar el slot ANTES: si era la última unidad, QuitarUserInvItem desequipa y pone
            // WeaponEqpSlot=0, y el refresh quedaría en el slot 0 (el cliente nunca veía el consumo).
            byte ws = inv.WeaponEqpSlot;
            Inventory.QuitarUserInvItem(u, ws, 1);
            ServerPackets.ChangeInventorySlot(u.Conn, ws,
                inv.Object[ws].ObjIndex, inv.Object[ws].Amount, inv.Object[ws].Equipped);
        }
        else if (usaMunicion)
        {
            // Arco: la flecha se consume sólo si atacó (impactó/disparó a un objetivo).
            if (atacado)
            {
                byte ms = inv.MunicionEqpSlot;
                Inventory.QuitarUserInvItem(u, ms, 1);
                if (inv.Object[ms].Amount > 0)
                { inv.MunicionEqpSlot = ms; inv.MunicionEqpObjIndex = inv.Object[ms].ObjIndex; inv.Object[ms].Equipped = true; }
                else { inv.MunicionEqpSlot = 0; inv.MunicionEqpObjIndex = 0; }
                ServerPackets.ChangeInventorySlot(u.Conn, ms,
                    inv.Object[ms].ObjIndex, inv.Object[ms].Amount, inv.Object[ms].Equipped);
            }
        }
        else
        {
            // Arpón/lanza/hacha (proyectil ≥1, sin munición): se consume el arma AL LANZARLA (VB6 "al
            // lanzarla"), impacte o no. Antes sólo se consumía si atacaba → quedaba equipada y se podía
            // equipar otra arma encima ("se equipan las dos").
            byte ws = inv.WeaponEqpSlot;
            if (ws >= 1 && ws <= Constants.MAX_INVENTORY_SLOTS)
            {
                Inventory.QuitarUserInvItem(u, ws, 1);
                if (inv.Object[ws].Amount > 0)
                { inv.WeaponEqpSlot = ws; inv.WeaponEqpObjIndex = inv.Object[ws].ObjIndex; inv.Object[ws].Equipped = true; }
                else { inv.WeaponEqpSlot = 0; inv.WeaponEqpObjIndex = 0; }
                ServerPackets.ChangeInventorySlot(u.Conn, ws,
                    inv.Object[ws].ObjIndex, inv.Object[ws].Amount, inv.Object[ws].Equipped);
            }
        }
    }

    private const int RANGO_VISION_Y_DIST = 6;

    /// <summary>Daño flotante sobre la cabeza (ChatOverHeadLocale). El cliente lo muestra encima del char
    /// y lo loguea en la pestaña Combate. 1:1 con WriteChatOverHeadLocale del VB6 (SistemaCombate.bas):
    /// el daño que INFLIGÍS aparece sobre TU PROPIA cabeza (modo 3, azul, "Golpeás por X"), enviado a vos;
    /// el daño que RECIBÍS aparece sobre la cabeza del ATACANTE (modo 2, rojo, "Te golpean por X"), enviado
    /// a la víctima. (El parámetro victimaChar quedó sin uso: el VB6 siempre muestra el número del golpe
    /// sobre la cabeza del que pega, no sobre la víctima.)</summary>
    private static void DanoInfligido(User dealer, short victimaChar, int dano)
    { if (dealer?.Conn != null) ServerPackets.ChatOverHeadLocale(dealer.Conn, dealer.Char.CharIndex, dano, 3); }
    private static void DanoRecibido(User victima, short atacanteChar, int dano)
    { if (victima?.Conn != null) ServerPackets.ChatOverHeadLocale(victima.Conn, atacanteChar, dano, 2); }
    /// <summary>"¡Fallas!" flotante sobre la cabeza del propio atacante (VB6: WriteChatOverHeadLocale(...,0,2)
    /// sobre la cabeza del atacante). id=0 → el cliente lo renderiza como "Fallas" y lo loguea en Combate.</summary>
    private static void FalloPropio(User atk)
    { if (atk?.Conn != null) ServerPackets.ChatOverHeadLocale(atk.Conn, atk.Char.CharIndex, 0, 3); }

    /// <summary>Efectos especiales de flechas al impactar a un NPC (SistemaCombate.bas:1160-1195):
    /// sonido + FX/partícula por tipo de munición. Explosiva/Incendiaria por nombre; Eléctrica(1086)/
    /// Envenenante(1083)/Paralizante(1085) por objindex.</summary>
    private static void AplicarFlechaEspecial(NpcManager.NpcInstance npc, short ammoIndex, string ammoName)
    {
        ammoName ??= "";
        if (ammoName.Contains("Explosiva", StringComparison.OrdinalIgnoreCase))
        {
            BroadcastWaveArea(npc.Map, npc.X, npc.Y, Sounds.FLECHA_EXPLOSIVA);
            BroadcastFX(npc.Map, npc.CharIndex, 33, 0);
        }
        else if (ammoName.Contains("Incendiaria", StringComparison.OrdinalIgnoreCase))
        {
            BroadcastWaveArea(npc.Map, npc.X, npc.Y, 79);
            BroadcastParticulaChar(npc.Map, npc.CharIndex, 6, 3000);
            ProgramarRemoverParticula(npc.Map, npc.CharIndex, 6, 3.0); // VB6: RegistrarParticulaParaLimpiar (3s)
        }
        else if (ammoIndex == 1086) // Flecha Eléctrica
        {
            BroadcastWaveArea(npc.Map, npc.X, npc.Y, 56);
            BroadcastParticulaChar(npc.Map, npc.CharIndex, 86, 3000);
            ProgramarRemoverParticula(npc.Map, npc.CharIndex, 86, 3.0); // VB6: RegistrarParticulaParaLimpiar (3s)
        }
        else if (ammoIndex == 1083) // Flecha Envenenante
        {
            BroadcastWaveArea(npc.Map, npc.X, npc.Y, 26);
            BroadcastParticulaChar(npc.Map, npc.CharIndex, 32, 3000);
            ProgramarRemoverParticula(npc.Map, npc.CharIndex, 32, 3.0); // VB6: RegistrarParticulaParaLimpiar (3s)
        }
        else if (ammoIndex == 1085) // Flecha Paralizante
        {
            BroadcastWaveArea(npc.Map, npc.X, npc.Y, 16);
            BroadcastFX(npc.Map, npc.CharIndex, 8, 0);
            NpcManager.ParalizarNpc(npc, 60.0); // VB6: Contadores.Paralisis = 60s
        }
    }

    /// <summary>Proyectil visual + sonido de munición al impactar a un usuario (UsuarioAtacaUsuario:1539).
    /// Arco con munición → flecha animada(GrhIndex munición) + Snd1 + FX(Snd2) + IMPACTO3 al atacante,
    /// más efectos especiales por tipo de flecha. Arrojadiza(proyectil 2) → arma animada + sonido 68.
    /// Cualquier otra arma (o sin arma) → IMPACTO cuerpo a cuerpo.</summary>
    private static void ProyectilSonidoPvp(User atk, User vic)
    {
        int map = atk.Pos.Map;
        short arma = atk.Invent.WeaponEqpObjIndex;
        if (arma > 0 && ObjData.Get(arma).Proyectil == 1 && atk.Invent.MunicionEqpObjIndex > 0)
        {
            var ammo = ObjData.Get(atk.Invent.MunicionEqpObjIndex);
            BroadcastArrow(map, atk.Char.CharIndex, vic.Char.CharIndex, atk.Pos.X, atk.Pos.Y, vic.Pos.X, vic.Pos.Y, (short)ammo.GrhIndex);
            if (ammo.Snd1 > 0) BroadcastWaveArea(map, atk.Pos.X, atk.Pos.Y, (short)ammo.Snd1);
            if (ammo.Snd2 > 0) BroadcastFX(map, vic.Char.CharIndex, (short)ammo.Snd2, 0);
            if (atk.Conn != null) ServerPackets.PlayWave(atk.Conn, Sounds.IMPACTO3, (byte)atk.Pos.X, (byte)atk.Pos.Y);
            AplicarFlechaEspecialUsuario(atk, vic, atk.Invent.MunicionEqpObjIndex, ammo.Name);
        }
        else if (arma > 0 && ObjData.Get(arma).Proyectil == 2)
        {
            BroadcastArrow(map, atk.Char.CharIndex, vic.Char.CharIndex, atk.Pos.X, atk.Pos.Y, vic.Pos.X, vic.Pos.Y, (short)ObjData.Get(arma).GrhIndex);
            BroadcastWaveArea(map, atk.Pos.X, atk.Pos.Y, Sounds.ARROJADIZA);
        }
        else
            BroadcastWaveArea(map, atk.Pos.X, atk.Pos.Y, Sounds.IMPACTO);
    }

    /// <summary>Efectos especiales de flechas al impactar a un usuario (UsuarioAtacaUsuario:1557-1611):
    /// sonido + FX/partícula + estado (incinera/veneno/parálisis) por tipo de munición. 1:1 VB6, incluido
    /// que se suman a UserEnvenena/UserIncinera (el VB6 también los aplica por separado).</summary>
    private static void AplicarFlechaEspecialUsuario(User atk, User vic, short ammoIndex, string ammoName)
    {
        ammoName ??= "";
        int map = atk.Pos.Map;
        if (ammoName.Contains("Explosiva", StringComparison.OrdinalIgnoreCase))
        {
            BroadcastWaveArea(map, vic.Pos.X, vic.Pos.Y, Sounds.FLECHA_EXPLOSIVA);
            BroadcastFX(map, vic.Char.CharIndex, 33, 0);
        }
        if (ammoName.Contains("Incendiaria", StringComparison.OrdinalIgnoreCase))
        {
            BroadcastWaveArea(map, vic.Pos.X, vic.Pos.Y, 79);
            BroadcastParticulaChar(map, vic.Char.CharIndex, 6, 3000);
            ProgramarRemoverParticula(map, vic.Char.CharIndex, 6, 3.0); // VB6: RegistrarParticulaParaLimpiar (3s)
            if (vic.flags.Incinerado == 0 && _rng.Next(1, 101) <= 15) // 15% incinerar
            {
                vic.flags.Incinerado = 1; vic._timerIncinera = 0;
                BroadcastWaveArea(map, vic.Pos.X, vic.Pos.Y, Sounds.INCINERADO); // sonido de incinerado (78)
                BroadcastFX(map, vic.Char.CharIndex, 8, 0);
                BroadcastFX(map, vic.Char.CharIndex, 123, 0);
                if (vic.Conn != null) ServerPackets.LocaleMsg(vic.Conn, 48);
            }
        }
        if (ammoIndex == 1086) // Flecha Eléctrica
        {
            BroadcastWaveArea(map, vic.Pos.X, vic.Pos.Y, 56);
            BroadcastParticulaChar(map, vic.Char.CharIndex, 86, 3000);
            ProgramarRemoverParticula(map, vic.Char.CharIndex, 86, 3.0); // VB6: RegistrarParticulaParaLimpiar (3s)
        }
        if (ammoIndex == 1083) // Flecha Envenenante
        {
            BroadcastWaveArea(map, vic.Pos.X, vic.Pos.Y, 26);
            BroadcastParticulaChar(map, vic.Char.CharIndex, 32, 3000);
            ProgramarRemoverParticula(map, vic.Char.CharIndex, 32, 3.0); // VB6: RegistrarParticulaParaLimpiar (3s)
            if (_rng.Next(1, 101) <= 60) // 60% envenenar
            {
                vic.flags.Envenenado = 1; vic._timerVeneno = 0;
                if (vic.flags.NivelVeneno < 1) vic.flags.NivelVeneno = 1;
                if (vic.Conn != null) ServerPackets.ConsoleMsg(vic.Conn, $"¡{atk.Name} te ha envenenado!", 4);
                ServerPackets.ConsoleMsg(atk.Conn, $"¡Has envenenado a {vic.Name}!", 4);
            }
        }
        if (ammoIndex == 1085) // Flecha Paralizante
        {
            BroadcastWaveArea(map, vic.Pos.X, vic.Pos.Y, 16);
            BroadcastFX(map, vic.Char.CharIndex, 8, 0);
            if (vic.flags.Paralizado == 0)
            {
                vic.flags.Paralizado = 1; vic.flags.Inmovilizado = 1;
                vic.flags.ParalisisExpira = Environment.TickCount64 / 1000.0 + DuracionParalisisUsuario;
                if (vic.Conn != null) ServerPackets.ParalizeOK(vic.Conn);
                DifundirParalisisUsuario(vic, DuracionParalisisUsuario);
            }
        }
    }

    /// <summary>Impacto + daño a un NPC para ataques a distancia (sin apuñalar; igual que UsuarioAtaca PvE).</summary>
    private static void GolpearNpcADistancia(int userIndex, User u, NpcManager.NpcInstance npc)
    {
        // VB6 PuedeAtacarNPC: Attackable=0 intocable; guardias por reglas de facción (Rinkel intocable).
        if (!NpcManager.UsuarioPuedeAtacarNpc(u, npc, out string motGuardia))
        { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, motGuardia, 1); return; }

        if (!UserImpactoNpc(userIndex, npc))
        {
            FalloPropio(u);   // "¡Fallas!" sobre la cabeza del atacante
            BroadcastFX(npc.Map, npc.CharIndex, FX_GOLPE_FALLO, 0);  // FX de fallo sobre el NPC (todos lo ven)
            return;
        }
        BroadcastFX(npc.Map, npc.CharIndex, FX_GOLPE_ACIERTO, 0);    // FX de acierto sobre el NPC (todos lo ven)
        int dano = CalcularDanio(u, npc);
        DanoInfligido(u, npc.CharIndex, dano);
        // Sonido del disparo (UsuarioAtacaNpc proyectil): arco → Snd1 de la munición + IMPACTO3;
        // arrojadiza (proyectil 2) → sonido 68.
        var arma = u.Invent.WeaponEqpObjIndex > 0 ? ObjData.Get(u.Invent.WeaponEqpObjIndex) : default;
        if (arma.Proyectil == 1 && u.Invent.MunicionEqpObjIndex > 0)
        {
            var ammo = ObjData.Get(u.Invent.MunicionEqpObjIndex);
            // Flecha animada origen→destino (SistemaCombate.bas:1148, GrhIndex de la munición).
            BroadcastArrow(npc.Map, u.Char.CharIndex, npc.CharIndex, u.Pos.X, u.Pos.Y, npc.X, npc.Y, (short)ammo.GrhIndex);
            if (ammo.Snd1 > 0) BroadcastWaveArea(npc.Map, npc.X, npc.Y, (short)ammo.Snd1);
            BroadcastWaveArea(npc.Map, npc.X, npc.Y, Sounds.IMPACTO3);
            // FX de impacto de la munición (ammo.Snd2 se usa como GrhFX en VB6) y efectos especiales.
            if (ammo.Snd2 > 0) BroadcastFX(npc.Map, npc.CharIndex, (short)ammo.Snd2, 0);
            AplicarFlechaEspecial(npc, u.Invent.MunicionEqpObjIndex, ammo.Name);
        }
        else if (arma.Proyectil == 2)
        {
            // Arma arrojadiza animada (SistemaCombate.bas:1205, GrhIndex del arma) + sonido 68.
            BroadcastArrow(npc.Map, u.Char.CharIndex, npc.CharIndex, u.Pos.X, u.Pos.Y, npc.X, npc.Y, (short)arma.GrhIndex);
            BroadcastWaveArea(npc.Map, npc.X, npc.Y, Sounds.ARROJADIZA);
        }
        else
            BroadcastWaveArea(npc.Map, npc.X, npc.Y, Sounds.IMPACTO);
        GolpeOrbeNpc(u, npc);             // Orbe Acuática/espadas con Paraliza(11) también a distancia
        NpcManager.ProvocarNpc(npc, u);   // aggro a distancia
        CalcularDarExp(userIndex, npc, dano);
        npc.MinHP -= dano;
        if (npc.MinHP <= 0) MatarNpc(u, npc);
    }

    /// <summary>
    /// GolpeParalizaNPC, rama OBJ869 (SistemaCombate.bas:3602): la Orbe Acuática equipada
    /// (EfectoMagico=Paraliza(11), también espadas de Tierra/Sable Wivern) paraliza a la
    /// criatura al golpear: 60% de probabilidad, 60 segundos.
    /// </summary>
    private static void GolpeOrbeNpc(User u, NpcManager.NpcInstance npc)
    {
        if (!Inventory.TieneEfectoMagico(u, 11, incluirArma: true)) return;
        // Doble parálisis permitida contra NPC: aunque ya esté paralizado, re-aplica y refresca el timer.
        if (_rng.Next(1, 101) > 60) return;
        NpcManager.ParalizarNpc(npc, 60.0); // VB6: Contadores.Paralisis = 60 segundos
        BroadcastWaveArea(npc.Map, npc.X, npc.Y, 17);  // VB6 PlayWave(17)
        BroadcastFX(npc.Map, npc.CharIndex, 8, 0);     // VB6 CreateFX(8)
        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "¡Tu golpe paraliza a la criatura!", 2);
    }

    /// <summary>userIndex del jugador con ese CharIndex (0 si ninguno).</summary>
    private static int FindUserByCharIndex(short charIndex)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Char.CharIndex == charIndex) return i;
        }
        return 0;
    }

    private const byte SkillApunalar = 4; // eSkill.Apuñalar
    // FX que se muestra sobre el objetivo al apuñalar (CreateFX). 180 = FX nativo de fxs.ind
    // (GRH animado 385765, 5 frames). Entra por el flujo normal handle_create_fx del cliente.
    private const short FX_APUNALAR = 180;
    // FX visibles para todos sobre el objetivo del golpe cuerpo a cuerpo / distancia:
    // 89 = golpe acertado, 90 = golpe fallado. Se difunden con BroadcastFX (todo el mapa los ve).
    private const short FX_GOLPE_ACIERTO = 89;
    private const short FX_GOLPE_FALLO = 90;

    // Dagas que pueden apuñalar (Puedeapualar, SistemaCombate.bas:3761) — lista HARDCODEADA del VB6.
    // Este server modded NO usa el flag Apuñala del obj.dat sino estos ObjIndex exactos.
    // 460=Daga Newbie, 15=Daga, 165=Daga+1, 365..367=Daga+2..+4, 746=Daga+5, 559=Daga Plata,
    // 994=Daga Corsario, 740=Daga Infernal, 1261=Daga Ígnea.
    private static readonly HashSet<int> DagasApunalar = new()
    { 460, 15, 165, 365, 366, 367, 746, 559, 994, 740, 1261 };

    /// <summary>
    /// VB6 Puedeapualar (SistemaCombate.bas:3729): clase Asesino(4)/Ladrón(5) + una de las dagas de
    /// la lista equipada + 20% prob. A mano limpia NO permite apuñalamiento automático.
    /// </summary>
    private static bool PuedeApunalar(User u)
    {
        if (u.Clase != 4 && u.Clase != 5) return false;            // Asesino o Ladrón
        if (u.Invent.WeaponEqpObjIndex <= 0) return false;          // requiere daga equipada (no manos)
        if (!DagasApunalar.Contains(u.Invent.WeaponEqpObjIndex)) return false; // debe ser una daga
        return _rng.Next(1, 101) <= 20;                             // 20% fijo
    }

    /// <summary>VB6: daño apuñalamiento = base * (0.25 + skill/100*1.25), mínimo 5. Sube skill.</summary>
    private static int DanoApunalamiento(User u, int danoBase)
    {
        double porc = 0.25 + (u.Stats.UserSkills[SkillApunalar] / 100.0) * 1.25;
        int dano = Math.Max(5, (int)(danoBase * porc));
        Skills.SubirSkill(u.id, SkillApunalar); // SubirSkill 1:1 (Trabajo.bas:2105)
        return dano;
    }

    /// <summary>
    /// GolpeParalizaNPC (SistemaCombate.bas:3521): parálisis de artes marciales contra NPC.
    /// Gladiador(8)/Bardo(6): con nudillos → prob = Wrestling/2 (50% máx); a mano limpia
    /// (sin arma ni nudillos) → prob = Wrestling/3 (33% máx).
    /// Guerrero(3): SOLO contra NPC (nunca PvP) y con probabilidad baja → nudillos = Wrestling/6
    /// (16% máx), mano limpia = Wrestling/9 (11% máx). Paraliza al NPC 60 segundos.
    /// </summary>
    private static void GolpeParalizaNpc(User u, NpcManager.NpcInstance npc)
    {
        if (u.Clase != 8 && u.Clase != 6 && u.Clase != 3) return;
        // Doble parálisis permitida contra NPC: aunque ya esté paralizado, re-aplica y refresca el timer.
        int divisor = u.Clase == 3 ? 3 : 1; // Guerrero: probabilidad baja (un tercio de la normal)
        int prob;
        if (u.Invent.NudiEqpObjIndex > 0) prob = u.Stats.UserSkills[SK_WRESTLING] / (2 * divisor);
        else if (u.Invent.WeaponEqpObjIndex == 0) prob = u.Stats.UserSkills[SK_WRESTLING] / (3 * divisor);
        else return; // arma normal equipada: no paraliza
        if (prob <= 0 || _rng.Next(1, 101) > prob) return;

        NpcManager.ParalizarNpc(npc, 60.0); // VB6 Contadores.Paralisis = 60 segundos
        BroadcastWaveArea(npc.Map, npc.X, npc.Y, 17);           // VB6 PlayWave(17)
        BroadcastFX(npc.Map, npc.CharIndex, 8, 0);              // VB6 CreateFX(8)
        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "Tu golpe ha paralizado a la criatura.", 1);
    }

    /// <summary>
    /// CheckUserLevel (Modulo_UsUaRiOs.bas:666) 1:1: mientras Exp >= ELU, sube de nivel.
    /// Vida y maná por SISTEMA FIJO (GetVidaFijaPorNivel/GetManaFijaPorNivel), stamina y HIT
    /// por clase, ELU de Niveles.dat. Avisa con LevelUp + UpdateUserStats.
    /// </summary>
    public static void CheckUserLevel(User u)
    {
        bool subio = false;
        while (u.Stats.ELV < Leveling.STAT_MAXELV && u.Stats.Exp >= u.Stats.ELU && u.Stats.ELU > 0)
        {
            u.Stats.Exp -= u.Stats.ELU;
            u.Stats.ELV++;
            u.Stats.ELU = u.Stats.ELV < Leveling.STAT_MAXELV ? Leveling.ELU(u.Stats.ELV) : 0;
            u.Stats.SkillPts += 5;

            // Vida fija para el nuevo nivel (CheckUserLevel:711-725): nunca baja; mínimo +1.
            int vidaNueva = Leveling.VidaFijaPorNivel(u.raza, u.Clase, u.Stats.ELV);
            if (vidaNueva < u.Stats.MaxHP) vidaNueva = u.Stats.MaxHP + 1;
            u.Stats.MaxHP = (short)Math.Min(vidaNueva, Leveling.STAT_MAXHP);
            u.Stats.MinHP = u.Stats.MaxHP;

            // AumentoHIT/AumentoSTA por clase (CheckUserLevel:730-799). AumentoSTDef=15 (Declares.bas:550).
            // Tablas centralizadas en Leveling para que /mod nivel aplique exactamente lo mismo.
            int aumentoHIT = Leveling.AumentoHit(u.Clase, u.Stats.ELV);
            int aumentoSTA = Leveling.AumentoSta(u.Clase);
            u.Stats.MaxSta = (short)Math.Min(u.Stats.MaxSta + aumentoSTA, Leveling.STAT_MAXSTA);

            // Maná fijo para el nuevo nivel (CheckUserLevel:811-828): nunca baja.
            int manaNuevo = Leveling.ManaFijaPorNivel(u.raza, u.Clase, u.Stats.ELV);
            if (manaNuevo < u.Stats.MaxMAN) manaNuevo = u.Stats.MaxMAN;
            u.Stats.MaxMAN = (short)Math.Min(manaNuevo, Leveling.STAT_MAXMAN);

            // HIT máx/mín con tope por nivel (CheckUserLevel:830-846).
            int topeHit = u.Stats.ELV < 36 ? Leveling.STAT_MAXHIT_UNDER36 : Leveling.STAT_MAXHIT_OVER36;
            u.Stats.MaxHIT = (short)Math.Min(u.Stats.MaxHIT + aumentoHIT, topeHit);
            u.Stats.MinHIT = (short)Math.Min(u.Stats.MinHIT + aumentoHIT, topeHit);
            subio = true;
        }
        if (subio)
        {
            // Al alcanzar el nivel máximo se limpia la exp (CheckUserLevel:917-921).
            if (u.Stats.ELV >= Leveling.STAT_MAXELV) { u.Stats.Exp = 0; u.Stats.ELU = 0; }
            ServerPackets.LevelUp(u.Conn, u.Stats.SkillPts);
            ServerPackets.UpdateUserStats(u.Conn, u);
            ServerPackets.ConsoleMsg(u.Conn, $"¡Has subido al nivel {u.Stats.ELV}!", 1);
            BroadcastWaveArea(u.Pos.Map, u.Pos.X, u.Pos.Y, Sounds.NIVEL_NUEVO); // sonido de subir nivel (72, custom)
            BattlePass.OnLevelUp(u.id); // puntos de pase por subir nivel de personaje

            // Nivel 15: deja de ser newbie → el Dungeon Newbie lo expulsa a la ciudad de su facción.
            Facciones.SalirDungeonNewbie(u, warpear: true);
        }
    }

    /// <summary>
    /// HandleCastSpell (Protocol.bas:2756) 1:1 VB6. El usuario selecciona un slot de hechizo.
    /// Si es AutoLanzar → lo lanza sobre sí mismo. Si no → pide target con WorkRequestTarget(Magia).
    /// </summary>
    public static void CastSpell(int userIndex, byte slot)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.flags.Muerto == 1) return;
        if (slot < 1 || slot > Constants.MAXUSERHECHIZOS) { u.SpellPendiente = 0; return; }

        short hIndex = u.Stats.UserHechizos[slot];
        if (hIndex <= 0) return; // slot vacío

        // VB6 PuedeLanzar: los GMs lanzan sin restricciones (incluso meditando).
        bool esGm = u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO;

        // VB6: no se puede lanzar meditando (salvo GM)
        if (!esGm && u.flags.Meditando)
        {
            ServerPackets.ConsoleMsg(u.Conn, "¡Estás meditando! No puedes lanzar hechizos.", 1);
            u.SpellPendiente = 0;
            return;
        }

        u.SpellPendiente = slot;

        var sp = SpellData.Get(hIndex);
        // Arma mágica (Tipo 10) se auto-lanza sobre uno mismo, igual que los AutoLanzar.
        if (sp.AutoLanzar || sp.Tipo == 10)
        {
            // Hechizo sobre uno mismo (invisibilidad, curar veneno propio, arma mágica, etc.): directo.
            LanzarHechizoEn(userIndex, (byte)u.Pos.X, (byte)u.Pos.Y);
        }
        else
        {
            // VB6: WriteWorkRequestTarget(eSkill.magia) → el cliente activa el cursor de apuntado.
            ServerPackets.WorkRequestTarget(u.Conn, 8); // eSkill.Magia = 8
        }
    }

    /// <summary>
    /// Lanza el hechizo pendiente sobre el tile (x,y). Lo invoca WorkLeftClick con skill=Magia.
    /// Aplica cura/daño según Hechizos.dat, descuenta maná, muestra FX y maneja muerte.
    /// </summary>
    public static void LanzarHechizoEn(int userIndex, byte x, byte y)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.flags.Muerto == 1) return;
        if (u.SpellPendiente == 0) return;

        // Intervalos de casteo (HandleWorkLeftClick magia, Protocol.bas:3649 1:1):
        //  arco read-only, GolpeMagia (¿pasó el tiempo desde el último golpe?); si no, cae al cooldown de casteo.
        if (!Intervals.PuedeUsarArco(u, actualizar: false)) return;
        if (!Intervals.PuedeGolpeMagia(u))
            if (!Intervals.PuedeLanzarSpell(u)) return;

        short hechizoIndex = u.Stats.UserHechizos[u.SpellPendiente];
        u.SpellPendiente = 0; // consumir la intención
        if (hechizoIndex <= 0) return;

        // Lanzar un hechizo cancela el casteo de la runa de teletransporte (LanzarHechizo:1131).
        if (u.CasteandoRuna > 0)
        {
            u.CasteandoRuna = 0; u.RunaSlot = 0;
            ServerPackets.RunaCastProgress(u.Conn, u.Char.CharIndex, 0, 6);
            ServerPackets.ConsoleMsg(u.Conn, "¡Teletransporte cancelado!", 1);
        }

        var sp = SpellData.Get(hechizoIndex);
        if (string.IsNullOrEmpty(sp.Nombre)) return;

        // VB6 PuedeLanzar: los GMs (EsGm) lanzan sin maná/stamina/skill ni consumo.
        bool esGm = u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO;

        // VB6 PuedeLanzar (modHechizos.bas:323). Los GM saltan TODAS las validaciones.
        // No se puede lanzar mientras se medita.
        if (!esGm && u.flags.Meditando)
        {
            ServerPackets.ConsoleMsg(u.Conn, "¡Estás meditando! Debes dejar de meditar para lanzar hechizos.", 1);
            return;
        }
        // Clase prohibida para este hechizo (ClasesProhibidas, usado en 120 hechizos).
        // Se valida ANTES que skill/stamina/maná para que el motivo correcto sea el que se muestra.
        if (!esGm && sp.ClasesProhibidas != null && Array.IndexOf(sp.ClasesProhibidas, (int)u.Clase) >= 0)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Tu clase no puede usar este hechizo.", 1);
            return;
        }
        // Hechizos especiales de guerrero (115-120) no validan skill de Magia para Guerrero/Gladiador/Mercenario.
        bool esHechizoEspecial = hechizoIndex >= 115 && hechizoIndex <= 120
            && (u.Clase == 3 || u.Clase == 8 || u.Clase == 17);
        if (!esGm && !esHechizoEspecial && u.Stats.UserSkills[8] < sp.MinSkill) // eSkill.Magia = 8
        {
            ServerPackets.ConsoleMsg(u.Conn, "No tienes suficientes puntos en Magia para lanzar este hechizo.", 1);
            return;
        }
        // Nivel mínimo del lanzador (hechizos de leveo). 0 = sin requisito. GMs exentos.
        if (!esGm && sp.MinLevel > 0 && u.Stats.ELV < sp.MinLevel)
        {
            ServerPackets.ConsoleMsg(u.Conn, $"Necesitas ser nivel {sp.MinLevel} para lanzar este hechizo.", 1);
            return;
        }
        if (!esGm && u.Stats.MinSta < sp.StaRequerido)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Estás muy cansado para lanzar este hechizo.", 1);
            return;
        }
        if (!esGm && u.Stats.MinMAN < sp.ManaRequerido)
        {
            ServerPackets.ConsoleMsg(u.Conn, "No tienes suficiente maná.", 1);
            return;
        }
        // Objeto requerido por el hechizo (ItemRequerido + CantidadRequerida).
        if (!esGm && sp.ItemRequerido > 0)
        {
            int cantReq = Math.Max(1, sp.CantidadRequerida);
            if (Inventory.ContarObjeto(u, sp.ItemRequerido) < cantReq)
            {
                string nombreItem = ObjData.Get(sp.ItemRequerido).Name ?? "objeto";
                ServerPackets.ConsoleMsg(u.Conn, $"Necesitas {cantReq} {nombreItem} para lanzar este hechizo.", 1);
                return;
            }
        }
        // Anillo requerido equipado/en inventario (PuedeLanzar, modHechizos.bas:389).
        if (!esGm && sp.Anillo > 0)
        {
            const int ANILLO_ESPECTRAL = 1329, ANILLO_PENUMBRAS = 1330;
            bool tienePenumbras = Inventory.ContarObjeto(u, ANILLO_PENUMBRAS) >= 1;
            bool tieneEspectral = Inventory.ContarObjeto(u, ANILLO_ESPECTRAL) >= 1;
            if (sp.Anillo == 1 && !tieneEspectral && !tienePenumbras)
            { ServerPackets.ConsoleMsg(u.Conn, "Necesitas el Anillo Espectral o el Anillo de Penumbras para lanzar este hechizo.", 1); return; }
            if (sp.Anillo == 2 && !tienePenumbras)
            { ServerPackets.ConsoleMsg(u.Conn, "Necesitas el Anillo de Penumbras para lanzar este hechizo.", 1); return; }
        }
        // Hechizos especiales de guerrero (116-120): costos y efectos propios (HandleHechizoUsuario:907).
        if (hechizoIndex >= 116 && hechizoIndex <= 120)
        {
            LanzarHechizoGuerrero(userIndex, u, hechizoIndex, sp, esGm);
            return;
        }

        // Portal de teletransporte (uCreateTelep, Tipo 5 / hechizo 53): inicia el casteo del portal.
        if (sp.Tipo == 5)
        {
            LanzarPortal(userIndex, u, x, y, sp, esGm);
            return;
        }

        // Arma mágica (Tipo 10): se auto-lanza y da un arma común que hace 120-170 de daño por 2 min.
        if (sp.Tipo == 10)
        {
            if (!esGm)
            {
                if (u.Stats.MinMAN < sp.ManaRequerido) { ServerPackets.ConsoleMsg(u.Conn, "No tienes suficiente maná.", 1); return; }
                u.Stats.MinMAN = (short)Math.Max(0, u.Stats.MinMAN - sp.ManaRequerido);
                ServerPackets.UpdateMana(u.Conn, u.Stats.MinMAN);
                if (sp.StaRequerido > 0)
                {
                    u.Stats.MinSta = (short)Math.Max(0, u.Stats.MinSta - sp.StaRequerido);
                    ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);
                }
            }
            Skills.SubirSkill(userIndex, 8); // VB6 SubirSkill incondicional (también GMs)
            DecirPalabrasMagicas(userIndex, sp.PalabrasMagicas); // modo 3 = palabras mágicas (burbuja, no consola ajena)
            if (sp.WAV > 0) BroadcastWaveArea(u.Pos.Map, u.Pos.X, u.Pos.Y, (short)sp.WAV);
            BroadcastFX(u.Pos.Map, u.Char.CharIndex, (short)sp.FXgrh, (short)Math.Max(0, sp.Loops));
            u.flags.ArmaMagicaExpira = Environment.TickCount64 / 1000.0 + 120.0; // 2 minutos
            ServerPackets.ConsoleMsg(u.Conn, "Un arma mágica se blande en tu mano. (120-170 de daño por 2 minutos)", 1);
            return;
        }

        // Determinar objetivo en (x,y). Incluye al propio usuario: hacer clic sobre uno mismo
        // es un target válido para hechizos de soporte (VB6 LookatTile no excluye al propio).
        var npc = NpcManager.NpcAt(u.Pos.Map, x, y);
        int targetUser = UserAt(u.Pos.Map, x, y, -1);

        // VB6 HandleCastSpell (Protocol.bas:2825): SOLO los hechizos AutoLanzar apuntan a uno
        // mismo automáticamente. El resto requiere apuntar a un objetivo (no se auto-lanzan).
        if (sp.AutoLanzar) targetUser = userIndex;

        // Validación 1:1 por tipo de objetivo (TargetType: 1=usuarios, 2=npc, 3=ambos, 4=terreno).
        // LanzarHechizo (modHechizos.bas:1172): un hechizo de usuarios NO afecta NPCs y viceversa.
        if (!sp.AutoLanzar && !sp.HechizoDeArea)
        {
            if (sp.Target == 1) npc = null;          // uUsuarios: ignora NPC apuntado
            else if (sp.Target == 2) targetUser = 0; // uNPC: ignora usuario apuntado
            // uUsuariosYnpc(3): ambos válidos; uTerreno(4): no apunta a entidad
        }

        // No se puede lanzar NINGÚN hechizo sobre un usuario muerto (casper), salvo los de revivir
        // (que usan UserAtIncluyeMuerto aparte). Cubre soporte y ofensivos por igual.
        if (targetUser > 0 && targetUser != userIndex
            && UserListManager.UserList[targetUser].flags.Muerto == 1 && !sp.Revivir)
        {
            ServerPackets.ConsoleMsg(u.Conn, "No puedes lanzar hechizos sobre un personaje muerto.", 1);
            return;
        }

        bool ofensivo = sp.SubeHP == 2 || sp.Paraliza || sp.Inmoviliza || sp.Ceguera
                        || sp.Envenena > 0 || sp.Incinera || sp.Estupidez
                        || sp.SubeFuerza == 2 || sp.SubeAgilidad == 2;  // debuffs de atributo
        // Sin objetivo: AutoLanzar (uno mismo), área (terreno) e invocación/revelar.
        // Sin objetivo: auto, área, invocación, revelar, detectar invisibles (12) y familiar (6).
        bool sinObjetivo = sp.AutoLanzar || sp.HechizoDeArea || sp.Invoca == 1 || sp.RemueveInvis
                           || sp.Tipo == 12 || sp.Tipo == 6 || sp.ResucitaFamiliar;

        // VB6 LanzarHechizo (modHechizos.bas:1172): si no es auto/área y no hay objetivo apuntado,
        // no se castea (ni se gasta maná). Antes cualquier hechizo se lanzaba sobre uno mismo.
        if (!sinObjetivo && npc == null && targetUser <= 0)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Primero debes seleccionar un objetivo.", 1);
            return;
        }
        // No te puedes lanzar a ti mismo un hechizo ofensivo.
        if (ofensivo && targetUser == userIndex)
        {
            ServerPackets.ConsoleMsg(u.Conn, "No puedes lanzarte ese hechizo a ti mismo.", 1);
            return;
        }

        // No se pueden lanzar hechizos de soporte (curar, remover parálisis, etc.) sobre criaturas
        // hostiles. Sólo se permite sobre las mascotas propias (MaestroUser == lanzador).
        if (npc != null && !ofensivo && !sp.HechizoDeArea && npc.MaestroUser != userIndex)
        {
            ServerPackets.ConsoleMsg(u.Conn, "No puedes lanzar hechizos de apoyo sobre criaturas hostiles.", 1);
            return;
        }

        // Hechizos de área ofensivos (Tormenta de fuego/ácida, etc.): sólo se pueden lanzar
        // clickeando sobre un NPC o usuario válido del área, no sobre terreno vacío.
        if (sp.HechizoDeArea && ofensivo && npc == null && targetUser <= 0)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Debes apuntar a una criatura para lanzar este hechizo.", 1);
            return;
        }

        // Ningún hechizo ofensivo sobre NPCs no atacables (Attackable=0) ni sobre guardias de Rinkel
        // (intocables) o de la propia facción/aliados. ANTES de gastar maná. Los GM saltan la restricción.
        if (!esGm && npc != null && ofensivo
            && !NpcManager.UsuarioPuedeAtacarNpc(u, npc, out string motGuardiaSpell))
        { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, motGuardiaSpell, 1); return; }

        // Seguridad/criminalidad sobre OTRO usuario, ANTES de gastar maná (1:1: PuedeAtacar para
        // ofensivos, PuedeAyudar para soporte; ambos ya muestran su propio mensaje al denegar).
        if (!esGm && targetUser > 0 && targetUser != userIndex)
        {
            if (ofensivo) { if (!PuedeAtacar(userIndex, targetUser)) return; }
            else if (!PuedeAyudar(userIndex, targetUser)) return;
        }

        // RemoverParalisis sólo se puede lanzar sobre un objetivo que esté paralizado/inmovilizado.
        // Si el objetivo no lo está, no se castea (ni se gasta maná ni salen las palabras mágicas).
        if (sp.RemoverParalisis && !sp.Paraliza && !sp.Inmoviliza)
        {
            bool objetivoParalizado;
            if (npc != null)
                objetivoParalizado = npc.ParalizadoHasta > 0;
            else if (targetUser > 0)
            {
                var tp = UserListManager.UserList[targetUser];
                objetivoParalizado = tp.flags.Paralizado == 1 || tp.flags.Inmovilizado == 1;
            }
            else objetivoParalizado = false;

            if (!objetivoParalizado)
            {
                ServerPackets.ConsoleMsg(u.Conn, "El objetivo no está paralizado.", 1);
                return;
            }
        }

        // Paralizar/Inmovilizar: la "doble parálisis" (re-aplicar sobre un objetivo ya paralizado,
        // refrescando el timer) SÓLO se permite contra NPC. Sobre usuarios se mantiene el bloqueo:
        // ParalizeOK es un toggle en el cliente y reaplicarlo desincronizaría el estado.
        if ((sp.Paraliza || sp.Inmoviliza) && npc == null && targetUser > 0)
        {
            var tp = UserListManager.UserList[targetUser];
            if (tp.flags.Paralizado == 1 || tp.flags.Inmovilizado == 1)
            {
                ServerPackets.ConsoleMsg(u.Conn, "El objetivo ya está paralizado.", 1);
                return;
            }
        }

        // Hechizos de leveo (121/122): curva de daño lineal PROPIA por nivel, en lugar del
        // escalado global EscalaMagia×ELV. base(nivel 15) + (ELV-15)×crecimiento, manteniendo
        // la diferencia min↔max. Ver LevelingSpellGrowth. El resto (maná/skill/efectos/FX) sin tocar.
        bool esLeveo = LevelingSpellGrowth.TryGetValue(hechizoIndex, out double crecimientoLeveo);
        int magnitud;
        if (esLeveo)
        {
            int nivelLeveo = Math.Clamp((int)u.Stats.ELV, 15, 50);
            int minLvl = (int)Math.Round(sp.MinHP + (nivelLeveo - 15) * crecimientoLeveo);
            int maxLvl = (int)Math.Round(sp.MaxHP + (nivelLeveo - 15) * crecimientoLeveo);
            magnitud = maxLvl > minLvl ? _rng.Next(minLvl, maxLvl + 1) : minLvl;
        }
        else
        {
            magnitud = sp.MaxHP >= sp.MinHP && sp.MaxHP > 0 ? _rng.Next(sp.MinHP, sp.MaxHP + 1) : sp.MinHP;
        }

        // Descontar maná y stamina (los GMs no consumen) y mostrar palabras mágicas.
        if (!esGm)
        {
            u.Stats.MinMAN = (short)Math.Max(0, u.Stats.MinMAN - sp.ManaRequerido);
            ServerPackets.UpdateMana(u.Conn, u.Stats.MinMAN);
            if (sp.StaRequerido > 0)
            {
                u.Stats.MinSta = (short)Math.Max(0, u.Stats.MinSta - sp.StaRequerido);
                ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);
            }
            // Consumir el objeto requerido por el hechizo (QuitarObjetos tras castear).
            if (sp.ItemRequerido > 0)
                Inventory.QuitarObjetos(u, sp.ItemRequerido, Math.Max(1, sp.CantidadRequerida));
        }
        Skills.SubirSkill(userIndex, 8); // sube skill de Magia 1:1 (VB6 SubirSkill incondicional, también GMs)
        DecirPalabrasMagicas(userIndex, sp.PalabrasMagicas); // modo 3 = palabras mágicas (burbuja, no consola ajena)

        // Mensajes del hechizo (HechizeroMsg al lanzador; TargetMsg/PropioMsg al objetivo).
        EnviarMensajesHechizo(u, sp, targetUser);

        short fx = (short)sp.FXgrh, fxLoops = (short)Math.Max(0, sp.Loops);

        // Sonido del hechizo (VB6 LanzarHechizo: PrepareMessagePlayWave(Hechizos(Spell).WAV, x, y) si WAV<>0).
        // Centralizado: cubre cura/daño/estados/área/self. Se reproduce en la posición casteada (x,y).
        if (sp.WAV > 0) BroadcastWaveArea(u.Pos.Map, x, y, (short)sp.WAV);

        // Partícula del hechizo (InfoHechizo, modHechizos.bas:2149): sobre el objetivo si es de un
        // solo target (usuario/NPC), o sobre el tile si es de área. TimeParticula = duración.
        // Los hechizos de REVIVIR no muestran su partícula propia (p.ej. Resurrección Particle=53,
        // "martillos"): usan sólo la partícula 18 de casteo sobre el lanzador (más abajo).
        if (sp.Particle > 0 && !sp.Revivir)
        {
            if (sp.HechizoDeArea)
            {
                // OJO: en el cliente time=0 significa "BORRAR la partícula del tile" (incl. las
                // ambientales del mapa). Para una partícula transitoria de hechizo, mandar una
                // duración positiva; si el dat trae 0, usar un default y nunca 0.
                int tp = sp.TimeParticula > 0 ? sp.TimeParticula : 1000;
                BroadcastParticulaTerreno(u.Pos.Map, (short)sp.Particle, x, y, tp);
            }
            else
            {
                short tgtChar = npc != null ? npc.CharIndex
                    : (targetUser > 0 ? UserListManager.UserList[targetUser].Char.CharIndex : u.Char.CharIndex);
                BroadcastParticulaChar(u.Pos.Map, tgtChar, (short)sp.Particle, sp.TimeParticula);
            }
        }

        // Hechizo de área: aplica el efecto a TODOS los del radio alrededor del tile, no solo
        // al objetivo único (VB6 HechizoDeArea/AreaEfecto). Daño y estados ofensivos van a
        // enemigos (NPCs y otros usuarios); cura/buffs van a usuarios (incluido el lanzador).
        if (sp.HechizoDeArea)
        {
            AplicarHechizoArea(u, sp, x, y, fx, fxLoops, ofensivo);
            return;
        }

        if (sp.SubeHP == 1) // CURA (escala con nivel: daño + Porcentaje(daño, 3*ELV))
        {
            var tgt = targetUser > 0 ? UserListManager.UserList[targetUser] : u;
            int sana = magnitud + Porcentaje(magnitud, 3 * u.Stats.ELV);
            tgt.Stats.MinHP = (short)Math.Min(tgt.Stats.MaxHP, tgt.Stats.MinHP + sana);
            if (tgt.Conn != null) ServerPackets.UpdateHP(tgt.Conn, tgt.Stats.MinHP);
            BroadcastFX(u.Pos.Map, tgt.Char.CharIndex, fx, fxLoops);
            // El sonido ya se reprodujo arriba con el WAV del hechizo (sp.WAV). Si el dat no trae WAV
            // para la cura, caer al SND_SANAR clásico para no quedar sin sonido.
            if (sp.WAV <= 0) BroadcastWaveArea(u.Pos.Map, u.Pos.X, u.Pos.Y, Sounds.SANAR); // SND_SANAR=55
            ServerPackets.ConsoleMsg(u.Conn, $"Curaste {sana} puntos de vida.", 1);
        }
        else if (sp.SubeHP == 2) // DAÑA
        {
            if (npc != null)
            {
                // Daño a NPC: escala 3*ELV + bonus de báculo (HechizoPropNPC, modHechizos.bas:2110).
                // Hechizos de leveo (esLeveo): magnitud YA es el daño final de la curva por nivel.
                int dano = (esLeveo ? magnitud : magnitud + Porcentaje(magnitud, BalanceData.Combate.EscalaMagiaPvE * u.Stats.ELV)) + BonusBaculoMagico(u);
                if (dano < 0) dano = 0;
                BroadcastFX(u.Pos.Map, npc.CharIndex, fx, fxLoops);
                CalcularDarExp(userIndex, npc, dano); // exp proporcional por daño mágico
                npc.MinHP -= dano;
                DanoInfligido(u, npc.CharIndex, dano);
                NpcManager.ProvocarNpc(npc, u);   // aggro por hechizo
                if (npc.MinHP <= 0) MatarNpc(u, npc);
            }
            else if (targetUser > 0)
            {
                // Daño a usuario: escala 2*ELV + bonus báculo - ResistenciaMágica del equipo del objetivo
                // (HechizoEstadoUsuario, modHechizos.bas:1879). El objetivo sube skill Resistencia.
                var tgt = UserListManager.UserList[targetUser];
                // Hechizos de leveo (esLeveo): magnitud YA es el daño final de la curva por nivel.
                int dano = (esLeveo ? magnitud : magnitud + Porcentaje(magnitud, BalanceData.Combate.EscalaMagiaPvP * u.Stats.ELV)) + BonusBaculoMagico(u)
                           - ResistenciaMagicaEquipo(tgt);
                // Anillo de Defensa Mágica (708, DisminuyeGolpe(7)): reduce el daño mágico en CuantoAumento%.
                int redPct = Inventory.CuantoEfectoMagico(tgt, 7);
                if (redPct > 0) dano -= dano * redPct / 100;
                if (dano < 0) dano = 0;
                Skills.SubirSkill(targetUser, 9); // eSkill.Resistencia = 9
                BroadcastFX(u.Pos.Map, tgt.Char.CharIndex, fx, fxLoops);
                tgt.Stats.MinHP -= (short)dano;
                DanoRecibido(tgt, u.Char.CharIndex, dano);     // rojo sobre el atacante para la víctima
                DanoInfligido(u, tgt.Char.CharIndex, dano);    // azul sobre la víctima para el lanzador
                if (tgt.Stats.MinHP <= 0) { tgt.Stats.MinHP = 0; if (tgt.Conn != null) ServerPackets.UpdateHP(tgt.Conn, 0); Facciones.ContarMuerte(targetUser, u.id); UserDie(targetUser); }
                else if (tgt.Conn != null) ServerPackets.UpdateHP(tgt.Conn, tgt.Stats.MinHP);
            }
            else
            {
                ServerPackets.ConsoleMsg(u.Conn, "No hay objetivo válido ahí.", 1);
            }
        }

        // --- Cura/quita stamina (SubeSta: 1=cura, 2=quita) ---
        if (sp.SubeSta > 0)
        {
            var tgt = targetUser > 0 ? UserListManager.UserList[targetUser] : u;
            int magSta = sp.MaxSta >= sp.MinSta && sp.MaxSta > 0 ? _rng.Next(sp.MinSta, sp.MaxSta + 1) : sp.MinSta;
            tgt.Stats.MinSta = sp.SubeSta == 1
                ? (short)Math.Min(tgt.Stats.MaxSta, tgt.Stats.MinSta + magSta)
                : (short)Math.Max(0, tgt.Stats.MinSta - magSta);
            if (tgt.Conn != null) ServerPackets.UpdateSta(tgt.Conn, tgt.Stats.MinSta);
            BroadcastFX(u.Pos.Map, tgt.Char.CharIndex, fx, fxLoops);
        }

        // --- Buff/debuff de fuerza/agilidad (HechizoEstadoUsuario, modHechizos.bas:1750/1793) ---
        //     SubeFU/SubeAG = 1 → aumenta (DuracionEfecto 1200); = 2 → reduce/ofensivo (700).
        //     Al expirar (TickEstados) o morir, los atributos vuelven al BackUP base. Cap [6..35].
        if (sp.SubeFuerza > 0 || sp.SubeAgilidad > 0)
        {
            const int MAXATRIB = 35, MINATRIB = 6;       // MAXATRIBUTOS=35, MINATRIBUTOS=6 (Declares.bas:390)
            var tgt = targetUser > 0 ? UserListManager.UserList[targetUser] : u;
            double ahoraAtr = Environment.TickCount64 / 1000.0;
            bool aplicado = false;

            if (sp.SubeFuerza == 1)      // aumenta
            {
                int m = sp.MaxFuerza >= sp.MinFuerza && sp.MaxFuerza > 0 ? _rng.Next(sp.MinFuerza, sp.MaxFuerza + 1) : sp.MinFuerza;
                tgt.Stats.UserAtributos[1] = (byte)Math.Min(MAXATRIB, tgt.Stats.UserAtributos[1] + m);
                tgt.flags.AtributoEfectoExpira = ahoraAtr + 120.0; aplicado = true;
                if (tgt.Conn != null) ServerPackets.UpdateStrenght(tgt.Conn, tgt.Stats.UserAtributos[1]);
            }
            else if (sp.SubeFuerza == 2) // reduce (ofensivo)
            {
                int m = sp.MaxFuerza >= sp.MinFuerza && sp.MaxFuerza > 0 ? _rng.Next(sp.MinFuerza, sp.MaxFuerza + 1) : sp.MinFuerza;
                tgt.Stats.UserAtributos[1] = (byte)Math.Max(MINATRIB, tgt.Stats.UserAtributos[1] - m);
                tgt.flags.AtributoEfectoExpira = ahoraAtr + 70.0; aplicado = true;
                if (tgt.Conn != null) ServerPackets.UpdateStrenght(tgt.Conn, tgt.Stats.UserAtributos[1]);
            }

            if (sp.SubeAgilidad == 1)
            {
                int m = sp.MaxAgilidad >= sp.MinAgilidad && sp.MaxAgilidad > 0 ? _rng.Next(sp.MinAgilidad, sp.MaxAgilidad + 1) : sp.MinAgilidad;
                tgt.Stats.UserAtributos[2] = (byte)Math.Min(MAXATRIB, tgt.Stats.UserAtributos[2] + m);
                tgt.flags.AtributoEfectoExpira = ahoraAtr + 120.0; aplicado = true;
                if (tgt.Conn != null) ServerPackets.UpdateDexterity(tgt.Conn, tgt.Stats.UserAtributos[2]);
            }
            else if (sp.SubeAgilidad == 2)
            {
                int m = sp.MaxAgilidad >= sp.MinAgilidad && sp.MaxAgilidad > 0 ? _rng.Next(sp.MinAgilidad, sp.MaxAgilidad + 1) : sp.MinAgilidad;
                tgt.Stats.UserAtributos[2] = (byte)Math.Max(MINATRIB, tgt.Stats.UserAtributos[2] - m);
                tgt.flags.AtributoEfectoExpira = ahoraAtr + 70.0; aplicado = true;
                if (tgt.Conn != null) ServerPackets.UpdateDexterity(tgt.Conn, tgt.Stats.UserAtributos[2]);
            }

            if (aplicado) { tgt.flags.TomoPocion = true; BroadcastFX(u.Pos.Map, tgt.Char.CharIndex, fx, fxLoops); }
        }

        // --- Invocación de criaturas (Invoca: NumNpc criaturas, Cant veces) ---
        // VB6 (modHechizos.bas:671): no se invocan criaturas en zonas seguras (mapa no-PK o ZONASEGURA).
        var mapInv = MapLoader.Get(u.Pos.Map);
        bool zonaSeguraInvoca = mapInv != null &&
            (!mapInv.Info.Pk || mapInv.GetTrigger(u.Pos.X, u.Pos.Y) == eTrigger.ZONASEGURA);
        // VB6 (modHechizos.bas:687): si el hechizo de invocación tiene Warp=1, en vez de invocar
        // acerca al amo su mascota más lejana (WarpMascota).
        if (sp.Invoca == 1 && sp.Warp)
        {
            NpcManager.WarpFarthestPet(userIndex, u.Pos.Map, (byte)u.Pos.X, (byte)u.Pos.Y);
        }
        else if (sp.Invoca == 1 && sp.NumNpc > 0 && !zonaSeguraInvoca)
        {
            int cant = Math.Max(1, sp.Cant);
            int invocadas = 0;
            for (int n = 0; n < cant && u.NroMascotas < Constants.MAXMASCOTAS; n++)
            {
                var inv = InvocarCriatura(u, sp.NumNpc, x, y);
                if (inv != null) invocadas++;
            }
            if (invocadas > 0) ServerPackets.ConsoleMsg(u.Conn, $"Has invocado {invocadas} criatura(s).", 1);
        }

        // --- Resucitar/Resurrección a un usuario muerto en el tile (con casteo) ---
        // El casteo muestra la partícula 18 sobre el LANZADOR (no el muerto). Si el lanzador se mueve,
        // se cancela. Al completar (5s Resucitar / 3s Resurrección) revive y se BORRA la partícula de
        // casteo. Resucitar revive con 20 HP; Resurrección con vida completa.
        if (sp.Revivir)
        {
            int vm = UserAtIncluyeMuerto(u.Pos.Map, x, y);
            if (vm > 0)
            {
                var tgt = UserListManager.UserList[vm];
                if (tgt.flags.Muerto == 1)
                {
                    bool esResurreccion = !string.IsNullOrEmpty(sp.Nombre)
                        && sp.Nombre.StartsWith("Resurrec", StringComparison.OrdinalIgnoreCase);
                    double ahoraR = Environment.TickCount64 / 1000.0;
                    u.ResucitandoHasta = ahoraR + (esResurreccion ? 3.0 : 5.0);
                    u.ResucitandoTarget = vm;
                    u.ResucitandoFull = esResurreccion;
                    u.ResucitandoX = (byte)u.Pos.X; u.ResucitandoY = (byte)u.Pos.Y; // posición para detectar movimiento
                    // Partícula 18 sobre el LANZADOR mientras castea (vida larga; se borra al terminar/cancelar).
                    BroadcastParticulaChar(u.Pos.Map, u.Char.CharIndex, 18, -1);
                    ServerPackets.ConsoleMsg(u.Conn, $"Comienzas a revivir a {tgt.Name}... no te muevas.", 1);
                }
            }
        }

        // --- Revelar ocultos/invisibles del área (RemueveInvis o uDetectarInvis Tipo 12) ---
        if (sp.RemueveInvis || sp.Tipo == 12)
            RevelarArea(u);

        // --- Familiar (uFamiliar Tipo 6 / ResucitaFamiliar): en VB6 solo da la animación ---
        if (sp.Tipo == 6 || sp.ResucitaFamiliar)
            BroadcastFX(u.Pos.Map, u.Char.CharIndex, fx, fxLoops);

        // --- Efectos de estado sobre NPC (HechizoEstadoNPC, modHechizos.bas:1963) ---
        if (npc != null && (sp.Paraliza || sp.Inmoviliza))
        {
            // Bot de sparring: INMOVILIZAR (no paralizar) NO lo remueve: queda quieto pero sigue pegando
            // (para poder darle golpes al aire si te acercás). Sólo PARALIZAR lo remueve (fin del test).
            if (npc.IsBot && npc.BotSpar && sp.Inmoviliza && !sp.Paraliza)
            {
                npc.InmovilizadoHasta = Environment.TickCount64 / 1000.0 + 60.0;
                npc.EstadoParalisisTick = Environment.TickCount64;  // para la reacción del bot al sacarse la inmovilización
                BroadcastFX(u.Pos.Map, npc.CharIndex, fx, fxLoops);
                ServerPackets.ConsoleMsg(u.Conn, $"Has inmovilizado a {npc.Name}.", 1);
            }
            else
            {
                NpcManager.ParalizarNpc(npc, 60.0);   // VB6 Contadores.Paralisis = 60 segundos
                BroadcastFX(u.Pos.Map, npc.CharIndex, fx, fxLoops);
                ServerPackets.ConsoleMsg(u.Conn, $"Has paralizado a {npc.Name}.", 1);
            }
        }
        // RemoverParalisis a un NPC: solo si es tu mascota (MaestroUser == lanzador).
        if (npc != null && sp.RemoverParalisis && npc.MaestroUser == userIndex && npc.ParalizadoHasta > 0)
        {
            npc.ParalizadoHasta = 0;
            NpcManager.DifundirParalisisNpc(npc, 0);
            ServerPackets.ConsoleMsg(u.Conn, $"Has removido la parálisis de {npc.Name}.", 1);
        }

        // --- Efectos de estado (sobre usuarios) ---
        if (targetUser > 0)
        {
            var tgt = UserListManager.UserList[targetUser];
            double ahora = Environment.TickCount64 / 1000.0;

            if (sp.RemoverParalisis && (tgt.flags.Paralizado == 1 || tgt.flags.Inmovilizado == 1))
            {
                tgt.flags.Paralizado = 0; tgt.flags.Inmovilizado = 0; tgt.flags.ParalisisExpira = 0;
                DifundirParalisisUsuario(tgt, 0);
                if (tgt.Conn != null) { ServerPackets.ParalizeOK(tgt.Conn); ServerPackets.ConsoleMsg(tgt.Conn, "Ya no estás paralizado.", 1); }
            }
            // No re-aplicar si ya está paralizado o inmovilizado: ParalizeOK es un toggle
            // en el cliente, reenviarlo lo desactivaría y dejaría el estado desincronizado.
            if ((sp.Paraliza || sp.Inmoviliza) && tgt.flags.Paralizado == 0 && tgt.flags.Inmovilizado == 0)
            {
                if (sp.Paraliza)
                {
                    tgt.flags.Paralizado = 1; tgt.flags.ParalisisExpira = ahora + DuracionParalisisUsuario;
                    if (tgt.Conn != null) { ServerPackets.ParalizeOK(tgt.Conn); ServerPackets.ConsoleMsg(tgt.Conn, $"{u.Name} te ha paralizado.", 1); }
                    DifundirParalisisUsuario(tgt, DuracionParalisisUsuario);
                }
                if (sp.Inmoviliza)
                {
                    tgt.flags.Inmovilizado = 1; tgt.flags.ParalisisExpira = ahora + DuracionParalisisUsuario;
                    if (tgt.Conn != null) { ServerPackets.ParalizeOK(tgt.Conn); ServerPackets.ConsoleMsg(tgt.Conn, $"{u.Name} te ha inmovilizado.", 1); }
                    DifundirParalisisUsuario(tgt, DuracionParalisisUsuario);
                }
            }
            if (sp.Ceguera)
            {
                tgt.flags.Ciego = 1; tgt.flags.CegueraExpira = ahora + 6.0;
                if (tgt.Conn != null) { ServerPackets.Blind(tgt.Conn); ServerPackets.ConsoleMsg(tgt.Conn, $"{u.Name} te ha cegado.", 1); }
            }
            if (sp.Estupidez)
            {
                tgt.flags.Estupido = 1; tgt.flags.EstupidezExpira = ahora + 6.0;
                if (tgt.Conn != null) { ServerPackets.Dumb(tgt.Conn); ServerPackets.ConsoleMsg(tgt.Conn, $"{u.Name} te ha aturdido.", 1); }
            }
            if (sp.RemoverEstupidez && tgt.flags.Estupido == 1)
            {
                tgt.flags.Estupido = 0; tgt.flags.EstupidezExpira = 0;
                if (tgt.Conn != null) { ServerPackets.DumbNoMore(tgt.Conn); ServerPackets.ConsoleMsg(tgt.Conn, "Ya no estás aturdido.", 1); }
            }
            if (sp.Envenena > 0)
            {
                tgt.flags.Envenenado = 1;
                tgt.flags.NivelVeneno = sp.Envenena; // nivel de veneno (mayor = más daño/tick)
                // Veneno crítico (nivel >= 5): dura sólo unos segundos (5s) y luego se neutraliza.
                tgt.flags.VenenoExpira = sp.Envenena >= 5 ? ahora + 5.0 : 0;
                if (tgt.Conn != null) ServerPackets.ConsoleMsg(tgt.Conn, $"¡{u.Name} te ha envenenado!", 4);
                ServerPackets.ConsoleMsg(u.Conn, $"¡Has envenenado a {tgt.Name}!", 1);
            }
            // Maldición: impide atacar por 5 segundos (el daño ya se aplicó en el bloque SubeHP).
            if (!string.IsNullOrEmpty(sp.Nombre) && sp.Nombre.StartsWith("Maldic", StringComparison.OrdinalIgnoreCase))
            {
                tgt.flags.Maldecido = 1; tgt.flags.MaldecidoExpira = ahora + 5.0;
                if (tgt.Conn != null) ServerPackets.ConsoleMsg(tgt.Conn, $"¡{u.Name} te ha maldecido! No puedes atacar.", 4);
                ServerPackets.ConsoleMsg(u.Conn, $"¡Has maldecido a {tgt.Name}!", 1);
            }
            if (sp.CuraVeneno && tgt.flags.Envenenado == 1)
            {
                tgt.flags.Envenenado = 0; tgt.flags.NivelVeneno = 0;
                if (tgt.Conn != null) ServerPackets.ConsoleMsg(tgt.Conn, "Te has curado del envenenamiento.", 1);
            }
            if (sp.Incinera)
            {
                tgt.flags.Incinerado = 1;
                tgt.flags.IncineradoExpira = ahora + 8.0; // la incineración dura 8 segundos
                BroadcastWaveArea(tgt.Pos.Map, tgt.Pos.X, tgt.Pos.Y, Sounds.INCINERADO); // sonido de incinerado (78)
                if (tgt.Conn != null) ServerPackets.ConsoleMsg(tgt.Conn, $"¡{u.Name} te ha incinerado!", 4);
                ServerPackets.ConsoleMsg(u.Conn, $"¡Has incinerado a {tgt.Name}!", 1);
            }
            if (sp.Invisibilidad)
            {
                // Invisibilidad mágica: difundir SetInvisible(true) al área del objetivo.
                for (int i = 1; i <= UserListManager.LastUser; i++)
                {
                    var o = UserListManager.UserList[i];
                    if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == tgt.Pos.Map)
                        ServerPackets.SetInvisible(o.Conn, tgt.Char.CharIndex, true);
                }
                // Invisibilidad mágica (NO la del skill Ocultarse): no se revela al atacar, sólo
                // termina cuando se acaba el tiempo (30s). Por eso usa flags.Invisible (no Oculto).
                tgt.flags.Invisible = 1;
                tgt.flags.InvisibleExpira = ahora + 30.0;
                if (tgt.Conn != null) ServerPackets.ConsoleMsg(tgt.Conn, "Te has vuelto invisible.", 1);
            }
            // VB6 InfoHechizo (modHechizos.bas:2149): el FX del hechizo se ve SIEMPRE sobre el
            // objetivo. Los bloques de cura/daño/stamina/atributos/sanación ya lo difunden; acá
            // se cubren los hechizos puramente de estado (Paralizar FX 8, Inmovilizar FX 12,
            // Ceguera, etc.) que antes no mostraban nada sobre el cuerpo del usuario.
            bool esEstado = sp.Paraliza || sp.Inmoviliza || sp.Ceguera || sp.Estupidez
                || sp.RemoverParalisis || sp.RemoverEstupidez || sp.Envenena > 0
                || sp.CuraVeneno || sp.Incinera || sp.Invisibilidad;
            if (esEstado && fx > 0 && sp.SubeHP == 0 && sp.SubeSta == 0
                && sp.SubeFuerza == 0 && sp.SubeAgilidad == 0 && !sp.Sanacion)
                BroadcastFX(u.Pos.Map, tgt.Char.CharIndex, fx, fxLoops);
            // --- Sanacion: cura total, quita incinerado y veneno (modHechizos.bas:1327) ---
            if (sp.Sanacion)
            {
                if (tgt.flags.Incinerado != 0) tgt.flags.Incinerado = 0;
                if (tgt.flags.Envenenado != 0) { tgt.flags.Envenenado = 0; tgt.flags.NivelVeneno = 0; }
                BroadcastFX(u.Pos.Map, tgt.Char.CharIndex, fx, fxLoops);
                if (tgt.Conn != null) ServerPackets.ConsoleMsg(tgt.Conn, "Te has sanado.", 1);
            }
            // --- Desencantar: remueve la metamorfosis del objetivo (modHechizos.bas:1576) ---
            if (sp.Desencantar)
            {
                if (tgt.flags.Metamorfoseado == 0)
                    ServerPackets.ConsoleMsg(u.Conn, "El objetivo no está bajo ningún efecto de metamorfosis.", 1);
                else
                {
                    RevertirMetamorfosis(targetUser);
                    if (tgt.Conn != null) ServerPackets.ConsoleMsg(tgt.Conn, "Tu metamorfosis ha sido desencantada.", 1);
                }
            }
            // --- Metamorfosis: transforma el body del lanzador (modHechizos.bas:1663). AutoLanzar=1,
            //     así el objetivo es siempre uno mismo (UserIndex == TargetIndex). ---
            if (sp.Metamorfosis && targetUser == userIndex)
            {
                if (tgt.flags.Metamorfoseado == 1)
                    ServerPackets.ConsoleMsg(u.Conn, "Ya estás transformado. Usa el hechizo Desencantar para sacarte el efecto.", 1);
                else
                {
                    // Guardar apariencia original (si no estaba guardada).
                    if (tgt.flags.OrigBody == 0) tgt.flags.OrigBody = tgt.Char.body;
                    if (tgt.flags.OrigHead == 0) tgt.flags.OrigHead = tgt.Char.Head;
                    tgt.flags.Metamorfoseado = 1;
                    tgt.flags.MetamorfosisBody = (short)sp.Body;
                    tgt.flags.MetamorfosisHead = 0;
                    tgt.Char.body = (short)sp.Body;
                    tgt.Char.Head = 0;
                    // Bonus de stats (se revierten al terminar).
                    if (sp.ExtraHIT > 0)
                    { tgt.Stats.ExtraHIT += (short)sp.ExtraHIT; tgt.flags.MetamorfosisExtraHIT = (short)sp.ExtraHIT; }
                    if (sp.ExtraDEF > 0)
                    { tgt.Stats.ExtraDEF += (short)sp.ExtraDEF; tgt.flags.MetamorfosisExtraDEF = (short)sp.ExtraDEF; }
                    tgt.flags.MetamorfosisExpira = ahora + 60.0; // 60s (Counters.Metamorfosis = 60)
                    BroadcastCharacterChange(tgt);
                    if (tgt.Conn != null) ServerPackets.ConsoleMsg(tgt.Conn, "Te has transformado.", 1);
                }
            }
        }
    }

    /// <summary>
    /// Difunde la apariencia actual del usuario (body/head/equipo) a todos los clientes de su
    /// mapa mediante CharacterChange — equiv. SendData(ToPCArea, ..., PrepareMessageCharacterChange).
    /// </summary>
    /// <summary>Difunde la apariencia/rumbo actual del usuario al área (p.ej. al girar en el lugar).</summary>
    public static void DifundirApariencia(User t) => BroadcastCharacterChange(t);

    /// <summary>Duración de la parálisis sobre usuarios, en segundos reales.</summary>
    public const double DuracionParalisisUsuario = 60.0;

    /// <summary>Difunde la barra de progreso de parálisis del usuario a todos los del mapa
    /// (se dibuja bajo el personaje, igual que la de los NPCs).</summary>
    public static void DifundirParalisisUsuario(User u, double segundos)
    {
        byte segs = (byte)Math.Min(255, (int)Math.Ceiling(segundos));
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.NpcParalysisProgress(o.Conn, u.Char.CharIndex, segs);
        }
    }

    private static void BroadcastCharacterChange(User t)
    {
        // Metamorfoseado: se oculta TODO el equipo (arma, escudo, casco). Sólo se ven el body
        // transformado, las auras y el nick. Al revertir, Metamorfoseado=0 y se ve el equipo real.
        bool meta = t.flags.Metamorfoseado == 1;
        short weapon = meta ? (short)0 : t.Char.WeaponAnim;
        short shield = meta ? (short)0 : t.Char.ShieldAnim;
        short casco  = meta ? (short)0 : t.Char.CascoAnim;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == t.Pos.Map)
                ServerPackets.CharacterChange(o.Conn, t.Char.CharIndex, t.Char.body, t.Char.Head,
                    t.Char.heading, weapon, shield, casco, t.Char.FX, t.Char.Loops, 0);
        }
    }

    /// <summary>
    /// RevertirMetamorfosis (modHechizos.bas:2529): restaura body/head originales, quita el bonus de
    /// ExtraHIT/ExtraDEF, limpia los flags y refresca la apariencia. Llamado por Desencantar y al expirar.
    /// </summary>
    public static void RevertirMetamorfosis(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.flags.Metamorfoseado == 0) return;

        if (u.flags.OrigBody > 0) { u.Char.body = u.flags.OrigBody; u.flags.OrigBody = 0; }
        if (u.flags.OrigHead > 0) { u.Char.Head = u.flags.OrigHead; u.flags.OrigHead = 0; }

        if (u.flags.MetamorfosisExtraHIT > 0)
        {
            u.Stats.ExtraHIT -= u.flags.MetamorfosisExtraHIT;
            if (u.Stats.ExtraHIT < 0) u.Stats.ExtraHIT = 0;
            u.flags.MetamorfosisExtraHIT = 0;
        }
        if (u.flags.MetamorfosisExtraDEF > 0)
        {
            u.Stats.ExtraDEF -= u.flags.MetamorfosisExtraDEF;
            if (u.Stats.ExtraDEF < 0) u.Stats.ExtraDEF = 0;
            u.flags.MetamorfosisExtraDEF = 0;
        }

        u.flags.Metamorfoseado = 0;
        u.flags.MetamorfosisBody = 0;
        u.flags.MetamorfosisHead = 0;
        u.flags.MetamorfosisExpira = 0;

        BroadcastCharacterChange(u);
        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "La metamorfosis ha terminado.", 1);
    }

    /// <summary>
    /// PuedeAtacar (SistemaCombate.bas:2548): ¿el atacante puede atacar a la víctima? Valida, EN ORDEN,
    /// atacante muerto, zona segura (mapa no-PK), pareja de casamiento, víctima muerta, mismo clan,
    /// misma party, facciones aliadas (no se atacan) y protección de GM (Dios/Soporte). 1:1 con VB6.
    /// </summary>
    /// <summary>Mapas con "todos contra todos" activado por un GM (/todosvstodos): en esos mapas
    /// PuedeAtacar saltea TODAS las protecciones (zona segura, pareja, clan, party, facciones
    /// aliadas y protección de dioses/GMs). Solo exige que ambos estén vivos.</summary>
    public static readonly HashSet<int> MapasTodosVsTodos = new();

    public static bool PuedeAtacar(int attackerIndex, int victimIndex, bool notificar = true)
    {
        var atk = UserListManager.UserList[attackerIndex];
        var vic = UserListManager.UserList[victimIndex];
        if (atk == null || vic == null) return false;
        void Msg(string s) { if (notificar) ServerPackets.ConsoleMsg(atk.Conn, s, 1); }

        if (atk.flags.Muerto == 1)
        { Msg("Estás muerto, no puedes atacar."); return false; }

        // Modo "todos contra todos" del mapa (/todosvstodos): cualquier usuario vivo puede atacar
        // a cualquier otro, incluidos dioses y GMs. Ignora el resto de las restricciones.
        if (MapasTodosVsTodos.Contains(atk.Pos.Map))
        {
            if (vic.flags.Muerto == 1)
            { Msg("No puedes atacar a alguien que está muerto."); return false; }
            return true;
        }

        // Zona segura: en mapas no-PK no se puede pelear.
        var mi = MapLoader.Get(atk.Pos.Map)?.Info;
        if (mi != null && !mi.Pk)
        { Msg("No puedes pelear en una zona segura."); return false; }

        // No puedes atacar a tu pareja de casamiento.
        if (!string.IsNullOrEmpty(atk.CasamientoPareja) &&
            string.Equals(vic.Name, atk.CasamientoPareja, StringComparison.OrdinalIgnoreCase))
        { Msg("No puedes atacar a tu pareja."); return false; }

        if (vic.flags.Muerto == 1)
        { Msg("No puedes atacar a alguien que está muerto."); return false; }

        // Torneo: si son RIVALES de un combate activo, siempre pueden pelear (ignora facción/clan/
        // grupo). No depende del trigger ZONAPELEA del mapa, así funciona aunque la arena no esté
        // marcada. Los compañeros de equipo no son rivales → caen al chequeo de party más abajo.
        if (TorneoEvento.SonRivalesEnCombate(attackerIndex, victimIndex)) return true;

        // Arena de pelea (incluye torneos): se evalúa ANTES de clan/party/facción para que en las
        // arenas se pueda combatir aunque sean del mismo bando o grupo. Si uno está dentro de la
        // arena y el otro fuera → prohíbe. Si ambos están en la misma arena → permite, salvo que
        // sean COMPAÑEROS de equipo de torneo (2v2/3v3); en 1v1 dos del mismo grupo SÍ se enfrentan.
        switch (TriggerZonaPelea(attackerIndex, victimIndex))
        {
            case eTrigger6.TRIGGER6_PROHIBE: return false;
            case eTrigger6.TRIGGER6_PERMITE:
                if (TorneoEvento.SonCompanerosDeEquipo(attackerIndex, victimIndex))
                { Msg("No puedes atacar a un compañero de equipo."); return false; }
                // Mantener la protección de administradores aun dentro de la arena.
                if (vic.FaccionStatus >= AdminLoader.STATUS_CONSEJERO &&
                    atk.FaccionStatus < AdminLoader.STATUS_CONSEJERO)
                { Msg("No puedes atacar a un administrador."); return false; }
                return true;
            // TRIGGER6_AUSENTE: sigue el flujo normal (clan/party/facción).
        }

        // Mismo clan.
        if (vic.GuildIndex > 0 && vic.GuildIndex == atk.GuildIndex) return false;

        // Misma party.
        if (atk.PartyId > 0 && vic.PartyId > 0 && atk.PartyId == vic.PartyId)
        { Msg("No puedes atacar a un miembro de tu grupo."); return false; }

        // Facciones aliadas: no se atacan entre sí (Imperiales: ciuda+armada; Legión: repu+milicia).
        bool aliados =
            (Facciones.EsRepu(vic) && Facciones.EsRepu(atk)) ||
            (Facciones.EsMili(vic) && Facciones.EsMili(atk)) ||
            (Facciones.EsRepu(vic) && Facciones.EsMili(atk)) ||
            (Facciones.EsMili(vic) && Facciones.EsRepu(atk)) ||
            (Facciones.EsCiuda(vic) && Facciones.EsCiuda(atk)) ||
            (Facciones.EsCiuda(vic) && Facciones.EsArmada(atk)) ||
            (Facciones.EsArmada(vic) && Facciones.EsCiuda(atk)) ||
            (Facciones.EsArmada(vic) && Facciones.EsArmada(atk));
        if (aliados)
        { Msg("No puedes atacar a un miembro de tu facción ni a un aliado."); return false; }

        // Protección de administradores (Dios/Soporte): nadie los ataca salvo otro GM.
        bool victGm = vic.FaccionStatus >= AdminLoader.STATUS_CONSEJERO;
        bool atkGm = atk.FaccionStatus >= AdminLoader.STATUS_CONSEJERO;
        if (victGm && !atkGm)
        { Msg("No puedes atacar a un administrador."); return false; }

        return true;
    }

    /// <summary>
    /// TriggerZonaPelea (SistemaCombate.bas:3364): compara el trigger del tile del origen y del
    /// destino. Si alguno es ZONAPELEA: PERMITE si son iguales, PROHIBE si distintos. Si ninguno es
    /// ZONAPELEA: AUSENTE. 1:1 con VB6.
    /// </summary>
    public static eTrigger6 TriggerZonaPelea(int origen, int destino)
    {
        var o = UserListManager.UserList[origen];
        var d = UserListManager.UserList[destino];
        byte tOrg = MapLoader.Get(o.Pos.Map)?.GetTrigger(o.Pos.X, o.Pos.Y) ?? 0;
        byte tDst = MapLoader.Get(d.Pos.Map)?.GetTrigger(d.Pos.X, d.Pos.Y) ?? 0;
        if (tOrg == eTrigger.ZONAPELEA || tDst == eTrigger.ZONAPELEA)
            return tOrg == tDst ? eTrigger6.TRIGGER6_PERMITE : eTrigger6.TRIGGER6_PROHIBE;
        return eTrigger6.TRIGGER6_AUSENTE;
    }

    /// <summary>
    /// PuedeAyudar (modHechizos.bas:2350): ¿el usuario puede lanzar un hechizo de soporte sobre tU?
    /// Permite a uno mismo, en Rinkel (mapa 20), a la misma party y a miembros de la MISMA alianza de
    /// facción (Imperiales: ciuda+armada; Legión: repu+milicia; Caos solo Caos; Renegado solo Renegado).
    /// Sin facción definida no puede ayudar a otros. 1:1 con VB6.
    /// </summary>
    public static bool PuedeAyudar(int userIndex, int tU, bool notificar = true)
    {
        if (userIndex == tU) return true;
        var u = UserListManager.UserList[userIndex];
        var t = UserListManager.UserList[tU];
        if (u == null || t == null) return false;
        void Msg(string s) { if (notificar) ServerPackets.ConsoleMsg(u.Conn, s, 1); }

        if (u.Pos.Map == 20) return true;  // Rinkel: todos pueden ayudarse
        if (u.PartyId > 0 && t.PartyId > 0 && u.PartyId == t.PartyId) return true;

        if (Facciones.EsArmada(u) || Facciones.EsCiuda(u))
        {
            if (Facciones.EsArmada(t) || Facciones.EsCiuda(t)) return true;
            Msg("No puedes ayudar a miembros de otras facciones. Solo a ciudadanos imperiales y armada.");
            return false;
        }
        if (Facciones.EsRepu(u) || Facciones.EsMili(u))
        {
            if (Facciones.EsRepu(t) || Facciones.EsMili(t)) return true;
            Msg("No puedes ayudar a miembros de otras facciones. Solo a republicanos y milicia.");
            return false;
        }
        if (Facciones.EsCaos(u))
        {
            if (Facciones.EsCaos(t)) return true;
            Msg("No puedes ayudar a miembros de otras facciones. Solo a legionarios del caos.");
            return false;
        }
        if (Facciones.EsRene(u))
        {
            if (Facciones.EsRene(t)) return true;
            Msg("No puedes ayudar a miembros de otras facciones. Solo a otros renegados.");
            return false;
        }
        return false; // sin facción definida: no puede ayudar a otros
    }

    /// <summary>
    /// Aplica un hechizo de área (HechizoTerrenoEstado, modHechizos.bas:464). El radio es FIJO
    /// ±1 (3x3) centrado en el tile clickeado (cx,cy), igual que VB6 — NO usa AreaEfecto como
    /// radio (ese campo solo indica que el hechizo ES de área).
    /// </summary>
    private static void AplicarHechizoArea(User u, SpellData.Spell sp, byte cx, byte cy, short fx, short fxLoops, bool ofensivo)
    {
        const int radio = 1; // 3x3 fijo (VB6: PosCasteadaX-1..+1)
        int map = u.Pos.Map;

        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (!t.flags.UserLogged || t.Pos.Map != map || t.flags.Muerto == 1) continue;
            if (Math.Abs(t.Pos.X - cx) > radio || Math.Abs(t.Pos.Y - cy) > radio) continue;
            if (ofensivo && i == u.id) continue;       // el daño no se aplica al lanzador
            if (i != u.id)                             // a otros usuarios, validar seguridad (sin spamear)
            {
                if (ofensivo) { if (!PuedeAtacar(u.id, i, false)) continue; }
                else if (!PuedeAyudar(u.id, i, false)) continue;
            }
            AplicarEfectoUsuario(u, sp, i, fx, fxLoops);
        }

        if (ofensivo || sp.SubeHP == 2)
        {
            foreach (var n in NpcManager.GetMapNpcs(map).ToArray())
            {
                if (n.Dead || n.MaestroUser > 0) continue;
                if (Math.Abs(n.X - cx) > radio || Math.Abs(n.Y - cy) > radio) continue;
                // Mismas reglas que el objetivo único: Attackable=0 y guardias aliados/Rinkel
                // no reciben el daño de área (sin spamear mensajes por cada NPC excluido).
                if (!NpcManager.UsuarioPuedeAtacarNpc(u, n, out _)) continue;
                AplicarEfectoNpc(u, sp, n, fx, fxLoops);
            }
        }
        ServerPackets.ConsoleMsg(u.Conn, "Lanzas un hechizo de área.", 1);
    }

    /// <summary>Aplica los efectos de un hechizo a un usuario (usado por el área y los buffs).</summary>
    private static void AplicarEfectoUsuario(User caster, SpellData.Spell sp, int targetIdx, short fx, short fxLoops)
    {
        var t = UserListManager.UserList[targetIdx];
        double ahora = Environment.TickCount64 / 1000.0;
        BroadcastFX(t.Pos.Map, t.Char.CharIndex, fx, fxLoops);

        if (sp.SubeHP == 1)
        {
            int m = sp.MaxHP >= sp.MinHP && sp.MaxHP > 0 ? _rng.Next(sp.MinHP, sp.MaxHP + 1) : sp.MinHP;
            t.Stats.MinHP = (short)Math.Min(t.Stats.MaxHP, t.Stats.MinHP + m);
            if (t.Conn != null) ServerPackets.UpdateHP(t.Conn, t.Stats.MinHP);
        }
        else if (sp.SubeHP == 2)
        {
            int m = sp.MaxHP >= sp.MinHP && sp.MaxHP > 0 ? _rng.Next(sp.MinHP, sp.MaxHP + 1) : sp.MinHP;
            t.Stats.MinHP -= (short)m;
            if (t.Conn != null) ServerPackets.ConsoleMsg(t.Conn, $"{caster.Name} te quitó {m} puntos de vida.", 1);
            if (t.Stats.MinHP <= 0) { t.Stats.MinHP = 0; if (t.Conn != null) ServerPackets.UpdateHP(t.Conn, 0); Facciones.ContarMuerte(targetIdx, caster.id); UserDie(targetIdx); return; }
            if (t.Conn != null) ServerPackets.UpdateHP(t.Conn, t.Stats.MinHP);
        }
        if (sp.SubeSta > 0)
        {
            int m = sp.MaxSta >= sp.MinSta && sp.MaxSta > 0 ? _rng.Next(sp.MinSta, sp.MaxSta + 1) : sp.MinSta;
            t.Stats.MinSta = sp.SubeSta == 1 ? (short)Math.Min(t.Stats.MaxSta, t.Stats.MinSta + m) : (short)Math.Max(0, t.Stats.MinSta - m);
            if (t.Conn != null) ServerPackets.UpdateSta(t.Conn, t.Stats.MinSta);
        }
        if (sp.SubeFuerza == 1)
        {
            int m = sp.MaxFuerza >= sp.MinFuerza && sp.MaxFuerza > 0 ? _rng.Next(sp.MinFuerza, sp.MaxFuerza + 1) : sp.MinFuerza;
            t.Stats.UserAtributos[1] = (byte)Math.Min(MAXATRIB, t.Stats.UserAtributos[1] + m);
            t.flags.AtributoEfectoExpira = ahora + 120.0; t.flags.TomoPocion = true;
            if (t.Conn != null) ServerPackets.UpdateStrenght(t.Conn, t.Stats.UserAtributos[1]);
        }
        if (sp.SubeAgilidad == 1)
        {
            int m = sp.MaxAgilidad >= sp.MinAgilidad && sp.MaxAgilidad > 0 ? _rng.Next(sp.MinAgilidad, sp.MaxAgilidad + 1) : sp.MinAgilidad;
            t.Stats.UserAtributos[2] = (byte)Math.Min(MAXATRIB, t.Stats.UserAtributos[2] + m);
            t.flags.AtributoEfectoExpira = ahora + 120.0; t.flags.TomoPocion = true;
            if (t.Conn != null) ServerPackets.UpdateDexterity(t.Conn, t.Stats.UserAtributos[2]);
        }
        // No re-aplicar si ya está paralizado/inmovilizado (ParalizeOK es toggle en cliente).
        if ((sp.Paraliza || sp.Inmoviliza) && t.flags.Paralizado == 0 && t.flags.Inmovilizado == 0)
        {
            if (sp.Paraliza) { t.flags.Paralizado = 1; t.flags.ParalisisExpira = ahora + DuracionParalisisUsuario; if (t.Conn != null) ServerPackets.ParalizeOK(t.Conn); DifundirParalisisUsuario(t, DuracionParalisisUsuario); }
            if (sp.Inmoviliza) { t.flags.Inmovilizado = 1; t.flags.ParalisisExpira = ahora + DuracionParalisisUsuario; if (t.Conn != null) ServerPackets.ParalizeOK(t.Conn); DifundirParalisisUsuario(t, DuracionParalisisUsuario); }
        }
        if (sp.Ceguera) { t.flags.Ciego = 1; t.flags.CegueraExpira = ahora + 6.0; if (t.Conn != null) ServerPackets.Blind(t.Conn); }
        if (sp.Envenena > 0) { t.flags.Envenenado = 1; t.flags.NivelVeneno = sp.Envenena; }
        if (sp.CuraVeneno && t.flags.Envenenado == 1) { t.flags.Envenenado = 0; t.flags.NivelVeneno = 0; }
        if (sp.RemoverParalisis && (t.flags.Paralizado == 1 || t.flags.Inmovilizado == 1))
        { t.flags.Paralizado = 0; t.flags.Inmovilizado = 0; t.flags.ParalisisExpira = 0; DifundirParalisisUsuario(t, 0); if (t.Conn != null) ServerPackets.ParalizeOK(t.Conn); }
    }

    /// <summary>Aplica los efectos de un hechizo a un NPC (daño/paralización del área).</summary>
    private static void AplicarEfectoNpc(User caster, SpellData.Spell sp, NpcManager.NpcInstance n, short fx, short fxLoops)
    {
        BroadcastFX(caster.Pos.Map, n.CharIndex, fx, fxLoops);
        if (sp.Paraliza || sp.Inmoviliza) NpcManager.ParalizarNpc(n, 8.0);
        if (sp.SubeHP == 2)
        {
            int m = sp.MaxHP >= sp.MinHP && sp.MaxHP > 0 ? _rng.Next(sp.MinHP, sp.MaxHP + 1) : sp.MinHP;
            m += Porcentaje(m, 3 * caster.Stats.ELV); // escala con nivel (igual que single-target)
            NpcManager.ProvocarNpc(n, caster);         // aggro por hechizo de área
            CalcularDarExp(caster.id, n, m);           // exp proporcional al daño de área
            n.MinHP -= m;
            if (n.MinHP <= 0) MatarNpc(caster, n);
        }
    }

    /// <summary>
    /// Mensajes del hechizo (InfoHechizo): HechizeroMsg lo ve el lanzador; TargetMsg el objetivo;
    /// PropioMsg cuando el hechizo es sobre uno mismo. Concatena el nombre cuando corresponde.
    /// </summary>
    private static void EnviarMensajesHechizo(User u, SpellData.Spell sp, int targetUser)
    {
        // Todos los mensajes de hechizos van a la pestaña Combate en rojo (font 2).
        if (targetUser <= 0 || targetUser == u.id)
        {
            if (!string.IsNullOrEmpty(sp.PropioMsg)) ServerPackets.ConsoleMsg(u.Conn, sp.PropioMsg, 2);
            else if (!string.IsNullOrEmpty(sp.HechizeroMsg)) ServerPackets.ConsoleMsg(u.Conn, sp.HechizeroMsg, 2);
            return;
        }
        var t = UserListManager.UserList[targetUser];
        if (!string.IsNullOrEmpty(sp.HechizeroMsg))
            ServerPackets.ConsoleMsg(u.Conn, $"{sp.HechizeroMsg} {t.Name}.", 2);
        if (!string.IsNullOrEmpty(sp.TargetMsg) && t.Conn != null)
            ServerPackets.ConsoleMsg(t.Conn, $"{u.Name} {sp.TargetMsg}.", 2);
    }

    /// <summary>Invoca una criatura como mascota del lanzador (hechizo Invoca).</summary>
    private static NpcManager.NpcInstance InvocarCriatura(User u, int npcIndex, byte x, byte y)
    {
        var n = NpcManager.SpawnAt(u.Pos.Map, npcIndex, x, y);
        if (n == null) return null;
        n.MaestroUser = u.id;
        n.Movement = 8; // SigueAmo
        for (int i = 1; i <= Constants.MAXMASCOTAS; i++)
            if (u.MascotasCharIndex[i] == 0) { u.MascotasCharIndex[i] = n.CharIndex; break; }
        u.NroMascotas++;
        return n;
    }

    /// <summary>userIndex en (x,y) incluyendo muertos (para Revivir). 0 si no hay.</summary>
    private static int UserAtIncluyeMuerto(int map, int x, int y)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Pos.Map == map && o.Pos.X == x && o.Pos.Y == y) return i;
        }
        return 0;
    }

    /// <summary>Revela a los ocultos/invisibles en el área del lanzador (hechizo RemueveInvis).</summary>
    private static void RevelarArea(User u)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (!o.flags.UserLogged || o.Conn == null || o.Pos.Map != u.Pos.Map) continue;
            if (Math.Abs(o.Pos.X - u.Pos.X) > 8 || Math.Abs(o.Pos.Y - u.Pos.Y) > 8) continue;
            if (o.flags.Oculto != 1) continue;
            o.flags.Oculto = 0;
            for (int j = 1; j <= UserListManager.LastUser; j++)
            {
                var p = UserListManager.UserList[j];
                if (p?.flags.UserLogged == true && p.Conn != null && p.Pos.Map == o.Pos.Map)
                    ServerPackets.SetInvisible(p.Conn, o.Char.CharIndex, false);
            }
            ServerPackets.ConsoleMsg(o.Conn, "Has sido revelado.", 1);
        }
        ServerPackets.ConsoleMsg(u.Conn, "Revelas a quienes se ocultan a tu alrededor.", 1);
    }

    /// <summary>
    /// Expira los efectos de estado vencidos de todos los usuarios. Lo llama un timer.
    /// </summary>
    public static void TickEstados()
    {
        ProcesarRemocionParticulas(); // borra partículas de char vencidas (hechizos de guerrero, etc.)
        double ahora = Environment.TickCount64 / 1000.0;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (!u.flags.UserLogged || u.Conn == null) continue;

            if (u.flags.ParalisisExpira > 0 && ahora >= u.flags.ParalisisExpira)
            {
                u.flags.ParalisisExpira = 0;
                if (u.flags.Paralizado == 1 || u.flags.Inmovilizado == 1)
                {
                    u.flags.Paralizado = 0; u.flags.Inmovilizado = 0;
                    DifundirParalisisUsuario(u, 0);
                    ServerPackets.ParalizeOK(u.Conn);
                    ServerPackets.ConsoleMsg(u.Conn, "El efecto de parálisis se ha desvanecido.", 1);
                }
            }
            if (u.flags.CegueraExpira > 0 && ahora >= u.flags.CegueraExpira)
            {
                u.flags.CegueraExpira = 0;
                if (u.flags.Ciego == 1)
                {
                    u.flags.Ciego = 0;
                    ServerPackets.BlindNoMore(u.Conn);
                }
            }
            if (u.flags.EstupidezExpira > 0 && ahora >= u.flags.EstupidezExpira)
            {
                u.flags.EstupidezExpira = 0;
                if (u.flags.Estupido == 1)
                {
                    u.flags.Estupido = 0;
                    ServerPackets.DumbNoMore(u.Conn);
                }
            }
            // Incineración: dura unos segundos (8s) y se apaga sola.
            if (u.flags.IncineradoExpira > 0 && ahora >= u.flags.IncineradoExpira)
            {
                u.flags.IncineradoExpira = 0;
                if (u.flags.Incinerado == 1)
                {
                    u.flags.Incinerado = 0;
                    ServerPackets.ConsoleMsg(u.Conn, "El fuego se ha extinguido.", 1);
                }
            }
            // Veneno crítico: dura unos segundos (5s) y luego se neutraliza solo.
            if (u.flags.VenenoExpira > 0 && ahora >= u.flags.VenenoExpira)
            {
                u.flags.VenenoExpira = 0;
                if (u.flags.Envenenado == 1)
                {
                    u.flags.Envenenado = 0; u.flags.NivelVeneno = 0;
                    ServerPackets.ConsoleMsg(u.Conn, "El veneno crítico se ha neutralizado.", 1);
                }
            }
            // Maldición: impide atacar por unos segundos (5s).
            if (u.flags.MaldecidoExpira > 0 && ahora >= u.flags.MaldecidoExpira)
            {
                u.flags.MaldecidoExpira = 0;
                if (u.flags.Maldecido == 1)
                {
                    u.flags.Maldecido = 0;
                    ServerPackets.ConsoleMsg(u.Conn, "La maldición se ha desvanecido.", 1);
                }
            }

            // Invisibilidad mágica: al expirar se vuelve visible y se muestra el FX de invisibilidad (46).
            if (u.flags.InvisibleExpira > 0 && ahora >= u.flags.InvisibleExpira)
            {
                u.flags.InvisibleExpira = 0;
                if (u.flags.Invisible == 1)
                {
                    u.flags.Invisible = 0;
                    for (int p = 1; p <= UserListManager.LastUser; p++)
                    {
                        var o = UserListManager.UserList[p];
                        if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                            ServerPackets.SetInvisible(o.Conn, u.Char.CharIndex, false);
                    }
                    BroadcastFX(u.Pos.Map, u.Char.CharIndex, 46, 0); // FX de invisibilidad al reaparecer
                    ServerPackets.ConsoleMsg(u.Conn, "Vuelves a ser visible.", 1);
                }
            }

            // Arma mágica: a los 2 minutos se desvanece.
            if (u.flags.ArmaMagicaExpira > 0 && ahora >= u.flags.ArmaMagicaExpira)
            {
                u.flags.ArmaMagicaExpira = 0;
                ServerPackets.ConsoleMsg(u.Conn, "Tu arma mágica se ha desvanecido.", 1);
            }

            // Metamorfosis: a los 60s vuelve a su apariencia original (Counters.Metamorfosis).
            if (u.flags.MetamorfosisExpira > 0 && ahora >= u.flags.MetamorfosisExpira)
                RevertirMetamorfosis(i);

            // Buff/debuff de atributos: al expirar, restaurar atributos base (General.bas:1475).
            if (u.flags.TomoPocion && u.flags.AtributoEfectoExpira > 0 && ahora >= u.flags.AtributoEfectoExpira)
                RestaurarAtributos(u);

            // Furor Ígneo: a los 5s termina el aumento de velocidad de ataque.
            if (u.flags.FurorIgneo && ahora >= u.flags.FurorIgneoExpira)
            {
                u.flags.FurorIgneo = false;
                ServerPackets.ConsoleMsg(u.Conn, "El Furor Ígneo se ha desvanecido.", 1);
            }
        }
    }

    /// <summary>Restaura UserAtributos al BackUP base y limpia el efecto temporal (DuracionEfecto a 0).</summary>
    public static void RestaurarAtributos(User u)
    {
        u.flags.TomoPocion = false;
        u.flags.AtributoEfectoExpira = 0;
        for (int a = 1; a <= Constants.NUMATRIBUTOS; a++)
            u.Stats.UserAtributos[a] = u.Stats.UserAtributosBackUP[a];
        if (u.Conn != null)
        {
            ServerPackets.UpdateStrenght(u.Conn, u.Stats.UserAtributos[1]);
            ServerPackets.UpdateDexterity(u.Conn, u.Stats.UserAtributos[2]);
        }
    }

    /// <summary>
    /// Hechizos especiales de guerrero 116-120 (HandleHechizoUsuario, modHechizos.bas:907). Cada uno
    /// tiene costo (Sta/HP) y efecto propio: 116 Temple (cooldown 2min), 117 Leviathan (mensaje),
    /// 118 Plegaria (cura+gasta toda la Sta), 119 Sacrificio Impío (próximo golpe certero),
    /// 120 Furor Ígneo (velocidad de ataque 5s, cooldown 20s). Manda los timers al cliente.
    /// </summary>
    private static void LanzarHechizoGuerrero(int userIndex, User u, int hechizoIndex, SpellData.Spell sp, bool esGm)
    {
        double ahora = Environment.TickCount64 / 1000.0;

        // Validación de costos/cooldowns especiales (los GM no pagan ni esperan).
        switch (hechizoIndex)
        {
            case 116:
                if (!esGm && u.flags.TempleCooldownExpira > ahora)
                { ServerPackets.ConsoleMsg(u.Conn, $"Debes esperar {(int)(u.flags.TempleCooldownExpira - ahora)} segundos para volver a usar Temple.", 1); return; }
                if (!esGm && (u.Stats.MinSta < 670 || u.Stats.MinHP <= 200))
                { ServerPackets.ConsoleMsg(u.Conn, "No tienes suficiente energía o vida para usar Temple.", 1); return; }
                break;
            case 118:
                if (!esGm && u.Stats.MinSta < 10)
                { ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado cansado para rezar una plegaria.", 1); return; }
                break;
            case 119:
                if (!esGm && u.Stats.MinSta < 300)
                { ServerPackets.ConsoleMsg(u.Conn, "No tienes suficiente energía para usar Sacrificio Impío.", 1); return; }
                break;
            case 120:
                if (!esGm && u.flags.FurorIgneoCooldownExpira > ahora)
                { ServerPackets.ConsoleMsg(u.Conn, $"Debes esperar {(int)(u.flags.FurorIgneoCooldownExpira - ahora)} segundos para volver a usar Furor Ígneo.", 1); return; }
                if (!esGm && (u.Stats.MinSta < 250 || u.Stats.MinHP <= 100))
                { ServerPackets.ConsoleMsg(u.Conn, "No tienes suficiente energía o vida para usar Furor Ígneo.", 1); return; }
                break;
        }

        // Maná + palabras mágicas + skill (común a todos).
        if (!esGm)
        {
            u.Stats.MinMAN = (short)Math.Max(0, u.Stats.MinMAN - sp.ManaRequerido);
            ServerPackets.UpdateMana(u.Conn, u.Stats.MinMAN);
        }
        Skills.SubirSkill(userIndex, 8); // VB6 SubirSkill incondicional (también GMs)
        DecirPalabrasMagicas(userIndex, sp.PalabrasMagicas); // modo 3 = palabras mágicas (burbuja, no consola ajena)
        short fx = (short)sp.FXgrh, fxLoops = (short)Math.Max(0, sp.Loops);
        if (sp.WAV > 0) BroadcastWaveArea(u.Pos.Map, u.Pos.X, u.Pos.Y, (short)sp.WAV); // sonido del hechizo
        BroadcastFX(u.Pos.Map, u.Char.CharIndex, fx, fxLoops);
        // Partícula del hechizo de guerrero (antes no se mostraba: la función retorna antes del
        // bloque de partículas general de LanzarHechizoEn). Se programa su borrado al terminar la
        // duración (el cliente mide la vida en frames y algunas quedan pegadas).
        if (sp.Particle > 0)
        {
            int vida = sp.TimeParticula > 0 ? sp.TimeParticula : 100; // vida en frames
            BroadcastParticulaChar(u.Pos.Map, u.Char.CharIndex, (short)sp.Particle, vida);
            // vida en frames → segundos (~60 fps) + margen; mínimo 1s para que se alcance a ver.
            ProgramarRemoverParticula(u.Pos.Map, u.Char.CharIndex, (short)sp.Particle, Math.Max(1.0, vida / 60.0 + 0.5));
        }

        switch (hechizoIndex)
        {
            case 116: // Temple
                if (!esGm) { u.Stats.MinSta = (short)Math.Max(0, u.Stats.MinSta - 670); u.Stats.MinHP = (short)Math.Max(0, u.Stats.MinHP - 200); }
                u.flags.TempleCooldownExpira = ahora + 120;
                ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP);
                ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);
                ServerPackets.TempleCooldown(u.Conn, 120);
                ServerPackets.ConsoleMsg(u.Conn, "Has utilizado Temple. Debes esperar 2 minutos para volver a usarlo.", 1);
                break;
            case 117: // Espíritu de Leviathan
                ServerPackets.ConsoleMsg(u.Conn, "¡El espíritu de Leviathan emerge de las profundidades para protegerte!", 1);
                break;
            case 118: // Plegaria: cura escalada (3*ELV) y consume toda la Sta
                int sana = (sp.MaxHP >= sp.MinHP && sp.MaxHP > 0 ? _rng.Next(sp.MinHP, sp.MaxHP + 1) : sp.MinHP);
                sana += Porcentaje(sana, 3 * u.Stats.ELV);
                u.Stats.MinHP = (short)Math.Min(u.Stats.MaxHP, u.Stats.MinHP + sana);
                if (!esGm) u.Stats.MinSta = 0;
                ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP);
                ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);
                ServerPackets.ConsoleMsg(u.Conn, $"Los dioses te han sanado {sana} puntos de vida por tu plegaria.", 1);
                break;
            case 119: // Sacrificio Impío: próximo golpe certero
                if (!esGm) u.Stats.MinSta = (short)Math.Max(0, u.Stats.MinSta - 300);
                u.flags.SacrificioImpio = true;
                ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);
                ServerPackets.ConsoleMsg(u.Conn, "¡Has realizado un Sacrificio Impío! Tu próximo golpe será certero.", 1);
                break;
            case 120: // Furor Ígneo: velocidad de ataque 5s, cooldown 20s
                if (!esGm) { u.Stats.MinSta = (short)Math.Max(0, u.Stats.MinSta - 250); u.Stats.MinHP = (short)Math.Max(0, u.Stats.MinHP - 100); }
                u.flags.FurorIgneo = true;
                u.flags.FurorIgneoExpira = ahora + 5;
                u.flags.FurorIgneoCooldownExpira = ahora + 20;
                ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP);
                ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);
                ServerPackets.FurorIgneoTimers(u.Conn, 5, 20);
                ServerPackets.ConsoleMsg(u.Conn, "¡Sientes el Furor Ígneo! Tu velocidad de ataque aumenta.", 1);
                break;
        }
    }

    /// <summary>
    /// Poción subtipo 5 (LanzaHechizo): aplica el hechizo de estado sobre UNO MISMO, sin maná,
    /// skill ni palabras mágicas — VB6 InvUsuario.bas:1798 → HechizoEstadoUsuario con TargetUser
    /// = el propio usuario (modHechizos.bas:1235). Devuelve true si el efecto se aplicó
    /// (HechizoCasteado) → la poción se consume; false → no se consume.
    /// Cubre los efectos de las pociones reales del obj.dat: Curar Veneno (1), Remover
    /// parálisis (10/32), Invisibilidad (14) y Desencantar (22).
    /// </summary>
    public static bool AplicarHechizoPocion(int userIndex, int hechizoIndex)
    {
        var u = UserListManager.UserList[userIndex];
        var sp = SpellData.Get(hechizoIndex);
        if (string.IsNullOrEmpty(sp.Nombre)) return false;

        double ahora = Environment.TickCount64 / 1000.0;
        bool casteado = false;

        // <-------- Sanación: quita incinerado y veneno (modHechizos.bas:1328) ---------->
        if (sp.Sanacion)
        {
            if (u.flags.Incinerado != 0) u.flags.Incinerado = 0;
            if (u.flags.Envenenado != 0) { u.flags.Envenenado = 0; u.flags.NivelVeneno = 0; }
            ServerPackets.ConsoleMsg(u.Conn, "Te has sanado.", 1);
            casteado = true;
        }

        // <-------- Invisibilidad (modHechizos.bas:1354) ---------->
        if (sp.Invisibilidad)
        {
            if (u.flags.Invisible == 1)
            { ServerPackets.ConsoleMsg(u.Conn, "Ya estás invisible.", 1); return false; }
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var o = UserListManager.UserList[i];
                if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                    ServerPackets.SetInvisible(o.Conn, u.Char.CharIndex, true);
            }
            u.flags.Invisible = 1;
            u.flags.InvisibleExpira = ahora + 30.0; // misma duración que el hechizo (30s)
            ServerPackets.ConsoleMsg(u.Conn, "Te has vuelto invisible.", 1);
            casteado = true;
        }

        // <-------- Cura veneno (modHechizos.bas:1457): exige estar envenenado o incinerado ---------->
        if (sp.CuraVeneno)
        {
            if (u.flags.Envenenado == 0 && u.flags.Incinerado == 0)
            { ServerPackets.ConsoleMsg(u.Conn, "No estás envenenado.", 1); return false; }
            u.flags.Envenenado = 0; u.flags.NivelVeneno = 0;
            u.flags.Incinerado = 0; u.flags.IncineradoExpira = 0;
            ServerPackets.ConsoleMsg(u.Conn, "Te has curado del envenenamiento.", 1);
            casteado = true;
        }

        // <-------- Remover parálisis (modHechizos.bas:1526): solo si está paralizado/inmovilizado ---------->
        if (sp.RemoverParalisis)
        {
            if (u.flags.Paralizado == 1 || u.flags.Inmovilizado == 1)
            {
                u.flags.Paralizado = 0; u.flags.Inmovilizado = 0; u.flags.ParalisisExpira = 0;
                DifundirParalisisUsuario(u, 0);
                if (u.Conn != null) { ServerPackets.ParalizeOK(u.Conn); ServerPackets.ConsoleMsg(u.Conn, "Ya no estás paralizado.", 1); }
                casteado = true;
            }
            else if (!casteado && !sp.CuraVeneno && !sp.Sanacion && sp.SubeHP == 0)
            {
                // Poción puramente removedora sobre alguien no paralizado: no se consume.
                ServerPackets.ConsoleMsg(u.Conn, "No estás paralizado.", 1);
                return false;
            }
        }

        // <-------- Remueve estupidez (modHechizos.bas:1551) ---------->
        if (sp.RemoverEstupidez && u.flags.Estupido == 1)
        {
            u.flags.Estupido = 0; u.flags.EstupidezExpira = 0;
            if (u.Conn != null) { ServerPackets.DumbNoMore(u.Conn); ServerPackets.ConsoleMsg(u.Conn, "Ya no estás aturdido.", 1); }
            casteado = true;
        }

        // <-------- Desencantar: remueve la metamorfosis (modHechizos.bas:1576) ---------->
        if (sp.Desencantar)
        {
            if (u.flags.Metamorfoseado == 0)
            { ServerPackets.ConsoleMsg(u.Conn, "No estás bajo ningún efecto de metamorfosis.", 1); return false; }
            RevertirMetamorfosis(userIndex);
            ServerPackets.ConsoleMsg(u.Conn, "Tu metamorfosis ha sido desencantada.", 1);
            casteado = true;
        }

        if (!casteado) return false;

        // InfoHechizo (modHechizos.bas:2149): sonido + FX + partícula del hechizo sobre el bebedor.
        if (sp.WAV > 0) BroadcastWaveArea(u.Pos.Map, u.Pos.X, u.Pos.Y, (short)sp.WAV);
        if (sp.FXgrh > 0) BroadcastFX(u.Pos.Map, u.Char.CharIndex, (short)sp.FXgrh, (short)Math.Max(0, sp.Loops));
        if (sp.Particle > 0)
        {
            int vida = sp.TimeParticula > 0 ? sp.TimeParticula : 100;
            BroadcastParticulaChar(u.Pos.Map, u.Char.CharIndex, (short)sp.Particle, vida);
            ProgramarRemoverParticula(u.Pos.Map, u.Char.CharIndex, (short)sp.Particle, Math.Max(1.0, vida / 60.0 + 0.5));
        }
        return true;
    }

    public const short PortalObjIndex = 672; // "Teleport a Intermundia" (obj.dat OBJ672, ObjType=Teleport)

    /// <summary>
    /// Inicia el casteo del portal de teletransporte (HechizoCreateTelep, modHechizos.bas:2436). Solo
    /// hechizo 53. No se puede en Prisión, Intermundia ni zona segura. El tile destino (x,y) debe estar
    /// libre (sin objeto, sin salida). Arranca el contador PortalTime; GameTimer.TickPortal crea el
    /// objeto 672 a los 5s (TileExit→Intermundia) y lo destruye a los 15s.
    /// </summary>
    private static void LanzarPortal(int userIndex, User u, byte x, byte y, SpellData.Spell sp, bool esGm)
    {
        var prision = CityData.Get(13);   // cPrision
        var inter = CityData.Get(15);     // cIntermundia
        var mi = MapLoader.Get(u.Pos.Map)?.Info;
        if (u.Pos.Map == prision.Map || u.Pos.Map == inter.Map || (mi != null && !mi.Pk))
        { ServerPackets.ConsoleMsg(u.Conn, "No puedes crear un portal aquí.", 1); return; }

        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return;
        if (x < 1 || x > 100 || y < 1 || y > 100) return;

        // El tile destino debe estar libre: sin objeto, sin salida existente y no bloqueado.
        if (map.FloorObj[x, y] != 0
            || (map.Exits[x, y].HasValue && map.Exits[x, y].Value.DestMap > 0)
            || map.Blocked[x, y])
        { ServerPackets.ConsoleMsg(u.Conn, "No puedes crear un portal ahí.", 1); return; }

        if (u.PortalTime > 0)
        { ServerPackets.ConsoleMsg(u.Conn, "Ya estás creando un portal.", 1); return; }

        // Consumo (HandleHechizoTerreno tras castear): maná, stamina, skill de Magia. Palabras mágicas.
        if (!esGm)
        {
            u.Stats.MinMAN = (short)Math.Max(0, u.Stats.MinMAN - sp.ManaRequerido);
            ServerPackets.UpdateMana(u.Conn, u.Stats.MinMAN);
            if (sp.StaRequerido > 0)
            { u.Stats.MinSta = (short)Math.Max(0, u.Stats.MinSta - sp.StaRequerido); ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta); }
        }
        Skills.SubirSkill(userIndex, 8); // VB6 SubirSkill incondicional (también GMs)
        DecirPalabrasMagicas(userIndex, sp.PalabrasMagicas); // modo 3 = palabras mágicas (burbuja, no consola ajena)

        u.PortalTime = 1;
        u.PortalMap = (short)u.Pos.Map; u.PortalX = x; u.PortalY = y;
        u.PortalCreado = false;

        // VB6 HechizoCreateTelep (modHechizos.bas:2495-2500): al castear suena el WAV del hechizo y
        // se forma la partícula del hechizo en el tile destino (EfectoTerrenoParticula, time -1 =
        // permanente hasta que se quite al cerrar/cancelar el portal).
        if (sp.WAV > 0) BroadcastWaveArea(u.Pos.Map, x, y, (short)sp.WAV);
        if (sp.Particle > 0) BroadcastParticulaTerreno(u.Pos.Map, (short)sp.Particle, x, y, -1);

        ServerPackets.ConsoleMsg(u.Conn, "Comienzas a crear un portal de teletransporte...", 1);
    }

    /// <summary>Porcentaje(valor, p) = valor * p / 100. Usado para escalar daño/cura mágico con el nivel.</summary>
    private static int Porcentaje(int valor, int p) => valor * p / 100;

    /// <summary>DecirPalabrasMagicas (modHechizos.bas:294): burbuja con las palabras mágicas al castear,
    /// salvo que el lanzador tenga equipado un ítem con EfectoMagico Silencio(16) (Amuleto del Silencio 755):
    /// castea en silencio, sin delatar el hechizo.</summary>
    private static void DecirPalabrasMagicas(int userIndex, string palabras)
    {
        if (string.IsNullOrEmpty(palabras)) return;
        var u = UserListManager.UserList[userIndex];
        if (u != null && Inventory.TieneEfectoMagico(u, 16)) return;
        Chat.TalkToMap(userIndex, palabras, 3); // modo 3 = palabras mágicas (burbuja)
    }

    /// <summary>Bonus de daño mágico del arma equipada (báculos con EfectoMagico = dañoMagico(14)).</summary>
    private static int BonusBaculoMagico(User u)
    {
        if (u.Invent.WeaponEqpObjIndex <= 0) return 0;
        var w = ObjData.Get(u.Invent.WeaponEqpObjIndex);
        return w.EfectoMagico == 14 ? w.CuantoAumento : 0;   // eMagicType.dañoMagico = 14
    }

    /// <summary>Resistencia mágica total del equipo del objetivo (casco+escudo+armadura+montura+anillo).</summary>
    private static int ResistenciaMagicaEquipo(User t)
    {
        int r = 0;
        void Add(short oi) { if (oi > 0) r += ObjData.Get(oi).ResistenciaMagica; }
        Add(t.Invent.CascoEqpObjIndex);
        Add(t.Invent.EscudoEqpObjIndex);
        Add(t.Invent.ArmourEqpObjIndex);
        Add(t.Invent.MonturaObjIndex);
        Add(t.Invent.AnilloEqpObjIndex);
        Add(t.Invent.MagicIndex);      // ítem mágico equipado (orbes/collares con ResistenciaMagica)
        return r;
    }

    /// <summary>Difunde una partícula sobre un personaje a todos los del mapa (EfectoCharParticula).</summary>
    /// <summary>Wrapper público: muestra una partícula sobre un char (lo usa el FX de flecha explosiva de los bots cazadores).</summary>
    public static void ParticulaEnChar(int map, short charIndex, short particle, int time = 400)
        => BroadcastParticulaChar(map, charIndex, particle, time);

    private static void BroadcastParticulaChar(int map, short charIndex, short particle, int time, bool remove = false)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == map)
                ServerPackets.EfectoCharParticula(o.Conn, charIndex, particle, time, remove);
        }
    }

    // Cola de partículas sobre personajes a borrar cuando termina su duración. El cliente
    // mide la vida en frames, así que algunas partículas (hechizos de guerrero) quedan pegadas;
    // acá programamos el remove explícito (EfectoCharParticula remove=true) tras su duración.
    private static readonly List<(int map, short charIndex, short particle, double dueSec)> _particulasARemover = new();

    /// <summary>Programa borrar una partícula de char dentro de <paramref name="segundos"/> segundos.</summary>
    private static void ProgramarRemoverParticula(int map, short charIndex, short particle, double segundos)
    {
        _particulasARemover.Add((map, charIndex, particle, Environment.TickCount64 / 1000.0 + segundos));
    }

    /// <summary>Procesa la cola de partículas vencidas y difunde su remove. Lo llama TickEstados.</summary>
    private static void ProcesarRemocionParticulas()
    {
        if (_particulasARemover.Count == 0) return;
        double ahora = Environment.TickCount64 / 1000.0;
        for (int k = _particulasARemover.Count - 1; k >= 0; k--)
        {
            var e = _particulasARemover[k];
            if (ahora < e.dueSec) continue;
            BroadcastParticulaChar(e.map, e.charIndex, e.particle, 0, remove: true);
            _particulasARemover.RemoveAt(k);
        }
    }

    /// <summary>Difunde una partícula sobre un tile a todos los del mapa (EfectoTerrenoParticula).</summary>
    private static void BroadcastParticulaTerreno(int map, short particle, byte x, byte y, int time)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == map)
                ServerPackets.EfectoTerrenoParticula(o.Conn, particle, x, y, time);
        }
    }

    /// <summary>
    /// Quita la partícula del tile del portal (time 0 = borrar). Se usa al CANCELAR el cast antes de
    /// formarse el objeto (cuando aún se ve la partícula del cast). Una vez creado el objeto teleport,
    /// la partícula la maneja el cliente por el objeto (ObjectCreate/Delete).
    /// </summary>
    public static void PortalFxQuitar(int map, byte x, byte y)
        => BroadcastParticulaTerreno(map, 0, x, y, 0);

    /// <summary>Difunde un FX sobre un personaje a todos los del mapa.</summary>
    private static void BroadcastFX(int map, short charIndex, short fx, short fxLoops)
    {
        if (fx <= 0) return;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.Pos.Map == map)
                ServerPackets.CreateFX(o.Conn, charIndex, fx, fxLoops);
        }
    }

    /// <summary>Devuelve el userIndex parado en (map,x,y) distinto de 'excepto', o 0.</summary>
    private static int UserAt(int map, int x, int y, int excepto)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            if (i == excepto) continue;
            var u = UserListManager.UserList[i];
            if (u.flags.UserLogged && u.Pos.Map == map && u.Pos.X == x && u.Pos.Y == y) return i;
        }
        return 0;
    }

    /// <summary>Mata un NPC y da la recompensa al usuario (reutilizado por melee y magia).</summary>
    private static void MatarNpc(User u, NpcManager.NpcInstance npc)
    {
        npc.Dead = true;
        int map = npc.Map;
        // Sonido de muerte del NPC (NPCs.dat Snd3).
        if (npc.Snd3 > 0) BroadcastWaveArea(map, npc.X, npc.Y, npc.Snd3);
        // Criaturas de entrenador (NoRespawn): desaparecen al morir y descuentan al maestro.
        if (npc.NoRespawn)
        {
            npc.RespawnAt = 0;
            if (npc.MaestroNpc > 0) NpcManager.QuitarMascotaNpc(map, npc.MaestroNpc);
        }
        else
        {
            npc.RespawnAt = Environment.TickCount64 / 1000.0 + NpcManager.RespawnSeconds;
        }
        // Quitar el NPC de la vista (limpia ADEMÁS los sets VisibleNpcs de los observadores; usar el
        // loop manual dejaba el CharIndex "pegado" en esos sets y, al reciclarse, el NPC nuevo con ese
        // índice no se creaba). Tras quitarlo, liberar el CharIndex para que se reuse (ver CharIndexPool).
        AreaVisibility.OnNpcRemoved(npc);
        CharIndexPool.Free(npc.CharIndex);   // el respawn pedirá un índice nuevo (puede ser éste reciclado)
        ServerPackets.ConsoleMsg(u.Conn, $"¡Has matado a {npc.Name}!", 2); // font 2 = rojo + tab Combate
        // La EXP se reparte por golpe en CalcularDarExp (pool ExpCount); el golpe mortal ya entregó
        // la porción final. NO dar exp acá para no duplicar (VB6: MuereNpc no re-otorga el total).
        if (npc.GiveGLD > 0)
        {
            // Boost de oro personal del Battle Pass sobre el oro soltado por el NPC.
            int gld = (int)(npc.GiveGLD * BattlePass.OroMult(u));
            u.Stats.GLD += gld;
            ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
        }

        // Battle Pass: puntos de pase por matar un NPC, escalados por dificultad (MaxHP) + misiones.
        BattlePass.OnNpcKilled(u.id, npc.MaxHP, npc.Name, npc.NpcIndex);

        // VB6 NPC_TIRAR_ITEMS: tirar drops al piso según probabilidad (Drops.dat)
        TirarDrops(npc, u);

        // Evento Invasión de Cofres: si el NPC era un cofre, dar recompensa al matador (OnCofreMuere).
        if (npc.NpcIndex == CofresEvento.NPC_ID_COFRE) CofresEvento.OnCofreMuere(npc, u.id);

        // Evento Inframundo: si el NPC era un Hechicero Elemental, actualizar el evento.
        if (InframundoEvento.EventoActivo) InframundoEvento.OnHechiceroMuere(npc, u.id);
    }

    /// <summary>
    /// NPC_TIRAR_ITEMS (Modulo_InventANDobj.bas:72) 1:1 VB6.
    /// Cada drop cae si prob >= 100 (siempre) o si Rnd()*100 <= prob.
    /// Lo deja en un tile libre cerca de la muerte y difunde ObjectCreate al área.
    /// </summary>
    private static void TirarDrops(NpcManager.NpcInstance npc, User killer)
    {
        if (npc.Drops == null || npc.Drops.Length == 0) return;
        var map = MapLoader.Get(npc.Map);
        if (map == null) return;

        const short SND_DROP = Sounds.DROP; // 132 (antes 14 = sonido de pescar, incorrecto)
        // Anillo Dorado de drop (1607 +100% / 1610 +20%, EfectoMagico=8): el matador con el anillo
        // equipado multiplica la probabilidad de cada drop por (1 + CuantoAumento/100). Acumulable
        // con el multiplicador de la Ruleta.
        int bonusDrop = killer != null ? Inventory.CuantoEfectoMagico(killer, 8) : 0;
        foreach (var (objIndex, amount, prob) in npc.Drops)
        {
            if (objIndex <= 0) continue;
            // Ruleta DROP x2: duplica la probabilidad de caída durante el evento.
            double probEvento = prob * Ruleta.MultiplicadorDrop();
            if (bonusDrop > 0) probEvento *= 1.0 + bonusDrop / 100.0;
            bool cae = probEvento >= 100 || (_rng.NextDouble() * 100.0 <= probEvento);
            if (!cae) continue;

            // Buscar tile libre: primero la posición del NPC, luego adyacentes.
            if (!TileLibreParaObj(map, npc.X, npc.Y, out int tx, out int ty)) continue;

            map.FloorObj[tx, ty] = objIndex;
            map.FloorAmount[tx, ty] = amount;

            // Difundir por área (VB6: MakeObj → SendToAreaByPos)
            AreaVisibility.ObjectAppeared(npc.Map, tx, ty, objIndex, amount);
            if (killer?.Conn != null)
                ServerPackets.PlayWave(killer.Conn, SND_DROP, (byte)tx, (byte)ty);
        }
    }

    /// <summary>Busca un tile sin objeto cerca de (x,y), priorizando el propio tile. Radio 1.</summary>
    private static bool TileLibreParaObj(MapData map, int x, int y, out int tx, out int ty)
    {
        // VB6 Tilelibre: primero el tile exacto, luego espiral de adyacentes.
        for (int r = 0; r <= 2; r++)
        for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r; dy++)
        {
            int nx = x + dx, ny = y + dy;
            if (nx < 1 || nx > 100 || ny < 1 || ny > 100) continue;
            if (map.Blocked[nx, ny]) continue;
            if (map.FloorObj[nx, ny] != 0) continue;
            tx = nx; ty = ny; return true;
        }
        tx = ty = 0; return false;
    }

    /// <summary>
    /// VB6: al atacar (UserImpactoNpc / UsuarioAtaca) se revela el ocultamiento.
    /// Limpia flags.Oculto, difunde SetInvisible(false) al área y avisa al usuario.
    /// </summary>
    private static void RevelarOculto(User u)
    {
        if (u.flags.Oculto != 1) return;
        // Anillo de las Sombras (1006, CaminaOculto(13)): atacar NO revela el ocultamiento
        // (VB6 Protocol.bas:2290 / SistemaCombate.bas:1121: TieneAnilloSombras).
        if (Inventory.TieneEfectoMagico(u, 13)) return;
        u.flags.Oculto = 0;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.SetInvisible(o.Conn, u.Char.CharIndex, false);
        }
        ServerPackets.ConsoleMsg(u.Conn, "¡Has vuelto a ser visible!", 1);
    }

    /// <summary>Un NPC golpea a un usuario (lo llama la IA). Aplica daño y puede matarlo.</summary>
    public static void NpcAtacaUsuario(NpcManager.NpcInstance npc, int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.flags.Muerto == 1 || u.Conn == null) return;
        // Bots: el golpe cuerpo a cuerpo SÓLO conecta si el usuario está en un tile vecino (no pega "en
        // área" ni a distancia). Los hechizos sí pueden ir de lejos; el golpe es estrictamente al lado.
        if (npc.IsBot && (Math.Abs(u.Pos.X - npc.X) > 1 || Math.Abs(u.Pos.Y - npc.Y) > 1)) return;
        // VB6 NpcAtacaUser: no ataca a usuarios invisibles/ocultos (Oculto=skill, Invisible=hechizo),
        // EXCEPTO los dragones (NPCtype=20) que ven a través de la invisibilidad.
        if ((u.flags.Oculto == 1 || u.flags.Invisible == 1) && npc.NpcType != 20) return;
        // Los NPCs no atacan a GMs/Dioses (Consejero o superior). EXCEPCIÓN: los bots de prueba
        // sí pegan a GMs/Dioses, pero NUNCA a su dueño (el invocador).
        // Excepción del sparring PvP: el bot de spar SÍ pega a su dueño (es su objetivo de testeo).
        if (npc.IsBot) { if (userIndex == npc.OwnerUserIndex && !npc.BotSpar) return; }
        else if (NpcManager.EsGmIntocable(u)) return;

        // Intervalo de ataque. Bots: timer de GOLPE propio + cruce con la magia (como un jugador).
        // Resto de NPCs: IntervaloPermiteAtacarNpc (3000ms; guardias 2000ms), compartido con el casteo.
        if (npc.IsBot) { if (!NpcManager.BotPuedeGolpear(npc)) return; }
        else if (!Intervals.PuedeAtacarNpc(ref npc.TimerAtaque, NpcManager.AttackIntervalFor(npc))) return;

        // VB6 NpcAtacaUser (SistemaCombate.bas:900): registrar qué NPC lo ataca (para el reset al morir).
        if (u.flags.AtacadoPorNpc == 0) u.flags.AtacadoPorNpc = npc.CharIndex;

        // Sonido de ataque del NPC (SistemaCombate.bas:911: .flags.Snd1).
        if (npc.Snd1 > 0) BroadcastWaveArea(npc.Map, npc.X, npc.Y, npc.Snd1);

        // VB6 NpcAtacaUser: las mascotas del usuario atacado defienden al amo (CheckElementales=False).
        NpcManager.CheckPets(npc, userIndex, false);

        if (NpcImpacto(npc, u))
        {
            // Impacto: sonido + FX de sangre (salvo meditando/navegando/montando) y daño.
            BroadcastWaveArea(npc.Map, u.Pos.X, u.Pos.Y, SND_IMPACTO_NPC);
            if (!u.flags.Meditando && !u.flags.Navegando && u.flags.Montando == 0)
                BroadcastFxArea(npc.Map, u.Char.CharIndex, FXSANGRE, 0);

            Npcdano(npc, userIndex);
        }
        else
        {
            // Falla: mensaje de fallo sobre la cabeza del NPC (modo 2).
            ServerPackets.ChatOverHead(u.Conn, "*Falló*", npc.CharIndex, 2);
        }

        // VB6: el usuario sube Tácticas al ser atacado (entrena la defensa).
        Skills.SubirSkill(userIndex, SK_TACTICAS);
    }

    /// <summary>
    /// NpcImpacto (SistemaCombate.bas:286): ¿el NPC impacta al usuario? Compara PoderAtaque del NPC
    /// contra la evasión del usuario (+ escudo). Si falla y hay escudo, chance de rechazo con escudo.
    /// 1:1 con VB6.
    /// </summary>
    private static bool NpcImpacto(NpcManager.NpcInstance npc, User u)
    {
        long userEvasion = PoderEvasion(u);
        long npcPoderAtaque = npc.PoderAtaque;
        long poderEvasionEscudo = PoderEvasionEscudo(u);
        int skillTacticas = u.Stats.UserSkills[SK_TACTICAS];
        int skillDefensa = u.Stats.UserSkills[SK_DEFENSA];

        if (u.Invent.EscudoEqpObjIndex > 0) userEvasion += poderEvasionEscudo;

        var cc = BalanceData.Combate;
        long probExito = Math.Max(cc.ImpactoMin, Math.Min(cc.ImpactoMax, cc.ImpactoBase + (npcPoderAtaque - userEvasion)));
        bool impacto = _rng.Next(1, 101) <= probExito;

        if (u.Invent.EscudoEqpObjIndex > 0 && !impacto && (skillDefensa + skillTacticas) > 0)
        {
            long probRechazo = Math.Max(10, Math.Min(90, 100L * skillDefensa / (skillDefensa + skillTacticas)));
            if (_rng.Next(1, 101) <= probRechazo)
            {
                const short SND_ESCUDO = 37;
                if (u.Conn != null)
                {
                    ServerPackets.PlayWave(u.Conn, SND_ESCUDO, (byte)u.Pos.X, (byte)u.Pos.Y);
                    ServerPackets.ConsoleMsg(u.Conn, "¡Has rechazado el ataque con tu escudo!", 1);
                }
                Skills.SubirSkill(u.id, SK_DEFENSA);
            }
        }
        return impacto;
    }

    /// <summary>
    /// Npcdaño (SistemaCombate.bas:641): daño del NPC al usuario. Tira una parte del cuerpo (1-6);
    /// cabeza→casco, resto→armadura(+escudo). Suma defensa de barco/montura. Mínimo 1. Aplica daño,
    /// rompe meditación si es alto y mata al usuario si llega a 0. 1:1 con VB6.
    /// </summary>
    private static void Npcdano(NpcManager.NpcInstance npc, int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        int dano = RangoOMin(npc.MinHIT, npc.MaxHIT);

        int defbarco = 0, defmontura = 0;
        if (u.flags.Navegando && u.Invent.BarcoObjIndex > 0)
        { var o = ObjData.Get(u.Invent.BarcoObjIndex); defbarco = RangoOMin(o.MinDef, o.MaxDef); }
        if (u.flags.Montando != 0 && u.Invent.MonturaObjIndex > 0)
        { var o = ObjData.Get(u.Invent.MonturaObjIndex); defmontura = RangoOMin(o.MinDef, o.MaxDef); }

        int lugar = _rng.Next(1, 7); // 1..6 (1=bCabeza)
        int absorbido = 0;
        if (lugar == 1)
        {
            if (u.Invent.CascoEqpObjIndex > 0)
            { var c = ObjData.Get(u.Invent.CascoEqpObjIndex); absorbido = RangoOMin(c.MinDef, c.MaxDef); }
        }
        else if (u.Invent.ArmourEqpObjIndex > 0)
        {
            var a = ObjData.Get(u.Invent.ArmourEqpObjIndex);
            if (u.Invent.EscudoEqpObjIndex > 0)
            { var e = ObjData.Get(u.Invent.EscudoEqpObjIndex); absorbido = RangoOMin(a.MinDef + e.MinDef, a.MaxDef + e.MaxDef); }
            else absorbido = RangoOMin(a.MinDef, a.MaxDef);
        }

        absorbido += defbarco + defmontura;
        dano -= absorbido;
        if (dano < 1) dano = 1;

        // Daño rojo flotante sobre el NPC + "Te golpean por X" en consola (lo arma el cliente).
        DanoRecibido(u, npc.CharIndex, dano);
        // Mensaje extra con la parte del cuerpo golpeada (detalle que el cliente no incluye).
        ServerPackets.ConsoleMsg(u.Conn, $"¡{npc.Name} te ha pegado en la parte {lugar}!", 6);

        u.Stats.MinHP -= (short)dano;

        // VB6: si está meditando y el golpe supera el umbral, deja de meditar.
        if (u.flags.Meditando)
        {
            int inte = u.Stats.UserAtributos[3];          // eAtributos.Inteligencia
            int medi = u.Stats.UserSkills[10];            // eSkill.Meditar
            double umbral = Math.Floor(u.Stats.MinHP / 100.0 * inte * medi / 100.0 * 12 / (_rng.Next(0, 6) + 7));
            if (dano > umbral)
            {
                u.flags.Meditando = false;
                if (u.Conn != null) { ServerPackets.MeditateToggle(u.Conn); ServerPackets.ConsoleMsg(u.Conn, "Dejas de meditar.", 1); }
                Facciones.QuitarParticulaMeditacion(u);
            }
        }

        if (u.Stats.MinHP <= 0)
        {
            u.Stats.MinHP = 0;
            if (u.Conn != null) ServerPackets.UpdateHP(u.Conn, 0);
            UserDie(userIndex);
        }
        else if (u.Conn != null)
        {
            ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP);
        }
    }

    private const short SND_IMPACTO_NPC = Sounds.IMPACTO; // 86 (antes 3 = sonido de warp, incorrecto)
    private const short FXSANGRE = 14;

    /// <summary>Difunde un PlayWave a todos los usuarios del mapa (placeholder de ToNPCArea/ToPCArea).</summary>
    private static void BroadcastWaveArea(int map, int x, int y, short wave)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == map)
                ServerPackets.PlayWave(o.Conn, wave, (byte)x, (byte)y);
        }
    }

    /// <summary>Difunde un CreateArrowProjectile (flecha/arma arrojadiza animada) a todos los del mapa.</summary>
    private static void BroadcastArrow(int map, short charOrigen, short charDestino,
        int xOrigen, int yOrigen, int xDestino, int yDestino, short grhIndex)
    {
        if (grhIndex <= 0) return;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == map)
                ServerPackets.CreateArrowProjectile(o.Conn, charOrigen, charDestino,
                    (short)xOrigen, (short)yOrigen, (short)xDestino, (short)yDestino, grhIndex);
        }
    }

    /// <summary>Difunde un CreateFX a todos los usuarios del mapa.</summary>
    private static void BroadcastFxArea(int map, short charIndex, short fx, short loops)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == map)
                ServerPackets.CreateFX(o.Conn, charIndex, fx, loops);
        }
    }

    /// <summary>
    /// NpcLanzaSpellSobreUser (modHechizos.bas:9) 1:1 VB6. El NPC lanza un hechizo al usuario:
    /// daño (resta resistencia mágica del equipo) o paralización, con FX/sonido.
    /// </summary>
    /// <returns>true si efectivamente lanzó el hechizo; false si abortó (muerto/oculto/cooldown/etc).
    /// Lo usa la IA para NO perder el tick de movimiento cuando el hechizo está en cooldown.</returns>
    /// <summary>Un NPC/bot caster lanza un hechizo a OTRO NPC (daño mágico + FX/partícula/sonido).
    /// Lo usan los bots para castear también a las criaturas, no solo a usuarios.</summary>
    public static bool NpcLanzaSpellANpc(NpcManager.NpcInstance npc, NpcManager.NpcInstance victima)
    {
        if (npc.Spells == null || npc.Spells.Length == 0) return false;
        if (victima == null || victima.Dead) return false;
        if (MapLoader.Get(npc.Map)?.Info?.NoMagia == true) return false;
        if (!Intervals.PuedeAtacarNpc(ref npc.TimerAtaque, NpcManager.AttackIntervalFor(npc))) return false;

        short spellIndex = npc.Spells[_rng.Next(npc.Spells.Length)];
        var sp = SpellData.Get(spellIndex);
        if (string.IsNullOrEmpty(sp.Nombre)) return false;

        short fx = (short)sp.FXgrh, loops = (short)Math.Max(0, sp.Loops);
        int map = npc.Map;
        if (sp.WAV > 0) BroadcastWaveArea(map, victima.X, victima.Y, (short)sp.WAV);
        if (sp.Particle > 0) BroadcastParticulaChar(map, victima.CharIndex, (short)sp.Particle, sp.TimeParticula);
        BroadcastFX(map, victima.CharIndex, fx, loops);

        if (sp.SubeHP == 2) // DAÑA
        {
            int dano = sp.MaxHP >= sp.MinHP && sp.MaxHP > 0 ? _rng.Next(sp.MinHP, sp.MaxHP + 1) : sp.MinHP;
            if (dano < 0) dano = 0;
            victima.MinHP -= dano;
            if (victima.MinHP <= 0) NpcManager.MatarNpcInstance(victima);
        }
        else if (sp.Paraliza || sp.Inmoviliza)
        {
            victima.ParalizadoHasta = Environment.TickCount64 / 1000.0 + 8;
        }
        return true;
    }

    /// <summary>Un bot clérigo cura a un NPC/bot aliado herido (FX + partícula + sonido del hechizo).</summary>
    public static bool NpcCuraANpc(NpcManager.NpcInstance caster, NpcManager.NpcInstance aliado, short spellIndex)
    {
        if (aliado == null || aliado.Dead || aliado.MinHP >= aliado.MaxHP) return false;
        if (!Intervals.PuedeAtacarNpc(ref caster.TimerAtaque, NpcManager.AttackIntervalFor(caster))) return false;
        var sp = SpellData.Get(spellIndex);
        if (string.IsNullOrEmpty(sp.Nombre)) return false;

        int cura = sp.MaxHP >= sp.MinHP && sp.MaxHP > 0 ? _rng.Next(sp.MinHP, sp.MaxHP + 1) : sp.MinHP;
        if (cura <= 0) cura = 50;
        aliado.MinHP = (short)Math.Min(aliado.MaxHP, aliado.MinHP + cura);

        int map = caster.Map;
        if (sp.WAV > 0) BroadcastWaveArea(map, aliado.X, aliado.Y, (short)sp.WAV);
        if (sp.Particle > 0) BroadcastParticulaChar(map, aliado.CharIndex, (short)sp.Particle, sp.TimeParticula);
        BroadcastFX(map, aliado.CharIndex, (short)sp.FXgrh, (short)Math.Max(0, sp.Loops));
        return true;
    }

    public static bool NpcLanzaSpell(NpcManager.NpcInstance npc, int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.flags.Muerto == 1 || u.Conn == null) return false;
        if (npc.Spells == null || npc.Spells.Length == 0) return false;

        // VB6: no lanza a usuarios invisibles/ocultos.
        if (u.flags.Oculto == 1) return false;
        // Los NPCs no lanzan hechizos a GMs/Dioses (Consejero o superior).
        // Excepción: el bot de sparring PvP SÍ le lanza hechizos a su dueño (aunque sea GM) para el testeo.
        if (NpcManager.EsGmIntocable(u) && !(npc.IsBot && npc.BotSpar && userIndex == npc.OwnerUserIndex)) return false;
        // Orbe de Inhibición (651) / armas con MagicasNoAtacan(9): los NPCs no pueden lanzarle
        // hechizos al usuario (VB6 modHechizos.bas:33-34). El NPC sigue pudiendo pegar cuerpo a cuerpo.
        if (Inventory.TieneEfectoMagico(u, 9, incluirArma: true)) return false;
        // Mapa con magia sin efecto: no lanza.
        if (MapLoader.Get(u.Pos.Map)?.Info?.NoMagia == true) return false;

        // Hechizo RANDOM del repertorio del NPC (no lanza todos: uno al azar por casteo).
        short spellIndex = npc.Spells[_rng.Next(npc.Spells.Length)];
        var sp = SpellData.Get(spellIndex);
        if (string.IsNullOrEmpty(sp.Nombre)) return false;

        // Intervalo de hechizo. Bots: timer de MAGIA propio + cruce con el golpe (respeta el intervalo de
        // hechizo, separado del melee, como un jugador). Resto de NPCs: IntervaloPermiteAtacarNpc compartido.
        if (npc.IsBot)
        {
            // Maná: el bot caster consume maná. Si no le alcanza, no castea (meleará y poteará azul).
            if (npc.MaxMana > 0 && npc.MinMana < sp.ManaRequerido) return false;
            if (!NpcManager.BotPuedeCastear(npc)) return false;
            npc.MinMana -= sp.ManaRequerido;
            NpcManager.NpcDicePalabrasMagicas(npc, sp.PalabrasMagicas); // palabras mágicas sobre su cabeza
        }
        else if (!Intervals.PuedeAtacarNpc(ref npc.TimerAtaque, NpcManager.AttackIntervalFor(npc))) return false;

        short fx = (short)sp.FXgrh, loops = (short)Math.Max(0, sp.Loops);
        int map = npc.Map;

        // Sonido del hechizo del NPC (VB6 NpcLanzaSpell: PlayWave(Hechizos(Spell).WAV) en la pos del objetivo).
        if (sp.WAV > 0) BroadcastWaveArea(map, u.Pos.X, u.Pos.Y, (short)sp.WAV);

        // Partícula del hechizo sobre el objetivo (igual que el casteo de usuario, InfoHechizo).
        if (sp.Particle > 0)
            BroadcastParticulaChar(map, u.Char.CharIndex, (short)sp.Particle, sp.TimeParticula);

        if (sp.SubeHP == 2) // DAÑA
        {
            int dano = sp.MaxHP >= sp.MinHP && sp.MaxHP > 0 ? _rng.Next(sp.MinHP, sp.MaxHP + 1) : sp.MinHP;
            // Resta resistencia mágica del equipo (casco/escudo/armadura/anillo).
            dano -= ResistenciaMagica(u);
            // Anillo de Defensa Mágica (708, DisminuyeGolpe(7)): reduce el daño mágico en CuantoAumento%.
            int redPctNpc = Inventory.CuantoEfectoMagico(u, 7);
            if (redPctNpc > 0) dano -= dano * redPctNpc / 100;
            if (dano < 0) dano = 0;

            BroadcastFX(map, u.Char.CharIndex, fx, loops);
            u.Stats.MinHP = (short)(u.Stats.MinHP - dano);
            ServerPackets.ConsoleMsg(u.Conn, $"{npc.Name} te lanzó {sp.Nombre}.", 4);
            Skills.SubirSkill(userIndex, 9); // eSkill.Resistencia 1:1 (modHechizos.bas:186)
            DanoRecibido(u, npc.CharIndex, dano);  // número rojo del daño mágico recibido
            if (u.Stats.MinHP < 1) { u.Stats.MinHP = 0; ServerPackets.UpdateHP(u.Conn, 0); UserDie(userIndex); }
            else ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP);
        }
        else if (sp.Paraliza || sp.Inmoviliza)
        {
            if (u.flags.Paralizado == 0)
            {
                double ahora = Environment.TickCount64 / 1000.0;
                if (sp.Inmoviliza) u.flags.Inmovilizado = 1;
                u.flags.Paralizado = 1;
                u.flags.ParalisisExpira = ahora + DuracionParalisisUsuario;
                BroadcastFX(map, u.Char.CharIndex, fx, loops);
                ServerPackets.ParalizeOK(u.Conn);
                DifundirParalisisUsuario(u, DuracionParalisisUsuario);
                ServerPackets.ConsoleMsg(u.Conn, $"{npc.Name} te ha paralizado.", 4);
                Skills.SubirSkill(userIndex, 9); // Resistencia también al recibir parálisis de NPC
            }
        }
        else if (sp.SubeHP == 1) // cura (raro en NPC hostil)
        {
            BroadcastFX(map, u.Char.CharIndex, fx, loops);
        }

        // Estados adicionales que el hechizo del NPC pueda aplicar (veneno/ceguera/incineración).
        double t = Environment.TickCount64 / 1000.0;
        if (sp.Envenena > 0 && u.flags.Envenenado == 0)
        {
            u.flags.Envenenado = 1; u.flags.NivelVeneno = sp.Envenena;
            ServerPackets.ConsoleMsg(u.Conn, $"¡{npc.Name} te ha envenenado!", 4);
        }
        if (sp.Ceguera && u.flags.Ciego == 0)
        {
            u.flags.Ciego = 1; u.flags.CegueraExpira = t + 6.0;
            ServerPackets.Blind(u.Conn);
            ServerPackets.ConsoleMsg(u.Conn, $"{npc.Name} te ha cegado.", 4);
        }
        if (sp.Incinera && u.flags.Incinerado == 0)
        {
            u.flags.Incinerado = 1;
            ServerPackets.ConsoleMsg(u.Conn, $"¡{npc.Name} te ha incinerado!", 4);
        }
        return true;
    }

    /// <summary>Suma la resistencia mágica del equipo equipado (casco+escudo+armadura+montura+anillo).</summary>
    private static int ResistenciaMagica(User u) => ResistenciaMagicaEquipo(u);

    /// <summary>
    /// UserDie (Modulo_UsUaRiOs.bas): el jugador muere. HP=0, flag Muerto, apariencia de
    /// fantasma (body/head clásicos de muerto en AO) difundida al mapa con CharacterChange.
    /// </summary>
    /// <summary>
    /// UsuarioAtacaUsuario (SistemaCombate.bas:1503) 1:1 VB6 — núcleo básico.
    /// Valida PK, distancia, daño, HP. Falta: facciones, evasión avanzada, drops.
    /// </summary>
    /// <summary>
    /// UsuarioAtacadoPorUsuario (SistemaCombate.bas:2475): combat state al ser atacado. Cancela el casteo
    /// de runa y la meditación de la víctima. (TiempoDeMapeo y contraataque de mascotas: PRIORIDAD 4.)
    /// </summary>
    private static void UsuarioAtacadoPorUsuario(int atkIdx, int vicIdx)
    {
        var vic = UserListManager.UserList[vicIdx];
        if (vic == null) return;
        if (vic.CasteandoRuna > 0)
        {
            vic.CasteandoRuna = 0; vic.RunaSlot = 0;
            ServerPackets.RunaCastProgress(vic.Conn, vic.Char.CharIndex, 0, 6);
            ServerPackets.ConsoleMsg(vic.Conn, "¡Tu teletransporte fue cancelado!", 1);
        }
        if (vic.flags.Meditando)
        {
            vic.flags.Meditando = false;
            ServerPackets.MeditateToggle(vic.Conn);
            ServerPackets.ConsoleMsg(vic.Conn, "Dejas de meditar.", 1);
            Facciones.QuitarParticulaMeditacion(vic);
        }
    }

    /// <summary>UserEnvenena (SistemaCombate.bas:3402): si el arma (o munición si es proyectil) tiene
    /// Envenena=1, 60% de probabilidad de envenenar a la víctima.</summary>
    private static void UserEnvenena(User atk, User vic)
    {
        bool envenena = false;
        short objInd = atk.Invent.WeaponEqpObjIndex;
        if (objInd > 0)
        {
            if (ObjData.Get(objInd).Proyectil == 1) objInd = atk.Invent.MunicionEqpObjIndex;
            if (objInd > 0) envenena = ObjData.Get(objInd).Envenena == 1;
        }
        // Orbe de la Ponzoña (57, EfectoMagico=Envenena(19)): envenena con cualquier golpe.
        if (!envenena) envenena = Inventory.TieneEfectoMagico(atk, 19, incluirArma: true);
        if (envenena && _rng.Next(1, 101) < 60)
        {
            if (vic.flags.Envenenado == 0) { vic.flags.Envenenado = 1; if (vic.flags.NivelVeneno < 1) vic.flags.NivelVeneno = 1; }
            if (vic.Conn != null) ServerPackets.ConsoleMsg(vic.Conn, $"¡{atk.Name} te ha envenenado!", 4);
            ServerPackets.ConsoleMsg(atk.Conn, $"¡Has envenenado a {vic.Name}!", 1);
        }
    }

    /// <summary>UserIncinera (SistemaCombate.bas:3489): arma proyectil con munición "orbe" (Snd3>0) →
    /// 5/35 de incinerar a la víctima (si no está ya incinerada).</summary>
    private static void UserIncinera(User atk, User vic)
    {
        if (vic.flags.Incinerado == 1) return;
        // Orbe Ígnea (868, EfectoMagico=Incinera(10), también Espada de Guerrero Abismal):
        // incinera con cualquier golpe. El VB6 tenía esta rama comentada (SistemaCombate.bas:3500).
        bool orbe = Inventory.TieneEfectoMagico(atk, 10, incluirArma: true);
        if (!orbe && atk.Invent.WeaponEqpObjIndex > 0)
        {
            var w = ObjData.Get(atk.Invent.WeaponEqpObjIndex);
            orbe = w.Proyectil > 0 && atk.Invent.MunicionEqpObjIndex > 0
                   && ObjData.Get(atk.Invent.MunicionEqpObjIndex).Snd3 > 0;
        }
        if (!orbe) return;
        if (_rng.Next(1, 36) <= 5) // 5/35
        {
            vic.flags.Incinerado = 1;
            if (vic.Conn != null) ServerPackets.ConsoleMsg(vic.Conn, "¡Estás siendo incinerado!", 4);
            ServerPackets.ConsoleMsg(atk.Conn, $"¡Has incinerado a {vic.Name}!", 1);
        }
    }

    /// <summary>
    /// GolpeParalizaUsuario, rama OBJ869 (SistemaCombate.bas:3658): la Orbe Acuática equipada
    /// (EfectoMagico=Paraliza(11)) paraliza al golpear a un usuario: 60% de prob, 3-5 segundos.
    /// </summary>
    private static void GolpeOrbeUsuario(User atk, User vic)
    {
        if (vic.flags.Paralizado == 1 || vic.flags.Inmovilizado == 1) return;
        if (!Inventory.TieneEfectoMagico(atk, 11, incluirArma: true)) return;
        if (_rng.Next(1, 101) > 60) return;
        int orbeSegs = _rng.Next(3, 6); // 3-5 s (VB6 90-150 ticks)
        vic.flags.Paralizado = 1;
        vic.flags.ParalisisExpira = Environment.TickCount64 / 1000.0 + orbeSegs;
        if (vic.Conn != null)
        {
            ServerPackets.ParalizeOK(vic.Conn);
            ServerPackets.ConsoleMsg(vic.Conn, $"¡{atk.Name} te ha paralizado con su orbe!", 2);
        }
        DifundirParalisisUsuario(vic, orbeSegs);
        ServerPackets.ConsoleMsg(atk.Conn, $"¡Tu orbe paraliza a {vic.Name}!", 2);
        BroadcastWaveArea(vic.Pos.Map, vic.Pos.X, vic.Pos.Y, 17);
        BroadcastFX(vic.Pos.Map, vic.Char.CharIndex, 8, 0);
    }

    /// <summary>EsStaff (SistemaCombate.bas:3959): báculo según WeaponAnim (6/16/17) o nombre con "BACULO".</summary>
    private static bool EsStaff(short objIndex)
    {
        if (objIndex <= 0) return false;
        var od = ObjData.Get(objIndex);
        if (od.WeaponAnim == 6 || od.WeaponAnim == 16 || od.WeaponAnim == 17) return true;
        return (od.Name ?? "").ToUpperInvariant().Contains("BACULO");
    }

    public static void UsuarioAtacaUsuario(int atkIdx, int vicIdx, User atk)
    {
        var vic = UserListManager.UserList[vicIdx];
        if (vic == null || !vic.flags.UserLogged || vic.flags.Muerto == 1) return;

        // VB6 PuedeAtacar COMPLETO (consistente con hechizos): zona segura, pareja, muerto, clan,
        // party, aliados de facción, protección GM. Muestra su propio mensaje al denegar.
        if (!PuedeAtacar(atkIdx, vicIdx)) return;

        // VB6 UsuarioImpacto: chance de impacto (poder de ataque vs evasión + escudo). Falla → no hay daño.
        if (!UsuarioImpacto(atkIdx, vicIdx))
        {
            const short SND_SWING3 = 2;
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var o = UserListManager.UserList[i];
                if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == atk.Pos.Map)
                    ServerPackets.PlayWave(o.Conn, SND_SWING3, (byte)atk.Pos.X, (byte)atk.Pos.Y);
            }
            FalloPropio(atk);   // "¡Fallas!" sobre la cabeza del atacante (VB6 SistemaCombate.bas:1669)
            BroadcastFX(vic.Pos.Map, vic.Char.CharIndex, FX_GOLPE_FALLO, 0);  // FX de fallo sobre la víctima (todos lo ven)
            if (vic.Conn != null) ServerPackets.ConsoleMsg(vic.Conn, $"¡{atk.Name} falló su golpe!", 1);
            return;
        }

        BroadcastFX(vic.Pos.Map, vic.Char.CharIndex, FX_GOLPE_ACIERTO, 0);  // FX de acierto sobre la víctima (todos lo ven)

        // Combat state: la víctima fue impactada → cancelar runa/meditación (UsuarioAtacadoPorUsuario).
        UsuarioAtacadoPorUsuario(atkIdx, vicIdx);

        // Calcular daño base (PvP: MinHIT/MaxHITPVP del arma).
        int dano = CalcularDanio(atk);
        int danoBasePvp = dano;

        // VB6: apuñalamiento (Asesino/Ladrón con daga). Reemplaza el daño base.
        bool apunalo = PuedeApunalar(atk);
        if (apunalo) { dano = DanoApunalamiento(atk, dano); danoBasePvp = dano; }

        // Veneno por arma/munición (UserEnvenena).
        UserEnvenena(atk, vic);

        // Daño extra por barco/montura del atacante; defensa extra del barco/montura de la víctima
        // (Userda�oUser:2134-2153).
        if (atk.flags.Navegando && atk.Invent.BarcoObjIndex > 0)
        { var bo = ObjData.Get(atk.Invent.BarcoObjIndex); dano += RangoOMin(bo.MinHIT, bo.MaxHIT); }
        if (atk.flags.Montando != 0 && atk.Invent.MonturaObjIndex > 0)
        { var mo = ObjData.Get(atk.Invent.MonturaObjIndex); dano += RangoOMin(mo.MinHIT, mo.MaxHIT); }
        int defExtra = 0;
        if (vic.flags.Navegando && vic.Invent.BarcoObjIndex > 0)
        { var bo = ObjData.Get(vic.Invent.BarcoObjIndex); defExtra += RangoOMin(bo.MinDef, bo.MaxDef); }
        if (vic.flags.Montando != 0 && vic.Invent.MonturaObjIndex > 0)
        { var mo = ObjData.Get(vic.Invent.MonturaObjIndex); defExtra += RangoOMin(mo.MinDef, mo.MaxDef); }

        // --- Armadura por parte del cuerpo (Userda�oUser): cabeza→casco, brazos→armadura×0.7,
        //     torso/piernas→armadura(+escudo). En PvP la armadura solo reduce el 25% (PORC_ARM_PVP). ---
        int lugar = _rng.Next(1, 7); // 1..6
        int absorbido = 0;
        if (lugar == 1 && vic.Invent.CascoEqpObjIndex > 0)               // bCabeza
        {
            var c = ObjData.Get(vic.Invent.CascoEqpObjIndex);
            absorbido = RangoOMin(c.MinDef, c.MaxDef);
        }
        else if ((lugar == 4 || lugar == 5) && vic.Invent.ArmourEqpObjIndex > 0) // brazos → 70% armadura
        {
            var a = ObjData.Get(vic.Invent.ArmourEqpObjIndex);
            absorbido = (int)(RangoOMin(a.MinDef, a.MaxDef) * 0.7);
        }
        else if (vic.Invent.ArmourEqpObjIndex > 0)                       // torso/piernas → armadura(+escudo)
        {
            var a = ObjData.Get(vic.Invent.ArmourEqpObjIndex);
            if (vic.Invent.EscudoEqpObjIndex > 0)
            {
                var e = ObjData.Get(vic.Invent.EscudoEqpObjIndex);
                absorbido = RangoOMin(a.MinDef + e.MinDef, a.MaxDef + e.MaxDef);
            }
            else absorbido = RangoOMin(a.MinDef, a.MaxDef);
        }
        absorbido += defExtra; // sumar defensa de barco/montura de la víctima
        var ccPvp = BalanceData.Combate;
        if (absorbido > 0)
            dano -= (int)(absorbido * ccPvp.ArmaduraDefiendePvP);
        int danoPvpMin = ccPvp.DanoMinimoPvP;
        if (dano < danoPvpMin) dano = danoPvpMin;

        // Multiplicador de daño por raza (clamp 0.5-1.5) + tope = danoBase * TopeBurstPvP.
        double multRaza = BalanceData.RazaDanoPvp(atk.raza);
        if (multRaza != 1) dano = (int)(dano * multRaza);
        if (dano < danoPvpMin) dano = danoPvpMin;
        int maxDano = Math.Max(danoPvpMin, (int)(danoBasePvp * ccPvp.TopeBurstPvP));
        if (dano > maxDano) dano = maxDano;

        // Magos no hacen daño físico con báculos (Userda�oUser:2253).
        if (atk.Clase == 2 && EsStaff(atk.Invent.WeaponEqpObjIndex)) dano = 0;

        // VB6 (UsuarioAtacaUsuario:1539): proyectil visual + sonido según arma. Arco con munición →
        // flecha animada + Snd1 + FX(Snd2) + IMPACTO3; arrojadiza → arma animada + 68; resto → IMPACTO.
        ProyectilSonidoPvp(atk, vic);

        vic.Stats.MinHP = (short)Math.Max(0, vic.Stats.MinHP - dano);
        ServerPackets.UpdateHP(vic.Conn, vic.Stats.MinHP);
        if (apunalo)
        {
            ServerPackets.ConsoleMsg(atk.Conn, $"¡Has apuñalado a {vic.Name} por {dano}!", 2); // font 2 = rojo + tab Combate
            ServerPackets.ConsoleMsg(vic.Conn, $"¡{atk.Name} te ha apuñalado por {dano}!", 2);
            BroadcastFX(vic.Pos.Map, vic.Char.CharIndex, FX_APUNALAR, 0);          // FX/logo de daga sobre el objetivo
        }
        // Número azul sobre la víctima (atacante) y rojo sobre el atacante (víctima), + consola.
        DanoInfligido(atk, vic.Char.CharIndex, dano);
        DanoRecibido(vic, atk.Char.CharIndex, dano);

        // Incineración por orbe (UserIncinera).
        UserIncinera(atk, vic);

        // Parálisis por Orbe Acuática (GolpeParalizaUsuario rama OBJ869).
        GolpeOrbeUsuario(atk, vic);

        // Parálisis de artes marciales (SistemaCombate.bas:2328-2379): SOLO Gladiador(8)/Bardo(6).
        // El Guerrero NO paraliza jugadores, solo NPCs (ver GolpeParalizaNpc).
        // Con nudillos equipados → prob = Wrestling/2 (50% máx); a mano limpia (sin arma ni nudillos)
        // → prob = Wrestling/3 (33% máx). No aplica con arma normal equipada.
        if ((atk.Clase == 8 || atk.Clase == 6) && vic.flags.Paralizado == 0)
        {
            int prob = -1;
            if (atk.Invent.NudiEqpObjIndex > 0) prob = atk.Stats.UserSkills[SK_WRESTLING] / 2;
            else if (atk.Invent.WeaponEqpObjIndex == 0) prob = atk.Stats.UserSkills[SK_WRESTLING] / 3;
            if (prob > 0 && _rng.Next(1, 101) <= prob)
            {
                vic.flags.Paralizado = 1;
                vic.flags.ParalisisExpira = Environment.TickCount64 / 1000.0 + DuracionParalisisUsuario;
                // A diferencia de la parálisis mágica, la parálisis por nudillos SÍ muestra FX sobre el usuario.
                BroadcastFX(vic.Pos.Map, vic.Char.CharIndex, 8, 0);
                if (vic.Conn != null) { ServerPackets.ParalizeOK(vic.Conn); ServerPackets.ConsoleMsg(vic.Conn, $"¡{atk.Name} te ha paralizado!", 4); }
                DifundirParalisisUsuario(vic, DuracionParalisisUsuario);
                ServerPackets.ConsoleMsg(atk.Conn, $"¡Has paralizado a {vic.Name}!", 1);
            }
        }

        if (vic.Stats.MinHP > 0) return;

        // Muerte PVP
        ServerPackets.ConsoleMsg(atk.Conn, $"¡Has matado a {vic.Name}!", 2); // font 2 = rojo + tab Combate
        Facciones.ContarMuerte(vicIdx, atkIdx); // frag por facción (VB6 ContarMuerte)
        UserDie(vicIdx);
    }

    /// <summary>
    /// UserDie (Modulo_UsUaRiOs.bas:1749) 1:1 VB6.
    /// Limpia estados, suelta items en mapa PK, desequipa todo, convierte en fantasma.
    /// </summary>
    public static void UserDie(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.flags.Muerto == 1) return; // ya está muerto: evitar doble drop/desequipo
        u.Stats.MinHP = 0;
        u.flags.Muerto = 1;
        u.flags.MuertesUsuario++;                  // contador de muertes (se ve en stats)
        u.flags.KillStreak = 0;                     // muere → se corta su racha de kills

        // AFK: al morir se limpia el estado de inactividad (quita la partícula 238 si la tenía y
        // reinicia el contador), si no quedaba el flag en true y la partícula no volvía a aparecer.
        if (u.flags.AfkParticula)
        {
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var o = UserListManager.UserList[i];
                if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                    ServerPackets.EfectoCharParticula(o.Conn, u.Char.CharIndex, GameTimer.AFK_PARTICULA, 0f, true);
            }
        }
        u.flags.AfkParticula = false;
        u.flags.LastActivityAt = Environment.TickCount64;

        // Sonido de muerte del usuario (SND_MUERTE_USUARIO=389) y limpieza del diálogo sobre el cadáver.
        BroadcastWaveArea(u.Pos.Map, u.Pos.X, u.Pos.Y, Sounds.MUERTE_USUARIO);
        BroadcastRemoveDialog(u);

        // Aggro (VB6 UserDie:1798): restaura el NPC que lo atacaba a su estado original, libera el loot
        // del NPC que atacaba si era suyo, resetea AtacadoPorNpc/NPCAtacado y suelta los targets (PerdioNpc).
        NpcManager.ResetAggroAlMorir(u);

        // VB6: limpiar estados al morir
        u.flags.Envenenado = 0; u.flags.NivelVeneno = 0;
        u.flags.Incinerado = 0;
        u._timerVeneno = 0; u._timerIncinera = 0;
        if (u.flags.Paralizado == 1 || u.flags.Inmovilizado == 1)
        { u.flags.Paralizado = 0; u.flags.Inmovilizado = 0; u.flags.ParalisisExpira = 0; DifundirParalisisUsuario(u, 0); ServerPackets.ParalizeOK(u.Conn); }
        if (u.flags.Ciego == 1) { u.flags.Ciego = 0; ServerPackets.BlindNoMore(u.Conn); }
        if (u.flags.Estupido == 1) { u.flags.Estupido = 0; u.flags.EstupidezExpira = 0; ServerPackets.DumbNoMore(u.Conn); }
        if (u.flags.Metamorfoseado == 1) RevertirMetamorfosis(userIndex); // restaurar body antes del fantasma
        if (u.flags.TomoPocion) RestaurarAtributos(u);                     // restaurar atributos buffeados/debuffeados
        u.flags.FurorIgneo = false; u.flags.SacrificioImpio = false;       // efectos de guerrero se cortan al morir
        u.flags.ArmaMagicaExpira = 0; u.flags.Maldecido = 0; u.flags.MaldecidoExpira = 0; // efectos temporales se cortan al morir
        if (u.flags.Descansar != 0) u.flags.Descansar = 0;
        if (u.flags.Meditando) { u.flags.Meditando = false; ServerPackets.MeditateToggle(u.Conn); Facciones.QuitarParticulaMeditacion(u); }
        if (u.flags.Trabajando) { u.flags.Trabajando = false; u.flags.WorkSkill = 0; }
        if (u.flags.Oculto == 1 || u.flags.Invisible == 1)
        {
            u.flags.Oculto = 0;
            u.flags.Invisible = 0; u.flags.InvisibleExpira = 0;
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var o = UserListManager.UserList[i];
                if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                    ServerPackets.SetInvisible(o.Conn, u.Char.CharIndex, false);
            }
        }

        // VB6 (InvUsuario.bas:2429): en mapa PK suelta todos los items al piso, EXCEPTO si está
        // parado en un trigger ZONAPELEA (arena) → no caen las cosas. Seguro de resu = fuera de arena.
        var mapData = MapLoader.Get(u.Pos.Map);
        var mapInfo = mapData?.Info;
        // enArena = tile ZONAPELEA O combate de torneo en curso (robusto aunque la tile no esté marcada).
        bool enArena = (mapData != null && mapData.GetTrigger(u.Pos.X, u.Pos.Y) == eTrigger.ZONAPELEA)
                       || TorneoEvento.EstaPeleando(userIndex);
        u.flags.SeguroResu = !enArena;
        // El Collar de Rykan (1601=3/3, 1846=2/3, 1847=1/3) equipado evita que se caigan los ítems
        // al morir y, al final de UserDie, resucita automáticamente consumiendo una carga
        // (VB6 TieneCollarRykan, Modulo_UsUaRiOs.bas:1856/1995 + cadena de cargas pendiente).
        bool collarRykan = u.Invent.MagicIndex is 1601 or 1846 or 1847;
        int slotCollar = collarRykan ? u.Invent.MagicSlot : 0;
        if (mapInfo != null && mapInfo.Pk && !enArena && !collarRykan)
        {
            // Pendiente del Sacrificio (1081=3/3, 1498=2/3, 1499=1/3) equipado: se sacrifica el
            // pendiente (cae SOLO él al piso) y protege el resto del inventario
            // (VB6 TirarTodosLosItems, InvUsuario.bas:2490-2517).
            if (u.Invent.MagicIndex is 1081 or 1498 or 1499)
                TirarSoloPendiente(u);
            else
                TirarTodo(u);
        }

        // VB6: desequipar todo (animaciones + auras; conserva el Collar de Rykan).
        // En un combate de torneo NO se desequipa: el caído conserva su equipo puesto para que
        // siga viéndose equipado (no desnudo) hasta que el evento lo reviva al terminar el combate.
        bool torneoFight = TorneoEvento.EstaPeleando(userIndex);
        if (!torneoFight)
        {
            DesequiparTodo(u);
            // Reenviar el inventario para que la UI no siga mostrando los ítems con el "+" de equipado
            // (DesequiparTodo limpia Equipped en el server pero no notificaba los slots al cliente).
            Inventory.EnviarInventarioCompleto(u);
        }

        // Montura: al morir se desmonta (VB6 UserDie: Montando=0 + WriteMontateToggle).
        if (u.flags.Montando == 1) { u.flags.Montando = 0; ServerPackets.MontateToggle(u.Conn); }

        // Mascotas: al morir el amo, mueren todas sus mascotas (VB6 MuereNpc por cada MascotasIndex).
        NpcManager.LiberarMascotasDe(userIndex);
        u.NroMascotas = 0;
        for (int m = 1; m < u.MascotasCharIndex.Length; m++) u.MascotasCharIndex[m] = 0;

        // Reset de FX persistente sobre el char (VB6: si Loops == INFINITE_LOOPS).
        if (u.Char.Loops == -1) { u.Char.FX = 0; u.Char.Loops = 0; }

        // Apariencia de fantasma. Si muere navegando, la barca fantasma (iFragataFantasmal=87) con
        // cabeza 0, manteniendo Navegando (igual que DoNavega/login con Muerto: Trabajo.bas:194,
        // TCP.bas:260). Si no, el fantasma normal: cuerpo 8, cabeza de muerto (500).
        // En combate de torneo se SALTA: el caído conserva su apariencia equipada (no se vuelve fantasma).
        if (!torneoFight)
        {
            if (u.flags.Navegando)
            {
                u.Char.body = 87;   // iFragataFantasmal
                u.Char.Head = 0;
            }
            else
            {
                u.Char.body = 8;
                u.Char.Head = 500;
            }
            u.Char.WeaponAnim = 0;
            u.Char.ShieldAnim = 0;
            u.Char.CascoAnim = 0;

            int map = u.Pos.Map;
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var o = UserListManager.UserList[i];
                if (o.flags.UserLogged && o.Conn != null && o.Pos.Map == map)
                    ServerPackets.CharacterChange(o.Conn, u.Char.CharIndex, u.Char.body, u.Char.Head,
                        u.Char.heading, 0, 0, 0, 0, 0, 0);
            }
        }
        ServerPackets.UpdateUserStats(u.Conn, u);
        ServerPackets.ConsoleMsg(u.Conn, torneoFight
            ? "¡Has caído en el torneo! Espera el resultado del combate."
            : "¡Has muerto! Busca un sacerdote para resucitar.", 1);

        // ======= Collar de Rykan: resurrección automática + consumo de carga =======
        // VB6 (Modulo_UsUaRiOs.bas:1995): DESPUÉS de morir visualmente, el collar resucita al
        // usuario y se consume. Cadena de cargas: 3/3(1601) → 2/3(1846) → 1/3(1847) → se rompe.
        // En arena no se gasta (ahí revive el propio evento).
        if (collarRykan && !enArena && slotCollar >= 1 && slotCollar <= Constants.MAX_INVENTORY_SLOTS)
        {
            short actual = u.Invent.Object[slotCollar].ObjIndex;
            short siguiente = actual switch { 1601 => (short)1846, 1846 => (short)1847, _ => (short)0 };

            ServerPackets.ConsoleMsg(u.Conn, "¡El Collar de Rykan te ha resucitado!", 1);
            BroadcastChatOverHead(u, "¡El Collar de Rykan brilla con poder divino!");

            // Consumir SOLO UN collar de la ranura equipada (antes se vaciaba el slot entero y se
            // perdían todos los collares apilados). La carga resultante (siguiente) CAE AL PISO como
            // loot; si era la última carga (siguiente=0), se rompe y no cae nada.
            short restantes = (short)(u.Invent.Object[slotCollar].Amount - 1);
            if (restantes > 0)
            {
                // Quedan más collares en la ranura: bajar 1 y mantener la pila equipada (sigue protegiendo).
                u.Invent.Object[slotCollar].Amount = restantes;
                ServerPackets.ChangeInventorySlot(u.Conn, (byte)slotCollar, actual, restantes, true);
            }
            else
            {
                // Era el último collar de la ranura: desequipar y vaciar.
                Inventory.Desequipar(u, (byte)slotCollar);
                u.Char.Anillo_Aura = 0;
                u.Invent.Object[slotCollar].ObjIndex = 0;
                u.Invent.Object[slotCollar].Amount = 0;
                u.Invent.Object[slotCollar].Equipped = false;
                if (u.Invent.NroItems > 0) u.Invent.NroItems--;
                ServerPackets.ChangeInventorySlot(u.Conn, (byte)slotCollar, 0, 0, false);
            }

            if (siguiente > 0 && mapData != null
                && TileLibreParaObj(mapData, u.Pos.X, u.Pos.Y, out int cx, out int cy))
            {
                mapData.FloorObj[cx, cy] = siguiente;
                mapData.FloorAmount[cx, cy] = 1;
                AreaVisibility.ObjectAppeared(u.Pos.Map, cx, cy, siguiente, 1);
                ServerPackets.ConsoleMsg(u.Conn, $"Tu Collar de Rykan se debilita y cae al suelo ({ObjData.Get(siguiente).Name}).", 1);
            }
            else
            {
                ServerPackets.ConsoleMsg(u.Conn, "El Collar de Rykan se ha consumido por completo.", 1);
            }
            Resucitar(userIndex);
        }

        // Evento Arena: si murió dentro de un duelo, resolver el round/match (revive y warpea).
        ArenaEvento.OnUserDeath(userIndex);

        // Torneo: si murió dentro de un combate de torneo, evaluar si su equipo fue eliminado.
        TorneoEvento.OnUserDeath(userIndex);

        // Modo "todos contra todos": si el mapa está en deathmatch, el caído resucita
        // automáticamente con vida completa para seguir peleando (no hace falta sacerdote).
        // Va al final para que el resto de UserDie (drop/desequipo/fantasma) ya se aplicó;
        // pero en mapa TvT no hay drop (no es Pk/arena) así que sólo se reanima al instante.
        if (u.flags.Muerto == 1 && MapasTodosVsTodos.Contains(u.Pos.Map))
            Resucitar(userIndex);
    }

    /// <summary>
    /// Pendiente del Sacrificio (VB6 TirarTodosLosItems, InvUsuario.bas:2501): al morir en mapa PK,
    /// cae al piso SOLO el pendiente equipado (se desequipa y se descuenta) y el resto del
    /// inventario queda protegido.
    /// </summary>
    private static void TirarSoloPendiente(User u)
    {
        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return;
        int slot = u.Invent.MagicSlot;
        if (slot < 1 || slot > Constants.MAX_INVENTORY_SLOTS) return;
        short oi = u.Invent.Object[slot].ObjIndex;
        if (oi <= 0) return;
        if (!TileLibreParaObj(map, u.Pos.X, u.Pos.Y, out int tx, out int ty)) return;

        map.FloorObj[tx, ty] = oi;
        map.FloorAmount[tx, ty] = 1;
        Inventory.QuitarUserInvItem(u, (byte)slot, 1); // desequipa (limpia MagicIndex/aura) y descuenta
        AreaVisibility.ObjectAppeared(u.Pos.Map, tx, ty, oi, 1);
        if (u.Conn != null)
        {
            ServerPackets.ChangeInventorySlot(u.Conn, (byte)slot,
                u.Invent.Object[slot].ObjIndex, u.Invent.Object[slot].Amount, u.Invent.Object[slot].Equipped);
            ServerPackets.ConsoleMsg(u.Conn, "¡El Pendiente del Sacrificio te ha protegido de perder tus items!", 1);
        }
    }

    /// <summary>Texto flotante sobre la cabeza del personaje difundido a todo el mapa (ChatOverHead).</summary>
    private static void BroadcastChatOverHead(User u, string texto)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.ChatOverHead(o.Conn, texto, u.Char.CharIndex, 0);
        }
    }

    /// <summary>VB6 TirarTodo: suelta todos los items del inventario al piso (mapa PK).</summary>
    private static void TirarTodo(User u)
    {
        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return;
        for (byte slot = 1; slot <= Constants.MAX_INVENTORY_SLOTS; slot++)
        {
            ref var item = ref u.Invent.Object[slot];
            if (item.ObjIndex <= 0 || item.Amount <= 0) continue;
            // VB6 (InvUsuario.bas:2570): cae si ItemSeCae (no NoSeCae/llave/barco/montura/runa) Y NO es
            // item newbie, salvo las excepciones 1082/1083/1085/1086 que sí se caen.
            if (!ItemSeCae(item.ObjIndex)) continue;
            if (ObjData.Get(item.ObjIndex).Newbie == 1
                && item.ObjIndex is not (1082 or 1083 or 1085 or 1086)) continue;
            // Buscar tile libre cerca del cadáver.
            if (!TileLibreParaObj(map, u.Pos.X, u.Pos.Y, out int tx, out int ty)) continue;
            // Si estaba equipado, desequipar ANTES de vaciar el slot (VB6 DropObj→QuitarUserInvItem):
            // limpia los punteros de equipo y revierte el EfectoMagico (anillos +skill/+atributo).
            if (item.Equipped) Inventory.Desequipar(u, slot);
            short oi = item.ObjIndex; int amt = item.Amount;
            map.FloorObj[tx, ty] = oi;
            map.FloorAmount[tx, ty] = amt;
            // Vaciar el slot
            item.ObjIndex = 0; item.Amount = 0; item.Equipped = false;
            if (u.Invent.NroItems > 0) u.Invent.NroItems--;
            // Difundir por área y actualizar slot del dueño
            AreaVisibility.ObjectAppeared(u.Pos.Map, tx, ty, oi, amt);
            ServerPackets.ChangeInventorySlot(u.Conn, slot, 0, 0, false);
        }
    }

    /// <summary>VB6 ItemSeCae (InvUsuario.bas:2447): true si el item cae al morir.</summary>
    private static bool ItemSeCae(short objIndex)
    {
        var od = ObjData.Get(objIndex);
        if (od.NoSeCae == 1) return false;
        // No caen: llaves(9), barcos(31), monturas(44), runas(38)
        if (od.Type == ObjType.Llaves || od.Type == ObjType.Barcos
            || od.Type == ObjType.Monturas || od.Type == ObjType.Runa) return false;
        return true;
    }

    /// <summary>UserDie (Modulo_UsUaRiOs.bas:1879): desequipa todas las piezas, limpia las referencias
    /// y las AURAS (Body/Arma/Head/Anillo/Escudo) difundiendo AuraToChar(0,1) para el arma. NO toca el
    /// MagicIndex si es el Collar de Rykan (1601), igual que el VB6.</summary>
    private static void DesequiparTodo(User u)
    {
        var inv = u.Invent;
        if (inv.ArmourEqpSlot > 0) { inv.Object[inv.ArmourEqpSlot].Equipped = false; inv.ArmourEqpObjIndex = 0; inv.ArmourEqpSlot = 0; }
        if (inv.NudiEqpSlot > 0) { inv.Object[inv.NudiEqpSlot].Equipped = false; inv.NudiEqpObjIndex = 0; inv.NudiEqpSlot = 0; }
        if (inv.WeaponEqpSlot > 0) { inv.Object[inv.WeaponEqpSlot].Equipped = false; inv.WeaponEqpObjIndex = 0; inv.WeaponEqpSlot = 0; }
        if (inv.CascoEqpSlot > 0) { inv.Object[inv.CascoEqpSlot].Equipped = false; inv.CascoEqpObjIndex = 0; inv.CascoEqpSlot = 0; }
        if (inv.AnilloEqpSlot > 0) { inv.Object[inv.AnilloEqpSlot].Equipped = false; inv.AnilloEqpObjIndex = 0; inv.AnilloEqpSlot = 0; }
        if (inv.MunicionEqpSlot > 0) { inv.Object[inv.MunicionEqpSlot].Equipped = false; inv.MunicionEqpObjIndex = 0; inv.MunicionEqpSlot = 0; }
        // Items mágicos: NO desequipar si es el Collar de Rykan (1601/1846/1847, se consume después
        // de resucitar). Si no, limpia el aura de anillo. OJO: usar Inventory.Desequipar (no limpiar
        // a mano) para que también revierta el EfectoMagico (anillos +skill/+atributo/sombras).
        if (inv.MagicIndex > 0 && inv.MagicIndex is not (1601 or 1846 or 1847))
        {
            if (inv.MagicSlot >= 1 && inv.MagicSlot <= Constants.MAX_INVENTORY_SLOTS)
                Inventory.Desequipar(u, (byte)inv.MagicSlot);
            inv.MagicIndex = 0; inv.MagicSlot = 0;
        }
        if (inv.EscudoEqpSlot > 0) { inv.Object[inv.EscudoEqpSlot].Equipped = false; inv.EscudoEqpObjIndex = 0; inv.EscudoEqpSlot = 0; }

        // BUG FIX: limpiar y DIFUNDIR la remoción de TODAS las auras al área. Antes solo se difundía la
        // del arma (slot 1) y, si el mapa era PK, los ítems se tiraban antes (slots ya en 0) y el aura
        // de escudo/cuerpo/casco/anillo quedaba pegada en el cliente tras revivir. Slots de aura:
        // 1=arma, 2=cuerpo, 3=escudo, 4=casco, 6=anillo (ver Inventory.SetAura).
        u.Char.Arma_Aura = 0; u.Char.Body_Aura = 0; u.Char.Escudo_Aura = 0;
        u.Char.Head_Aura = 0; u.Char.Anillo_Aura = 0;
        BroadcastAuraArea(u, 0, 1);
        BroadcastAuraArea(u, 0, 2);
        BroadcastAuraArea(u, 0, 3);
        BroadcastAuraArea(u, 0, 4);
        BroadcastAuraArea(u, 0, 6);
    }

    /// <summary>Limpia el diálogo flotante sobre el personaje (VB6 RemoveCharDialog): el cliente borra
    /// el texto al recibir ChatOverHead con cadena vacía.</summary>
    private static void BroadcastRemoveDialog(User u)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.ChatOverHead(o.Conn, "", u.Char.CharIndex, 0);
        }
    }

    /// <summary>Difunde AuraToChar al área (equiv. SendData ToPCArea + PrepareMessageAuraToChar).</summary>
    private static void BroadcastAuraArea(User u, byte aura, byte slot)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.AuraToChar(o.Conn, u.Char.CharIndex, aura, slot);
        }
    }

    /// <summary>
    /// HandleResucitate: revive al jugador (versión núcleo, sin requerir sacerdote).
    /// Restaura HP/apariencia y avisa al mapa.
    /// </summary>
    /// <summary>
    /// Avanza el casteo de resucitar/resurrección. Al completarse revive al objetivo (Resucitar: 20 HP;
    /// Resurrección: vida completa). Se cancela si el objetivo dejó de estar muerto, desconectó o se alejó.
    /// </summary>
    public static void TickResucitar(int userIndex, User u)
    {
        double ahora = Environment.TickCount64 / 1000.0;
        int vm = u.ResucitandoTarget;
        var tgt = (vm > 0 && vm <= UserListManager.LastUser) ? UserListManager.UserList[vm] : null;

        // Cancelaciones: el LANZADOR se movió/murió, o el objetivo es inválido/ya vivo/desconectado/lejos.
        bool cancelar = u.flags.Muerto == 1 || u.Pos.X != u.ResucitandoX || u.Pos.Y != u.ResucitandoY
            || tgt == null || !tgt.flags.UserLogged || tgt.flags.Muerto == 0
            || tgt.Pos.Map != u.Pos.Map
            || Math.Abs(tgt.Pos.X - u.Pos.X) > 10 || Math.Abs(tgt.Pos.Y - u.Pos.Y) > 10;
        if (cancelar)
        {
            CancelarResucitar(u);
            ServerPackets.ConsoleMsg(u.Conn, "El conjuro de resurrección se interrumpió.", 1);
            return;
        }
        if (ahora < u.ResucitandoHasta) return; // todavía casteando

        bool full = u.ResucitandoFull;
        QuitarParticulaResucitar(u);                 // borrar la partícula de casteo del lanzador
        u.ResucitandoHasta = 0; u.ResucitandoTarget = 0;
        Resucitar(vm, full ? -1 : 20);
        ServerPackets.ConsoleMsg(u.Conn, $"Has {(full ? "revivido" : "resucitado")} a {tgt.Name}.", 1);
    }

    /// <summary>Cancela el casteo de resucitar y borra la partícula de casteo del lanzador.</summary>
    public static void CancelarResucitar(User u)
    {
        if (u.ResucitandoHasta <= 0) return;
        QuitarParticulaResucitar(u);
        u.ResucitandoHasta = 0; u.ResucitandoTarget = 0;
    }

    /// <summary>Borra (remove) la partícula 18 de casteo sobre el lanzador.</summary>
    private static void QuitarParticulaResucitar(User u)
        => BroadcastParticulaChar(u.Pos.Map, u.Char.CharIndex, 18, 0, remove: true);

    /// <param name="hpForzado">HP con el que revive; -1 = vida completa (MaxHP).</param>
    public static void Resucitar(int userIndex, int hpForzado = -1)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.flags.Muerto == 0) return;

        u.flags.Muerto = 0;

        // VB6: si estaba meditando al resucitar, cancelar meditación y avisar al cliente
        if (u.flags.Meditando)
        {
            u.flags.Meditando = false;
            ServerPackets.MeditateToggle(u.Conn);
            Facciones.QuitarParticulaMeditacion(u);
        }

        // VB6 DarVida: restaurar HP y apariencia SIN recargar el .chr (no tocar el inventario).
        // hpForzado >= 0 → revive con esa vida (p.ej. Resucitar da sólo 20); -1 → vida completa.
        u.Stats.MinHP = hpForzado >= 0 ? (short)Math.Min(u.Stats.MaxHP, hpForzado) : u.Stats.MaxHP;

        // Body (DarVida, Modulo_UsUaRiOs.bas:74): navegando → body original con cabeza 0;
        // armadura equipada → su Ropaje; SIN armadura → cuerpo desnudo por raza/género
        // (NO OrigChar.body, que guarda el body CON armadura del login → mostraría armadura inexistente).
        if (u.flags.Navegando)
        {
            // Revive navegando → body de la barca vivo (su Ropaje, o 87 por defecto), cabeza 0.
            u.Char.body = u.Invent.BarcoObjIndex > 0
                ? (short)(ObjData.Get(u.Invent.BarcoObjIndex).Ropaje is var r && r > 0 ? r : 87)
                : (u.OrigChar.body != 0 ? u.OrigChar.body : u.Char.body);
            u.Char.Head = 0;
        }
        else if (u.Invent.ArmourEqpObjIndex > 0)
        {
            u.Char.body = (short)ObjData.Get(u.Invent.ArmourEqpObjIndex).Ropaje;
            u.Char.Head = u.OrigChar.Head != 0 ? u.OrigChar.Head : u.Char.Head;
        }
        else
        {
            Inventory.DarCuerpoDesnudo(u);
            u.Char.Head = u.OrigChar.Head != 0 ? u.OrigChar.Head : u.Char.Head;
        }
        // Anims desde el equipo que tenga puesto.
        u.Char.WeaponAnim = u.Invent.WeaponEqpObjIndex > 0 ? (short)ObjData.Get(u.Invent.WeaponEqpObjIndex).WeaponAnim : (short)0;
        u.Char.ShieldAnim = u.Invent.EscudoEqpObjIndex > 0 ? (short)ObjData.Get(u.Invent.EscudoEqpObjIndex).ShieldAnim : (short)0;
        u.Char.CascoAnim  = u.Invent.CascoEqpObjIndex  > 0 ? (short)ObjData.Get(u.Invent.CascoEqpObjIndex).CascoAnim  : (short)0;

        int map = u.Pos.Map;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.Pos.Map == map)
                ServerPackets.CharacterChange(o.Conn, u.Char.CharIndex, u.Char.body, u.Char.Head,
                    u.Char.heading, u.Char.WeaponAnim, u.Char.ShieldAnim, u.Char.CascoAnim, 0, 0, 0);
        }
        ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP);
        // NOTA: el sonido de revivir (204/84) NO se reproduce acá: solo debe sonar cuando te revive
        // el sacerdote (Accion.cs), no en las resurrecciones por hechizo. Acá queda solo la partícula.
        // OJO: el cliente decrementa el alive_counter del grupo POR FRAME (no por ms), y el VB6
        // quita la partícula explícitamente en DarVida (remove=true). Como acá la reanimación es
        // instantánea y no mandamos el remove, un valor alto (3000) la dejaba PEGADA al personaje.
        // Usamos una vida finita corta (100) para que el efecto se vea y auto-expire, igual que sanar.
        BroadcastParticulaChar(u.Pos.Map, u.Char.CharIndex, 22, 100);
        ServerPackets.ConsoleMsg(u.Conn, "¡Has resucitado!", 1);
    }

    /// <summary>
    /// CalcularDanio (versión núcleo): con arma usa MinHIT/MaxHIT del arma; sin arma,
    /// los MinHIT/MaxHIT de los stats del usuario. Mínimo 1.
    /// </summary>
    // Constantes de daño (SistemaCombate.bas / Declares.bas).
    private const int MAXATRIB = 35;                 // MAXATRIBUTOS
    private const int ESPADA_MATADRAGONES = 402, ARCO_CAZADEMONIOS = 666;
    private const byte NPCTYPE_DRAGON = 20, NPCTYPE_DEMONIO = 1;

    private static int RangoOMin(int min, int max)
    {
        if (max < min) max = min;
        return max > 0 ? _rng.Next(min, max + 1) : (min > 0 ? min : 0);
    }

    private static int DanoArma(ObjData.Obj arma, bool pve)
    {
        if (pve) return arma.MaxHITPVE > 0 ? RangoOMin(arma.MinHITPVE, arma.MaxHITPVE) : RangoOMin(arma.MinHIT, arma.MaxHIT);
        return arma.MaxHITPVP > 0 ? RangoOMin(arma.MinHITPVP, arma.MaxHITPVP) : RangoOMin(arma.MinHIT, arma.MaxHIT);
    }

    /// <summary>
    /// CalcularDanio (SistemaCombate.bas:344). PvP (npc=null) usa MinHIT/MaxHITPVP del arma; PvE usa PVE
    /// (fallback a MinHIT/MaxHIT). + ExtraHIT (metamorfosis), mínimo 1, armas especiales (×50 a dragones /
    /// ×5 a demonios en PvE) y +7% si Fuerza y Agilidad están al máximo (35).
    /// </summary>
    private static int CalcularDanio(User u, NpcManager.NpcInstance npc = null)
    {
        bool pve = npc != null;
        int danoBase;
        // Arma mágica activa: el golpe cuerpo a cuerpo hace 120-170 (arma común invocada), ignora el arma real.
        if (u.flags.ArmaMagicaExpira > Environment.TickCount64 / 1000.0)
        {
            danoBase = _rng.Next(120, 171);
            if (u.Stats.ExtraHIT > 0) danoBase += u.Stats.ExtraHIT;
            return danoBase;
        }
        // Munición: si el arma es un arco (Proyectil=1) y hay una flecha equipada, el daño lo define
        // SÓLO la flecha (el arco no interfiere). Así "Flecha Paralizante", "Flecha de Plata", etc.
        // pegan exactamente su MinHIT/MaxHIT y cambiar de arco no altera el daño del disparo.
        if (u.Invent.WeaponEqpObjIndex > 0 && u.Invent.MunicionEqpObjIndex > 0
            && ObjData.Get(u.Invent.WeaponEqpObjIndex).Proyectil == 1)
            danoBase = DanoArma(ObjData.Get(u.Invent.MunicionEqpObjIndex), pve);
        else if (u.Invent.WeaponEqpObjIndex > 0) danoBase = DanoArma(ObjData.Get(u.Invent.WeaponEqpObjIndex), pve);
        else if (u.Invent.NudiEqpObjIndex > 0) danoBase = DanoArma(ObjData.Get(u.Invent.NudiEqpObjIndex), pve);
        else danoBase = RangoOMin(u.Stats.MinHIT, u.Stats.MaxHIT);

        if (u.Stats.ExtraHIT > 0) danoBase += u.Stats.ExtraHIT;
        // Brazalete del Ogro (707, AumentaGolpe(6)): suma CuantoAumento al daño del golpe.
        danoBase += Inventory.CuantoEfectoMagico(u, 6);
        if (danoBase < 1) danoBase = 1;

        if (pve)
        {
            int armaIdx = u.Invent.WeaponEqpObjIndex > 0 ? u.Invent.WeaponEqpObjIndex
                        : (u.Invent.NudiEqpObjIndex > 0 ? u.Invent.NudiEqpObjIndex : 0);
            if (armaIdx == ESPADA_MATADRAGONES && npc.NpcType == NPCTYPE_DRAGON) danoBase *= 50;
            else if (armaIdx == ARCO_CAZADEMONIOS && npc.NpcType == NPCTYPE_DEMONIO) danoBase *= 5;
        }

        if (u.Stats.UserAtributos[1] >= MAXATRIB && u.Stats.UserAtributos[2] >= MAXATRIB)
            danoBase += (int)(danoBase * BalanceData.Combate.BonusStatsMax);

        return danoBase;
    }

    /// <summary>
    /// CalcularDarExp (SistemaCombate.bas:3158): experiencia proporcional al daño infligido al NPC,
    /// tomada de su pool ExpCount (= GiveEXP). Aplica multiplicador de evento y reparto por party
    /// (mismo mapa, distancia ≤ 15). Reemplaza dar toda la exp al matar (evita doble conteo). 1:1 VB6.
    /// </summary>
    private static void CalcularDarExp(int userIndex, NpcManager.NpcInstance npc, int elDano)
    {
        if (elDano <= 0 || npc.MaxHP <= 0 || npc.ExpCount <= 0) return;
        if (elDano > npc.MinHP) elDano = Math.Max(0, npc.MinHP);

        int expaDar = (int)((long)elDano * npc.GiveEXP / npc.MaxHP);
        if (expaDar <= 0) return;
        if (expaDar > npc.ExpCount) { expaDar = npc.ExpCount; npc.ExpCount = 0; }
        else npc.ExpCount -= expaDar;

        expaDar *= Math.Max(1, Events.ExpMultiplicador);

        var u = UserListManager.UserList[userIndex];
        // Boost de exp personal del Battle Pass (encima del multiplicador global de evento).
        double bpExpMult = BattlePass.ExpMult(u);
        if (bpExpMult > 1.0) expaDar = (int)(expaDar * bpExpMult);
        var receptores = new List<User>();
        if (u.PartyId != 0)
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var m = UserListManager.UserList[i];
                if (m.flags.UserLogged && m.PartyId == u.PartyId && m.flags.Muerto == 0 && m.Pos.Map == npc.Map
                    && Math.Abs(m.Pos.X - npc.X) <= 15 && Math.Abs(m.Pos.Y - npc.Y) <= 15)
                    receptores.Add(m);
            }
        if (receptores.Count == 0) receptores.Add(u);

        int cada = Math.Max(1, expaDar / receptores.Count);
        foreach (var m in receptores)
        {
            m.Stats.Exp += cada;
            // Mensaje de experiencia: font 13 (celeste, locale_smg 140 del VB6) → pestaña Combate.
            if (m.Conn != null) { ServerPackets.ConsoleMsg(m.Conn, $"Has ganado {cada} puntos de experiencia.", 13); ServerPackets.UpdateExp(m.Conn, (int)m.Stats.Exp); }
            CheckUserLevel(m);
        }
    }

    // ===================== EVASIÓN / PROBABILIDAD DE IMPACTO (SistemaCombate.bas) =====================
    // eSkill: Tacticas=1, Armas=2, Wrestling=3, ArmasArrojadizas=5, Proyectiles=6, Defensa=7.
    // eAtributos: Agilidad=2.
    private const int SK_TACTICAS = 1, SK_ARMAS = 2, SK_WRESTLING = 3, SK_ARROJADIZAS = 5,
                      SK_PROYECTILES = 6, SK_DEFENSA = 7, AT_AGILIDAD = 2;

    /// <summary>Cálculo escalonado común de poder de ataque (skill &lt;31/&lt;61/&lt;91/else con bonus de agilidad).</summary>
    private static long PoderAtaqueBase(User u, int skill, double modClase)
    {
        int sk = u.Stats.UserSkills[skill];
        int ag = u.Stats.UserAtributos[AT_AGILIDAD];
        double t = sk < 31 ? sk : sk < 61 ? sk + ag : sk < 91 ? sk + 2 * ag : sk + 3 * ag;
        var cc = BalanceData.Combate;
        return (long)(t * modClase + cc.PesoNivel * Math.Max(u.Stats.ELV - cc.NivelBase, 0));
    }

    private static long PoderAtaqueArma(User u)      => PoderAtaqueBase(u, SK_ARMAS,       BalanceData.Get(u.Clase).AtaqueArmas);
    private static long PoderAtaqueProyectil(User u) => PoderAtaqueBase(u, SK_PROYECTILES, BalanceData.Get(u.Clase).AtaqueProyectiles);
    private static long PoderAtaqueArpon(User u)     => PoderAtaqueBase(u, SK_ARROJADIZAS, BalanceData.Get(u.Clase).AtaqueArpon);
    // VB6 PoderAtaqueWrestling usa el multiplicador AtaqueArmas (SistemaCombate.bas:226).
    private static long PoderAtaqueWrestling(User u) => PoderAtaqueBase(u, SK_WRESTLING,   BalanceData.Get(u.Clase).AtaqueArmas);

    /// <summary>PoderEvasion (SistemaCombate.bas:139): (Tacticas + Tacticas/33*Agi)*ModEvasion + nivel, ×0.5.</summary>
    private static long PoderEvasion(User u)
    {
        var mc = BalanceData.Get(u.Clase);
        int tac = u.Stats.UserSkills[SK_TACTICAS];
        int ag = u.Stats.UserAtributos[AT_AGILIDAD];
        double lTemp = (tac + tac / 33.0 * ag) * mc.Evasion;
        var cc = BalanceData.Combate;
        return (long)((lTemp + cc.PesoNivel * Math.Max(u.Stats.ELV - cc.NivelBase, 0)) * 0.5);
    }

    /// <summary>PoderEvasionEscudo (SistemaCombate.bas:121): SkillDefensa * ModEscudo * 2.</summary>
    private static long PoderEvasionEscudo(User u) => (long)(u.Stats.UserSkills[SK_DEFENSA] * BalanceData.Get(u.Clase).Escudo * 2);

    /// <summary>Selecciona poder de ataque + skill según el arma equipada (nudillos/proyectil/arpón/arma/puños).</summary>
    private static (long poder, int skill) PoderAtaqueUsuario(User u)
    {
        if (u.Invent.NudiEqpObjIndex > 0) return (PoderAtaqueWrestling(u), SK_WRESTLING);
        short arma = u.Invent.WeaponEqpObjIndex;
        if (arma > 0)
        {
            int proy = ObjData.Get(arma).Proyectil;
            if (proy == 1) return (PoderAtaqueProyectil(u), SK_PROYECTILES);
            if (proy == 2) return (PoderAtaqueArpon(u), SK_ARROJADIZAS);
            return (PoderAtaqueArma(u), SK_ARMAS);
        }
        return (PoderAtaqueWrestling(u), SK_WRESTLING);
    }

    /// <summary>UserImpactoNpc (SistemaCombate.bas:239): ¿el usuario impacta al NPC? Sube skill al acertar.</summary>
    public static bool UserImpactoNpc(int userIndex, NpcManager.NpcInstance npc)
    {
        var u = UserListManager.UserList[userIndex];
        var (poder, skill) = PoderAtaqueUsuario(u);
        var cc = BalanceData.Combate;
        long prob = Math.Max(cc.ImpactoMin, Math.Min(cc.ImpactoMax, cc.ImpactoBase + (poder - npc.PoderEvasion)));
        if (u.flags.SacrificioImpio) { prob = 100; u.flags.SacrificioImpio = false; ServerPackets.ConsoleMsg(u.Conn, "¡Tu Sacrificio Impío guía tu golpe!", 1); }
        bool hit = _rng.Next(1, 101) <= prob;
        if (hit) Skills.SubirSkill(userIndex, skill);
        return hit;
    }

    /// <summary>UsuarioImpacto (SistemaCombate.bas:1378): ¿el atacante impacta a la víctima (PvP)? Escudo bloquea.</summary>
    public static bool UsuarioImpacto(int atkIdx, int vicIdx)
    {
        var atk = UserListManager.UserList[atkIdx];
        var vic = UserListManager.UserList[vicIdx];
        long evas = PoderEvasion(vic);
        if (vic.Invent.EscudoEqpObjIndex > 0) evas += PoderEvasionEscudo(vic);

        var (poder, skill) = PoderAtaqueUsuario(atk);
        var cc = BalanceData.Combate;
        long prob = Math.Max(cc.ImpactoMin, Math.Min(cc.ImpactoMax, cc.ImpactoBase + (poder - evas)));
        if (vic.flags.Meditando) { long pe = (long)((100 - prob) * 0.75); prob = Math.Min(cc.ImpactoMax, 100 - pe); }
        if (atk.flags.SacrificioImpio) { prob = 100; atk.flags.SacrificioImpio = false; ServerPackets.ConsoleMsg(atk.Conn, "¡Tu Sacrificio Impío guía tu golpe!", 1); }

        bool hit = _rng.Next(1, 101) <= prob;
        if (hit) { Skills.SubirSkill(atkIdx, skill); return true; }

        // Falló: si la víctima tiene escudo, puede rechazar el golpe (ProbRechazo) y sube Defensa.
        if (vic.Invent.EscudoEqpObjIndex > 0)
        {
            int sd = vic.Stats.UserSkills[SK_DEFENSA], st = vic.Stats.UserSkills[SK_TACTICAS];
            if (sd + st > 0)
            {
                int prRech = Math.Max(25, Math.Min(99, 100 * sd / (sd + st)));
                if (_rng.Next(1, 101) <= prRech)
                {
                    const short SND_ESCUDO = 37;
                    if (vic.Conn != null) { ServerPackets.PlayWave(vic.Conn, SND_ESCUDO, (byte)vic.Pos.X, (byte)vic.Pos.Y); ServerPackets.ConsoleMsg(vic.Conn, $"¡Has rechazado el ataque de {atk.Name} con tu escudo!", 1); }
                    ServerPackets.ConsoleMsg(atk.Conn, $"¡{vic.Name} rechazó tu ataque con su escudo!", 1);
                    Skills.SubirSkill(vicIdx, SK_DEFENSA);
                }
            }
        }
        return false;
    }
}
