using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Reemplaza el array global UserList() de VB6 y sus helpers (NameIndex,
/// CuentaConectada, etc.). Indexado base-1 igual que VB6: el slot 0 no se usa.
/// </summary>
public static class UserListManager
{
    public const int MaxUsers = 1000;

    /// <summary>
    /// Candado global del estado del mundo. El server VB6 era de UN solo hilo: toda la lógica
    /// (mover, atacar, loguear, IA de NPCs, eventos) corría secuencialmente, así que el código
    /// migrado asume que NADIE más toca UserList[]/NPCs/HashSets de visibilidad mientras él los
    /// modifica. En C# eso se rompió: hay un hilo de recepción POR conexión (handlers de packets),
    /// el hilo del flush/IA (TickAI, TickRespawns, eventos) y OnClose (desconexión) mutando el mismo
    /// estado A LA VEZ. Un HashSet de visibilidad mutado por dos hilos lanza excepción; si cae en el
    /// hilo del flush, la tarea muere callada y el server se "congela" sin error (bug de login).
    /// Tomar SIEMPRE este lock alrededor de cualquier mutación del mundo restaura la semántica
    /// monohilo del VB6. Es el lock más externo: si se anida con otros (incoming/_gate de subsistemas)
    /// va primero, para que el orden sea consistente y no haya deadlock.
    /// </summary>
    public static readonly object GameLock = new();

    // 1..MaxUsers (índice 0 ignorado).
    public static readonly User[] UserList = new User[MaxUsers + 1];

    public static int LastUser;

    static UserListManager()
    {
        for (int i = 1; i <= MaxUsers; i++) UserList[i] = new User();
    }

    /// <summary>Asigna un slot libre a una conexión nueva (equivale a NextOpenUser).</summary>
    public static int NextOpenUser()
    {
        for (int i = 1; i <= MaxUsers; i++)
            if (UserList[i].Conn == null && !UserList[i].flags.UserLogged)
                return i;
        return 0;
    }

    /// <summary>NameIndex: devuelve el UserIndex logueado con ese nombre, o 0.</summary>
    public static int NameIndex(string name)
    {
        for (int i = 1; i <= LastUser; i++)
            if (UserList[i].flags.UserLogged &&
                string.Equals(UserList[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
    }

    /// <summary>Devuelve el usuario logueado con ese nombre, o null.</summary>
    public static User GetByName(string name)
    {
        int i = NameIndex(name);
        return i > 0 ? UserList[i] : null;
    }

    /// <summary>Cuenta cuántos usuarios logueados hay con esa cuenta.</summary>
    public static int CuentaConectada(string account)
    {
        int n = 0;
        for (int i = 1; i <= LastUser; i++)
            if (UserList[i].flags.UserLogged &&
                string.Equals(UserList[i].Account, account, StringComparison.OrdinalIgnoreCase))
                n++;
        return n;
    }

    public static int OnlineCount()
    {
        int n = 0;
        for (int i = 1; i <= LastUser; i++)
            if (UserList[i].flags.UserLogged) n++;
        return n;
    }

    /// <summary>Libera el slot de un usuario al desconectarse (equivale a Cerrar_Usuario, versión mínima).</summary>
    /// <summary>
    /// ExecuteQuit (Protocol.bas:6829): saca al personaje del mundo y lo deja listo para volver
    /// al panel de selección SIN cerrar el socket. Guarda el char, avisa CharacterRemove a los
    /// del mapa y resetea el estado del personaje, PERO conserva Conn (devuelve la cuenta para
    /// que el caller reenvíe la lista de personajes por la misma conexión).
    /// </summary>
    public static string LogoutToCharList(int userIndex)
    {
        if (userIndex < 1 || userIndex > MaxUsers) return "";
        var u = UserList[userIndex];
        string cuenta = u.Account;

        if (u.Trade != null) UserTrade.Cancel(userIndex);

        // VB6 CloseUser (TCP.bas:1782): al desloguear se DESEQUIPAN la montura (Desequipar→DoEquita:
        // desmonta, Montando=0, restaura el body a pie) y el ítem mágico ANTES de guardar. Sin esto,
        // el .chr quedaba con Montando=1 + montura equipada y al reloguear volvías montado con la
        // velocidad de montura.
        if (u.Invent.MonturaObjIndex > 0 && u.Invent.MonturaSlot > 0)
            Inventory.Desequipar(u, u.Invent.MonturaSlot);
        if (u.Invent.MagicIndex > 0 && u.Invent.MagicSlot > 0)
            Inventory.Desequipar(u, (byte)u.Invent.MagicSlot);

        // Mascota compañera persistente: no debe quedar huérfana vagando el mapa (Tipo/Nivel/Exp
        // ya quedan a salvo en el User, se reinvoca con el hechizo al reconectar).
        Combat.CancelarCasteoMascota(u, avisar: false); // si se va en pleno conjuro, no queda pendiente
        Combat.DespawnMascotaPersistente(u);

        if (u.flags.UserLogged && !string.IsNullOrEmpty(u.Name)) CharSaver.SaveUser(u);

        // Sonido de desconexión (SND_DESCONEXION=434): SOLO lo escucha el que se desloguea.
        if (u.Conn != null) ServerPackets.PlayWave(u.Conn, Sounds.DESCONEXION, (byte)u.Pos.X, (byte)u.Pos.Y);
        if (u.flags.UserLogged && u.Char.CharIndex > 0)
        {
            for (int i = 1; i <= LastUser; i++)
            {
                if (i == userIndex) continue;
                var other = UserList[i];
                if (!other.flags.UserLogged || other.Conn == null) continue;
                if (other.Pos.Map != u.Pos.Map) continue;
                ServerPackets.CharacterRemove(other.Conn, u.Char.CharIndex);
            }
        }

        // [[b4_usersbymap]] GAP identificado en la auditoría B4.3: este camino (volver al panel de
        // personajes sin cerrar el socket) NO pasa por AreaVisibility.OnUserLeave — hace su propio
        // broadcast manual arriba. Sacar del índice acá a mano, antes de resetear Pos.
        UsersByMapIndex.Remove(u.Pos.Map, userIndex);

        u.flags.KillStreak = 0;
        u.flags.UserLogged = false;
        u.Name = "";
        CharIndexPool.Free(u.Char.CharIndex);   // reusar el índice (el pool del cliente está acotado a 10000)
        u.Char.CharIndex = 0;
        u.Pos = default;
        // Conserva u.Conn y u.Account: el caller reenvía la lista de personajes.
        return cuenta;
    }

    public static void CloseUser(int userIndex)
    {
        if (userIndex < 1 || userIndex > MaxUsers) return;
        var u = UserList[userIndex];

        // Cancelar comercio usuario-a-usuario en curso (libera al otro participante). No transfiere
        // nada: mismo camino defensivo que Cancel, con su propio nombre/mensaje para el caso de
        // desconexión (ver UserTrade.Cleanup).
        if (u.Trade != null) UserTrade.Cleanup(userIndex);

        // Sacar del grupo (CleanupUserParty): disuelve o traspasa liderazgo y avisa al resto.
        if (u.PartyId > 0) PartySystem.Cleanup(userIndex);

        // Sacar de la cola/duelo de arena (CheckArenaDisconnect): da la victoria al rival.
        ArenaEvento.CheckArenaDisconnect(userIndex);

        // Sacar del torneo: descarta el equipo en inscripción o resuelve el combate si quedó vacío.
        TorneoEvento.CheckDisconnect(userIndex);

        // Centinela: si era el usuario bajo revisión, limpiar (CentinelaUserLogout).
        Centinela.OnUserLogout(userIndex);

        // Si estaba metamorfoseado, revertir antes de guardar para no persistir el body transformado.
        if (u.flags.Metamorfoseado == 1) Combat.RevertirMetamorfosis(userIndex);

        // VB6 CloseUser (TCP.bas:1782): al desloguear se DESEQUIPAN la montura (Desequipar→DoEquita:
        // desmonta, Montando=0, restaura el body a pie) y el ítem mágico ANTES de guardar. Sin esto,
        // el .chr quedaba con Montando=1 + montura equipada y al reloguear volvías montado con la
        // velocidad de montura. Navegando sí persiste (el VB6 no baja de la barca al desloguear).
        if (u.Invent.MonturaObjIndex > 0 && u.Invent.MonturaSlot > 0)
            Inventory.Desequipar(u, u.Invent.MonturaSlot);
        if (u.Invent.MagicIndex > 0 && u.Invent.MagicSlot > 0)
            Inventory.Desequipar(u, (byte)u.Invent.MagicSlot);

        // Si tenía atributos buffeados/debuffeados, restaurar para no persistir valores temporales.
        if (u.flags.TomoPocion) Combat.RestaurarAtributos(u);
        // Ídem el bonus de daño del Scroll del Trueno (vive en Stats.ExtraHIT).
        Combat.QuitarScrollTrueno(u, avisar: false);
        // Si tenía un portal en curso/abierto, cerrarlo para no dejar el objeto y la salida colgados.
        if (u.PortalTime > 0) GameTimer.CancelarPortal(u);

        // Mascota compañera persistente: no debe quedar huérfana vagando el mapa (Tipo/Nivel/Exp
        // ya quedan a salvo en el User, se reinvoca con el hechizo al reconectar).
        Combat.CancelarCasteoMascota(u, avisar: false); // si se va en pleno conjuro, no queda pendiente
        Combat.DespawnMascotaPersistente(u);

        // Persistir el personaje antes de soltar el slot (equivale a SaveUser en logout).
        if (u.flags.UserLogged && !string.IsNullOrEmpty(u.Name))
            CharSaver.SaveUser(u);

        // Avisar a los demás del mapa que este personaje se va (CharacterRemove) y limpiar la
        // visibilidad por área (lo saca del set de quienes lo veían).
        // Sonido de desconexión (SND_DESCONEXION=434): SOLO lo escucha el que se desloguea.
        if (u.flags.UserLogged && u.Conn != null)
            ServerPackets.PlayWave(u.Conn, Sounds.DESCONEXION, (byte)u.Pos.X, (byte)u.Pos.Y);
        if (u.flags.UserLogged && u.Char.CharIndex > 0)
            AreaVisibility.OnUserLeave(userIndex);

        u.flags.KillStreak = 0;
        u.flags.UserLogged = false;
        u.ConnIDValida = false;
        u.ConnID = -1;
        u.Conn = null;
        u.Name = "";
        u.Account = "";
        CharIndexPool.Free(u.Char.CharIndex);   // reusar el índice
        u.Char.CharIndex = 0;
    }
}
