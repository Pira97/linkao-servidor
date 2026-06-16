using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sistema de facciones (ModFacciones.bas + GameLogic.bas). Facción del jugador en
/// User.Faccion.Status (1=Renegado, 2=Ciudadano, 3=Republicano, 4=Caos, 5=Armada, 6=Milicia).
/// Cubre: helpers es*(), conteo de frags por facción (ContarMuerte) y enlistamiento.
/// </summary>
public static class Facciones
{
    public const byte RENEGADO = 1, CIUDADANO = 2, REPUBLICANO = 3, CAOS = 4, ARMADA = 5, MILICIA = 6;
    private const int LIMITE_NEWBIE = 15; // Declares.bas:224

    // GameLogic.bas:17-43
    public static bool EsArmada(User u) => u.Faccion.Status == ARMADA;
    public static bool EsCaos(User u)   => u.Faccion.Status == CAOS;
    public static bool EsMili(User u)   => u.Faccion.Status == MILICIA;
    public static bool EsRene(User u)   => u.Faccion.Status == RENEGADO;
    public static bool EsCiuda(User u)  => u.Faccion.Status == CIUDADANO;
    public static bool EsRepu(User u)   => u.Faccion.Status == REPUBLICANO;
    public static bool EsFaccion(User u) => u.Faccion.Status is CAOS or ARMADA or MILICIA;
    public static bool EsNewbie(User u) => u.Stats.ELV <= LIMITE_NEWBIE;

    public const byte CDUNGEON_NEWBIE = 6; // eCiudad

    /// <summary>Ciudad de la facción del jugador: imperiales (Ciudadano/Armada) → Nix,
    /// republicanos (Republicano/Milicia) → Illiandor, renegados/caos → Rinkel.</summary>
    public static byte CiudadDeFaccion(User u) => u.Faccion.Status switch
    {
        REPUBLICANO or MILICIA => cIlliandor,
        RENEGADO or CAOS => cRinkel,
        _ => cNix, // Ciudadano/Armada (imperiales)
    };

    /// <summary>
    /// Al llegar al nivel 15 el personaje deja de ser newbie: si su hogar sigue siendo el
    /// Dungeon Newbie pasa a la ciudad de su facción, y si está parado dentro del dungeon se
    /// lo manda a esa ciudad. warpear=true usa WarpUser (en juego, tras subir de nivel);
    /// false solo reubica u.Pos (login, antes de que se envíe el mundo al cliente).
    /// </summary>
    public static void SalirDungeonNewbie(User u, bool warpear)
    {
        if (u.Stats.ELV < LimiteNewbie) return;
        if (u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO) return; // GMs pueden quedarse

        byte ciudad = CiudadDeFaccion(u);
        if (u.Hogar == CDUNGEON_NEWBIE) u.Hogar = ciudad;

        var dn = CityData.Get(CDUNGEON_NEWBIE);
        if (dn.Map <= 0 || u.Pos.Map != dn.Map) return;
        var c = CityData.Get(ciudad);
        if (c.Map <= 0) return;

        if (warpear && u.Conn != null)
        {
            Movement.WarpUser(u.Conn.UserIndex, c.Map, c.X, c.Y);
            ServerPackets.ConsoleMsg(u.Conn, "Ya no eres newbie. El Dungeon Newbie te expulsa hacia la ciudad de tu facción.", FONT_INFO);
        }
        else
        {
            u.Pos.Map = c.Map;
            u.Pos.X = c.X;
            u.Pos.Y = c.Y;
        }
    }

    /// <summary>ParticleToLevel (Modulo_UsUaRiOs.bas:3331) 1:1: índice de partícula de meditación según
    /// nivel + facción. GM → 128. cambioStats=true mira el nivel+1 (al subir de nivel).</summary>
    public static int ParticleToLevel(User u, bool cambioStats = false)
    {
        if (u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO) return 128;
        int nivel = u.Stats.ELV + (cambioStats ? 1 : 0);
        int st = u.Faccion.Status;
        if (nivel < 15) return st switch { 1 => 299, 2 => 281, 3 => 289, _ => 42 };
        if (nivel < 30) return st switch { 1 => 300, 2 => 282, 3 => 290, _ => 81 };
        if (nivel < 40) return st switch { 1 => 303, 2 => 283, 3 => 291, 4 => 37, 5 => 38, 6 => 66, _ => 41 };
        if (nivel < 45) return st switch { 1 => 301, 2 => 284, 3 => 292, 4 => 155, 5 => 38, 6 => 66, _ => 41 };
        if (nivel < 50) return st switch { 1 => 302, 2 => 285, 3 => 293, 4 => 298, 5 => 287, 6 => 295, _ => 107 };
        if (nivel == 50) return st switch { 1 => 304, 2 => 286, 3 => 294, 4 => 297, 5 => 288, 6 => 296, _ => 107 };
        return 107;
    }

