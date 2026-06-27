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

    /// <summary>
    /// Rumbos DIAGONALES (18-ago-2026, agregado nuestro — el AO original no los tiene).
    ///
    /// Principio de diseño: <b>una diagonal es un MOVIMIENTO, no una orientación</b>. Sólo
    /// viajan dentro del paquete Walk; <see cref="MoveUserChar"/> mueve en diagonal pero deja
    /// en <c>u.Char.heading</c> un rumbo ORTOGONAL (1..4), que es lo único que se guarda y se
    /// difunde. Así nada del resto del servidor ni de los otros clientes ve jamás un 5..8:
    /// los sprites siguen teniendo 4 vistas, los ataques, los bots y el índice de personajes
    /// siguen razonando con 4 rumbos, y no hay que tocar el protocolo.
    /// </summary>
    public const byte NORTHEAST = 5, SOUTHEAST = 6, SOUTHWEST = 7, NORTHWEST = 8;

    public static bool EsDiagonal(byte h) => h >= NORTHEAST && h <= NORTHWEST;

    /// <summary>Las dos componentes ortogonales de una diagonal (vertical, horizontal).</summary>
    public static (byte v, byte h) Componentes(byte head) => head switch
    {
        NORTHEAST => (NORTH, EAST),
        SOUTHEAST => (SOUTH, EAST),
        SOUTHWEST => (SOUTH, WEST),
        NORTHWEST => (NORTH, WEST),
        _ => (head, head),
    };

    /// <summary>
    /// Ventana del anti-rebote de teleports (ver RecienTeleportado en MoveUserChar). Tiene que
    /// ser bastante MAYOR que el intervalo de paso del cliente (32px / 134px por segundo ≈ 238ms,
    /// o ~193 montado y ~139 volando) para tapar el rebote por tecla mantenida, y bastante MENOR
    /// que lo que tarda una persona en decidir volver a entrar al teleport.
    /// </summary>
    private const int ANTIREBOTE_MS = 400;

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
            // Diagonales (ver NORTHEAST..NORTHWEST). Un solo paso que cambia los dos ejes.
            case NORTHEAST: nx = (short)(x + 1); ny = (short)(y - 1); break;
            case SOUTHEAST: nx = (short)(x + 1); ny = (short)(y + 1); break;
            case SOUTHWEST: nx = (short)(x - 1); ny = (short)(y + 1); break;
            case NORTHWEST: nx = (short)(x - 1); ny = (short)(y - 1); break;
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

        // 1c) Zona con franja de niveles que ya no le corresponde (subió/bajó de nivel afuera, o
        // quedó guardado adentro): a su ciudad. Va ANTES del clamp para que el clamp y el
        // anti-telefrag trabajen sobre la posición final.
        MapasPorNivel.ExpulsarSiNoCorresponde(u, warpear: false);

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

    /// <summary>
    /// Reubica a un GM invisible al tile pisable más cercano (radio creciente 1..3) cuando un
    /// jugador común "camina a través" de él. Sin esto quedaban las dos posiciones pisadas
    /// (el común y el GM) superpuestas hasta que el GM se moviera solo. Si no encuentra tile
    /// libre en el radio, no hace nada (el GM sigue invisible ahí, simplemente sin bloquear).
    /// </summary>
    private static void KickInvisibleGM(int gmIdx, User gm)
    {
        if (gm?.Conn == null) return;
        var map = MapLoader.Get(gm.Pos.Map);
        if (map == null) return;

        for (int r = 1; r <= 3; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // sólo el anillo del radio actual
                int tx = gm.Pos.X + dx, ty = gm.Pos.Y + dy;
                if (tx < 1 || tx > 100 || ty < 1 || ty > 100) continue;
                if (map.IsBlocked(tx, ty)) continue;
                bool agua = map.HasWater(tx, ty);
                if (gm.flags.Navegando ? !agua : agua) continue;
                if (UsuarioEnTile(gm.Pos.Map, tx, ty, 0, out _) != 0) continue;

                gm.Pos.X = (short)tx;
                gm.Pos.Y = (short)ty;
                AreaVisibility.OnUserTeleportSameMap(gmIdx);
                return;
            }
        }
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

    /// <summary>¿Tiene puestas las Alas de Ángel (OBJ2035, ShieldAnim=88)? Da vuelo (Movement.cs)
    /// igual que una montura voladora, pero sin ocupar el slot de montura.</summary>
    private static bool TieneAlasVoladoras(User u)
    {
        int escudo = u.Invent.EscudoEqpObjIndex;
        return escudo > 0 && ObjData.Get(escudo).ShieldAnim == 88;
    }

    /// <summary>¿Vuela ahora mismo (montura voladora O Alas)? Expuesto para Inventory.cs
    /// (no dejar bajarse de las Alas sobre un tile que sería ilegal a pie).</summary>
    public static bool Volando(User u) => u.flags.Vuela == 1 || TieneAlasVoladoras(u);

    /// <summary>BUG-008: dentro de un dungeon, volar (Alas o montura voladora) NO debe dejar
    /// atravesar bloqueos — solo afuera. Mismo criterio que Clima.EsDungeon/DayNightCycle.EsDungeon
    /// (modClima.bas:451): mapa 37 (Dungeon Newbie) o Zona == "DUNGEON".</summary>
    private static bool EsDungeon(int map)
    {
        if (map <= 0) return false;
        if (map == 37) return true;
        var md = MapLoader.Get(map);
        return md != null && string.Equals(md.Info.Zona.Trim(), "DUNGEON", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>¿La posición ACTUAL del usuario sería legal parado a pie (sin volar)? No
    /// bloqueada por el mapa, y agua solo si está navegando. Usada para no dejar desequipar
    /// las Alas en el aire sobre agua/estructura — quedaría atascado ahí.
    /// NO cubre "parado arriba de un techo": el server no tiene cargada la capa 4 (gráficos de
    /// techo, solo la carga el cliente para dibujar) y el campo Trigger de acá NO sirve de proxy
    /// — ZONASEGURA/ANTIPIQUETE/ZONAPELEA (4/5/6) son triggers reales y activos en plazas y
    /// arenas SIN techo (Work.cs, Combat.cs los usan así), así que "Trigger&gt;0 = techo" da
    /// falsos positivos ahí. Ese caso se resuelve en el CLIENTE (game.html), que sí conoce la
    /// posición exacta de los techos (capa 4, mapEntry.roofs) — ver estoyEnTechoLocal().</summary>
    public static bool PosicionLegalAPie(User u)
    {
        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return true;
        if (map.IsBlocked(u.Pos.X, u.Pos.Y)) return false;
        bool agua = map.HasWater(u.Pos.X, u.Pos.Y);
        return u.flags.Navegando ? agua : !agua;
    }

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

        // Rumbo GUARDADO: siempre ortogonal (ver NORTHEAST..NORTHWEST). En una diagonal manda
        // SIEMPRE la componente vertical (N/S), nunca el perfil.
        //
        // Por qué, medido sobre el arte el 18-ago-2026: en un tile el personaje avanza 32 px
        // pero la zancada dibujada es de 8,5 px, así que el pie apoyado resbala. De frente o de
        // espaldas eso es invisible (no hay referencia horizontal contra la que compararlo);
        // de perfil el ojo sigue el pie contra el piso que scrollea y lo ve patinar. La primera
        // versión conservaba el rumbo que el jugador ya traía, y eso dejaba el peor caso
        // posible: moverte en diagonal mostrando el perfil. El heading SIEMPRE se actualiza
        // aunque el paso se bloquee (igual que el VB6).
        byte facing = EsDiagonal(nHeading) ? Componentes(nHeading).v : nHeading;
        u.Char.heading = facing;

        WorldPos nPos = u.Pos;
        HeadtoPos(nHeading, ref nPos);

        // Validación contra el mapa real (.csm): tile dentro de límites, no bloqueado y no ocupado.
        // Colisión (MoveToLegalPos): un usuario VIVO o un NPC en el tile destino lo bloquea. Los
        // muertos (caspers) son atravesables. Navegando (barca): sólo agua (PuedeAtravesarAgua).
        // Vuela (montura voladora o Alas): ignora agua/tierra, paredes/estructuras (IsBlocked) Y
        // los NPCs por completo — a diferencia de Navegando, que sigue exigiendo agua (una barca
        // no vuela sobre tierra). Lo único que NO se saltea volando es otro usuario VIVO parado
        // en el tile (occupant/occMuertoF, más abajo).
        var map = MapLoader.Get(nPos.Map);
        bool sailing = u.flags.Navegando;
        bool inBounds = nPos.X >= 1 && nPos.X <= 100 && nPos.Y >= 1 && nPos.Y <= 100;
        bool occMuerto = false;
        int occupant = inBounds ? UsuarioEnTile(nPos.Map, nPos.X, nPos.Y, userIndex, out occMuerto) : 0;
        bool occMuertoF = occupant > 0 && occMuerto;
        // Un GM/Dios invisible parado ahí NO bloquea a un jugador común: para él ese personaje
        // no existe (CrearUsuarioParaObs ni se lo dibujó). Bloquearlo delataría al GM ("me choco
        // con algo invisible"). En vez de trabarlo, se "patea" al GM a un tile libre cercano;
        // otro GM que sí lo ve invisible SÍ sigue chocando con él normalmente.
        bool occInvisibleGM = occupant > 0 && !occMuerto
                               && UserListManager.UserList[occupant].flags.Invisible == 1
                               && u.FaccionStatus < AdminLoader.STATUS_CONSEJERO;
        bool agua = map != null && map.HasWater(nPos.X, nPos.Y);
        // Vuela por montura (flags.Vuela, DoEquita) O por llevar las Alas puestas (ítem OBJ2035,
        // ShieldAnim=88 en obj.dat — mismo ítem que en el cliente web se anima siempre y da +25%
        // de velocidad, ver SHIELDS_WINGS en game.html). Se lee directo del escudo REALMENTE
        // equipado (Invent.EscudoEqpObjIndex) en vez de cachear un flag aparte: ese campo ya es
        // la fuente de verdad que usa todo Combat.cs, y así no hace falta sincronizar un flag
        // nuevo con cada lugar que puede desequipar un escudo (swap, muerte, desarme, login).
        bool volando = Volando(u);
        // BUG-008: en un dungeon el vuelo NO exime de los bloqueos (paredes/estructuras) —
        // solo afuera. NpcManager/agua/tierra abajo siguen igual (el ticket habla solo de
        // "bloqueos"; ni agua ni NPCs relevantes dentro de un dungeon normalmente).
        bool volandoIgnoraBloqueo = volando && !EsDungeon(nPos.Map);
        bool puedeMover = inBounds
                          && (map == null || !map.IsBlocked(nPos.X, nPos.Y) || volandoIgnoraBloqueo)
                          && (volando || NpcManager.NpcAt(nPos.Map, nPos.X, nPos.Y) == null)  // volando: también pasa sobre NPCs
                          && (occupant == 0 || occMuertoF || occInvisibleGM) // vivos bloquean; caspers y GM invisible (a ojos de un común) no
                          && (map == null || volando || (sailing ? agua : !agua)); // agua↔tierra según navegación (volando: sin restricción)

        // ANTI-CORTE DE ESQUINA (sólo diagonales): un paso diagonal exige que los DOS tiles
        // ortogonales intermedios sean pisables. Sin esto se atraviesan las esquinas de las
        // paredes en diagonal, que es el agujero clásico al agregar 8 rumbos: dos paredes que
        // se tocan en un vértice dejarían de sellar. Se mira sólo el bloqueo estático del mapa
        // (y agua↔tierra), NO la ocupación: que haya alguien parado al lado no tiene por qué
        // impedirte pasar en diagonal, y además la ocupación cambia todo el tiempo.
        if (puedeMover && EsDiagonal(nHeading) && !volandoIgnoraBloqueo)
        {
            var (cv, chz) = Componentes(nHeading);
            foreach (var comp in new[] { cv, chz })
            {
                WorldPos lado = u.Pos;
                HeadtoPos(comp, ref lado);
                bool ladoOk = lado.X >= 1 && lado.X <= 100 && lado.Y >= 1 && lado.Y <= 100
                              && (map == null || !map.IsBlocked(lado.X, lado.Y))
                              && (map == null || volando || (sailing ? map.HasWater(lado.X, lado.Y)
                                                                    : !map.HasWater(lado.X, lado.Y)));
                if (!ladoOk) { puedeMover = false; break; }
            }
        }

        // ¿El tile destino tiene una salida a otro mapa (TileExit)? Se mira ANTES de aplicar el
        // movimiento: el teleport al Dungeon Newbie "patea" a los nivel 15+ (el paso se rechaza
        // y rebota como un tile bloqueado, así nunca quedan parados sobre el teleport).
        var exit = puedeMover ? map?.GetExit(nPos.X, nPos.Y) : null;

        // Primer paso tras un teleport: ignorar el TileExit del tile destino. Así, al entrar a un
        // dungeon caminando, el paso que cae sobre el teleport de retorno NO te rebota afuera. El
        // flag se consume en este movimiento (siguiente paso ya dispara los exits normalmente).
        //
        // Y ADEMÁS con ventana de tiempo: el flag por sí solo no puede distinguir el rebote
        // automático (venías con la tecla apretada, el paso sale a los ~238ms) de que el jugador
        // esté volviendo a entrar a propósito. Se comía ese paso deliberado, y el síntoma era
        // "piso el teleport y no entra, tengo que dar un paso al costado primero". Un regreso
        // pensado siempre tarda más que la ventana, así que el rebote sigue cubierto.
        if (puedeMover && u.RecienTeleportado)
        {
            u.RecienTeleportado = false;
            if ((DateTime.UtcNow - u.RecienTeleportadoAt).TotalMilliseconds <= ANTIREBOTE_MS)
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
        // Zona con franja de niveles (MapasPorNivel): mismo trato que el Dungeon Newbie, se
        // rechaza el paso para que no quede parado encima del teleport.
        else if (exit.HasValue && !MapasPorNivel.Permitido(u, exit.Value.DestMap))
        {
            puedeMover = false;
            ServerPackets.ConsoleMsg(u.Conn, MapasPorNivel.MotivoRechazo(exit.Value.DestMap), 1);
        }

        if (puedeMover)
        {
            // VB6 (Modulo_UsUaRiOs.bas:1175): al pisar el tile de un casper, se le envía PosUpdate
            // para resincronizar su cliente ("empuje de casper").
            if (occMuertoF)
            {
                var casper = UserListManager.UserList[occupant];
                if (casper?.Conn != null)
                    { var (px, py) = Continuous.Pos(casper.Pos.Map, casper.Pos.X, casper.Pos.Y); ServerPackets.PosUpdate(casper.Conn, px, py); }
            }
            else if (occInvisibleGM)
            {
                KickInvisibleGM(occupant, UserListManager.UserList[occupant]);
            }

            u.Pos = nPos;
            // Visibilidad por área (AOI server-driven): manda CharacterMove a quienes lo ven, y
            // CharacterCreate/CharacterRemove a los que entran/salen de su área. Reemplaza la difusión
            // por mapa completo (ver MODAREAS_AUDIT.md / AreaVisibility).
            AreaVisibility.OnUserMoved(userIndex);

            // Grupo: avisar la nueva posición a los compañeros (minimapa/mapamundi).
            PartySystem.SendPartyMemberPos(userIndex);

            // AFK: al moverse se registra actividad y se quita la partícula de AFK si la tenía.
            GameTimer.ClearAfk(userIndex);

            // Casteo de resucitar: al moverse se interrumpe (y se borra la partícula de casteo).
            if (u.ResucitandoHasta > 0)
            {
                Combat.CancelarResucitar(u);
                ServerPackets.ConsoleMsg(u.Conn, "El conjuro de resurrección se interrumpió al moverte.", 1);
            }

            // Conjuro de invocación de la mascota: mismo criterio. Se corta acá (y no sólo en el
            // tick) para que el aviso llegue en el mismo instante del paso, no hasta un tick después.
            if (u.InvocandoPetHasta > 0)
            {
                Combat.CancelarCasteoMascota(u, avisar: false);
                ServerPackets.ConsoleMsg(u.Conn, "El vínculo se cortó al moverte: la invocación se interrumpió.", 1);
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

            // Pisó un TileExit (ya validado arriba) → teletransportar. Con el FX de warp
            // (remolino + distorsión) pero SIN sonido: cruzar mapas caminando es frecuente
            // y el sonido en cada cruce resultaría molesto.
            if (exit.HasValue)
                WarpUser(userIndex, exit.Value.DestMap, exit.Value.DestX, exit.Value.DestY, fx: true, sonido: false);
        }
        else
        {
            // Movimiento ilegal: rebotar al cliente a su posición real.
            { var (px, py) = Continuous.Pos(u.Pos.Map, u.Pos.X, u.Pos.Y); ServerPackets.PosUpdate(u.Conn, px, py); }
        }

        // Si un Dios lo está espiando: mandarle ESTE paso (el espiado no recibe su propio
        // movimiento, así que el espejo no lo copia de ningún lado) y, si cambió de mapa,
        // llevarle también el personaje. Dentro del mismo mapa no hace falta mover a nadie:
        // el espía ve por los ojos del espiado (su cliente cree que su personaje es el de
        // él), así que la cámara ya lo sigue sola.
        Espia.SeguirAlEspiado(userIndex);
    }

    /// <summary>
    /// Teletransporta al usuario a otro mapa/posición (al pisar un TileExit).
    /// Lo quita del mapa viejo, lo crea en el nuevo, y le reenvía el contenido del mapa.
    /// </summary>
    /// <summary>Mapas deshabilitados temporalmente: cualquier intento de entrar patea al jugador
    /// a Intermundia (cCiudad 15) con un aviso. Los GMs/Dioses (Consejero+) pueden entrar igual.</summary>
    public static readonly HashSet<int> MapasDeshabilitados = new() { 839 };

    /// <summary>FX de teletransporte (VB6 WarpUserChar FX:=True): sonido 3 + FX 1 (FXWARP).</summary>
    private const short SND_WARP = 3, FXWARP = 1;

    /// <summary>
    /// Efecto de DESAPARICIÓN en el tile de origen: se usa EfectoTerrenoFX (anclado al tile, no al
    /// char) porque el CharacterRemove del warp borra el personaje del cliente antes de que un
    /// CreateFX sobre él llegue a verse. skipUserIndex excluye al que se va (su cliente está por
    /// recibir ChangeMap y limpiar el mundo).
    /// </summary>
    private static void FxWarpOrigen(int map, byte x, byte y, int skipUserIndex, bool sonido = true)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            if (i == skipUserIndex) continue;
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == map)
            {
                if (sonido)
                    ServerPackets.PlayWave(o.Conn, SND_WARP, x, y);
                ServerPackets.EfectoTerrenoFX(o.Conn, FXWARP, x, y, 0);
            }
        }
    }

    /// <summary>Efecto de APARICIÓN sobre el personaje en el destino (CreateFX + sonido de warp).</summary>
    private static void FxWarpDestino(int map, short charIndex, byte x, byte y, bool sonido = true)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == map)
            {
                if (sonido)
                    ServerPackets.PlayWave(o.Conn, SND_WARP, x, y);
                ServerPackets.CreateFX(o.Conn, charIndex, FXWARP, 0);
            }
        }
    }

    public static void WarpUser(int userIndex, short destMap, short destX, short destY, bool fx = true, bool sonido = true)
    {
        // Defensa: una coord fuera de [1,100] (p.ej. un clic GM sobre terreno del mapa vecino
        // mandado como coord extendida) dejaría al PJ en posición inválida sin poder moverse.
        destX = Math.Clamp(destX, (short)1, (short)100);
        destY = Math.Clamp(destY, (short)1, (short)100);

        // Mundo continuo: si el destino cae en la BANDA DE SOLAPE que posee un mapa vecino
        // (margen bloqueado del destino pedido), re-resolver al dueño real — warpear al margen
        // deja al PJ encerrado entre tiles bloqueados sin poder caminar.
        if (Continuous.Enabled && RegionLayout.TryGetOffset(destMap, out var _wOff)
            && RegionLayout.TryGlobalToLocal(destMap, _wOff.X + destX, _wOff.Y + destY,
                                             out int _wMap, out int _wX, out int _wY)
            && _wMap != destMap)
        {
            destMap = (short)_wMap; destX = (short)_wX; destY = (short)_wY;
        }

        var u = UserListManager.UserList[userIndex];
        int oldMap = u.Pos.Map;
        byte oldX = (byte)u.Pos.X, oldY = (byte)u.Pos.Y;

        // Mapa deshabilitado: NO teletransportar. El jugador se queda donde está y recibe el aviso
        // (los GMs/Dioses pasan). Se le reenvía su posición real para resincronizar el cliente.
        if (MapasDeshabilitados.Contains(destMap) && u.FaccionStatus < AdminLoader.STATUS_CONSEJERO)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Ese mapa está deshabilitado temporalmente.", 1);
            { var (px, py) = Continuous.Pos(u.Pos.Map, u.Pos.X, u.Pos.Y); ServerPackets.PosUpdate(u.Conn, px, py); }
            return;
        }

        // ¿Es el cruce CAMINANDO del borde (seam)? Requiere mundo continuo + ambos mapas de la
        // región + destino globalmente ADYACENTE al origen (los TileExits del seam caen en el
        // mismo tile global por el solape). Un teleport GM (/telep, warp) entre mapas de la región
        // NO es adyacente: debe ir por el ChangeMap clásico — el cliente trata SeamlessCross como
        // continuidad de la caminata (no re-estampa el tile del char ni resetea la cámara) y un
        // salto de posición por esa vía dejaba al PJ "bugueado" sin poder caminar.
        bool seamCross = false;
        var (gxOld, gyOld) = (0, 0);
        if (Continuous.IsSeamCross(oldMap, destMap))
        {
            (gxOld, gyOld) = Continuous.Pos(oldMap, oldX, oldY);
            var (gxNew, gyNew) = Continuous.Pos(destMap, destX, destY);
            seamCross = Math.Abs(gxNew - gxOld) <= 1 && Math.Abs(gyNew - gyOld) <= 1;
        }

        // El warp se concreta: el primer paso posterior no debe disparar otro TileExit (anti-rebote
        // dungeon: el destino cae pegado al teleport de retorno). EXCEPTO en el cruce seamless de
        // borde: ahí "teleportar" es solo cruzar la costura caminando y el destino queda dentro del
        // mapa nuevo — comerse el primer paso obligaba a dar un movimiento extra para poder volver
        // a cruzar (el paso sobre el TileExit de vuelta no disparaba).
        u.RecienTeleportado = !seamCross;
        u.RecienTeleportadoAt = DateTime.UtcNow;   // ver ANTIREBOTE_MS

        // Teleport dentro del mismo mapa: reposición ligera, SIN ChangeMap. El ChangeMap haría que el
        // cliente borre todo char_list y recargue el mundo entero → tirón/freeze (típico del telep GM).
        // En su lugar movemos el char propio con PosUpdate y diffeamos sólo la vista de área.
        if (oldMap == destMap)
        {
            // FX de desaparición en el origen: el propio usuario también lo ve (sigue en el mapa).
            if (fx)
                FxWarpOrigen(oldMap, oldX, oldY, 0, sonido);
            u.Pos.X = destX;
            u.Pos.Y = destY;
            { var (px, py) = Continuous.Pos(destMap, destX, destY); ServerPackets.PosUpdate(u.Conn, px, py); }
            AreaVisibility.OnUserTeleportSameMap(userIndex);
            PartySystem.SendPartyMemberPos(userIndex);
            // FX de aparición sobre el PJ recién reubicado (los observadores ya lo tienen creado).
            if (fx)
                FxWarpDestino(destMap, u.Char.CharIndex, (byte)destX, (byte)destY, sonido);
            Console.WriteLine($"[ServidorCS] {u.Name} teleport en mapa {destMap} → ({destX},{destY})");
            return;
        }

        // Mundo continuo: cruzar un borde del overworld (seam) → SIN ChangeMap (sin teardown del cliente).
        // El cliente conserva su char_list; la continuidad la sostiene el re-anclado de coords globales (4a).
        // ClearClientView va ANTES de OnUserLeave (que limpia los sets del mover) para poder quitar del
        // cliente lo que veía del mapa viejo y no dejar fantasmas.
        if (seamCross)
        {
            // gxOld/gyOld (ya calculados): si el destino cae en el mismo tile global (caso normal
            // del seam, los mapas se solapan), los observadores no necesitan ningún packet.
            AreaVisibility.ClearClientView(userIndex);
            // [[b4_usersbymap]] Único camino que cambia Pos.Map SIN pasar por OnUserLeave/OnUserEnter
            // (usa OnUserSeamCross en su lugar, que no toca el índice): actualizar acá a mano.
            UsersByMapIndex.Move(userIndex, oldMap, destMap);
            u.Pos.Map = destMap; u.Pos.X = destX; u.Pos.Y = destY;
            NpcManager.MoverMascotaConDueño(u, oldMap, destMap, (byte)destX, (byte)destY);
            var (sgx, sgy) = Continuous.Pos(destMap, destX, destY);
            ServerPackets.SeamlessCross(u.Conn, destMap, sgx, sgy);
            if (u.flags.Oculto == 1 || u.flags.Invisible == 1)
                ServerPackets.SetInvisible(u.Conn, u.Char.CharIndex, true);
            // NO OnUserLeave+OnUserEnter: eso hacía remove+create del char para los observadores
            // cross-map → lo veían saltar/parpadear al cruzar el borde. OnUserSeamCross diffea.
            AreaVisibility.OnUserSeamCross(userIndex, gxOld, gyOld);
            PartySystem.SendPartyMemberPos(userIndex);
            Console.WriteLine($"[ServidorCS] {u.Name} seamless {oldMap}→{destMap} ({destX},{destY})");
            return;
        }

        // FX de desaparición en el tile de origen, ANTES de sacarlo de la vista (el CharacterRemove
        // del OnUserLeave borra el char; el FX queda anclado al tile así que sobrevive al borrado).
        if (fx)
            FxWarpOrigen(oldMap, oldX, oldY, userIndex, sonido);

        // Sacar el PJ de la vista de los observadores del mapa viejo y limpiar sus sets de área.
        AreaVisibility.OnUserLeave(userIndex);

        u.Pos.Map = destMap;
        u.Pos.X = destX;
        u.Pos.Y = destY;
        NpcManager.MoverMascotaConDueño(u, oldMap, destMap, (byte)destX, (byte)destY);

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

        // FX de aparición sobre el PJ en el destino (después del OnUserEnter, cuando los
        // observadores del área nueva ya recibieron su CharacterCreate).
        if (fx)
            FxWarpDestino(destMap, u.Char.CharIndex, (byte)destX, (byte)destY, sonido);

        // Clima del nuevo mapa (oldMap para el sonido de salir de dungeon hacia la lluvia).
        Clima.EnviarClimaAUsuario(userIndex, oldMap);

        // Ciclo Día/Noche: re-evaluar el flag de dungeon del nuevo mapa.
        DayNightCycle.EnviarAUsuario(userIndex);

        // Grupo: avisar el cambio de mapa/posición a los compañeros (minimapa/mapamundi).
        PartySystem.SendPartyMemberPos(userIndex);

        Console.WriteLine($"[ServidorCS] {u.Name} cambió a mapa {destMap} ({destX},{destY})");
    }

}
