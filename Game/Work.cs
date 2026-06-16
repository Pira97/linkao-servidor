using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Trabajo / recolección (talar, pescar, minar). Porta HandleWorkLeftClick (Protocol.bas:3308).
/// VALIDA el recurso real del tile: talar requiere objeto otArboles, minar requiere otYacimiento.
/// Respeta IntervaloUserPuedeTrabajar (700ms). Sube la skill usada.
/// </summary>
public static class Work
{
    // Skills (eSkill): pesca=18, mineria=19, talar=20, botanica=21.
    public const byte SkillPesca = 18, SkillMineria = 19, SkillTalar = 20, SkillBotanica = 21;
    private const byte Pesca = 18, Mineria = 19, Talar = 20, Botanica = 21;
    // Recursos (objindex, Declares.bas).
    private const short Lena = 58, Pescado = 139, Raiz = 888;
    // Clases (eClass) y esfuerzo (stamina) por clase (Declares.bas:352-362).
    private const byte ClPescador = 11, ClLenador = 13, ClMinero = 14, ClDruida = 7;
    private const int EsfPescarPescador = 1, EsfPescarGeneral = 3, EsfTalarLenador = 2, EsfTalarGeneral = 4,
                      EsfExcavarMinero = 2, EsfExcavarGeneral = 5, EsfBotanicaDruida = 2, EsfBotanicaGeneral = 4;

    /// <summary>
    /// DoTrabajar (Trabajo.bas:2899) 1:1 VB6.
    /// Llamado por el GameTimer mientras flags.Trabajando = true.
    /// </summary>
    public static void DoTrabajar(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged || !u.flags.Trabajando) return;

        // VB6: si stamina < 2 → dejar de trabajar
        if (u.Stats.MinSta < 2)
        {
            u.flags.Trabajando = false;
            ServerPackets.ConsoleMsg(u.Conn, "Dejas de trabajar debido a tu poca energía.", 1);
            return;
        }

        // VB6: IntervaloPermiteTrabajar (cooldown ~2s)
        if (!Intervals.PuedeTrabajar(u)) return;