    /// <summary>Quita la partícula de meditación del char (al interrumpirse la meditación). Difunde
    /// EfectoCharParticula con remove=true usando el mismo ParticleToLevel que se mandó al iniciar.</summary>
    public static void QuitarParticulaMeditacion(User u)
    {
        int p = ParticleToLevel(u);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.EfectoCharParticula(o.Conn, u.Char.CharIndex, (short)p, 0f, true);
        }
    }

    /// <summary>Envía (difunde) la partícula de meditación actual del char (loops infinitos).</summary>
    public static void EnviarParticulaMeditacion(User u)
    {
        int p = ParticleToLevel(u);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.EfectoCharParticula(o.Conn, u.Char.CharIndex, (short)p, -1f, false);
        }
    }

    /// <summary>ParticleToLevel (Modulo_UsUaRiOs.bas): al cambiar de facción/nivel mientras se medita,
    /// la partícula de meditación cambia de índice. Quita la VIEJA (status actual), ejecuta el cambio
    /// y manda la NUEVA, para que no quede pegada la vieja. Si no medita, solo ejecuta el cambio.</summary>
    public static void ConCambioDeFaccion(User u, Action cambio)
    {
        if (!u.flags.Meditando) { cambio(); return; }
        QuitarParticulaMeditacion(u);  // partícula del status viejo
        cambio();
        EnviarParticulaMeditacion(u);  // partícula del status nuevo
    }

    /// <summary>
    /// ContarMuerte (Modulo_UsUaRiOs.bas:2037): suma un frag al atacante según la facción del
    /// muerto. No cuenta si el atacante saca >10 niveles, si el muerto es newbie, está desnudo
    /// o pelean en zona de pelea (trigger). Se llama al matar a un usuario (golpe o magia).
    /// </summary>
    public static void ContarMuerte(int muertoIdx, int atacanteIdx)
    {
        var atacante = UserListManager.UserList[atacanteIdx];
        var muerto = UserListManager.UserList[muertoIdx];
        if (atacante == null || muerto == null) return;

        // Killstreak (racha de kills seguidas): suena a los cercanos del matador. Se reinicia al morir
        // o desconectarse (UserDie/CloseUser). 1=primera sangre, 2=doble, 3=triple, >=7=racha.
        SonarKillstreak(atacante);

        // Evento Cacería por Facción: contar el kill por facción del atacante (VB6: en ContarMuerte).
        CaceriaEvento.SumarKill(atacanteIdx, muertoIdx);

        // El atacante con >10 niveles de ventaja no suma frag (evita farmeo de bajos).
        if (atacante.Stats.ELV > muerto.Stats.ELV + 10)
        {
            if (atacante.Conn != null)
                ServerPackets.ConsoleMsg(atacante.Conn, "El nivel de tu enemigo es muy bajo para sumar una muerte.", 1);
            return;
        }

        if (EsNewbie(muerto)) return;
        // VB6 también excluye muerto Desnudo y TriggerZonaPelea (TRIGGER6); ambos dependen de
        // sistemas aún no portados (flag Desnudo / triggers de zona) → se omiten por ahora.

        // Battle Pass: puntos de pase por kill legítimo en PvP (ya pasó nivel/newbie).
        BattlePass.OnPvpKill(atacanteIdx);

        // Sumar al contador del atacante según la facción de la víctima.
        switch (muerto.Faccion.Status)
        {
            case RENEGADO:    atacante.Faccion.RenegadosMatados++;    break;
            case CIUDADANO:   atacante.Faccion.CiudadanosMatados++;   break;
            case REPUBLICANO: atacante.Faccion.RepublicanosMatados++; break;
            case ARMADA:      atacante.Faccion.ArmadaMatados++;       break;
            case MILICIA:     atacante.Faccion.MilicianosMatados++;   break;
            case CAOS:        atacante.Faccion.CaosMatados++;         break;
        }
    }

    /// <summary>Avanza la racha de kills del matador y difunde el sonido correspondiente a los del mapa
    /// cercanos a su posición (262 1ª, 261 2da, 270 3ra, 175 a partir de la 7ma).</summary>
    private static void SonarKillstreak(User atacante)
    {
        atacante.flags.KillStreak++;
        short snd = atacante.flags.KillStreak switch
        {
            1 => Sounds.FIRST_BLOOD,
            2 => Sounds.DOUBLE_KILL,
            3 => Sounds.TRIPLE_KILL,
            _ => 0,
        };
        if (snd == 0) return;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Conn != null && o.Pos.Map == atacante.Pos.Map)
                ServerPackets.PlayWave(o.Conn, snd, (byte)atacante.Pos.X, (byte)atacante.Pos.Y);
        }
    }

    // --- Constantes (ModFacciones.bas) ---
    private const int PERDON = 100000;        // oro para redimir a un renegado
    private const byte RAZA_GNOMO = 4, RAZA_ENANO = 5;
    // eCiudad: hogar que se asigna al cambiar de facción.
    private const byte cNix = 1, cIlliandor = 2, cLindos = 7, cSURAMEI = 11;
    // eClass (índice → offset de ropa faccionaria).
    private const byte CLERIGO = 1, MAGO = 2, GUERRERO = 3, ASESINO = 4, BARDO = 6, DRUIDA = 7,
                       GLADIADOR = 8, PALADIN = 9, CAZADOR = 10, MERCENARIO = 17, NIGROMANTE = 18;
    private const byte FONT_GUILD = 5, FONT_INFO = 1; // colores de consola

    /// <summary>Difunde CharStatus (color de nombre por facción) a los del mapa. VB6: ToPCArea.</summary>
    public static void BroadcastCharStatus(User u)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.CharStatus(o.Conn, u.Char.CharIndex, LoginFlow.NickStatus(u));
        }
    }

    private static void OverHead(User u, short npcCharIndex, string msg)
    {
        if (u.Conn != null) ServerPackets.ChatOverHead(u.Conn, msg, npcCharIndex, 7);
    }

    /// <summary>EntrarImperial (ModFacciones.bas:73): renegado → ciudadano imperial pagando PERDON.</summary>
    public static void EntrarImperial(User u, short npcCharIndex)
    {
        if (EsCiuda(u)) { OverHead(u, npcCharIndex, "¡No puedo hacerte un ciudadano imperial porque ya lo eres!."); return; }
        if (u.Faccion.Status != RENEGADO)
        { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "No aceptamos seguidores de facciones enemigas, lárgate de aquí.", FONT_INFO); return; }
        if (u.GuildIndex > 0) { OverHead(u, npcCharIndex, "¡Para realizar esta acción no debes pertenecer a ningún clan!."); return; }
        if (u.Stats.GLD <= PERDON)
        { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, $"Necesitas {PERDON} monedas de oro para redimirte.", FONT_INFO); return; }

        u.Stats.GLD -= PERDON;
        u.Faccion.Status = CIUDADANO;
        u.Hogar = cNix;
        BroadcastCharStatus(u);
        if (u.Conn != null) ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
        OverHead(u, npcCharIndex, "Bienvenido al Imperio.");
    }

    /// <summary>EntrarRepublica (ModFacciones.bas:11): renegado → republicano pagando PERDON.</summary>
    public static void EntrarRepublica(User u, short npcCharIndex)
    {
        if (EsRepu(u)) { OverHead(u, npcCharIndex, "¡No puedo hacerte republicano porque ya lo eres!."); return; }
        if (u.Faccion.Status != RENEGADO)
        { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "No aceptamos seguidores de facciones enemigas, lárgate de aquí.", FONT_INFO); return; }
        if (u.GuildIndex > 0) { OverHead(u, npcCharIndex, "¡Para realizar esta acción no debes pertenecer a ningún clan!."); return; }
        if (u.Stats.GLD <= PERDON)
        { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, $"Necesitas {PERDON} monedas de oro para redimirte.", FONT_INFO); return; }

        u.Stats.GLD -= PERDON;
        u.Faccion.Status = REPUBLICANO;
        u.Hogar = u.Pos.Map switch { 194 => cIlliandor, 63 => cLindos, 184 => cSURAMEI, _ => cIlliandor };
        BroadcastCharStatus(u);
        if (u.Conn != null) ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
        OverHead(u, npcCharIndex, "Bienvenido a la República.");
    }

    /// <summary>EnlistarArmadaReal (ModFacciones.bas:341): ciudadano → Armada (15 frags, nivel 25).</summary>
    public static void EnlistarArmadaReal(User u, short npcCharIndex)
    {
        if (EsArmada(u)) { OverHead(u, npcCharIndex, "Ya perteneces a las tropas reales, ve a combatir enemigos."); return; }
        if (u.Faccion.Status != CIUDADANO)
        { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "No aceptamos seguidores de facciones enemigas, lárgate de aquí.", FONT_INFO); return; }
        int matados = u.Faccion.RenegadosMatados + u.Faccion.CaosMatados + u.Faccion.RepublicanosMatados + u.Faccion.MilicianosMatados;
        if (matados < 15) { OverHead(u, npcCharIndex, $"Para unirte a nuestras fuerzas debes matar al menos 15 enemigos, solo has matado {matados}."); return; }
        if (u.Stats.ELV < 25) { OverHead(u, npcCharIndex, "Para unirte a nuestras fuerzas debes ser al menos nivel 25."); return; }

        u.Faccion.Status = ARMADA;
        u.Faccion.Rango = 1;
        byte bajos = EsBajo(u) ? (byte)1 : (byte)0;
        short ropa = u.Clase switch
        {
            CLERIGO => (short)(1544 + bajos), MAGO => (short)(1546 + bajos), GUERRERO => (short)(1548 + bajos),
            ASESINO => (short)(1550 + bajos), BARDO => (short)(1552 + bajos), DRUIDA => (short)(1554 + bajos),
            GLADIADOR => (short)(1556 + bajos), PALADIN => (short)(1558 + bajos), CAZADOR => (short)(1560 + bajos),
            MERCENARIO => (short)(1562 + bajos), NIGROMANTE => (short)(1564 + bajos), _ => 0,
        };
        if (ropa > 0) Inventory.AddItemToInventory(u, ropa, 1);
        BroadcastCharStatus(u);
        OverHead(u, npcCharIndex, "¡Bienvenido a la Armada Real del Imperio! Aquí tienes tus vestimentas.");
        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "Has sido enlistado en la Armada Real del Imperio.", FONT_GUILD);
    }

    /// <summary>EnlistarMilicia (ModFacciones.bas:248): republicano → Milicia (15 frags, nivel 25).</summary>
    public static void EnlistarMilicia(User u, short npcCharIndex)
    {
        if (EsMili(u)) { OverHead(u, npcCharIndex, "Ya perteneces a las tropas milicianas, ve a combatir enemigos."); return; }
        if (u.Faccion.Status != REPUBLICANO)
        { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "No aceptamos seguidores de facciones enemigas, lárgate de aquí.", FONT_INFO); return; }
        int matados = u.Faccion.RenegadosMatados + u.Faccion.CaosMatados + u.Faccion.ArmadaMatados + u.Faccion.CiudadanosMatados;
        if (matados < 15) { OverHead(u, npcCharIndex, $"Para unirte a nuestras fuerzas debes matar al menos 15 enemigos, solo has matado {matados}."); return; }
        if (u.Stats.ELV < 25) { OverHead(u, npcCharIndex, "Para unirte a nuestras fuerzas debes ser al menos nivel 25."); return; }

        u.Faccion.Status = MILICIA;
        u.Faccion.Rango = 1;
        Inventory.AddItemToInventory(u, EsBajo(u) ? (short)1589 : (short)1588, 1);
        BroadcastCharStatus(u);
        OverHead(u, npcCharIndex, "¡Bienvenido a la Milicia Republicana! Aquí tienes tu armadura.");
        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "Has sido enlistado en la Milicia Republicana.", FONT_GUILD);
    }

    /// <summary>EnlistarCaos (ModFacciones.bas:132): renegado → Caos (30 frags, nivel 40).</summary>
    public static void EnlistarCaos(User u, short npcCharIndex)
    {
        if (EsCaos(u)) { OverHead(u, npcCharIndex, "Ya perteneces a la horda del caos, tráeme más almas."); return; }
        if (u.Faccion.Status != RENEGADO)
        { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "No aceptamos seguidores de facciones enemigas, lárgate de aquí.", FONT_INFO); return; }
        int matados = u.Faccion.RenegadosMatados + u.Faccion.ArmadaMatados + u.Faccion.CiudadanosMatados + u.Faccion.MilicianosMatados + u.Faccion.RepublicanosMatados;
        if (matados < 30) { OverHead(u, npcCharIndex, $"Para unirte a nuestras fuerzas debes matar al menos 30 enemigos, solo has matado {matados}."); return; }
        if (u.Stats.ELV < 40) { OverHead(u, npcCharIndex, "Para unirte a nuestras fuerzas debes ser al menos nivel 40."); return; }

        u.Faccion.Status = CAOS;
        u.Faccion.Rango = 1;
        byte bajos = EsBajo(u) ? (byte)1 : (byte)0;
        short ropa = u.Clase switch
        {
            CLERIGO => (short)(1500 + bajos), MAGO => (short)(1502 + bajos), GUERRERO => (short)(1504 + bajos),
            ASESINO => (short)(1506 + bajos), BARDO => (short)(1508 + bajos), DRUIDA => (short)(1510 + bajos),
            GLADIADOR => (short)(1512 + bajos), PALADIN => (short)(1514 + bajos), CAZADOR => (short)(1516 + bajos),
            MERCENARIO => (short)(1518 + bajos), NIGROMANTE => (short)(1520 + bajos), _ => 0,
        };
        if (ropa > 0) Inventory.AddItemToInventory(u, ropa, 1);
        BroadcastCharStatus(u);
        OverHead(u, npcCharIndex, "¡¡¡Bienvenido a las Hordas del Caos!!! Aquí tienes tus vestimentas.");
        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "Has sido enlistado en las Hordas del Caos.", FONT_GUILD);
    }

    private static bool EsBajo(User u) => u.raza == RAZA_ENANO || u.raza == RAZA_GNOMO;

    /// <summary>Entrega la armadura faccionaria correspondiente al jugador (misma tabla que el
    /// enlistado por NPC: Caos 1500+, Armada 1544+, Milicia 1588/1589 según raza baja y clase) y
    /// fija Rango 1. Ciudadano/Republicano/Renegado no tienen armadura propia. Devuelve el ObjIndex
    /// entregado (0 si la facción/clase no tiene set). Usado por el comando GM /darfaccion.</summary>
    public static short DarArmaduraFaccion(User u)
    {
        byte bajos = EsBajo(u) ? (byte)1 : (byte)0;
        short ropa = u.Faccion.Status switch
        {
            CAOS => u.Clase switch
            {
                CLERIGO => (short)(1500 + bajos), MAGO => (short)(1502 + bajos), GUERRERO => (short)(1504 + bajos),
                ASESINO => (short)(1506 + bajos), BARDO => (short)(1508 + bajos), DRUIDA => (short)(1510 + bajos),
                GLADIADOR => (short)(1512 + bajos), PALADIN => (short)(1514 + bajos), CAZADOR => (short)(1516 + bajos),
                MERCENARIO => (short)(1518 + bajos), NIGROMANTE => (short)(1520 + bajos), _ => (short)0,
            },
            ARMADA => u.Clase switch
            {
                CLERIGO => (short)(1544 + bajos), MAGO => (short)(1546 + bajos), GUERRERO => (short)(1548 + bajos),
                ASESINO => (short)(1550 + bajos), BARDO => (short)(1552 + bajos), DRUIDA => (short)(1554 + bajos),
                GLADIADOR => (short)(1556 + bajos), PALADIN => (short)(1558 + bajos), CAZADOR => (short)(1560 + bajos),
                MERCENARIO => (short)(1562 + bajos), NIGROMANTE => (short)(1564 + bajos), _ => (short)0,
            },
            MILICIA => EsBajo(u) ? (short)1589 : (short)1588,
            _ => (short)0,
        };
        if (u.Faccion.Status is CAOS or ARMADA or MILICIA) u.Faccion.Rango = 1;
        if (ropa > 0) Inventory.AddItemToInventory(u, ropa, 1);
        return ropa;
    }

    // --- Tablas de matados requeridos por rango (ModFacciones.bas:895-960) ---
    private static int MatadosArmada(int rango) => rango switch
    { 1 => 20, 2 => 25, 3 => 30, 4 => 35, 5 => 40, 6 => 45, 7 => 50, 8 => 55, 9 => 60, _ => 0 };
    private static int MatadosCaos(int rango) => rango switch
    { 1 => 20, 2 => 30, 3 => 40, 4 => 50, 5 => 60, 6 => 70, 7 => 80, 8 => 90, 9 => 100, _ => 0 };
    private static int MatadosMilicia(int rango) => rango switch
    { 1 => 25, 2 => 30, 3 => 35, 4 => 40, 5 => 45, 6 => 50, _ => 0 };

    /// <summary>RecompensaArmadaReal (ModFacciones.bas:456): sube de rango al re-hablar con el NPC
    /// si juntó los frags del rango; en rango ≥6 da la armadura de Segunda Jerarquía. Máx rango 10.</summary>
    public static void RecompensaArmadaReal(User u, short npcCharIndex)
    {
        if (u.Faccion.Rango == 10) { OverHead(u, npcCharIndex, "Ya alcanzaste el rango más alto aquí."); return; }
        int matados = u.Faccion.RenegadosMatados + u.Faccion.CaosMatados + u.Faccion.MilicianosMatados + u.Faccion.RepublicanosMatados;
        if (matados < MatadosArmada(u.Faccion.Rango))
        { OverHead(u, npcCharIndex, $"Mata {MatadosArmada(u.Faccion.Rango) - matados} criminales más para recibir la próxima recompensa."); return; }

        u.Faccion.Rango++;
        OverHead(u, npcCharIndex, "¡Felicidades! Has subido de rango.");
        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, $"¡Felicidades! Has alcanzado el rango de {TituloReal(u)}.", FONT_GUILD);
        if (u.Faccion.Rango >= 6) // Segunda jerarquía
        {
            byte b = EsBajo(u) ? (byte)1 : (byte)0;
            short ropa = u.Clase switch
            {
                CLERIGO => (short)(1566 + b), MAGO => (short)(1568 + b), GUERRERO => (short)(1570 + b),
                ASESINO => (short)(1572 + b), BARDO => (short)(1574 + b), DRUIDA => (short)(1576 + b),
                GLADIADOR => (short)(1578 + b), PALADIN => (short)(1580 + b), CAZADOR => (short)(1582 + b),
                MERCENARIO => (short)(1584 + b), NIGROMANTE => (short)(1586 + b), _ => 0,
            };
            if (ropa > 0) Inventory.AddItemToInventory(u, ropa, 1);
            if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "Has recibido tu armadura de Segunda Jerarquía por tu lealtad al Imperio.", FONT_GUILD);
        }
    }

    /// <summary>RecompensaCaos (ModFacciones.bas:627): sube rango; en rango ≥6 da armadura de Segunda
    /// Jerarquía del Caos. Máx rango 10.</summary>
    public static void RecompensaCaos(User u, short npcCharIndex)
    {
        if (u.Faccion.Rango == 10) { OverHead(u, npcCharIndex, "Ya alcanzaste el rango más alto aquí."); return; }
        int matados = u.Faccion.RenegadosMatados + u.Faccion.ArmadaMatados + u.Faccion.CiudadanosMatados + u.Faccion.MilicianosMatados + u.Faccion.RepublicanosMatados;
        if (matados < MatadosCaos(u.Faccion.Rango))
        { OverHead(u, npcCharIndex, $"Mata {MatadosCaos(u.Faccion.Rango) - matados} enemigos más para recibir la próxima recompensa."); return; }

        u.Faccion.Rango++;
        OverHead(u, npcCharIndex, "¡Felicidades! Has subido de rango.");
        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, $"¡Felicidades! Has alcanzado el rango de {TituloCaos(u)}.", FONT_GUILD);
        if (u.Faccion.Rango >= 6)
        {
            byte b = EsBajo(u) ? (byte)1 : (byte)0;
            short ropa = u.Clase switch
            {
                CLERIGO => (short)(1522 + b), MAGO => (short)(1524 + b), GUERRERO => (short)(1526 + b),
                ASESINO => (short)(1528 + b), BARDO => (short)(1530 + b), DRUIDA => (short)(1532 + b),
                GLADIADOR => (short)(1534 + b), PALADIN => (short)(1536 + b), CAZADOR => (short)(1538 + b),
                MERCENARIO => (short)(1540 + b), NIGROMANTE => (short)(1542 + b), _ => 0,
            };
            if (ropa > 0) Inventory.AddItemToInventory(u, ropa, 1);
            if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "Has recibido tu armadura de Segunda Jerarquía por tu lealtad a las Hordas del Caos.", FONT_GUILD);
        }
    }

    /// <summary>RecompensaMilicia (ModFacciones.bas:553): sube rango; en rango ≥4 da armadura de
    /// Segunda Jerarquía (depende del tipo de clase). Máx rango 7.</summary>
    public static void RecompensaMilicia(User u, short npcCharIndex)
    {
        if (u.Faccion.Rango == 7) { OverHead(u, npcCharIndex, "Ya alcanzaste el rango más alto aquí."); return; }
        int matados = u.Faccion.RenegadosMatados + u.Faccion.CaosMatados + u.Faccion.ArmadaMatados + u.Faccion.CiudadanosMatados;
        if (matados < MatadosMilicia(u.Faccion.Rango))
        { OverHead(u, npcCharIndex, $"Mata {MatadosMilicia(u.Faccion.Rango) - matados} criminales más para recibir la próxima recompensa."); return; }

        u.Faccion.Rango++;
        OverHead(u, npcCharIndex, "¡Felicidades! Has subido de rango.");
        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, $"¡Felicidades! Has alcanzado el rango de {TituloMilicia(u)}.", FONT_GUILD);
        if (u.Faccion.Rango >= 4)
        {
            byte b = EsBajo(u) ? (byte)1 : (byte)0;
            // Magos/clérigos/bardos/druidas/nigromantes 1592; físicos 1590.
            bool magica = u.Clase is CLERIGO or MAGO or BARDO or DRUIDA or NIGROMANTE;
            short ropa = (short)((magica ? 1592 : 1590) + b);
            Inventory.AddItemToInventory(u, ropa, 1);
            if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "Has recibido tu armadura de Segunda Jerarquía por tu lealtad a la República.", FONT_GUILD);
        }
    }

    // --- Títulos por rango (ModFacciones.bas:820-893) ---
    public static string TituloReal(User u) => u.Faccion.Rango switch
    {
        1 => "Legionario", 2 => "Soldado Real", 3 => "Teniente Real", 4 => "Comandante Real",
        5 => "General Real", 6 => "Elite Real", 7 => "Guardian del Bien", 8 => "Caballero Imperial",
        9 => "Justiciero", 10 => "Guardia Imperial", _ => "",
    };
    public static string TituloCaos(User u) => u.Faccion.Rango switch
    {
        1 => "Miembro de las Hordas", 2 => "Guerrero del Caos", 3 => "Teniente del Caos",
        4 => "Comandante del Caos", 5 => "General del Caos", 6 => "Elite del Caos",
        7 => "Asolador de las Sombras", 8 => "Caballero Negro", 9 => "Emisario de las Sombras",
        10 => "Avatar del Apocalipsis", _ => "",
    };
    public static string TituloMilicia(User u) => u.Faccion.Rango switch
    {
        1 => "Milicia de Reserva", 2 => "Miliciano", 3 => "Miliciano Elite", 4 => "Soldado de la República",
        5 => "Soldado Raso", 6 => "Soldado Elite", 7 => "Comandante de la República", _ => "",
    };

    /// <summary>ExpulsarFaccionReal/Caos/Milicia (ModFacciones.bas:724-819): saca de la facción,
    /// vuelve a la facción base (Armada→Ciudadano, Caos→Renegado, Milicia→Republicano), rango 0,
    /// quita los items faccionarios.</summary>
    public static void ExpulsarFaccion(User u)
    {
        switch (u.Faccion.Status)
        {
            case ARMADA:  u.Faccion.Status = CIUDADANO;   break;
            case CAOS:    u.Faccion.Status = RENEGADO;    break;
            case MILICIA: u.Faccion.Status = REPUBLICANO; break;
            default: return; // no está en facción de guerra
        }
        u.Faccion.Rango = 0;
        QuitarItemsFaccionarios(u);
        BroadcastCharStatus(u);
        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "Has sido expulsado de tu facción.", FONT_INFO);
    }

    /// <summary>QuitarItemsFaccionarios (ModFacciones.bas:1108): quita del inventario los items
    /// con flag Real/Caos/Milicia. Si estaban equipados, limpia el equipo correspondiente.</summary>
    public static void QuitarItemsFaccionarios(User u)
    {
        for (byte i = 1; i <= Constants.MAX_INVENTORY_SLOTS; i++)
        {
            short oi = u.Invent.Object[i].ObjIndex;
            if (oi <= 0) continue;
            var od = ObjData.Get(oi);
            if (od.Real != 1 && od.Caos != 1 && od.Milicia != 1) continue;

            // Si el item está equipado, desequiparlo PRIMERO (resetea body/anim y difunde el cambio,
            // como el VB6 Desequipar) para que no quede vistiéndolo ni defendiendo.
            if (u.Invent.Object[i].Equipped)
                Inventory.Desequipar(u, i);
            // Quitar del inventario (cantidad completa) — QuitarUserInvItem actualiza el slot al cliente.
            Inventory.QuitarUserInvItem(u, i, u.Invent.Object[i].Amount);
        }
    }

    /// <summary>DarFaccion (ModFacciones.bas:962): comando GM, asigna una facción y da los items
    /// correspondientes (Caos/Armada/Milicia por clase; Renegado/Ciudadano/Republicano sin items).</summary>
    public static void DarFaccion(User u, byte faccion)
    {
        u.Faccion.Status = faccion;
        u.Faccion.Rango = 1;
        byte b = EsBajo(u) ? (byte)1 : (byte)0;
        short ropa = 0;
        switch (faccion)
        {
            case CAOS:
                ropa = u.Clase switch
                {
                    CLERIGO => (short)(1500 + b), MAGO => (short)(1502 + b), GUERRERO => (short)(1504 + b),
                    ASESINO => (short)(1506 + b), BARDO => (short)(1508 + b), DRUIDA => (short)(1510 + b),
                    GLADIADOR => (short)(1512 + b), PALADIN => (short)(1514 + b), CAZADOR => (short)(1516 + b),
                    MERCENARIO => (short)(1518 + b), NIGROMANTE => (short)(1520 + b), _ => 0,
                }; break;
            case ARMADA:
                ropa = u.Clase switch
                {
                    CLERIGO => (short)(1544 + b), MAGO => (short)(1546 + b), GUERRERO => (short)(1548 + b),
                    ASESINO => (short)(1550 + b), BARDO => (short)(1552 + b), DRUIDA => (short)(1554 + b),
                    GLADIADOR => (short)(1556 + b), PALADIN => (short)(1558 + b), CAZADOR => (short)(1560 + b),
                    MERCENARIO => (short)(1562 + b), NIGROMANTE => (short)(1564 + b), _ => 0,
                }; break;
            case MILICIA:
                ropa = EsBajo(u) ? (short)1587 : (short)1588; break;
            default: // Renegado/Ciudadano/Republicano: sin items
                u.Faccion.Rango = 0;
                BroadcastCharStatus(u);
                if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, $"Has sido asignado a la facción {faccion}.", FONT_GUILD);
                return;
        }
        if (ropa > 0) Inventory.AddItemToInventory(u, ropa, 1);
        BroadcastCharStatus(u);
    }

    // eCiudad faltantes para el reasignamiento de hogar al retirarse (Declares.bas:174).
    private const byte cUllathorpe = 3, cBanderbill = 4, cRinkel = 5;
    private const byte NPCTYPE_FACCIONES = 5;

    /// <summary>
    /// HandleEnlist (Protocol.bas:7413) 1:1. Valida NPC de facciones seleccionado + distancia ≤4,
    /// y enlista según el Status del NPC (1=Armada, 2=Milicia, 4=Caos).
    /// </summary>
    public static void Enlist(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.TargetNpcCharIndex == 0) { if (u.Conn != null) ServerPackets.LocaleMsg(u.Conn, 22); return; }
        var npc = NpcManager.NpcByCharIndex(u.Pos.Map, u.TargetNpcCharIndex);
        if (npc == null || npc.NpcType != NPCTYPE_FACCIONES || u.flags.Muerto != 0) return;

        // Distancia (Manhattan) > 4 → demasiado lejos.
        if (Math.Abs(u.Pos.X - npc.X) + Math.Abs(u.Pos.Y - npc.Y) > 4)
        { if (u.Conn != null) ServerPackets.LocaleMsg(u.Conn, 8, "", 12, 1); return; }

        // Si medita al enlistarse, refrescar la partícula de meditación (cambia con la facción).
        ConCambioDeFaccion(u, () =>
        {
            switch (npc.Status)
            {
                case 1: EnlistarArmadaReal(u, npc.CharIndex); break;
                case 2: EnlistarMilicia(u, npc.CharIndex);    break;
                case 4: EnlistarCaos(u, npc.CharIndex);       break;
            }
        });
    }

    /// <summary>
    /// HandleRetirarFaccion (Protocol.bas:18197) 1:1. Saca al jugador de su facción y le reasigna
    /// el hogar base. No newbies (ELV≤14), ni renegados, ni miembros de clan.
    /// </summary>
    public static void RetirarFaccion(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.flags.Muerto == 1) return;                       // DeadCheck
        if (u.Stats.ELV <= 14)
        { if (u.Conn != null) ServerPackets.LocaleMsg(u.Conn, 425, LimiteNewbie.ToString()); return; }
        if (EsRene(u) || u.GuildIndex > 0) return;

        // VB6: si está meditando, recalcula la partícula de meditación (cambia con la facción).
        ConCambioDeFaccion(u, () =>
        {
            switch (u.Faccion.Status)
            {
                case MILICIA: // Milicia → Republicano (hogar republicano según mapa actual)
                    ExpulsarFaccion(u);
                    u.Hogar = u.Pos.Map switch { 194 => cIlliandor, 63 => cLindos, 184 => cSURAMEI, _ => cIlliandor };
                    break;
                case CAOS:    // Caos → Renegado (Rinkel)
                    ExpulsarFaccion(u);
                    u.Hogar = cRinkel;
                    break;
                case ARMADA:  // Armada → Imperial/Ciudadano (hogar imperial según mapa actual)
                    ExpulsarFaccion(u);
                    u.Hogar = u.Pos.Map switch { 1 => cUllathorpe, 34 => cNix, 59 => cBanderbill, _ => cUllathorpe };
                    break;
                default:      // Ciudadanos/Republicanos: se vuelven renegados
                    u.Faccion.Status = RENEGADO;
                    u.Hogar = cRinkel;
                    break;
            }
        });
        BroadcastCharStatus(u);
    }

    private const byte LimiteNewbie = 15;
}
