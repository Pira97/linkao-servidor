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
    /// Crea el char de 'user' en el cliente de 'obsConn'. Si 'user' está oculto/invisible, manda
    /// además SetInvisible(true) para que el nuevo observador NO lo vea (VB6 ModAreas:324-330).
    /// Esto corrige también un bug previo: quien entraba al área veía a los ocultos.
    /// </summary>
    private static void CrearUsuarioParaObs(Connection obsConn, User user)
    {
        LoginFlow.SendCharCreate(obsConn, user);
        if (user.flags.Oculto == 1 || user.flags.Invisible == 1)
            ServerPackets.SetInvisible(obsConn, user.Char.CharIndex, true);
        // Estado visual no incluido en CharacterCreate: la partícula de meditación se difunde solo
        // al togglear Meditar, así que quien entra al área (o reloguea) no la recibiría.
        if (user.flags.Meditando)
            ServerPackets.EfectoCharParticula(obsConn, user.Char.CharIndex,
                (short)Facciones.ParticleToLevel(user), -1f, false);
    }

    /// <summary>¿La posición b está en el área de visión del observador en a? (mismo criterio que AreaRecive&AreaPertenece).</summary>
    private static bool EnArea(int ax, int ay, int bx, int by)
        => Math.Abs(Block(ax) - Block(bx)) <= VIEW && Math.Abs(Block(ay) - Block(by)) <= VIEW;

    // ============================ JUGADORES ============================

    /// <summary>
    /// Login / entrada al mundo / warp: crea para el observador todo lo visible en su área y hace que
    /// los demás del área lo vean a él. Inicializa los sets y el bloque de área.
    /// </summary>
    public static void OnUserEnter(int idx)
    {
        var u = UserListManager.UserList[idx];
        if (u?.Conn == null) return;
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
    /// El usuario se movió un tile. Actualiza la vista que los DEMÁS tienen de él (create/remove/move)
    /// y, sólo si cruzó de bloque de área, recalcula su PROPIA vista (qué jugadores/NPCs ve).
    /// </summary>
    public static void OnUserMoved(int moverIdx)
    {
        var mover = UserListManager.UserList[moverIdx];
        if (mover?.Conn == null) return;
        int map = mover.Pos.Map;
        byte mx = (byte)mover.Pos.X, my = (byte)mover.Pos.Y;

        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            if (i == moverIdx) continue;
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged != true || o.Conn == null || o.Pos.Map != map) continue;

            bool vis = EnArea(o.Pos.X, o.Pos.Y, mover.Pos.X, mover.Pos.Y);
            bool had = o.VisibleUsers.Contains(moverIdx);
            if (vis && !had) { CrearUsuarioParaObs(o.Conn, mover); o.VisibleUsers.Add(moverIdx); }
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
            if (EnArea(o.Pos.X, o.Pos.Y, mover.Pos.X, mover.Pos.Y))
            {
                CrearUsuarioParaObs(o.Conn, mover);
                o.VisibleUsers.Add(idx);
            }
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
            if (o?.flags.UserLogged != true || o.Conn == null || o.Pos.Map != map) continue;

            bool vis = EnArea(obs.Pos.X, obs.Pos.Y, o.Pos.X, o.Pos.Y);
            bool had = obs.VisibleUsers.Contains(i);
            if (vis && !had) { CrearUsuarioParaObs(obs.Conn, o); obs.VisibleUsers.Add(i); }
            else if (!vis && had) { ServerPackets.CharacterRemove(obs.Conn, o.Char.CharIndex, false); obs.VisibleUsers.Remove(i); }

            if (sendSelfToOthers)
            {
                bool hadMe = o.VisibleUsers.Contains(idx);
                if (vis && !hadMe) { CrearUsuarioParaObs(o.Conn, obs); o.VisibleUsers.Add(idx); }
                else if (!vis && hadMe) { ServerPackets.CharacterRemove(o.Conn, obs.Char.CharIndex, false); o.VisibleUsers.Remove(idx); }
            }
        }

        // --- NPCs ---
        foreach (var n in NpcManager.GetMapNpcs(map))
        {
            if (n.Dead) continue;
            bool vis = EnArea(obs.Pos.X, obs.Pos.Y, n.X, n.Y);
            bool had = obs.VisibleNpcs.Contains(n.CharIndex);
            if (vis && !had) { NpcManager.SendNpcCreate(obs.Conn, n); obs.VisibleNpcs.Add(n.CharIndex); }
            else if (!vis && had) { ServerPackets.CharacterRemove(obs.Conn, (short)n.CharIndex, false); obs.VisibleNpcs.Remove(n.CharIndex); }
        }

        // --- Objetos del piso (escaneo de la ventana 45×45 vía FloorObj, autoritativo) ---
        var md = MapLoader.Get(map);
        if (md != null)
        {
            int bx = Block(obs.Pos.X), by = Block(obs.Pos.Y);
            int x0 = Math.Max(1, (bx - VIEW) * 9), x1 = Math.Min(100, (bx + VIEW) * 9 + 8);
            int y0 = Math.Max(1, (by - VIEW) * 9), y1 = Math.Min(100, (by + VIEW) * 9 + 8);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    short oi = md.FloorObj[x, y];
                    // Teleport: el cliente lo renderiza desde su .csm; no se envía por red.
                    if (oi <= 0 || ObjData.Get(oi).Type == ObjType.Teleport) continue;
                    if (obs.VisibleObjs.Add(x * 101 + y))
                    {
                        ServerPackets.ObjectCreate(obs.Conn, (byte)x, (byte)y, oi, (short)md.FloorAmount[x, y]);
                        // Puertas: el cliente recarga el bloqueo del .csm al entrar al mapa (puerta
                        // cerrada por defecto). Si el server la tiene abierta (Blocked=false) hay que
                        // re-sincronizar el bloqueo, sino se ve abierta pero queda intransitable.
                        // Idempotente: para una cerrada manda Blocked=true (redundante e inofensivo).
                        if (ObjData.Get(oi).Type == ObjType.Puertas)
                        {
                            ServerPackets.BlockPosition(obs.Conn, (byte)x, (byte)y, md.Blocked[x, y]);
                            if (x - 1 >= 1)
                                ServerPackets.BlockPosition(obs.Conn, (byte)(x - 1), (byte)y, md.Blocked[x - 1, y]);
                        }
                    }
                }
            // Quitar los que salieron de la ventana.
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
        int code = x * 101 + y;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u?.flags.UserLogged != true || u.Conn == null || u.Pos.Map != map) continue;
            if (EnArea(u.Pos.X, u.Pos.Y, x, y))
            {
                ServerPackets.ObjectCreate(u.Conn, (byte)x, (byte)y, objIndex, (short)amount);
                u.VisibleObjs.Add(code);
            }
        }
    }

    /// <summary>Se quitó un objeto del piso: ObjectDelete a los usuarios que lo tenían visible.</summary>
    public static void ObjectRemoved(int map, int x, int y)
    {
        int code = x * 101 + y;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u?.flags.UserLogged != true || u.Conn == null || u.Pos.Map != map) continue;
            if (u.VisibleObjs.Remove(code))
                ServerPackets.ObjectDelete(u.Conn, (byte)x, (byte)y);
        }
    }

    // ============================ NPCs ============================

    /// <summary>Un NPC se movió un tile: a cada usuario del mapa, create/remove/move según su área.</summary>
    public static void OnNpcMoved(NpcManager.NpcInstance n)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u?.flags.UserLogged != true || u.Conn == null || u.Pos.Map != n.Map) continue;
            bool vis = EnArea(u.Pos.X, u.Pos.Y, n.X, n.Y);
            bool had = u.VisibleNpcs.Contains(n.CharIndex);
            if (vis && !had) { NpcManager.SendNpcCreate(u.Conn, n); u.VisibleNpcs.Add(n.CharIndex); }
            else if (!vis && had) { ServerPackets.CharacterRemove(u.Conn, (short)n.CharIndex, false); u.VisibleNpcs.Remove(n.CharIndex); }
            else if (vis && had) { ServerPackets.CharacterMove(u.Conn, (short)n.CharIndex, n.X, n.Y); }
        }
    }

    /// <summary>Un NPC apareció (spawn/respawn): create para los usuarios cuyo área lo cubre.</summary>
    public static void OnNpcSpawn(NpcManager.NpcInstance n)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u?.flags.UserLogged != true || u.Conn == null || u.Pos.Map != n.Map) continue;
            if (EnArea(u.Pos.X, u.Pos.Y, n.X, n.Y) && u.VisibleNpcs.Add(n.CharIndex))
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
}
