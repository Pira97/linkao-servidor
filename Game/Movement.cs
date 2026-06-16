using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Movimiento de personajes. Portado 1:1 desde GameLogic.bas (HeadtoPos) y
/// Modulo_UsUaRiOs.bas (MoveUserChar / InvertHeading).
///
/// eHeading: NORTH=1, EAST=2, SOUTH=3, WEST=4 (ver [[ao_heading_order]]).
/// </summary>
public static class Movement
{
    public const byte NORTH = 1, EAST = 2, SOUTH = 3, WEST = 4;

    /// <summary>HeadtoPos: avanza una celda desde pos según el heading. 1:1 con GameLogic.bas.</summary>
    public static void HeadtoPos(byte head, ref WorldPos pos)
    {
        short x = pos.X, y = pos.Y, nx = x, ny = y;
        switch (head)
        {
            case NORTH: nx = x;            ny = (short)(y - 1); break;
            case SOUTH: nx = x;            ny = (short)(y + 1); break;
            case EAST:  nx = (short)(x + 1); ny = y;            break;
            case WEST:  nx = (short)(x - 1); ny = y;            break;
        }
        pos.X = nx;
        pos.Y = ny;
    }

    /// <summary>¿Hay un usuario VIVO (no casper) en el tile? (excepto 'salvo'). Bloquea el paso (LegalWalk).</summary>
    private static bool HayUsuarioVivo(int map, int x, int y, int salvo)
    {
        return UsuarioEnTile(map, x, y, salvo, out bool muerto) > 0 && !muerto;
    }

    /// <summary>LegalPos para spawnear al loguear (TCP.bas:200/210): tile en límites, no bloqueado,
    /// agua/tierra acorde a 'esAgua', sin usuario (vivo o casper) ni NPC.</summary>
    public static bool LegalPosLogin(int map, int x, int y, bool esAgua)
    {
        if (x < 1 || x > 100 || y < 1 || y > 100) return false;
        var m = MapLoader.Get(map);
        if (m == null || m.IsBlocked(x, y)) return false;
        if (m.HasWater(x, y) != esAgua) return false;
        if (UsuarioEnTile(map, x, y, 0, out _) > 0) return false;
        if (NpcManager.NpcAt(map, x, y) != null) return false;
        return true;
    }

    /// <summary>ConnectUser (TCP.bas:170-242,283) 1:1: saneamiento de posición al loguear. Mapa
    /// inválido → Intermundia; clamp a los bordes (1..100); anti-telefrag (si el tile está ocupado
    /// por usuario o NPC, busca un tile legal en el 3×3 manteniendo agua/tierra).</summary>
    public static void SanearPosicionLogin(User u)
    {
        // 1) Mapa inválido → Intermundia (cCiudad 15).
        if (MapLoader.Get(u.Pos.Map) == null)
        {
            var ci = CityData.Get(15);
            u.Pos.Map = ci.Map; u.Pos.X = (short)ci.X; u.Pos.Y = (short)ci.Y;
        }

        // 1b) Nadie entra al mundo DENTRO del mapa de arenas: a la arena siempre se llega por warp
        // en vivo, nunca logueando ahí. Si un .chr quedó guardado en la arena (p.ej. el usuario se
        // desconectó en pleno duelo y reconectó antes de que su sesión vieja persistiera la pos de
        // origen), lo mandamos a Intermundia para que no quede atrapado en la arena.
        if (u.Pos.Map == ArenaEvento.ARENA_MAP)
        {
            var ci = CityData.Get(15);
            u.Pos.Map = ci.Map; u.Pos.X = (short)ci.X; u.Pos.Y = (short)ci.Y;
        }

        // 2) Clamp a los bordes del mapa.
        if (u.Pos.X < 1) u.Pos.X = 1; else if (u.Pos.X > 100) u.Pos.X = 100;
        if (u.Pos.Y < 1) u.Pos.Y = 1; else if (u.Pos.Y > 100) u.Pos.Y = 100;

        // 3) Anti-telefrag: si el tile destino tiene usuario o NPC, buscar pos legal en el entorno 3×3.
        bool ocupado = UsuarioEnTile(u.Pos.Map, u.Pos.X, u.Pos.Y, u.id, out _) > 0
                       || NpcManager.NpcAt(u.Pos.Map, u.Pos.X, u.Pos.Y) != null;
        if (!ocupado) return;

        bool esAgua = MapLoader.Get(u.Pos.Map)?.HasWater(u.Pos.X, u.Pos.Y) == true;
        for (int ty = u.Pos.Y - 1; ty <= u.Pos.Y + 1; ty++)
            for (int tx = u.Pos.X - 1; tx <= u.Pos.X + 1; tx++)
                if (LegalPosLogin(u.Pos.Map, tx, ty, esAgua)) { u.Pos.X = (short)tx; u.Pos.Y = (short)ty; return; }
        // Si no hay lugar libre, el VB6 desconecta al ocupante; acá lo dejamos en su pos (el AOI lo resuelve).
    }

