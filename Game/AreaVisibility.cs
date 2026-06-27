using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Visibilidad por área (AOI) server-driven. Reemplaza la difusión "por mapa completo" para entidades
/// MÓVILES (jugadores y NPCs) por un modelo donde el servidor mantiene, por observador, el set de
/// entidades que tiene renderizadas y le manda CharacterCreate al ENTRAR a su área y
/// CharacterRemove(desvanecido=false) al SALIR. Equivale al subsistema ModAreas/ConnGroups del VB6,
/// pero adaptado a este cliente Godot: el cliente NO hace culling por AreaChanged (su handler es
/// informativo), así que el servidor DEBE mandar el remove explícito. El cliente ya soporta
/// CharacterRemove(desvanecido=false) = "salió del área visible". Sin culling cliente → sin fantasmas.
///
/// Área = ±2 bloques de 9 tiles (ventana 45×45), idéntico al AreaRecive del VB6 (InitAreas).
/// Objetos del piso: siguen en modelo full-map (estáticos, ya consistente; ver MODAREAS_AUDIT.md).
/// </summary>
public static class AreaVisibility
{
    private const int VIEW = 2; // ±2 bloques (VB6 AreasRecive incluye L-2..L+2)

    private static int Block(int coord) => coord / 9;

    /// <summary>
    /// Crea el char de 'user' en el cliente de 'obsConn'. Si 'user' está oculto/invisible, el
    /// trato depende de quién mira (VB6 ModAreas:324-330 + la nota "para que los GM se vean
    /// entre sí" que quedó sin aplicar): un GM (obsStatus &gt;= Consejero) SÍ lo recibe, con
    /// SetInvisible(true) — el cliente lo dibuja como fantasma semitransparente CON nick. Un
    /// jugador común directamente no lo recibe: para él el personaje no existe.
    ///
    /// Devuelve false si NO lo creó: el que llama no debe anotarlo en su VisibleUsers (si no,
    /// el AOI creería que el cliente lo tiene dibujado y le mandaría CharacterMove por cada
    /// paso de un personaje que del otro lado no existe).
    /// </summary>
    private static bool CrearUsuarioParaObs(Connection obsConn, byte obsStatus, User user)
    {
        // MODO ESPÍA: el Dios que está espiando NO EXISTE para nadie. Antes esto se resolvía
        // a mano (un CharacterRemove en el momento de empezar a espiar y otro al cambiar de
        // mapa), pero el AOI vuelve a crear los chars cada vez que alguien recalcula su área
        // —al cruzar de bloque, al cambiar de mapa, al cruzar un borde del mundo continuo—
        // y ahí reaparecía. Con SetInvisible no alcanza: el cliente dibuja a un invisible
        // como fantasma semitransparente CON el nick (esa marca existe para que los GM se
        // vean entre sí). La única forma de que no esté es no crearlo nunca.
        if (Espia.AQuienEspia(user.Conn) != null) return false;
        bool oculto = user.flags.Oculto == 1 || user.flags.Invisible == 1;
        if (oculto && obsStatus < AdminLoader.STATUS_CONSEJERO) return false;
        LoginFlow.SendCharCreate(obsConn, user);
        if (oculto)
            ServerPackets.SetInvisible(obsConn, user.Char.CharIndex, true);
        // Estado visual no incluido en CharacterCreate: la partícula de meditación se difunde solo
        // al togglear Meditar, así que quien entra al área (o reloguea) no la recibiría.
        if (user.flags.Meditando)
            ServerPackets.EfectoCharParticula(obsConn, user.Char.CharIndex,
                (short)Facciones.ParticleToLevel(user), -1f, false);
        // Ídem la partícula de inactividad (AFK, 238): se difunde una sola vez al cruzar el umbral
        // de tiempo (GameTimer), así que quien entra al área de un usuario ya AFK no la vería.
        if (user.flags.AfkParticula)
            ServerPackets.EfectoCharParticula(obsConn, user.Char.CharIndex,
                GameTimer.AFK_PARTICULA, -1f, false);
        return true;
    }