        switch (u.flags.WorkSkill)
        {
            case SkillPesca:    DoPescar(userIndex, u);   break;
            case SkillMineria:  DoMineria(userIndex, u);  break;
            case SkillTalar:    DoTalar(userIndex, u);    break;
            case SkillBotanica: DoBotanica(userIndex, u); break;
        }
    }

    // VB6 DoOcultarse (Trabajo.bas:96) 1:1
    public static void DoOcultarse(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged) return;
        if (u.flags.Oculto == 1) return;            // ya está oculto
        if (u.flags.Navegando)
        { ServerPackets.ConsoleMsg(u.Conn, "No podés ocultarte si estás navegando.", 1); return; }
        if (u.flags.Montando != 0)
        { ServerPackets.ConsoleMsg(u.Conn, "No podés ocultarte si estás montando.", 1); return; }

        int skill = u.Stats.UserSkills[OcultarseSkill];
        // VB6: Suerte = (((0.000002*sk - 0.0002)*sk + 0.0064)*sk + 0.1124) * 100
        double suerte = (((0.000002 * skill - 0.0002) * skill + 0.0064) * skill + 0.1124) * 100;
        int res = Random.Shared.Next(1, 101);

        if (res <= suerte)
        {
            u.flags.Oculto = 1;
            // Difundir SetInvisible(charIndex, true) al área
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var o = UserListManager.UserList[i];
                if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                    ServerPackets.SetInvisible(o.Conn, u.Char.CharIndex, true);
            }
            Skills.SubirSkill(userIndex, OcultarseSkill); // SubirSkill 1:1 (Trabajo.bas:118)
            ServerPackets.ConsoleMsg(u.Conn, "¡Te has ocultado entre las sombras!", 1);
        }
    }

    private const byte OcultarseSkill = 11; // eSkill.Ocultarse
    private const byte RobarSkill = 14;     // eSkill.Robar
    private const byte DomarSkill = 12;     // eSkill.Domar
    private const byte CarismaAtrib = 4;    // eAtributos.Carisma

    /// <summary>
    /// DoDomar (Trabajo.bas:1149) 1:1 VB6. Doma un NPC domable y lo vuelve mascota del usuario.
    /// </summary>
    public static void DoDomarEnTile(int userIndex, byte x, byte y)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged || u.flags.Muerto == 1) return;

        // Buscar NPC en el tile clickeado
        var npc = NpcManager.NpcAt(u.Pos.Map, x, y);
        if (npc == null) { ServerPackets.ConsoleMsg(u.Conn, "No puedes domar eso.", 1); return; }

        // Debe ser domable
        if (npc.Domable <= 0) { ServerPackets.ConsoleMsg(u.Conn, "No puedes domar esa criatura.", 1); return; }
        // Distancia ≤ 2
        if (Math.Abs(u.Pos.X - x) + Math.Abs(u.Pos.Y - y) > 2)
        { ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos.", 1); return; }
        // Ya domada
        if (npc.MaestroUser > 0)
        {
            if (npc.MaestroUser == userIndex) ServerPackets.ConsoleMsg(u.Conn, "Esta criatura ya es tuya.", 1);
            else ServerPackets.ConsoleMsg(u.Conn, "No puedes domar esa criatura.", 1);
            return;
        }
        // Límite de mascotas
        if (u.NroMascotas >= Constants.MAXMASCOTAS)
        { ServerPackets.ConsoleMsg(u.Conn, "No puedes controlar más criaturas.", 1); return; }

        // VB6: puntosDomar = Carisma * skillDomar; éxito si requeridos<=puntos Y Random(1,5)==1
        int puntosDomar = u.Stats.UserAtributos[CarismaAtrib] * u.Stats.UserSkills[DomarSkill];
        if (npc.Domable <= puntosDomar && Random.Shared.Next(1, 6) == 1)
        {
            u.NroMascotas++;
            for (int i = 1; i <= Constants.MAXMASCOTAS; i++)
                if (u.MascotasCharIndex[i] == 0) { u.MascotasCharIndex[i] = npc.CharIndex; break; }
            npc.MaestroUser = userIndex;
            npc.Hostil = false;             // ya no ataca a su amo
            npc.Movement = 8;               // SigueAmo
            ServerPackets.ConsoleMsg(u.Conn, "¡Has domado a la criatura!", 1);
            Skills.SubirSkill(userIndex, DomarSkill); // SubirSkill 1:1 (Trabajo.bas:1222)
        }
        else
        {
            ServerPackets.ConsoleMsg(u.Conn, "No has logrado domar la criatura.", 1);
        }
    }

    // VB6 DoRobar (Trabajo.bas:1797) — núcleo 1:1. Busca víctima en el tile clickeado.
    public static void DoRobarEnTile(int ladronIdx, byte x, byte y)
    {
        var ladron = UserListManager.UserList[ladronIdx];
        if (ladron == null || !ladron.flags.UserLogged || ladron.flags.Muerto == 1) return;

        // El mapa debe ser PK para robar (VB6: If MapInfo(...).Pk)
        var map = MapLoader.Get(ladron.Pos.Map);
        if (map != null && !map.Info.Pk)
        { ServerPackets.ConsoleMsg(ladron.Conn, "No podés robar en zona segura.", 1); return; }

        // Buscar víctima en el tile (alcance 1, VB6)
        int vicIdx = -1;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (i != ladronIdx && t.flags.UserLogged && t.flags.Muerto == 0
                && t.Pos.Map == ladron.Pos.Map && t.Pos.X == x && t.Pos.Y == y)
            { vicIdx = i; break; }
        }
        if (vicIdx < 0) return;

        // Alcance: adyacente (≤1 tile Manhattan)
        var vic = UserListManager.UserList[vicIdx];
        if (Math.Abs(ladron.Pos.X - vic.Pos.X) + Math.Abs(ladron.Pos.Y - vic.Pos.Y) > 1)
        { ServerPackets.ConsoleMsg(ladron.Conn, "Estás demasiado lejos.", 1); return; }

        // VB6 (Protocol.bas:3695): no se puede robar si la víctima o el ladrón están en ZONASEGURA.
        if ((map != null && map.GetTrigger(vic.Pos.X, vic.Pos.Y) == eTrigger.ZONASEGURA)
            || (map != null && map.GetTrigger(ladron.Pos.X, ladron.Pos.Y) == eTrigger.ZONASEGURA))
        { ServerPackets.ConsoleMsg(ladron.Conn, "No puedes robar aquí.", 1); return; }

        DoRobar(ladronIdx, vicIdx, ladron, vic);
    }

    private static void DoRobar(int ladronIdx, int vicIdx, User ladron, User vic)
    {
        // VB6: requiere energía >= 15
        if (ladron.Stats.MinSta < 15)
        { ServerPackets.ConsoleMsg(ladron.Conn, "Estás muy cansado para robar.", 1); return; }
        ladron.Stats.MinSta = (short)Math.Max(0, ladron.Stats.MinSta - 15);
        ServerPackets.UpdateSta(ladron.Conn, ladron.Stats.MinSta);

        // Tabla de suerte por skill (VB6 DoRobar)
        int sk = ladron.Stats.UserSkills[RobarSkill];
        int suerte =
            sk <= 10 ? 35 : sk <= 20 ? 30 : sk <= 30 ? 28 : sk <= 40 ? 24 :
            sk <= 50 ? 22 : sk <= 60 ? 20 : sk <= 70 ? 18 : sk <= 80 ? 15 :
            sk <= 90 ? 10 : sk < 100 ? 7 : 5;
        int res = Random.Shared.Next(1, suerte + 1);

        if (res >= 3) // VB6: éxito si res < 3
        {
            ServerPackets.ConsoleMsg(ladron.Conn, "No has logrado robar nada.", 1);
            return;
        }

        // Robo de oro (ladron=Clase 5: 50k-500k, otros: 10k-50k)
        if (vic.Stats.GLD > 0)
        {
            bool esLadron = ladron.Clase == 5;
            long n = esLadron ? Random.Shared.Next(50000, 500001) : Random.Shared.Next(10000, 50001);
            if (n > vic.Stats.GLD) n = vic.Stats.GLD;
            vic.Stats.GLD -= (int)n;
            ladron.Stats.GLD += (int)n;
            ServerPackets.ConsoleMsg(ladron.Conn, $"Le has robado {n} monedas de oro a {vic.Name}.", 1);
            ServerPackets.ConsoleMsg(vic.Conn, $"{ladron.Name} te ha robado {n} monedas de oro.", 4);
            ServerPackets.UpdateGold(ladron.Conn, ladron.Stats.GLD);
            ServerPackets.UpdateGold(vic.Conn, vic.Stats.GLD);
            Skills.SubirSkill(ladronIdx, RobarSkill); // SubirSkill 1:1 (Trabajo.bas:1920)
        }
        else
        {
            ServerPackets.ConsoleMsg(ladron.Conn, $"{vic.Name} no tiene oro.", 1);
        }
    }

    // Minerales crudos (iMinerales, Declares.bas:123) y minerales que pide cada lingote
    // (MineralesParaLingote, Trabajo.bas:891).
    private const short HierroCrudo = 192, PlataCruda = 193, OroCrudo = 194;
    private static int MineralesParaLingote(short obji) => obji switch
    {
        HierroCrudo => 14,
        PlataCruda => 20,
        OroCrudo => 35,
        _ => 10000,
    };

    /// <summary>
    /// FundirMetal (HandleWorkLeftClick, Protocol.bas:3755): valida que (x,y) sea una fragua cercana
    /// y que el slot marcado (Lingoteando) tenga minerales, y funde. LookatTile ya fue llamado antes.
    /// </summary>
    public static void FundirMetal(int userIndex, byte x, byte y)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged) return;

        // Intervalo de trabajo (VB6: IntervaloPermiteTrabajar)
        if (!Intervals.PuedeTrabajar(u)) return;

        // Rango ≤ 2 (Manhattan)
        if (Math.Abs(u.Pos.X - x) + Math.Abs(u.Pos.Y - y) > 2)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos.", 1); // msg 8
            u.flags.Lingoteando = 0;
            return;
        }

        // ¿Hay una fragua en el tile?
        var map = MapLoader.Get(u.Pos.Map);
        short tileObj = (map != null && x >= 1 && x <= 100 && y >= 1 && y <= 100) ? map.FloorObj[x, y] : (short)0;
        if (tileObj <= 0 || ObjData.Get(tileObj).Type != ObjType.Fragua)
        {
            ServerPackets.ConsoleMsg(u.Conn, "¡Ahí no hay ninguna fragua!", 1); // msg 402
            u.flags.Lingoteando = 0;
            return;
        }

        // Verificar el mineral seleccionado (slot guardado en Lingoteando).
        int slot = u.flags.Lingoteando;
        if (slot > 0 && slot <= Constants.MAX_INVENTORY_SLOTS
            && u.Invent.Object[slot].ObjIndex > 0
            && ObjData.Get(u.Invent.Object[slot].ObjIndex).Type == ObjType.Minerales)
        {
            u.flags.Trabajando = true;
            DoLingotes(userIndex);
            u.flags.Lingoteando = 0;
        }
        else
        {
            ServerPackets.ConsoleMsg(u.Conn, "¡No tienes más minerales!", 1); // msg 401 / 195
        }
    }

    /// <summary>DoLingotes (Trabajo.bas:916) 1:1: funde los minerales del slot marcado en lingotes.</summary>
    public static void DoLingotes(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];

        int cantidadItems = Math.Max(1, (u.Stats.ELV - 4) / 5);
        int slot = u.flags.Lingoteando;
        short obji = u.Invent.Object[slot].ObjIndex;

        bool tieneMinerales = false;
        while (cantidadItems > 0 && !tieneMinerales)
        {
            if (u.Invent.Object[slot].Amount >= MineralesParaLingote(obji) * cantidadItems)
                tieneMinerales = true;
            else
                cantidadItems--;
        }

        if (!tieneMinerales || ObjData.Get(obji).Type != ObjType.Minerales)
        {
            ServerPackets.ConsoleMsg(u.Conn, "No tenés suficientes minerales.", 1); // msg 206
            return;
        }

        u.Invent.Object[slot].Amount -= MineralesParaLingote(obji) * cantidadItems;
        if (u.Invent.Object[slot].Amount < 1)
        {
            u.Invent.Object[slot].Amount = 0;
            u.Invent.Object[slot].ObjIndex = 0;
            if (u.Invent.NroItems > 0) u.Invent.NroItems--;
        }

        short lingoteIdx = (short)ObjData.Get(obji).LingoteIndex;
        var miObj = new UserObj { ObjIndex = lingoteIdx, Amount = cantidadItems };
        if (!AddItem(u, miObj))
            DropItemAtPos(u.Pos, miObj);

        var o = u.Invent.Object[slot];
        ServerPackets.ChangeInventorySlot(u.Conn, (byte)slot, o.ObjIndex, o.Amount, o.Equipped);
        SendInvUpdate(u);
        ServerPackets.ConsoleMsg(u.Conn, $"¡Has obtenido {cantidadItems} lingotes!", 1); // msg 207
    }

    // VB6 DoPescar (Trabajo.bas:1627) 1:1
    /// <summary>Suerte de extracción (Trabajo.bas): Int(-0.00125·S² - 0.3·S + 49). Mínimo 1.</summary>
    private static int Suerte(int skill) => Math.Max(1, (int)(-0.00125 * skill * skill - 0.3 * skill + 49));

    /// <summary>PlayWave al área del usuario (ToPCArea).</summary>
    private static void WaveArea(User u, short wave)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.PlayWave(o.Conn, wave, (byte)u.Pos.X, (byte)u.Pos.Y);
        }
    }

    private static void Entregar(User u, short objIndex, int amount)
    {
        var miObj = new UserObj { ObjIndex = objIndex, Amount = amount };
        if (!AddItem(u, miObj)) DropItemAtPos(u.Pos, miObj); else SendInvUpdate(u);
    }

    // DoPescar (Trabajo.bas:1627) 1:1.
    private static void DoPescar(int userIndex, User u)
    {
        WorldPos front = u.Pos; Movement.HeadtoPos(u.Char.heading, ref front);
        var map = MapLoader.Get(u.Pos.Map);
        if (map == null || !map.HasWater(front.X, front.Y))
        { ServerPackets.ConsoleMsg(u.Conn, "No hay agua donde pescar.", 1); u.flags.Trabajando = false; return; }

        WaveArea(u, 14);
        u.Stats.MinSta = (short)Math.Max(0, u.Stats.MinSta - (u.Clase == ClPescador ? EsfPescarPescador : EsfPescarGeneral));
        ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);

        int skill = u.Stats.UserSkills[SkillPesca];
        if (Random.Shared.Next(1, Suerte(skill) + 1) <= 6)
        {
            Entregar(u, Pescado, Random.Shared.Next(10, 31));
            ServerPackets.ConsoleMsg(u.Conn, "¡Has pescado algo!", 1);
            Skills.SubirSkill(userIndex, SkillPesca); // SubirSkill 1:1 (Trabajo.bas:1691)
            BattlePass.OnWork(userIndex);
        }
        else ServerPackets.ConsoleMsg(u.Conn, "¡No has pescado nada!", 1);
    }

    // DoTalar (Trabajo.bas:2135) 1:1.
    private static void DoTalar(int userIndex, User u)
    {
        WorldPos front = u.Pos; Movement.HeadtoPos(u.Char.heading, ref front);
        var map = MapLoader.Get(u.Pos.Map);
        short obj = (map != null && front.X >= 1 && front.X <= 100 && front.Y >= 1 && front.Y <= 100) ? map.FloorObj[front.X, front.Y] : (short)0;
        if (obj <= 0 || ObjData.Get(obj).Type != ObjType.Arboles)
        { ServerPackets.ConsoleMsg(u.Conn, "No hay un árbol para talar.", 1); u.flags.Trabajando = false; return; }

        WaveArea(u, 13);
        u.Stats.MinSta = (short)Math.Max(0, u.Stats.MinSta - (u.Clase == ClLenador ? EsfTalarLenador : EsfTalarGeneral));
        ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);

        int skill = u.Stats.UserSkills[SkillTalar];
        if (Random.Shared.Next(1, Suerte(skill) + 1) <= 6)
        {
            Entregar(u, Lena, Random.Shared.Next(10, 31));
            ServerPackets.ConsoleMsg(u.Conn, "¡Has conseguido algo de leña!", 1);
            Skills.SubirSkill(userIndex, SkillTalar); // SubirSkill 1:1 (Trabajo.bas:2203)
            BattlePass.OnWork(userIndex);
        }
        else ServerPackets.ConsoleMsg(u.Conn, "¡No has obtenido leña!", 1);
    }

    // DoMineria (Trabajo.bas:2281) 1:1. Extrae el MineralIndex del yacimiento al frente.
    private static void DoMineria(int userIndex, User u)
    {
        WorldPos front = u.Pos; Movement.HeadtoPos(u.Char.heading, ref front);
        var map = MapLoader.Get(u.Pos.Map);
        short obj = (map != null && front.X >= 1 && front.X <= 100 && front.Y >= 1 && front.Y <= 100) ? map.FloorObj[front.X, front.Y] : (short)0;
        if (obj <= 0 || ObjData.Get(obj).Type != ObjType.Yacimiento)
        { ServerPackets.ConsoleMsg(u.Conn, "No hay un yacimiento para minar.", 1); u.flags.Trabajando = false; return; }

        WaveArea(u, 15);
        u.Stats.MinSta = (short)Math.Max(0, u.Stats.MinSta - (u.Clase == ClMinero ? EsfExcavarMinero : EsfExcavarGeneral));
        ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);

        int skill = u.Stats.UserSkills[SkillMineria];
        if (Random.Shared.Next(1, Suerte(skill) + 1) <= 5)  // minería usa <=5 (no 6)
        {
            short mineral = (short)ObjData.Get(obj).MineralIndex;
            if (mineral <= 0) return;
            Entregar(u, mineral, Random.Shared.Next(10, 31) * Ruleta.MultiplicadorMineria());
            ServerPackets.ConsoleMsg(u.Conn, "¡Has extraído algunos minerales!", 1);
            Skills.SubirSkill(userIndex, SkillMineria); // SubirSkill 1:1 (Trabajo.bas:2346)
            BattlePass.OnWork(userIndex);
        }
        else ServerPackets.ConsoleMsg(u.Conn, "¡No has conseguido nada!", 1);
    }

    // DoBotanica (Trabajo.bas:2224) 1:1. Tijeras → raíces.
    private static void DoBotanica(int userIndex, User u)
    {
        u.Stats.MinSta = (short)Math.Max(0, u.Stats.MinSta - (u.Clase == ClDruida ? EsfBotanicaDruida : EsfBotanicaGeneral));
        ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);

        int skill = u.Stats.UserSkills[SkillBotanica];
        if (Random.Shared.Next(1, Suerte(skill) + 1) <= 6)
        {
            int amount = u.Clase == ClDruida ? Random.Shared.Next(1, 7) : Random.Shared.Next(1, 3);
            Entregar(u, Raiz, amount);
            ServerPackets.ConsoleMsg(u.Conn, $"¡Has obtenido raíces! ({amount})", 1);
            BattlePass.OnWork(userIndex);
        }
        else ServerPackets.ConsoleMsg(u.Conn, "¡No has obtenido raíces!", 1);
        Skills.SubirSkill(userIndex, SkillBotanica); // SubirSkill 1:1 (Trabajo.bas:2268, fuera del bloque éxito)
    }

    private static bool AddItem(User u, UserObj item)
    {
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == item.ObjIndex) { u.Invent.Object[s].Amount += item.Amount; return true; }
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == 0) { u.Invent.Object[s] = item; u.Invent.NroItems++; return true; }
        return false;
    }

    private static void DropItemAtPos(WorldPos pos, UserObj item)
    {
        var map = MapLoader.Get(pos.Map);
        if (map != null) { map.FloorObj[pos.X, pos.Y] = item.ObjIndex; map.FloorAmount[pos.X, pos.Y] = item.Amount; }
    }

    private static void SendInvUpdate(User u)
    {
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
        {
            var o = u.Invent.Object[s];
            if (o.ObjIndex > 0) ServerPackets.ChangeInventorySlot(u.Conn, (byte)s, o.ObjIndex, o.Amount, o.Equipped);
        }
    }

    private static int FindSlot(User u, short objIndex)
    {
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == objIndex) return s;
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == 0) return s;
        return 0;
    }
}