    /// <summary>
    /// Devuelve el índice del usuario en el tile (0 si ninguno), e indica si es un casper (muerto).
    /// Equivale a leer MapData(map,x,y).UserIndex del VB6. Los caspers son atravesables.
    /// </summary>
    private static int UsuarioEnTile(int map, int x, int y, int salvo, out bool muerto)
    {
        muerto = false;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            if (i == salvo) continue;
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged
                && o.Pos.Map == map && o.Pos.X == x && o.Pos.Y == y)
            {
                muerto = o.flags.Muerto != 0;
                return i;
            }
        }
        return 0;
    }

    /// <summary>InvertHeading: devuelve el heading opuesto. 1:1 con Modulo_UsUaRiOs.bas.</summary>
    public static byte InvertHeading(byte h) => h switch
    {
        EAST => WEST,
        WEST => EAST,
        SOUTH => NORTH,
        NORTH => SOUTH,
        _ => h,
    };

    /// <summary>
    /// MoveUserChar: mueve al usuario en la dirección dada. Subset 1:1 de
    /// Modulo_UsUaRiOs.bas:1013 (sin montura/runa/empuje, que dependen de más módulos).
    ///
    /// Flujo: calcular destino → validar → si es legal, actualizar Pos + heading y
    /// notificar a los demás con CharacterMove; si no es legal, WritePosUpdate (rebote).
    /// </summary>
    public static void MoveUserChar(int userIndex, byte nHeading)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.Conn == null) return;

        // El heading SIEMPRE se actualiza (igual que el VB6, aunque el movimiento se bloquee).
        u.Char.heading = nHeading;

        WorldPos nPos = u.Pos;
        HeadtoPos(nHeading, ref nPos);

        // Validación contra el mapa real (.csm): tile dentro de límites, no bloqueado y no ocupado.
        // Colisión (MoveToLegalPos): un usuario VIVO o un NPC en el tile destino lo bloquea. Los
        // muertos (caspers) son atravesables. Agua/navegación: PuedeAtravesarAgua (Navegando||Vuela).
        // VB6 MoveToLegalPos(map, x, y, sailing, Not sailing): si navega sólo pisa agua; a pie sólo tierra.
        var map = MapLoader.Get(nPos.Map);
        bool sailing = u.flags.Navegando || u.flags.Vuela == 1;
        bool inBounds = nPos.X >= 1 && nPos.X <= 100 && nPos.Y >= 1 && nPos.Y <= 100;
        bool occMuerto = false;
        int occupant = inBounds ? UsuarioEnTile(nPos.Map, nPos.X, nPos.Y, userIndex, out occMuerto) : 0;
        bool occMuertoF = occupant > 0 && occMuerto;
        bool agua = map != null && map.HasWater(nPos.X, nPos.Y);
        bool puedeMover = inBounds
                          && (map == null || !map.IsBlocked(nPos.X, nPos.Y))
                          && NpcManager.NpcAt(nPos.Map, nPos.X, nPos.Y) == null
                          && (occupant == 0 || occMuertoF)            // vivos bloquean; caspers no
                          && (map == null || (sailing ? agua : !agua)); // agua↔tierra según navegación

        // ¿El tile destino tiene una salida a otro mapa (TileExit)? Se mira ANTES de aplicar el
        // movimiento: el teleport al Dungeon Newbie "patea" a los nivel 15+ (el paso se rechaza
        // y rebota como un tile bloqueado, así nunca quedan parados sobre el teleport).
        var exit = puedeMover ? map?.GetExit(nPos.X, nPos.Y) : null;

        // Primer paso tras un teleport: ignorar el TileExit del tile destino. Así, al entrar a un
        // dungeon caminando, el paso que cae sobre el teleport de retorno NO te rebota afuera. El
        // flag se consume en este movimiento (siguiente paso ya dispara los exits normalmente).
        if (puedeMover && u.RecienTeleportado)
        {
            u.RecienTeleportado = false;
            exit = null;
        }

        if (exit.HasValue
            && exit.Value.DestMap == CityData.Get(Facciones.CDUNGEON_NEWBIE).Map
            && u.Stats.ELV >= 15
            && u.FaccionStatus < AdminLoader.STATUS_CONSEJERO)
        {
            puedeMover = false;
            ServerPackets.ConsoleMsg(u.Conn, "Sólo los personajes de nivel 1 a 14 pueden entrar al Dungeon Newbie.", 1);
        }
        // Salida hacia un mapa deshabilitado: se rechaza el paso (rebota como tile bloqueado) y avisa.
        else if (exit.HasValue
                 && MapasDeshabilitados.Contains(exit.Value.DestMap)
                 && u.FaccionStatus < AdminLoader.STATUS_CONSEJERO)
        {
            puedeMover = false;
            ServerPackets.ConsoleMsg(u.Conn, "Ese mapa está deshabilitado temporalmente.", 1);
        }

        if (puedeMover)
        {
            // VB6 (Modulo_UsUaRiOs.bas:1175): al pisar el tile de un casper, se le envía PosUpdate
            // para resincronizar su cliente ("empuje de casper").
            if (occMuertoF)
            {
                var casper = UserListManager.UserList[occupant];
                if (casper?.Conn != null)
                    ServerPackets.PosUpdate(casper.Conn, (byte)casper.Pos.X, (byte)casper.Pos.Y);
            }

            u.Pos = nPos;
            // Visibilidad por área (AOI server-driven): manda CharacterMove a quienes lo ven, y
            // CharacterCreate/CharacterRemove a los que entran/salen de su área. Reemplaza la difusión
            // por mapa completo (ver MODAREAS_AUDIT.md / AreaVisibility).
            AreaVisibility.OnUserMoved(userIndex);

            // AFK: al moverse se registra actividad y se quita la partícula de AFK si la tenía.
            u.flags.LastActivityAt = Environment.TickCount64;
            if (u.flags.AfkParticula)
            {
                u.flags.AfkParticula = false;
                for (int k = 1; k <= UserListManager.LastUser; k++)
                {
                    var o = UserListManager.UserList[k];
                    if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                        ServerPackets.EfectoCharParticula(o.Conn, u.Char.CharIndex, GameTimer.AFK_PARTICULA, 0f, true);
                }
            }

            // Casteo de resucitar: al moverse se interrumpe (y se borra la partícula de casteo).
            if (u.ResucitandoHasta > 0)
            {
                Combat.CancelarResucitar(u);
                ServerPackets.ConsoleMsg(u.Conn, "El conjuro de resurrección se interrumpió al moverte.", 1);
            }

            // VB6 HandleWalk: al moverse cancela descanso y trabajo
            if (u.flags.Descansar != 0)
            {
                u.flags.Descansar = 0;
                ServerPackets.ConsoleMsg(u.Conn, "Has dejado de descansar.", 1);
            }
            if (u.flags.Trabajando)
            {
                u.flags.Trabajando = false;
                u.flags.Lingoteando = 0;
                u.flags.WorkSkill = 0;
                ServerPackets.ConsoleMsg(u.Conn, "Dejas de trabajar.", 1);
            }
            // VB6 HandleWalk: al moverse se deja de meditar (solo si no está paralizado/inmovilizado)
            if (u.flags.Meditando && u.flags.Paralizado == 0 && u.flags.Inmovilizado == 0)
            {
                u.flags.Meditando = false;
                u.Char.FX = 0;
                u.Char.Loops = 0;
                ServerPackets.MeditateToggle(u.Conn);
                ServerPackets.ConsoleMsg(u.Conn, "Dejas de meditar.", 1);
                Facciones.QuitarParticulaMeditacion(u);
            }
            // VB6: moverse cancela el casteo de la runa de teletransporte.
            if (u.CasteandoRuna > 0)
            {
                u.CasteandoRuna = 0;
                u.RunaSlot = 0;
                ServerPackets.RunaCastProgress(u.Conn, u.Char.CharIndex, 0, 6);
                ServerPackets.ConsoleMsg(u.Conn, "El teletransporte fue interrumpido.", 1);
            }
            // VB6: moverse NO revela el ocultamiento (solo atacar lo hace).

            // Pisó un TileExit (ya validado arriba) → teletransportar.
            if (exit.HasValue)
                WarpUser(userIndex, exit.Value.DestMap, exit.Value.DestX, exit.Value.DestY);
        }
        else
        {
            // Movimiento ilegal: rebotar al cliente a su posición real.
            ServerPackets.PosUpdate(u.Conn, (byte)u.Pos.X, (byte)u.Pos.Y);
        }
    }

    /// <summary>
    /// Teletransporta al usuario a otro mapa/posición (al pisar un TileExit).
    /// Lo quita del mapa viejo, lo crea en el nuevo, y le reenvía el contenido del mapa.
    /// </summary>
    /// <summary>Mapas deshabilitados temporalmente: cualquier intento de entrar patea al jugador
    /// a Intermundia (cCiudad 15) con un aviso. Los GMs/Dioses (Consejero+) pueden entrar igual.</summary>
    public static readonly HashSet<int> MapasDeshabilitados = new() { 839 };

    public static void WarpUser(int userIndex, short destMap, short destX, short destY)
    {
        var u = UserListManager.UserList[userIndex];
        int oldMap = u.Pos.Map;

        // Mapa deshabilitado: NO teletransportar. El jugador se queda donde está y recibe el aviso
        // (los GMs/Dioses pasan). Se le reenvía su posición real para resincronizar el cliente.
        if (MapasDeshabilitados.Contains(destMap) && u.FaccionStatus < AdminLoader.STATUS_CONSEJERO)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Ese mapa está deshabilitado temporalmente.", 1);
            ServerPackets.PosUpdate(u.Conn, (byte)u.Pos.X, (byte)u.Pos.Y);
            return;
        }

        // El warp se concreta: el primer paso posterior no debe disparar otro TileExit (anti-rebote dungeon).
        u.RecienTeleportado = true;

        // Teleport dentro del mismo mapa: reposición ligera, SIN ChangeMap. El ChangeMap haría que el
        // cliente borre todo char_list y recargue el mundo entero → tirón/freeze (típico del telep GM).
        // En su lugar movemos el char propio con PosUpdate y diffeamos sólo la vista de área.
        if (oldMap == destMap)
        {
            u.Pos.X = destX;
            u.Pos.Y = destY;
            ServerPackets.PosUpdate(u.Conn, (byte)destX, (byte)destY);
            AreaVisibility.OnUserTeleportSameMap(userIndex);
            Console.WriteLine($"[ServidorCS] {u.Name} teleport en mapa {destMap} → ({destX},{destY})");
            return;
        }

        // Sacar el PJ de la vista de los observadores del mapa viejo y limpiar sus sets de área.
        AreaVisibility.OnUserLeave(userIndex);

        u.Pos.Map = destMap;
        u.Pos.X = destX;
        u.Pos.Y = destY;

        // Recrear el mundo del nuevo mapa para el cliente (ChangeMap limpia todos los chars en el cliente).
        ServerPackets.ChangeMap(u.Conn, destMap, 0);
        LoginFlow.SendCharCreate(u.Conn, u);                 // su propio PJ en la nueva pos
        // ChangeMap recreó el char propio SIN el estado de invisibilidad → el cliente perdía el alpha.
        // Re-enviar SetInvisible a uno mismo si está oculto (skill) o invisible (hechizo).
        if (u.flags.Oculto == 1 || u.flags.Invisible == 1)
            ServerPackets.SetInvisible(u.Conn, u.Char.CharIndex, true);
        // Las partículas ambientales del nuevo mapa las carga el cliente desde su .csm (no el server).

        // Visibilidad por área en el nuevo mapa: crea los jugadores/NPCs/objetos de su área y lo hace visible a ellos.
        AreaVisibility.OnUserEnter(userIndex);

        // Clima del nuevo mapa (oldMap para el sonido de salir de dungeon hacia la lluvia).
        Clima.EnviarClimaAUsuario(userIndex, oldMap);

        // Ciclo Día/Noche: re-evaluar el flag de dungeon del nuevo mapa.
        DayNightCycle.EnviarAUsuario(userIndex);

        Console.WriteLine($"[ServidorCS] {u.Name} cambió a mapa {destMap} ({destX},{destY})");
    }

}
