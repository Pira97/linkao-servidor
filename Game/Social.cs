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

    // =============================== LISTA DE AMIGOS (1:1 VB6) ===============================

    /// <summary>
    /// HandleAddAmigo (Protocol.bas:19867). caso=1 envía solicitud; caso=2 confirma (/FACCEPT).
    /// 'nombre' es el otro personaje; tUser su userIndex online (0 = offline).
    /// </summary>
    public static void AddAmigo(int userIndex, string nombre, byte caso)
    {
        var u = UserListManager.UserList[userIndex];
        int tUser = UserListManager.NameIndex(nombre);

        if (caso == 1) // Mandar solicitud de amistad
        {
            // Validaciones del lado del solicitante (NO requieren que el otro esté online).
            if (string.Equals(u.Name, nombre, StringComparison.OrdinalIgnoreCase))
            { ServerPackets.ConsoleMsg(u.Conn, "No puedes agregarte a tu propia lista de amigos.", FONT_INFO); return; }
            if (NoTieneEspacioAmigos(u))
            { ServerPackets.ConsoleMsg(u.Conn, "La lista de amigos está llena.", FONT_INFO); return; }
            if (BuscarSlotAmigoName(u, nombre))
            { ServerPackets.ConsoleMsg(u.Conn, nombre + " ya está en tu lista de amigos.", FONT_INFO); return; }

            if (tUser > 0)
            {
                // Destinatario ONLINE: validación extra (su lista llena) + aviso en vivo.
                var t = UserListManager.UserList[tUser];
                if (NoTieneEspacioAmigos(t))
                { ServerPackets.ConsoleMsg(u.Conn, "La lista de amigos del jugador está llena.", FONT_INFO); return; }
                ServerPackets.ConsoleMsg(u.Conn, $"{t.Name} fue agregado a tu lista de amigos, espera confirmación.", FONT_INFO);
                ServerPackets.ConsoleMsg(t.Conn, $"{u.Name} te envió una solicitud de amistad. Abrí la solapa Amigos para aceptarla o rechazarla.", FONT_INFO);
                t.QuienAmigo = u.Name;
                ServerPackets.AmigoRequest(t.Conn, u.Name);     // que aparezca en su panel
                AmigoRequestStore.Set(t.Name, u.Name);          // persistir por si se desconecta sin aceptar
            }
            else
            {
                // Destinatario OFFLINE: si el personaje existe, persistir la solicitud (se entrega al loguear).
                if (!CharLoader.PersonajeExiste(nombre))
                { ServerPackets.ConsoleMsg(u.Conn, "Ese personaje no existe.", FONT_INFO); return; }
                AmigoRequestStore.Set(nombre, u.Name);
                ServerPackets.ConsoleMsg(u.Conn, $"{nombre} no está conectado. La solicitud le llegará cuando entre al juego.", FONT_INFO);
            }
        }
        else if (caso == 2) // Confirmar solicitud
        {
            if (!IntentarAgregarAmigo(userIndex, tUser, out string razon))
            { ServerPackets.ConsoleMsg(u.Conn, razon, FONT_INFO); return; }
            var t = UserListManager.UserList[tUser];
            if (u.QuienAmigo == null || u.QuienAmigo.Length < 3) return;
            if (!string.Equals(u.QuienAmigo, t.Name, StringComparison.OrdinalIgnoreCase))
            { ServerPackets.ConsoleMsg(u.Conn, "Accion invalida", FONT_INFO); return; }

            byte slot = BuscarSlotAmigoVacio(u);
            u.Amigos[slot].Nombre = t.Name;
            slot = BuscarSlotAmigoVacio(t);
            t.Amigos[slot].Nombre = u.Name;

            ServerPackets.ConsoleMsg(u.Conn, $"{t.Name} esta jugando en Mohurall (Argentina).", FONT_INFO);
            ServerPackets.ConsoleMsg(t.Conn, $"{u.Name} esta jugando en Mohurall (Argentina).", FONT_INFO);
            t.flags.CantidadAmigos++;
            u.flags.CantidadAmigos++;
            if (u.flags.CantidadAmigos == 1) u.flags.CheckAmigos = 1;
            if (t.flags.CantidadAmigos == 1) t.flags.CheckAmigos = 1;
            slot = ObtenerIndexLibre(u);
            if (slot > 0) u.Amigos[slot].index = tUser;
            slot = ObtenerIndexLibre(t);
            if (slot > 0) t.Amigos[slot].index = userIndex;
            u.QuienAmigo = "";

            // (NUEVO) refrescar el panel de la solapa Amigos de ambos jugadores y
            // limpiar la solicitud pendiente del que aceptó (memoria + disco).
            AmigoRequestStore.Clear(u.Name);
            ServerPackets.AmigoRequest(u.Conn, "");
            SendAmigosList(userIndex);
            SendAmigosList(tUser);
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
        AmigoRequestStore.Clear(u.Name); // también del store persistente
        ServerPackets.ConsoleMsg(u.Conn, $"Rechazaste la solicitud de amistad de {nombre}.", FONT_INFO);
        ServerPackets.AmigoRequest(u.Conn, ""); // limpiar la solicitud del panel
    }

    /// <summary>
    /// (NUEVO, no VB6) Al loguear, entrega la solicitud de amistad pendiente persistida (si la hay):
    /// setea QuienAmigo, la muestra en el panel y avisa por consola. Se llama desde LoginFlow.EnterWorld.
    /// </summary>
    public static void DeliverPendingAmigoRequest(int userIndex)
    {
        if (userIndex <= 0) return;
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.Conn == null) return;
        string req = AmigoRequestStore.Get(u.Name);
        if (string.IsNullOrEmpty(req) || req.Length < 3) return;
        // Si ya son amigos (la aceptó en otra sesión por otra vía), limpiar y salir.
        if (BuscarSlotAmigoName(u, req)) { AmigoRequestStore.Clear(u.Name); return; }
        u.QuienAmigo = req;
        ServerPackets.AmigoRequest(u.Conn, req);
        ServerPackets.ConsoleMsg(u.Conn, $"{req} te envió una solicitud de amistad mientras no estabas. Abrí la solapa Amigos para aceptarla o rechazarla.", FONT_INFO);
    }

    /// <summary>HandleDelAmigo (Protocol.bas:19955). Quita 'nick' de la lista (mutuo si está online).</summary>
    public static void DelAmigo(int userIndex, string nick)
    {
        var u = UserListManager.UserList[userIndex];
        if (!CharLoader.PersonajeExiste(nick)) return;

        byte slot = 0;
        for (int i = 1; i <= u.flags.CantidadAmigos; i++)
            if (string.Equals(nick, u.Amigos[i].Nombre, StringComparison.OrdinalIgnoreCase)) { slot = (byte)i; break; }

        if (slot <= 0 || slot > u.flags.CantidadAmigos) return;
        if (u.Amigos[slot].Nombre == VACIO) return;

        int tUser = UserListManager.NameIndex(u.Amigos[slot].Nombre);
        ServerPackets.ConsoleMsg(u.Conn, $"{u.Amigos[slot].Nombre} fue quitado de tu lista de amigos.", FONT_INFO);

        // Resetear/compactar el slot propio.
        byte cantidad = u.flags.CantidadAmigos;
        if (slot == cantidad)
        {
            u.Amigos[slot].Nombre = VACIO;
            u.QuienAmigo = "";
        }
        else
        {
            for (int looper = slot; looper < cantidad; looper++)
            {
                u.Amigos[looper].Nombre = u.Amigos[looper + 1].Nombre;
                u.Amigos[looper + 1].Nombre = VACIO;
                u.Amigos[looper].index = u.Amigos[looper + 1].index;
                u.Amigos[looper + 1].index = 0;
            }
            u.QuienAmigo = "";
        }
        u.flags.CantidadAmigos--;
        if (u.flags.CantidadAmigos == 0) u.flags.CheckAmigos = 0;

        // Si el amigo está online, quitarnos también de su lista.
        if (tUser > 0)
        {
            var t = UserListManager.UserList[tUser];
            if (BuscarSlotAmigoName(t, u.Name))
            {
                ServerPackets.ConsoleMsg(t.Conn, $"{u.Name} te ha quitado de su lista de amigos.", FONT_INFO);
                byte tslot = BuscarSlotAmigoNameSlot(t, u.Name);
                byte tcant = t.flags.CantidadAmigos;
                t.flags.CantidadAmigos--;
                if (t.flags.CantidadAmigos == 0) t.flags.CheckAmigos = 0;
                if (tslot == tcant)
                {
                    t.Amigos[tslot].Nombre = VACIO;
                    t.QuienAmigo = "";
                }
                else
                {
                    for (int looper = tslot; looper < tcant; looper++)
                    {
                        t.Amigos[looper].Nombre = t.Amigos[looper + 1].Nombre;
                        t.Amigos[looper + 1].Nombre = VACIO;
                        t.Amigos[looper].index = t.Amigos[looper + 1].index;
                        t.Amigos[looper + 1].index = 0;
                    }
                }
                SendAmigosList(tUser); // (NUEVO) refrescar panel del otro jugador
            }
        }
        SendAmigosList(userIndex); // (NUEVO) refrescar panel propio
    }

    /// <summary>HandleMsgAmigo (Protocol.bas:19777). Mensaje a todos los amigos online + a uno mismo.</summary>
    public static void MsgAmigos(int userIndex, string mensaje)
    {
        var u = UserListManager.UserList[userIndex];
        for (int i = 1; i <= u.flags.CantidadAmigos; i++)
        {
            // VB6 usa Amigos(i).index (cacheado al conectar). Resolvemos por nombre como respaldo robusto.
            int dest = u.Amigos[i].index > 0 ? u.Amigos[i].index : UserListManager.NameIndex(u.Amigos[i].Nombre);
            if (dest > 0)
            {
                var d = UserListManager.UserList[dest];
                if (d.flags.UserLogged && d.Conn != null)
                    ServerPackets.ConsoleMsg(d.Conn, $"[{u.Name}] {mensaje}", FONT_INFOBOLD4);
            }
        }
        ServerPackets.ConsoleMsg(u.Conn, $"[{u.Name}] {mensaje}", FONT_INFOBOLD4);
    }

    /// <summary>
    /// (NUEVO, no VB6) Envía la lista de amigos estructurada para el panel de la solapa Amigos
    /// (packet AmigosList 182). Por cada amigo: nombre + online (0/1) + mapa actual (0 si offline).
    /// </summary>
    public static void SendAmigosList(int userIndex)
    {
        if (userIndex <= 0) return;
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.Conn == null) return;

        var amigos = new System.Collections.Generic.List<(string Nombre, bool Online, int Mapa)>();
        for (int i = 1; i <= u.flags.CantidadAmigos; i++)
        {
            string nombre = u.Amigos[i].Nombre;
            if (string.IsNullOrEmpty(nombre) || nombre == VACIO) continue;
            int tUser = UserListManager.NameIndex(nombre);
            bool online = tUser > 0 && UserListManager.UserList[tUser].flags.UserLogged;
            int mapa = online ? UserListManager.UserList[tUser].Pos.Map : 0;
            amigos.Add((nombre, online, mapa));
        }
        ServerPackets.AmigosList(u.Conn, amigos);
        // (NUEVO) reenviar la solicitud pendiente (si hay) para que el panel la muestre.
        ServerPackets.AmigoRequest(u.Conn, (u.QuienAmigo != null && u.QuienAmigo.Length >= 3) ? u.QuienAmigo : "");
    }

    /// <summary>HandleOnAmigo (Protocol.bas:19825). Lista los amigos con su estado online/offline.</summary>
    public static void OnAmigos(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        string list = "";
        for (int i = 1; i <= u.flags.CantidadAmigos; i++)
        {
            string nombre = u.Amigos[i].Nombre;
            int tUser2 = UserListManager.NameIndex(nombre);
            bool ultimo = i == u.flags.CantidadAmigos;
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

    // --- Helpers de amigos (GameLogic.bas) ---

    /// <summary>BuscarSlotAmigoVacio: primer slot con Nombre="Vacio" (0 si ninguno).</summary>
    private static byte BuscarSlotAmigoVacio(User u)
    {
        for (int i = 1; i <= Constants.MAXAMIGOS; i++)
            if (u.Amigos[i].Nombre == VACIO) return (byte)i;
        return 0;
    }

    /// <summary>BuscarSlotAmigoName: true si 'nombre' está en la lista.</summary>
    private static bool BuscarSlotAmigoName(User u, string nombre)
    {
        for (int i = 1; i <= Constants.MAXAMIGOS; i++)
            if (string.Equals(u.Amigos[i].Nombre, nombre, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>BuscarSlotAmigoNameSlot: slot donde está 'nombre' (0 si no).</summary>
    private static byte BuscarSlotAmigoNameSlot(User u, string nombre)
    {
        for (int i = 1; i <= Constants.MAXAMIGOS; i++)
            if (string.Equals(u.Amigos[i].Nombre, nombre, StringComparison.OrdinalIgnoreCase)) return (byte)i;
        return 0;
    }

    /// <summary>NoTieneEspacioAmigos: true si los MAXAMIGOS slots están ocupados.</summary>
    private static bool NoTieneEspacioAmigos(User u)
    {
        int count = 0;
        for (int i = 1; i <= Constants.MAXAMIGOS; i++)
            if (u.Amigos[i].Nombre != VACIO) count++;
        return count == Constants.MAXAMIGOS;
    }

    /// <summary>ObtenerIndexLibre: primer slot con index<=0 (0 si ninguno).</summary>
    private static byte ObtenerIndexLibre(User u)
    {
        for (int i = 1; i <= Constants.MAXAMIGOS; i++)
            if (u.Amigos[i].index <= 0) return (byte)i;
        return 0;
    }

    /// <summary>IntentarAgregarAmigo (GameLogic.bas:1386): valida la posibilidad de agregar. razon = motivo del fallo.</summary>
    private static bool IntentarAgregarAmigo(int usuario, int otro, out string razon)
    {
        razon = "";
        if (otro == 0 || usuario == 0) { razon = "Usuario offline."; return false; }
        if (usuario == otro) { razon = "No puedes agregarte a tu propia lista de amigos."; return false; }
        if (NoTieneEspacioAmigos(UserListManager.UserList[usuario])) { razon = "La lista de amigos está llena."; return false; }
        if (NoTieneEspacioAmigos(UserListManager.UserList[otro])) { razon = "La lista de amigos del jugador está llena."; return false; }
        if (BuscarSlotAmigoName(UserListManager.UserList[usuario], UserListManager.UserList[otro].Name))
        { razon = UserListManager.UserList[otro].Name + " ya está en tu lista de amigos"; return false; }
        return true;
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
