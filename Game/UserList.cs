using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Reemplaza el array global UserList() de VB6 y sus helpers (NameIndex,
/// CuentaConectada, etc.). Indexado base-1 igual que VB6: el slot 0 no se usa.
/// </summary>
public static class UserListManager
{
    public const int MaxUsers = 1000;

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

        // Cancelar comercio usuario-a-usuario en curso (libera al otro participante).
        if (u.Trade != null) UserTrade.Cancel(userIndex);

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
        // Montado/navegando: NO se limpian los flags al desloguear — deben persistir para reaparecer
        // montado/en barca tal cual se veía en el render (igual que LogoutToCharList). El [INIT] del
        // .chr igual se guarda con el body a pie (CharSaver.AparienciaAPie detecta estos flags), y el
        // loader reconstruye el body de montura/barca al reloguear desde Montando/Navegando + el slot.
        // Si tenía atributos buffeados/debuffeados, restaurar para no persistir valores temporales.
        if (u.flags.TomoPocion) Combat.RestaurarAtributos(u);
        // Si tenía un portal en curso/abierto, cerrarlo para no dejar el objeto y la salida colgados.
        if (u.PortalTime > 0) GameTimer.CancelarPortal(u);

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
