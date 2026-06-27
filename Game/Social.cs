using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sistema social: susurros privados (Whisper) y lista de amigos (agregar, quitar,
/// mensaje a amigos online). Los mensajes llegan vía ConsoleMsg al destinatario.
/// </summary>
public static class Social
{
    private const byte FONT_TALK = 0;       // FONTTYPE_TALK
    private const byte FONT_INFO = 3;       // FONTTYPE_INFO
    private const byte FONT_INFOBOLD4 = 24; // FONTTYPE_INFOBOLD4 (Protocol.bas enum)
    private const string VACIO = "Vacio";

    /// <summary>Whisper: mensaje privado a un personaje por nombre.</summary>
    public static void Whisper(int userIndex, string nombre, string chat)
    {
        var u = UserListManager.UserList[userIndex];
        int dest = FindOnline(nombre);
        if (dest == 0)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Ese personaje no está online.", FONT_INFO);
            return;
        }
        var d = UserListManager.UserList[dest];
        ServerPackets.ConsoleMsg(d.Conn, $"{u.Name} te susurra: {chat}", FONT_TALK);
        ServerPackets.ConsoleMsg(u.Conn, $"Le susurras a {d.Name}: {chat}", FONT_TALK);
    }

    // =============================== LISTA DE AMIGOS (por CUENTA) ===============================
    // La amistad se guarda a nivel de CUENTA (.cnt [AMIGOS]), no de personaje: al agregar a un
    // personaje, quedan vinculadas las dos CUENTAS completas, y el panel muestra TODOS los
    // personajes de la cuenta amiga (ver AccountManager.GetAmigosCuentas/GetPersonajes).

    /// <summary>
    /// HandleAddAmigo (Protocol.bas:19867). caso=1 envía solicitud; caso=2 confirma (/FACCEPT).
    /// 'nombre' es el personaje del otro jugador; se resuelve a su cuenta para operar el vínculo.
    /// </summary>
    public static void AddAmigo(int userIndex, string nombre, byte caso)
    {
        var u = UserListManager.UserList[userIndex];
        string miCuenta = u.Account;
        int tUser = UserListManager.NameIndex(nombre);

        if (caso == 1) // Mandar solicitud de amistad
        {
            string otraCuenta = AccountManager.GetAccountByCharacter(nombre);
            if (string.IsNullOrEmpty(otraCuenta))
            { ServerPackets.ConsoleMsg(u.Conn, "Ese personaje no existe.", FONT_INFO); return; }
            if (string.Equals(miCuenta, otraCuenta, StringComparison.OrdinalIgnoreCase))
            { ServerPackets.ConsoleMsg(u.Conn, "No puedes agregar un personaje de tu propia cuenta.", FONT_INFO); return; }
            if (AccountManager.NoTieneEspacioAmigosCuenta(miCuenta))
            { ServerPackets.ConsoleMsg(u.Conn, "La lista de amigos está llena.", FONT_INFO); return; }
            if (AccountManager.EsAmigoCuenta(miCuenta, otraCuenta))
            { ServerPackets.ConsoleMsg(u.Conn, nombre + " ya está en tu lista de amigos.", FONT_INFO); return; }

            if (tUser > 0)
            {
                // Destinatario ONLINE: validación extra (su lista llena) + aviso en vivo.
                var t = UserListManager.UserList[tUser];
                if (AccountManager.NoTieneEspacioAmigosCuenta(otraCuenta))
                { ServerPackets.ConsoleMsg(u.Conn, "La lista de amigos del jugador está llena.", FONT_INFO); return; }
                ServerPackets.ConsoleMsg(u.Conn, $"{t.Name} fue agregado a tu lista de amigos, espera confirmación.", FONT_INFO);
                ServerPackets.ConsoleMsg(t.Conn, $"{u.Name} te envió una solicitud de amistad. Abrí la solapa Amigos para aceptarla o rechazarla.", FONT_INFO);
                t.QuienAmigo = u.Name;
                ServerPackets.AmigoRequest(t.Conn, u.Name);              // que aparezca en su panel
                AmigoRequestStore.Set(otraCuenta, miCuenta + "|" + u.Name); // persistir por si se desconecta sin aceptar
            }
            else
            {
                // Destinatario OFFLINE: persistir la solicitud (se entrega al loguear cualquier PJ de esa cuenta).
                AmigoRequestStore.Set(otraCuenta, miCuenta + "|" + u.Name);
                ServerPackets.ConsoleMsg(u.Conn, $"{nombre} no está conectado. La solicitud le llegará cuando entre al juego.", FONT_INFO);
            }
        }
        else if (caso == 2) // Confirmar solicitud
        {
            if (u.QuienAmigo == null || u.QuienAmigo.Length < 3) return;
            if (!string.Equals(u.QuienAmigo, nombre, StringComparison.OrdinalIgnoreCase))
            { ServerPackets.ConsoleMsg(u.Conn, "Accion invalida", FONT_INFO); return; }

            string otraCuenta = AccountManager.GetAccountByCharacter(u.QuienAmigo);
            if (string.IsNullOrEmpty(otraCuenta))
            { ServerPackets.ConsoleMsg(u.Conn, "Ese personaje ya no existe.", FONT_INFO); u.QuienAmigo = ""; return; }
            if (AccountManager.NoTieneEspacioAmigosCuenta(miCuenta))
            { ServerPackets.ConsoleMsg(u.Conn, "La lista de amigos está llena.", FONT_INFO); return; }
            if (AccountManager.NoTieneEspacioAmigosCuenta(otraCuenta))
            { ServerPackets.ConsoleMsg(u.Conn, "La lista de amigos del jugador está llena.", FONT_INFO); return; }

            AccountManager.AgregarAmigoCuenta(miCuenta, otraCuenta);
            AccountManager.AgregarAmigoCuenta(otraCuenta, miCuenta);

            string requesterName = u.QuienAmigo;
            u.QuienAmigo = "";
            AmigoRequestStore.Clear(miCuenta);
            ServerPackets.AmigoRequest(u.Conn, "");
            ServerPackets.ConsoleMsg(u.Conn, $"{requesterName} esta jugando en Mohurall (Argentina).", FONT_INFO);
            if (tUser > 0)
                ServerPackets.ConsoleMsg(UserListManager.UserList[tUser].Conn, $"{u.Name} esta jugando en Mohurall (Argentina).", FONT_INFO);

            // Refrescar el panel de TODOS los personajes online de ambas cuentas.
            RefrescarAmigosDeCuenta(miCuenta);
            RefrescarAmigosDeCuenta(otraCuenta);
        }
    }

    /// <summary>
    /// (NUEVO, no VB6) Rechaza la solicitud de amistad pendiente recibida de 'nombre'.
    /// Limpia QuienAmigo, avisa al solicitante (si está online) y limpia el panel del que rechaza.
    /// </summary>
    public static void RejectAmigo(int userIndex, string nombre)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null) return;
        // Solo se puede rechazar la solicitud que efectivamente está pendiente.
        if (string.IsNullOrEmpty(u.QuienAmigo) ||
            !string.Equals(u.QuienAmigo, nombre, StringComparison.OrdinalIgnoreCase))
        {
            ServerPackets.AmigoRequest(u.Conn, ""); // sincroniza: ya no hay solicitud
            return;
        }

        int tUser = UserListManager.NameIndex(u.QuienAmigo);
        if (tUser > 0)
        {
            var t = UserListManager.UserList[tUser];
            ServerPackets.ConsoleMsg(t.Conn, $"{u.Name} rechazó tu solicitud de amistad.", FONT_INFO);
        }
        u.QuienAmigo = "";
        AmigoRequestStore.Clear(u.Account); // también del store persistente (clave = cuenta)
        ServerPackets.ConsoleMsg(u.Conn, $"Rechazaste la solicitud de amistad de {nombre}.", FONT_INFO);
        ServerPackets.AmigoRequest(u.Conn, ""); // limpiar la solicitud del panel
    }

    /// <summary>
    /// (NUEVO, no VB6) Al loguear, entrega la solicitud de amistad pendiente persistida (si la hay):
    /// setea QuienAmigo, la muestra en el panel y avisa por consola. Se llama desde LoginFlow.EnterWorld.
    /// La solicitud está persistida por CUENTA: cualquier PJ que loguee de esa cuenta la recibe.
    /// </summary>
    public static void DeliverPendingAmigoRequest(int userIndex)
    {
        if (userIndex <= 0) return;
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.Conn == null || string.IsNullOrEmpty(u.Account)) return;
        string stored = AmigoRequestStore.Get(u.Account);
        if (string.IsNullOrEmpty(stored)) return;
        int sep = stored.IndexOf('|');
        string requesterCuenta = sep > 0 ? stored.Substring(0, sep) : stored;
        string requesterName = sep > 0 ? stored.Substring(sep + 1) : stored;
        if (string.IsNullOrEmpty(requesterName) || requesterName.Length < 3) return;
        // Si ya son amigos (la aceptó en otra sesión por otra vía), limpiar y salir.
        if (AccountManager.EsAmigoCuenta(u.Account, requesterCuenta)) { AmigoRequestStore.Clear(u.Account); return; }
        u.QuienAmigo = requesterName;
        ServerPackets.AmigoRequest(u.Conn, requesterName);
        ServerPackets.ConsoleMsg(u.Conn, $"{requesterName} te envió una solicitud de amistad mientras no estabas. Abrí la solapa Amigos para aceptarla o rechazarla.", FONT_INFO);
    }

    /// <summary>HandleDelAmigo (Protocol.bas:19955). Quita la cuenta del personaje 'nick' de la lista (mutuo).</summary>
    public static void DelAmigo(int userIndex, string nick)
    {
        var u = UserListManager.UserList[userIndex];
        string miCuenta = u.Account;
        string otraCuenta = AccountManager.GetAccountByCharacter(nick);
        if (string.IsNullOrEmpty(otraCuenta) || !AccountManager.EsAmigoCuenta(miCuenta, otraCuenta)) return;

        AccountManager.QuitarAmigoCuenta(miCuenta, otraCuenta);
        AccountManager.QuitarAmigoCuenta(otraCuenta, miCuenta);
        ServerPackets.ConsoleMsg(u.Conn, $"{nick} fue quitado de tu lista de amigos.", FONT_INFO);

        // Avisar a quien esté online de la cuenta amiga y refrescar los paneles de ambas cuentas.
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Conn != null &&
                string.Equals(o.Account, otraCuenta, StringComparison.OrdinalIgnoreCase))
                ServerPackets.ConsoleMsg(o.Conn, $"{u.Name} te ha quitado de su lista de amigos.", FONT_INFO);
        }
        RefrescarAmigosDeCuenta(miCuenta);
        RefrescarAmigosDeCuenta(otraCuenta);
    }

    /// <summary>HandleMsgAmigo (Protocol.bas:19777). Mensaje a todos los personajes online de las cuentas amigas.</summary>
    public static void MsgAmigos(int userIndex, string mensaje)
    {
        var u = UserListManager.UserList[userIndex];
        var cuentasAmigas = AccountManager.GetAmigosCuentas(u.Account);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var d = UserListManager.UserList[i];
            if (d == null || !d.flags.UserLogged || d.Conn == null) continue;
            if (cuentasAmigas.Contains(d.Account, StringComparer.OrdinalIgnoreCase))
                ServerPackets.ConsoleMsg(d.Conn, $"[{u.Name}] {mensaje}", FONT_INFOBOLD4);
        }
        ServerPackets.ConsoleMsg(u.Conn, $"[{u.Name}] {mensaje}", FONT_INFOBOLD4);
    }

    /// <summary>
    /// (NUEVO, no VB6) Envía la lista de amigos estructurada para el panel de la solapa Amigos
    /// (packet AmigosList 182). Por cada CUENTA amiga se listan TODOS sus personajes: nombre +
    /// online (0/1) + mapa actual (0 si offline).
    /// </summary>
    public static void SendAmigosList(int userIndex)
    {
        if (userIndex <= 0) return;
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.Conn == null || string.IsNullOrEmpty(u.Account)) return;

        var amigos = new System.Collections.Generic.List<(string Nombre, bool Online, int Mapa)>();
        foreach (var cuentaAmiga in AccountManager.GetAmigosCuentas(u.Account))
        {
            foreach (var nombre in AccountManager.GetPersonajes(cuentaAmiga))
            {
                int tUser = UserListManager.NameIndex(nombre);
                bool online = tUser > 0 && UserListManager.UserList[tUser].flags.UserLogged;
                int mapa = online ? UserListManager.UserList[tUser].Pos.Map : 0;
                amigos.Add((nombre, online, mapa));
            }
        }
        ServerPackets.AmigosList(u.Conn, amigos);
        // (NUEVO) reenviar la solicitud pendiente (si hay) para que el panel la muestre.
        ServerPackets.AmigoRequest(u.Conn, (u.QuienAmigo != null && u.QuienAmigo.Length >= 3) ? u.QuienAmigo : "");
    }

    /// <summary>Refresca el panel de Amigos de todos los personajes ONLINE de una cuenta.</summary>
    private static void RefrescarAmigosDeCuenta(string cuenta)
    {
        if (string.IsNullOrEmpty(cuenta)) return;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Conn != null &&
                string.Equals(o.Account, cuenta, StringComparison.OrdinalIgnoreCase))
                SendAmigosList(i);
        }
    }

    /// <summary>HandleOnAmigo (Protocol.bas:19825). Lista los personajes de las cuentas amigas, online/offline.</summary>
    public static void OnAmigos(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        var nombres = new System.Collections.Generic.List<string>();
        foreach (var cuentaAmiga in AccountManager.GetAmigosCuentas(u.Account))
            nombres.AddRange(AccountManager.GetPersonajes(cuentaAmiga));

        string list = "";
        for (int i = 0; i < nombres.Count; i++)
        {
            string nombre = nombres[i];
            int tUser2 = UserListManager.NameIndex(nombre);
            bool ultimo = i == nombres.Count - 1;
            if (tUser2 <= 0)
                list += nombre + "(Offline)" + (ultimo ? "." : ",");
            else
            {
                int mapa = UserListManager.UserList[tUser2].Pos.Map;
                list += nombre + "(Online)(Mapa " + mapa + ")" + (ultimo ? "." : ", ");
            }
        }
        if (list.Length > 0)
            ServerPackets.ConsoleMsg(u.Conn, "Amigos conectados: " + list, FONT_INFO);
        else
            ServerPackets.ConsoleMsg(u.Conn, "Tu lista de amigos está vacía.", FONT_INFO);
    }

    // eCiudad (Declares.bas:174). Hogar → mapa donde está el revividor de esa ciudad (tabla Equidad VB6).
    private const byte cNix = 1, cIlliandor = 2, cUllathorpe = 3, cBanderbill = 4, cRinkel = 5,
                       cLindos = 7, cARGHAL = 8, cTIAMA = 9, cORAC = 10, cSURAMEI = 11, cNueva = 12;
    private const byte NPCTYPE_REVIVIDOR = 1;

    /// <summary>
    /// HandleSeleccionarHogar (Protocol.bas:19063) 1:1. caso0: valida Revividor a ≤5 y pide confirmación
    /// (ShowMessageBox accion 5). caso1: fija el hogar según el mapa actual (por ciudad/facción).
    /// </summary>
    public static void SeleccionarHogar(int userIndex, byte caso)
    {
        var u = UserListManager.UserList[userIndex];
        if (caso == 0)
        {
            if (u.TargetNpcCharIndex == 0)
            { ServerPackets.ConsoleMsg(u.Conn, "Primero tienes que seleccionar un personaje, haz click izquierdo sobre él.", FONT_INFO); return; }
            var npc = NpcManager.NpcByCharIndex(u.Pos.Map, u.TargetNpcCharIndex);
            if (npc == null || npc.NpcType != NPCTYPE_REVIVIDOR) return;
            if (Math.Abs(u.Pos.X - npc.X) + Math.Abs(u.Pos.Y - npc.Y) > 5)
            { ServerPackets.LocaleMsg(u.Conn, 8, "", 12, 1); return; }
            ServerPackets.ShowMessageBox(u.Conn, "", true, 5); // accion 5 = confirmar hogar
            return;
        }

        // caso 1: confirmar. Equidad = mapa "hogar" actual del usuario; si ya estás ahí, no cambia.
        int equidad = u.Hogar switch
        {
            1 => 34, 2 => 194, 3 => 1, 4 => 59, 5 => 20, 6 => 37, 7 => 62,
            8 => 151, 9 => 218, 10 => 180, 11 => 185, 12 => 111, _ => 0,
        };
        if (u.Pos.Map == equidad)
        { ServerPackets.ConsoleMsg(u.Conn, $"El mapa {u.Pos.Map} es tu hogar.", FONT_INFO); return; }

        // Mapas con hogar fijo, o por facción según el mapa actual.
        switch (u.Pos.Map)
        {
            case 20:  u.Hogar = cRinkel; break;
            case 151: u.Hogar = cARGHAL; break;
            case 218: u.Hogar = cTIAMA; break;
            case 180: u.Hogar = cORAC; break;
            case 112: u.Hogar = cNueva; break;
            default:
                if (Facciones.EsArmada(u) || Facciones.EsCiuda(u))
                {
                    switch (u.Pos.Map)
                    { case 1: u.Hogar = cUllathorpe; break; case 34: u.Hogar = cNix; break; case 59: u.Hogar = cBanderbill; break;
                      default: ServerPackets.ConsoleMsg(u.Conn, "Ciudad invalida.", FONT_INFO); return; }
                }
                else if (Facciones.EsRepu(u) || Facciones.EsMili(u))
                {
                    switch (u.Pos.Map)
                    { case 194: u.Hogar = cIlliandor; break; case 63: u.Hogar = cLindos; break; case 184: u.Hogar = cSURAMEI; break;
                      default: ServerPackets.ConsoleMsg(u.Conn, "Ciudad invalida.", FONT_INFO); return; }
                }
                else { ServerPackets.ConsoleMsg(u.Conn, "Ciudad invalida.", FONT_INFO); return; }
                break;
        }
        ServerPackets.ConsoleMsg(u.Conn, $"Tu nuevo hogar ahora es el mapa {u.Pos.Map}.", FONT_INFO);
    }

    private static int FindOnline(string nombre)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && string.Equals(o.Name, nombre, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }
}