    /// <summary>
    /// Le manda a 'destino' TODO lo que 'dueño' tiene visible ahora mismo (jugadores, NPCs y
    /// objetos del piso de su área), sin tocar los sets de nadie. Es la "foto del mundo" para
    /// una conexión que mira por los ojos de otro: el modo ESPECTADOR, que no tiene personaje
    /// propio y por lo tanto no tiene área ni puede teletransportarse para armarla.
    /// Los sets del dueño ya están calculados: se recorren y se reenvían tal cual.
    /// </summary>
    public static void CrearVistaDe(Connection destino, User dueño)
    {
        if (destino == null || dueño?.Conn == null) return;

        foreach (int i in dueño.VisibleUsers)
        {
            var o = UserListManager.UserList[i];
            // El que mira por acá es el espía (Dios): siempre trato de GM, ve fantasmas ocultos.
            if (o?.flags.UserLogged == true) CrearUsuarioParaObs(destino, AdminLoader.STATUS_DIOS, o);
        }
        foreach (int nci in dueño.VisibleNpcs)
        {
            var n = NpcManager.NpcByCharIndex(dueño.Pos.Map, nci);
            // Mundo continuo: el NPC puede estar en un mapa vecino (AOI cross-map).
            if (n == null && Continuous.Enabled && RegionLayout.InRegion(dueño.Pos.Map))
                foreach (int nm in RegionLayout.NearbyMaps(dueño.Pos.Map))
                    if ((n = NpcManager.NpcByCharIndex(nm, nci)) != null) break;
            if (n != null) NpcManager.SendNpcCreate(destino, n);
        }
        bool cont = Continuous.Enabled && RegionLayout.InRegion(dueño.Pos.Map);
        foreach (int code in dueño.VisibleObjs)
        {
            int m = cont ? code / 10201 : dueño.Pos.Map;
            int r = cont ? code % 10201 : code;
            int x = r / 101, y = r % 101;
            var md = MapLoader.Get(m);
            if (md == null) continue;
            short oi = md.FloorObj[x, y];
            if (oi <= 0) continue;
            var (gx, gy) = Continuous.Rel(dueño.Pos.Map, m, x, y);
            ServerPackets.ObjectCreate(destino, gx, gy, oi, (short)md.FloorAmount[x, y]);
        }
    }

    /// <summary>¿La posición b está en el área de visión del observador en a? (mismo criterio que AreaRecive&AreaPertenece).</summary>
    private static bool EnArea(int ax, int ay, int bx, int by)
        => Math.Abs(Block(ax) - Block(bx)) <= VIEW && Math.Abs(Block(ay) - Block(by)) <= VIEW;

    /// <summary>
    /// Visibilidad de un objetivo (en tgtMap, tx, ty) para un observador, SOPORTANDO mapas vecinos
    /// (AOI cross-map del mundo continuo). Mismo mapa → EnArea local de siempre. Distinto mapa + mundo
    /// continuo activo + ambos del overworld → comparación de bloques en coordenadas GLOBALES (continuas
    /// entre mapas), así se ve a jugadores/NPCs del mapa de al lado cerca del borde. Si no, no visible
    /// (comportamiento clásico: solo mismo mapa).
    /// </summary>
    private static bool VeContinuo(User obs, int tgtMap, int tx, int ty)
    {
        if (obs.Pos.Map == tgtMap) return EnArea(obs.Pos.X, obs.Pos.Y, tx, ty);
        if (Continuous.Enabled
            && RegionLayout.TryLocalToGlobal(obs.Pos.Map, obs.Pos.X, obs.Pos.Y, out int ogx, out int ogy)
            && RegionLayout.TryLocalToGlobal(tgtMap, tx, ty, out int tgx, out int tgy))
            return Math.Abs(Block(ogx) - Block(tgx)) <= VIEW && Math.Abs(Block(ogy) - Block(tgy)) <= VIEW;
        return false;
    }

    // ============================ JUGADORES ============================

    /// <summary>
    /// Login / entrada al mundo / warp: crea para el observador todo lo visible en su área y hace que
    /// los demás del área lo vean a él. Inicializa los sets y el bloque de área.
    /// </summary>
    public static void OnUserEnter(int idx)
    {
        var u = UserListManager.UserList[idx];
        if (u?.Conn == null) return;
        // [[b4_usersbymap]] Login/warp entre mapas/fin de modo espía: registrar en el mapa ACTUAL
        // (ya actualizado por el caller antes de llamar acá). Idempotente si ya estaba (HashSet.Add).
        UsersByMapIndex.Add(u.Pos.Map, idx);
        u.VisibleUsers.Clear();
        u.VisibleNpcs.Clear();
        u.VisibleObjs.Clear();
        u.AreaBlockX = Block(u.Pos.X);
        u.AreaBlockY = Block(u.Pos.Y);

        // Limpiar cualquier referencia STALE a este idx en los demás observadores (el slot de usuario
        // se recicla). Si no, FullUpdate(sendSelfToOthers) ve hadMe=true y OMITE el CharacterCreate,
        // y el nuevo PJ no se veía hasta teletransportarse. (WarpUser ya lo hacía vía OnUserLeave.)
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            if (i == idx) continue;
            UserListManager.UserList[i]?.VisibleUsers.Remove(idx);
        }

        FullUpdate(idx, sendSelfToOthers: true);
    }

    /// <summary>
    /// El usuario se va del área/mapa (logout, warp a otro mapa, muerte que recrea, etc.): manda
    /// CharacterRemove de él a todos los que lo veían y lo saca de sus sets; limpia sus propios sets.
    /// </summary>
    public static void OnUserLeave(int idx, bool desvanecido = false)
    {
        var u = UserListManager.UserList[idx];
        if (u == null) return;
        // [[b4_usersbymap]] Logout/warp fuera del mapa/inicio de modo espía: sacar del mapa en el
        // que estaba (todavía no mutado por el caller en el camino normal de WarpUser/CloseUser).
        UsersByMapIndex.Remove(u.Pos.Map, idx);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            if (i == idx) continue;
            var o = UserListManager.UserList[i];
            if (o == null || o.Conn == null) continue;
            if (o.VisibleUsers.Remove(idx) && o.flags.UserLogged)
                ServerPackets.CharacterRemove(o.Conn, u.Char.CharIndex, desvanecido);
        }
        u.VisibleUsers.Clear();
        u.VisibleNpcs.Clear();
        u.VisibleObjs.Clear();
        u.AreaBlockX = -1;
        u.AreaBlockY = -1;
    }

    /// <summary>
    /// Cruce seamless de borde (mundo continuo). Antes se hacía OnUserLeave+OnUserEnter, pero eso
    /// mandaba CharacterRemove+CharacterCreate del que cruza a los observadores que lo seguían
    /// viendo cross-map → lo veían "pegar un salto" (parpadeo) en el borde. Acá se DIFFEA:
    ///   • Observador que lo sigue viendo: nada (el TileExit del seam cae en el MISMO tile global
    ///     que pisó al cruzar; su último CharacterMove ya lo dejó ahí) o CharacterMove si el
    ///     destino difiere en global.
    ///   • Entró/salió de su vista: create/remove como siempre.
    /// La vista PROPIA del mover: chars recreados (ClearClientView los quitó del cliente; sus
    /// coords locales eran del mapa viejo); objetos DIFFEADOS (VisibleObjs intacto, sin borrar).
    /// Llamar DESPUÉS de actualizar u.Pos. gxOld/gyOld = posición global previa al cruce.
    /// </summary>
    public static void OnUserSeamCross(int idx, int gxOld, int gyOld)
    {
        var u = UserListManager.UserList[idx];
        if (u?.Conn == null) return;
        var (gx, gy) = Continuous.Pos(u.Pos.Map, u.Pos.X, u.Pos.Y);

        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            if (i == idx) continue;
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged != true || o.Conn == null) continue;

            bool vis = VeContinuo(o, u.Pos.Map, u.Pos.X, u.Pos.Y);
            bool had = o.VisibleUsers.Contains(idx);
            if (vis && !had) { if (CrearUsuarioParaObs(o.Conn, o.FaccionStatus, u)) o.VisibleUsers.Add(idx); }
            else if (!vis && had) { ServerPackets.CharacterRemove(o.Conn, u.Char.CharIndex, false); o.VisibleUsers.Remove(idx); }
            else if (vis && had && (gx != gxOld || gy != gyOld))
                ServerPackets.CharacterMove(o.Conn, u.Char.CharIndex, gx, gy);
        }

        // Su propia vista: chars fueron vaciados por ClearClientView → se recrean; objetos se
        // diffean contra VisibleObjs (que se conservó): solo creates/deletes de los que entran/salen.
        u.AreaBlockX = Block(u.Pos.X);
        u.AreaBlockY = Block(u.Pos.Y);
        FullUpdate(idx, sendSelfToOthers: false);
    }

    /// <summary>
    /// El usuario se movió un tile. Actualiza la vista que los DEMÁS tienen de él (create/remove/move)
    /// y, sólo si cruzó de bloque de área, recalcula su PROPIA vista (qué jugadores/NPCs ve).
    /// </summary>
    // [[b4_usersbymap]] B4 Fase 2: antes recorría 1..LastUser (todo el server) para encontrar a
    // quién avisarle que 'mover' se movió. VeContinuo(o, mover.Pos.Map, ...) sólo puede dar true si
    // 'o' está en mover.Pos.Map, o (mundo continuo activo Y mover.Pos.Map es de la región) en un
    // mapa de RegionLayout.NearbyMaps(mover.Pos.Map) — el mismo invariante que ya explotan
    // FullUpdate/CrearVistaDe/FullUpdateObjectsContinuous para NPCs/objetos en este mismo archivo.
    // Ruta rápida sin mundo continuo (o mapa fuera de la región): NearbyMaps ni se consulta, porque
    // VeContinuo colapsa a "mismo mapa" en ese caso — IMPORTANTE: los mapas fuera de la región
    // (dungeons/interiores) también deben seguir viéndose entre sí aunque Continuous.Enabled sea
    // true globalmente; por eso el gate es "Continuous.Enabled && RegionLayout.InRegion(map)", no
    // sólo "Continuous.Enabled" — si no, un dungeon con MundoContinuo=1 quedaría sin candidatos
    // (NearbyMaps de un mapa fuera de la región devuelve lista vacía).
    // Orden: no importa. Cada candidato 'o' sólo lee su PROPIO VisibleUsers y recibe COMO MÁXIMO un
    // paquete (create, remove o move — ramas mutuamente excluyentes), sin depender de qué otro
    // candidato se procesó antes. UsersByMapIndex garantiza que cada usuario pertenece a EXACTAMENTE
    // un mapa, y RegionLayout.NearbyMaps nunca repite un mapa → ningún candidato se visita dos veces.
    public static void OnUserMoved(int moverIdx)
    {
        var mover = UserListManager.UserList[moverIdx];
        if (mover?.Conn == null) return;
        int map = mover.Pos.Map;
        // Mundo continuo: coords que se envían al cliente en global (identidad si el flag está apagado).
        // Nota: EnArea() sigue usando Pos.X/Y LOCALES (el AOI trabaja en coords locales del mapa).
        var (mx, my) = Continuous.Pos(map, mover.Pos.X, mover.Pos.Y);

        void Visit(int i)
        {
            if (i == moverIdx) return;
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged != true || o.Conn == null) return;

            bool vis = VeContinuo(o, mover.Pos.Map, mover.Pos.X, mover.Pos.Y);
            bool had = o.VisibleUsers.Contains(moverIdx);
            if (vis && !had) { if (CrearUsuarioParaObs(o.Conn, o.FaccionStatus, mover)) o.VisibleUsers.Add(moverIdx); }
            else if (!vis && had) { ServerPackets.CharacterRemove(o.Conn, mover.Char.CharIndex, false); o.VisibleUsers.Remove(moverIdx); }
            else if (vis && had) { ServerPackets.CharacterMove(o.Conn, mover.Char.CharIndex, mx, my); }
        }

        if (!Continuous.Enabled || !RegionLayout.InRegion(map))
        {
            foreach (int i in UsersByMapIndex.Get(map)) Visit(i);
        }
        else
        {
            foreach (int m in RegionLayout.NearbyMaps(map))
                foreach (int i in UsersByMapIndex.Get(m)) Visit(i);
        }

        if (mover.AreaBlockX != Block(mover.Pos.X) || mover.AreaBlockY != Block(mover.Pos.Y))
        {
            FullUpdate(moverIdx, sendSelfToOthers: false);
            mover.AreaBlockX = Block(mover.Pos.X);
            mover.AreaBlockY = Block(mover.Pos.Y);
        }
    }

    // TEMPORAL, solo para el test de compatibilidad de B4 Fase 2 — borrar antes de cerrar la ronda.
    public static void OnUserMoved_OLDBENCH(int moverIdx)
    {
        var mover = UserListManager.UserList[moverIdx];
        if (mover?.Conn == null) return;
        int map = mover.Pos.Map;
        var (mx, my) = Continuous.Pos(map, mover.Pos.X, mover.Pos.Y);

        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            if (i == moverIdx) continue;
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged != true || o.Conn == null) continue;

            bool vis = VeContinuo(o, mover.Pos.Map, mover.Pos.X, mover.Pos.Y);
            bool had = o.VisibleUsers.Contains(moverIdx);
            if (vis && !had) { if (CrearUsuarioParaObs(o.Conn, o.FaccionStatus, mover)) o.VisibleUsers.Add(moverIdx); }
            else if (!vis && had) { ServerPackets.CharacterRemove(o.Conn, mover.Char.CharIndex, false); o.VisibleUsers.Remove(moverIdx); }
            else if (vis && had) { ServerPackets.CharacterMove(o.Conn, mover.Char.CharIndex, mx, my); }
        }

        if (mover.AreaBlockX != Block(mover.Pos.X) || mover.AreaBlockY != Block(mover.Pos.Y))
        {
            FullUpdate(moverIdx, sendSelfToOthers: false);
            mover.AreaBlockX = Block(mover.Pos.X);
            mover.AreaBlockY = Block(mover.Pos.Y);
        }
    }

    /// <summary>
    /// Teleport DENTRO del mismo mapa (mejora sobre el VB6: evita el ChangeMap, que en este cliente
    /// borra todo char_list y recrea el mundo → freeze visible). El char propio se reposiciona con
    /// PosUpdate (lo manda WarpUser). Acá hacemos el diff de área sin recargar nada:
    ///   • Cómo lo ven los DEMÁS: a quien lo tenía visible se le manda remove de su pos vieja y, si la
    ///     pos nueva sigue en su área, create en la nueva → teleport instantáneo, sin el "slide" de un
    ///     CharacterMove a través del mapa.
    ///   • Qué ve ÉL: FullUpdate(sendSelfToOthers:false) diffea sus sets (sin limpiarlos) y manda
    ///     remove de lo que salió de su área y create de lo que entró.
    /// </summary>
    public static void OnUserTeleportSameMap(int idx)
    {
        var mover = UserListManager.UserList[idx];
        if (mover?.Conn == null) return;
        int map = mover.Pos.Map;

        // Cómo lo ven los demás: remove (pos vieja) + create (pos nueva) = salto instantáneo.
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            if (i == idx) continue;
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged != true || o.Conn == null || o.Pos.Map != map) continue;

            if (o.VisibleUsers.Remove(idx))
                ServerPackets.CharacterRemove(o.Conn, mover.Char.CharIndex, false);
            if (EnArea(o.Pos.X, o.Pos.Y, mover.Pos.X, mover.Pos.Y)
                && CrearUsuarioParaObs(o.Conn, o.FaccionStatus, mover))
                o.VisibleUsers.Add(idx);
        }

        // Qué ve él: diff incremental de su propia vista (sin limpiar sets → manda los removes que faltan).
        FullUpdate(idx, sendSelfToOthers: false);
        mover.AreaBlockX = Block(mover.Pos.X);
        mover.AreaBlockY = Block(mover.Pos.Y);
    }

    /// <summary>
    /// Recalcula la vista del observador: usuarios y NPCs que entraron/salieron de su área.
    /// Con sendSelfToOthers=true (login/warp) además hace que los demás del área lo vean a él.
    /// </summary>
    private static void FullUpdate(int idx, bool sendSelfToOthers)
    {
        var obs = UserListManager.UserList[idx];
        if (obs?.Conn == null) return;
        int map = obs.Pos.Map;

        // --- Usuarios ---
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            if (i == idx) continue;
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged != true || o.Conn == null) continue;

            bool vis = VeContinuo(obs, o.Pos.Map, o.Pos.X, o.Pos.Y);
            bool had = obs.VisibleUsers.Contains(i);
            if (vis && !had) { if (CrearUsuarioParaObs(obs.Conn, obs.FaccionStatus, o)) obs.VisibleUsers.Add(i); }
            else if (!vis && had) { ServerPackets.CharacterRemove(obs.Conn, o.Char.CharIndex, false); obs.VisibleUsers.Remove(i); }

            if (sendSelfToOthers)
            {
                bool hadMe = o.VisibleUsers.Contains(idx);
                if (vis && !hadMe) { if (CrearUsuarioParaObs(o.Conn, o.FaccionStatus, obs)) o.VisibleUsers.Add(idx); }
                else if (!vis && hadMe) { ServerPackets.CharacterRemove(o.Conn, obs.Char.CharIndex, false); o.VisibleUsers.Remove(idx); }
            }
        }

        // --- NPCs (mundo continuo: además de los del mapa actual, escanea los de mapas vecinos
        //     cuya área global alcanza al observador cerca del borde) ---
        if (Continuous.Enabled && RegionLayout.InRegion(map))
        {
            foreach (int nm in RegionLayout.NearbyMaps(map))
                foreach (var n in NpcManager.GetMapNpcs(nm))
                    NpcVisibilityDiff(obs, n);
        }
        else
        {
            foreach (var n in NpcManager.GetMapNpcs(map))
                NpcVisibilityDiff(obs, n);
        }

        // --- Objetos del piso ---
        // Con el mundo continuo, escanea también los mapas vecinos y usa clave/coords cross-map.
        // Sin el flag, camino ORIGINAL intacto (clave x*101+y, mismo mapa) → cero cambio en el beta.
        if (Continuous.Enabled && RegionLayout.InRegion(map))
            FullUpdateObjectsContinuous(obs, map);
        else
            FullUpdateObjectsSameMap(obs, map);
    }

    /// <summary>Clave de objeto del piso qualificada por mapa (evita colisión entre mapas en mundo continuo).</summary>
    private static int ObjKey(int map, int x, int y) => map * 10201 + x * 101 + y;

    /// <summary>Objetos del piso — camino clásico (mismo mapa, clave x*101+y). 1:1 con el original.</summary>
    private static void FullUpdateObjectsSameMap(User obs, int map)
    {
        var md = MapLoader.Get(map);
        if (md == null) return;
        int bx = Block(obs.Pos.X), by = Block(obs.Pos.Y);
        int x0 = Math.Max(1, (bx - VIEW) * 9), x1 = Math.Min(100, (bx + VIEW) * 9 + 8);
        int y0 = Math.Max(1, (by - VIEW) * 9), y1 = Math.Min(100, (by + VIEW) * 9 + 8);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                short oi = md.FloorObj[x, y];
                if (oi <= 0) continue;
                if (ObjData.Get(oi).Type == ObjType.Teleport && !md.DynamicTeleports.Contains(x * 101 + y)) continue;
                if (obs.VisibleObjs.Add(x * 101 + y))
                {
                    ServerPackets.ObjectCreate(obs.Conn, (byte)x, (byte)y, oi, (short)md.FloorAmount[x, y]);
                    if (ObjData.Get(oi).Type == ObjType.Puertas)
                    {
                        ServerPackets.BlockPosition(obs.Conn, (byte)x, (byte)y, md.Blocked[x, y]);
                        if (x - 1 >= 1)
                            ServerPackets.BlockPosition(obs.Conn, (byte)(x - 1), (byte)y, md.Blocked[x - 1, y]);
                    }
                }
            }
        obs.VisibleObjs.RemoveWhere(code =>
        {
            int x = code / 101, y = code % 101;
            if (x < x0 || x > x1 || y < y0 || y > y1)
            {
                ServerPackets.ObjectDelete(obs.Conn, (byte)x, (byte)y);
                return true;
            }
            return false;
        });
    }

    /// <summary>
    /// Objetos del piso — mundo continuo: escanea el mapa actual + vecinos (NearbyMaps), acotando en cada
    /// uno la ventana global del observador a coords locales. Clave qualificada por mapa (ObjKey). Coords
    /// al cliente en GLOBAL (Continuous.Pos); el cliente las reconvierte a su mapa.
    /// </summary>
    private static void FullUpdateObjectsContinuous(User obs, int map)
    {
        if (!RegionLayout.TryLocalToGlobal(map, obs.Pos.X, obs.Pos.Y, out int ogx, out int ogy)) { FullUpdateObjectsSameMap(obs, map); return; }
        int gx0 = (Block(ogx) - VIEW) * 9, gx1 = (Block(ogx) + VIEW) * 9 + 8;
        int gy0 = (Block(ogy) - VIEW) * 9, gy1 = (Block(ogy) + VIEW) * 9 + 8;
        var nowVisible = new HashSet<int>();
        foreach (int sm in RegionLayout.NearbyMaps(map))
        {
            var smd = MapLoader.Get(sm);
            if (smd == null || !RegionLayout.TryGetOffset(sm, out var off)) continue;
            int lx0 = Math.Max(1, gx0 - off.X), lx1 = Math.Min(100, gx1 - off.X);
            int ly0 = Math.Max(1, gy0 - off.Y), ly1 = Math.Min(100, gy1 - off.Y);
            if (lx0 > lx1 || ly0 > ly1) continue;
            for (int x = lx0; x <= lx1; x++)
                for (int y = ly0; y <= ly1; y++)
                {
                    short oi = smd.FloorObj[x, y];
                    if (oi <= 0) continue;
                    if (ObjData.Get(oi).Type == ObjType.Teleport && !smd.DynamicTeleports.Contains(x * 101 + y)) continue;
                    int key = ObjKey(sm, x, y);
                    nowVisible.Add(key);
                    if (obs.VisibleObjs.Add(key))
                    {
                        var (gx, gy) = Continuous.Rel(obs.Pos.Map, sm, x, y);
                        ServerPackets.ObjectCreate(obs.Conn, gx, gy, oi, (short)smd.FloorAmount[x, y]);
                        if (ObjData.Get(oi).Type == ObjType.Puertas)
                        {
                            ServerPackets.BlockPosition(obs.Conn, gx, gy, smd.Blocked[x, y]);
                            if (x - 1 >= 1) { var (bgx, bgy) = Continuous.Rel(obs.Pos.Map, sm, x - 1, y); ServerPackets.BlockPosition(obs.Conn, bgx, bgy, smd.Blocked[x - 1, y]); }
                        }
                    }
                }
        }
        obs.VisibleObjs.RemoveWhere(code =>
        {
            if (nowVisible.Contains(code)) return false;
            int m = code / 10201, r = code % 10201, x = r / 101, y = r % 101;
            var (gx, gy) = Continuous.Rel(obs.Pos.Map, m, x, y);
            ServerPackets.ObjectDelete(obs.Conn, gx, gy);
            return true;
        });
    }

    // ============================ OBJETOS DEL PISO ============================

    /// <summary>Apareció/cambió un objeto del piso: ObjectCreate a los usuarios cuyo área lo cubre.</summary>
    public static void ObjectAppeared(int map, int x, int y, short objIndex, int amount)
    {
        // NOTA: antes se omitían los Teleport (los estáticos vienen del .csm), pero esto bloqueaba el
        // portal planar dinámico (obj 672): el cliente nunca recibía ObjectCreate → su handler no creaba
        // la partícula del teleport, y al cerrar no recibía ObjectDelete → no la borraba. ObjectAppeared
        // solo se llama para objetos DINÁMICOS (drops, portal), no para los teleports del .csm, así que
        // es seguro mandarlos. El cliente (handle_object_create/delete) maneja la partícula 34 por el objeto.
        if (objIndex <= 0) return;
        bool cont = Continuous.Enabled && RegionLayout.InRegion(map);
        int code = cont ? ObjKey(map, x, y) : x * 101 + y;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u?.flags.UserLogged != true || u.Conn == null) continue;
            bool vis = cont ? VePos(u, map, x, y) : (u.Pos.Map == map && EnArea(u.Pos.X, u.Pos.Y, x, y));
            if (vis)
            {
                var (gx, gy) = Continuous.Rel(u.Pos.Map, map, x, y); // relativo al mapa del observador
                ServerPackets.ObjectCreate(u.Conn, gx, gy, objIndex, (short)amount);
                u.VisibleObjs.Add(code);
            }
        }
        // Espectadores del panel (Espia.cs): NO son un User real, así que el loop de arriba no los
        // alcanza — sin esto, un ítem tirado DESPUÉS de que alguien empezó a espectar un bot nunca
        // aparecía en su pantalla (se veía bien para un jugador real, "bugueado" para el espectador).
        Espia.ParaObservadoresDe(map, x, y, c => ServerPackets.ObjectCreate(c, x, y, objIndex, (short)amount));
    }

    /// <summary>Se quitó un objeto del piso: ObjectDelete a los usuarios que lo tenían visible.</summary>
    public static void ObjectRemoved(int map, int x, int y)
    {
        bool cont = Continuous.Enabled && RegionLayout.InRegion(map);
        int code = cont ? ObjKey(map, x, y) : x * 101 + y;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u?.flags.UserLogged != true || u.Conn == null) continue;
            if (!cont && u.Pos.Map != map) continue;   // clásico: solo mismo mapa
            if (u.VisibleObjs.Remove(code))
            {
                var (gx, gy) = Continuous.Rel(u.Pos.Map, map, x, y);
                ServerPackets.ObjectDelete(u.Conn, gx, gy);
            }
        }
        // Mismo motivo que en ObjectAppeared: un espectador no es un User real. Sin esto, un bot
        // que levanta un drop mientras lo estás espectando lo dejaba "pegado" en el piso en pantalla.
        Espia.ParaObservadoresDe(map, x, y, c => ServerPackets.ObjectDelete(c, x, y));
    }

    // ============================ NPCs ============================

    /// <summary>Un NPC se movió un tile: a cada usuario del mapa, create/remove/move según su área.</summary>
    // [[b4_usersbymap]] B4 Fase 2: mismo cambio y mismo razonamiento que OnUserMoved (ver ese
    // comentario) — VeContinuo(u, n.Map, ...) sólo puede dar true para usuarios de n.Map o (mundo
    // continuo + n.Map en la región) de RegionLayout.NearbyMaps(n.Map).
    public static void OnNpcMoved(NpcManager.NpcInstance n)
    {
        // Mundo continuo: posición del NPC en global para el envío (identidad si el flag está apagado).
        var (nmx, nmy) = Continuous.Pos(n.Map, n.X, n.Y);

        void Visit(int i)
        {
            var u = UserListManager.UserList[i];
            if (u?.flags.UserLogged != true || u.Conn == null) return;
            bool vis = VeContinuo(u, n.Map, n.X, n.Y);
            bool had = u.VisibleNpcs.Contains(n.CharIndex);
            if (vis && !had) { NpcManager.SendNpcCreate(u.Conn, n); u.VisibleNpcs.Add(n.CharIndex); }
            else if (!vis && had) { ServerPackets.CharacterRemove(u.Conn, (short)n.CharIndex, false); u.VisibleNpcs.Remove(n.CharIndex); }
            else if (vis && had) { ServerPackets.CharacterMove(u.Conn, (short)n.CharIndex, nmx, nmy); }
        }

        if (!Continuous.Enabled || !RegionLayout.InRegion(n.Map))
        {
            foreach (int i in UsersByMapIndex.Get(n.Map)) Visit(i);
        }
        else
        {
            foreach (int m in RegionLayout.NearbyMaps(n.Map))
                foreach (int i in UsersByMapIndex.Get(m)) Visit(i);
        }
    }

    // TEMPORAL, solo para el test de compatibilidad de B4 Fase 2 — borrar antes de cerrar la ronda.
    public static void OnNpcMoved_OLDBENCH(NpcManager.NpcInstance n)
    {
        var (nmx, nmy) = Continuous.Pos(n.Map, n.X, n.Y);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u?.flags.UserLogged != true || u.Conn == null) continue;
            bool vis = VeContinuo(u, n.Map, n.X, n.Y);
            bool had = u.VisibleNpcs.Contains(n.CharIndex);
            if (vis && !had) { NpcManager.SendNpcCreate(u.Conn, n); u.VisibleNpcs.Add(n.CharIndex); }
            else if (!vis && had) { ServerPackets.CharacterRemove(u.Conn, (short)n.CharIndex, false); u.VisibleNpcs.Remove(n.CharIndex); }
            else if (vis && had) { ServerPackets.CharacterMove(u.Conn, (short)n.CharIndex, nmx, nmy); }
        }
    }

    /// <summary>Un NPC apareció (spawn/respawn): create para los usuarios cuyo área lo cubre.</summary>
    public static void OnNpcSpawn(NpcManager.NpcInstance n)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u?.flags.UserLogged != true || u.Conn == null) continue;
            if (VeContinuo(u, n.Map, n.X, n.Y) && u.VisibleNpcs.Add(n.CharIndex))
                NpcManager.SendNpcCreate(u.Conn, n);
        }
    }

    /// <summary>Un NPC desapareció (muerte/quitar): remove para todos los que lo tenían.</summary>
    public static void OnNpcRemoved(NpcManager.NpcInstance n, bool desvanecido = false)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u == null || u.Conn == null) continue;
            if (u.VisibleNpcs.Remove(n.CharIndex) && u.flags.UserLogged)
                ServerPackets.CharacterRemove(u.Conn, (short)n.CharIndex, desvanecido);
        }
    }

    /// <summary>¿El usuario observador tiene esa posición dentro de su área? (para difundir FX/sonidos/chat por área).</summary>
    public static bool ObsVe(User obs, int x, int y) => EnArea(obs.Pos.X, obs.Pos.Y, x, y);

    /// <summary>Como ObsVe pero SOPORTA mapas vecinos (mundo continuo): ¿el observador ve la posición
    /// (map,x,y), incluso si está en un mapa de al lado? Para difundir sonidos/FX de posición cross-map.</summary>
    public static bool VePos(User obs, int map, int x, int y) => VeContinuo(obs, map, x, y);

    /// <summary>¿El observador ve al personaje charIndex del mapa 'map'? Mismo mapa = sí (clásico);
    /// mundo continuo = también si lo tiene visible desde el mapa vecino (NPC o usuario). Para difundir
    /// chat/diálogos/FX anclados a un personaje a través del borde.</summary>
    public static bool VeChar(User obs, int map, int charIndex)
    {
        if (obs.Pos.Map == map) return true;
        if (!Continuous.Enabled) return false;
        if (obs.VisibleNpcs.Contains(charIndex)) return true;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.Char.CharIndex == charIndex) return obs.VisibleUsers.Contains(i);
        }
        return false;
    }

    /// <summary>Diff de visibilidad de UN NPC para el observador (create/remove). Usa VeContinuo → soporta cross-map.</summary>
    private static void NpcVisibilityDiff(User obs, NpcManager.NpcInstance n)
    {
        if (n.Dead) return;
        bool vis = VeContinuo(obs, n.Map, n.X, n.Y);
        bool had = obs.VisibleNpcs.Contains(n.CharIndex);
        if (vis && !had) { NpcManager.SendNpcCreate(obs.Conn, n); obs.VisibleNpcs.Add(n.CharIndex); }
        else if (!vis && had) { ServerPackets.CharacterRemove(obs.Conn, (short)n.CharIndex, false); obs.VisibleNpcs.Remove(n.CharIndex); }
    }

    /// <summary>
    /// Mundo continuo (SeamlessCross): quita del CLIENTE del usuario los CHARS que tenía visibles
    /// (usuarios/NPCs) y limpia esos sets, SIN el teardown total de ChangeMap. Se usa antes de
    /// OnUserSeamCross al cruzar un seam para que no queden "fantasmas" del mapa viejo: el cliente
    /// guarda los chars con coords locales del mapa VIEJO y no los rebasa al cruzar, así que hay
    /// que recrearlos (el FullUpdate posterior los manda con coords del mapa nuevo).
    /// Los OBJETOS del piso NO se tocan: en el cliente viven en el MapTile del mapa que los posee
    /// (no necesitan rebase), y las claves de VisibleObjs van cualificadas por mapa (ObjKey), así
    /// que siguen válidas tras el cruce. Borrarlos acá hacía desaparecer al cruzar los objetos
    /// tirados (el ObjectCreate del re-envío caía en la ventana async del cambio de mapa).
    /// No afecta la vista que OTROS tienen de él.
    /// </summary>
    public static void ClearClientView(int idx)
    {
        var u = UserListManager.UserList[idx];
        if (u?.Conn == null) return;
        foreach (int oi in u.VisibleUsers)
        {
            var o = UserListManager.UserList[oi];
            if (o != null) ServerPackets.CharacterRemove(u.Conn, o.Char.CharIndex, false);
        }
        foreach (int nci in u.VisibleNpcs)
            ServerPackets.CharacterRemove(u.Conn, (short)nci, false);
        u.VisibleUsers.Clear();
        u.VisibleNpcs.Clear();
    }
}
