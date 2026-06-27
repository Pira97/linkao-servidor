using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Modo espía: un Dios ve EN VIVO la pantalla de otro jugador.
///
/// Cómo funciona, y por qué es tan poco código: los 119 paquetes que el servidor le manda
/// a alguien pasan todos por ServerPackets.Send(conn, p). Basta con que ahí, además de
/// encolarle el paquete al dueño, se le encole una copia al espía. El cliente del espía
/// recibe EXACTAMENTE los mismos bytes que el espiado, así que dibuja lo mismo: su mapa,
/// los personajes que lo rodean, su vida bajando, su inventario, los mensajes de su
/// consola. No hace falta nada instalado del lado del jugador ni ningún paquete nuevo.
///
/// El espejo tiene DOS mitades, y las dos son necesarias:
///
///   1. COPIAR todo lo que el servidor le manda al espiado.
///   2. SILENCIAR todo lo que el servidor le manda al espía (Rutear/_pasanIgual). Esta es
///      la mitad que faltaba: el espía sigue siendo un jugador vivo del mundo, así que su
///      propio HP regenerándose, su hambre, su clima y —lo peor— su propia visibilidad por
///      área le llegaban MEZCLADOS con los del espiado y le pisaban la pantalla. El caso
///      grave: el AOI del espía quedó anclado donde se paró, así que en cuanto el espiado
///      caminaba ~18 tiles su área lo dejaba de cubrir y le mandaba un CharacterRemove...
///      del personaje que su cliente está usando como "yo". Adiós pantalla.
///
/// Lo único que le llega al espía por su cuenta es TEXTO (consola, carteles) y las
/// respuestas a consultas que él mismo pidió con un comando: ver _pasanIgual.
///
/// Detalle que lo hace seguro: Connection.EnqueueOutgoing hace packet.ToArray(), o sea que
/// COPIA los bytes en vez de consumir el ByteQueue. Por eso el mismo paquete se puede
/// encolar en dos conexiones sin que el segundo reciba basura.
///
/// La "foto inicial" no se arma acá: el comando /espiar teletransporta al Dios (invisible)
/// hasta el espiado y recién DESPUÉS enciende el espejo, y el warp normal ya le manda el
/// mapa y todo lo que hay alrededor. Sin eso verías los cambios sobre una pantalla vacía.
///
/// Lo único que el espejo NO puede copiar es lo que el espiado tampoco recibe:
///   • SU PROPIO PASO. Camina por predicción de su cliente y el CharacterMove sale solo
///     hacia los demás, así que hay que mandárselo al espía a mano (SeguirAlEspiado).
///   • SU MOUSE, que no viaja en el protocolo. Se le pide al cliente del espiado que lo
///     reporte (EspiaReportarMouse) y se reenvía (MouseDelEspiado → EspiaMousePos).
/// Y además el cliente del Dios tiene que SABER que está de espectador (EspiaVista): sin
/// eso seguía mandando sus propios pasos y prediciéndolos sobre el muñeco del otro.
/// Esos tres paquetes son nuevos y solo se le mandan a clientes que declararon soporte
/// (Connection.Caps): el cliente Godot viejo no sabe saltear ids desconocidos.
///
/// SOLO PRIVILEGIO 9 (Dios). Lo valida Chat.cs antes de llamar acá.
/// </summary>
public static class Espia
{
    // Los DOS sentidos del espejo. Send() consulta los dos en cada paquete del servidor
    // (¿hay que copiarlo?, ¿hay que silenciarlo?) y con un solo diccionario uno de los dos
    // sería un recorrido lineal sobre todos los jugadores.
    private static readonly Dictionary<Connection, Connection> _espiaDe = new();  // espiado -> espía
    private static readonly Dictionary<Connection, Connection> _mirando = new();  // espía   -> espiado
    private static readonly object _candado = new();

    // Atajo sin lock para el caso normal (nadie espiando a nadie): Send() corre para CADA
    // paquete de CADA jugador y no puede pagar un lock por paquete si no hay ningún espejo.
    private static volatile bool _hayEspejos;

    // Paquetes PROPIOS que le siguen llegando al espía aunque su stream esté silenciado.
    // Criterio: texto (no toca nada de lo que se dibuja del mundo) y respuestas a consultas
    // que él mismo pidió tecleando un comando. Todo lo demás suyo se descarta.
    private static readonly HashSet<byte> _pasanIgual = new()
    {
        (byte)ServerPacketID.Disconnect,      // que lo puedan echar
        (byte)ServerPacketID.ConsoleMsg,      // consola: sus comandos le contestan igual
        (byte)ServerPacketID.LocaleMsg,
        (byte)ServerPacketID.ShowMessageBox,
        (byte)ServerPacketID.SendMsgBox,
        (byte)ServerPacketID.Pong,            // latencia del cliente
        // Respuestas del panel GM (las pide él, no dibujan mundo): /quien, /donde, ficha de
        // personaje, lista de NPCs del mapa, reportes y editor de objetos.
        (byte)ServerPacketID.UserNameList,
        (byte)ServerPacketID.MostrarUbicacion,
        (byte)ServerPacketID.CharacterInfo,
        (byte)ServerPacketID.MapNpcsList,
        (byte)ServerPacketID.ReportSubmitted,
        (byte)ServerPacketID.ReportList,
        (byte)ServerPacketID.ReportDetail,
        (byte)ServerPacketID.ReportNotify,
        (byte)ServerPacketID.ObjEditorList,
        (byte)ServerPacketID.ObjEditorDetail,
        (byte)ServerPacketID.ObjEditorResult,
        (byte)ServerPacketID.NpcCatalog,
    };

    // Escape del silenciador. Lo poco que el servidor SÍ tiene que mandarle al espía y no
    // viene del espiado (su paso, el re-apuntado del charIndex) se manda adentro de
    // Directo(). Es por hilo: dos espías atendidos en paralelo no se pisan.
    [ThreadStatic] private static bool _directo;

    /// <summary>
    /// Manda algo al espía SALTEANDO el silenciador. Sin esto, Send() lo tiraría por ser un
    /// paquete dirigido a él.
    /// </summary>
    public static void Directo(Connection espia, Action<Connection> enviar)
    {
        if (espia == null) return;
        bool previo = _directo;
        _directo = true;
        try { enviar(espia); }
        finally { _directo = previo; }
    }

    // Lo contrario de Directo: manda algo AL ESPIADO sin que el espejo le pase la copia al
    // espía. Es para las órdenes internas que no son "lo que ve el jugador" (hoy: el pedido
    // de que reporte el mouse) — copiárselas al Dios haría que él también las obedezca.
    [ThreadStatic] private static bool _sinEspejo;

    /// <summary>Manda algo al espiado SIN copiárselo al espía.</summary>
    public static void SinEspejo(Action enviar)
    {
        bool previo = _sinEspejo;
        _sinEspejo = true;
        try { enviar(); }
        finally { _sinEspejo = previo; }
    }

    /// <summary>
    /// Decisión de ruteo de UN paquete, resuelta con un solo lock (la llama Send por cada
    /// paquete del servidor). Devuelve true si el paquete NO debe llegarle a 'conn' (es un
    /// espía y esto es ruido suyo), y deja en 'espia' la conexión que debe recibir una copia
    /// (si 'conn' es un espiado).
    /// </summary>
    public static bool Rutear(Connection conn, ByteQueue p, out Connection espia)
    {
        espia = null;
        if (!_hayEspejos || conn == null) return false;
        lock (_candado)
        {
            if (!_sinEspejo && _espiaDe.TryGetValue(conn, out var e)) espia = e;
            if (_directo || !_mirando.ContainsKey(conn)) return false;
        }
        return p.Length == 0 || !_pasanIgual.Contains(p.PeekByte());
    }

    /// <summary>Conexión del Dios que está espiando a este usuario, o null.</summary>
    public static Connection DeQuien(Connection espiado)
    {
        if (espiado == null || !_hayEspejos) return null;
        lock (_candado)
        {
            return _espiaDe.TryGetValue(espiado, out var e) ? e : null;
        }
    }

    /// <summary>¿Este Dios está espiando a alguien? Devuelve a quién (o null).</summary>
    public static Connection AQuienEspia(Connection espia)
    {
        if (espia == null || !_hayEspejos) return null;
        lock (_candado)
        {
            return _mirando.TryGetValue(espia, out var e) ? e : null;
        }
    }

    /// <summary>
    /// El espía ve por los ojos del espiado: su cliente cree que su personaje ES el del
    /// otro (ver Chat.cs::Cmd_Espiar), así que DENTRO de un mapa la cámara lo sigue sola.
    /// Se llama en cada paso del espiado y hace las dos cosas que el espejo no puede copiar
    /// (porque el espiado tampoco las recibe): su propio movimiento y el cambio de mapa.
    /// </summary>
    public static void SeguirAlEspiado(int userIndexEspiado)
    {
        if (!_hayEspejos) return;
        var espiado = UserListManager.UserList[userIndexEspiado];
        if (espiado?.Conn == null) return;
        var connEspia = DeQuien(espiado.Conn);
        if (connEspia == null) return;

        // El espejo copia lo que el servidor le manda AL ESPIADO, y el espiado NUNCA recibe
        // su propio movimiento: camina por predicción del cliente, y el CharacterMove sale
        // solo hacia LOS DEMÁS. O sea que su paso no viajaba por ningún lado; el espía solo
        // se enteraba cuando algún otro paquete corregía la posición, y de ahí los tirones.
        // Mandándoselo explícitamente, su cliente lo camina como a cualquier personaje.
        var (px, py) = Continuous.Pos(espiado.Pos.Map, espiado.Pos.X, espiado.Pos.Y);
        Directo(connEspia, c => ServerPackets.CharacterMove(c, espiado.Char.CharIndex, px, py));

        // Cambio de mapa: al espía hay que llevarlo también. Su cliente ya se entera por el
        // espejo (el ChangeMap del espiado se copia), pero su personaje del SERVIDOR tiene
        // que viajar igual para no quedar tirado en el mapa viejo cuando corte el espionaje.
        int idxEspia = IndiceDe(connEspia);
        if (idxEspia == 0) return;
        var e = UserListManager.UserList[idxEspia];
        if (e == null || e.Pos.Map == espiado.Pos.Map) return;

        Movement.WarpUser(idxEspia, (short)espiado.Pos.Map, espiado.Pos.X, espiado.Pos.Y,
                          fx: false, sonido: false);
        e.flags.Invisible = 1;
        e.flags.InvisibleExpira = double.MaxValue;
        // El warp lo dio de alta en el mapa nuevo: sacarlo de la pantalla de los presentes.
        for (int j = 1; j <= UserListManager.LastUser; j++)
        {
            var o = UserListManager.UserList[j];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Conn != e.Conn
                && o.Pos.Map == e.Pos.Map)
            {
                o.VisibleUsers.Remove(idxEspia);   // que el AOI lo pueda volver a crear al salir
                ServerPackets.CharacterRemove(o.Conn, e.Char.CharIndex);
            }
        }
        // Defensivo: el espejo ya le mandó el ChangeMap + CharacterCreate del espiado, pero
        // que su "yo" siga apuntando al otro es LO que hace que la cámara lo siga.
        Directo(connEspia, c => ServerPackets.UserCharIndexInServer(c, espiado.Char.CharIndex));
    }

    public static void Empezar(User espiaU, User espiadoU)
    {
        var espia = espiaU?.Conn;
        var espiado = espiadoU?.Conn;
        if (espia == null || espiado == null || espia == espiado) return;
        Connection desplazado = null;
        lock (_candado)
        {
            // Un Dios espía a UNO por vez: si ya estaba mirando a otro, se corta ese.
            if (_mirando.TryGetValue(espia, out var anterior)) QuitarPar(anterior, espia);
            // Y a un jugador lo espía UN Dios por vez (el espejo es un diccionario por
            // espiado). Si ya lo estaba mirando otro, a ese hay que devolverle la pantalla:
            // si no, quedaba silenciado para siempre viendo un mundo congelado.
            if (_espiaDe.TryGetValue(espiado, out var otroDios) && otroDios != espia)
            {
                QuitarPar(espiado, otroDios);
                desplazado = otroDios;
            }
            _espiaDe[espiado] = espia;
            _mirando[espia] = espiado;
            _hayEspejos = true;
        }
        if (desplazado != null)
        {
            ServerPackets.EspiaVista(desplazado, false, 0, "");
            DevolverPantalla(desplazado, "Otro Dios empezó a espiar a ese jugador.");
        }
        // Al cliente del Dios: "estás de espectador, el personaje que ves como tuyo es de
        // este otro". Con eso bloquea el input y camina los pasos que le lleguen. Va con
        // Directo porque a partir de Empezar su stream propio está silenciado.
        Directo(espia, c => ServerPackets.EspiaVista(c, true, espiadoU.Char.CharIndex, espiadoU.Name));
        // Y al cliente del espiado: que empiece a reportar el mouse (sin avisarle nada).
        ServerPackets.EspiaReportar(espiado, true);
    }

    /// <summary>Corta lo que este Dios esté espiando. Devuelve false si no espiaba a nadie.</summary>
    public static bool Terminar(Connection espia)
    {
        if (espia == null) return false;
        Connection espiado;
        lock (_candado)
        {
            if (!_mirando.TryGetValue(espia, out espiado)) return false;
            QuitarPar(espiado, espia);
        }
        // Fuera del lock y con el espejo YA apagado: los dos avisos salen normales.
        ServerPackets.EspiaVista(espia, false, 0, "");
        ServerPackets.EspiaReportar(espiado, false);
        return true;
    }

    /// <summary>Saca el par espiado→espía de los dos diccionarios. Llamar con el lock tomado.</summary>
    private static void QuitarPar(Connection espiado, Connection espia)
    {
        // Solo borra el espejo del espiado si sigue siendo el de ESTE Dios (si otro tomó el
        // relevo mientras tanto, es suyo y no se toca).
        if (_espiaDe.TryGetValue(espiado, out var actual) && actual == espia) _espiaDe.Remove(espiado);
        if (_mirando.TryGetValue(espia, out var mira) && mira == espiado) _mirando.Remove(espia);
        _hayEspejos = _espiaDe.Count > 0;
    }

    /// <summary>Avisa y le reconstruye la pantalla a un Dios al que se le cortó el espejo.</summary>
    private static void DevolverPantalla(Connection espia, string motivo)
    {
        // Bajo GameLock: RestaurarPantalla toca UserList y la visibilidad de los demás, igual
        // que CloseUser (ver GameServer.OnClose). Es reentrante: si ya veníamos de un handler
        // (que lo toma en HandleIncomingData), este lock no cuesta nada.
        lock (UserListManager.GameLock)
        {
            int idx = IndiceDe(espia);
            if (idx == 0) return;
            var u = UserListManager.UserList[idx];
            // Espectador puro: no tiene pantalla propia que devolver (nunca entró al mundo).
            // Solo se le avisa; su cliente vuelve a la lista de jugadores.
            if (u != null && u.flags.UserLogged != true)
            {
                ServerPackets.ConsoleMsg(espia, motivo, 4);
                return;
            }
            // El espionaje lo dejó invisible a mano (Chat.EsconderDelMapa, sin vencimiento) y
            // no hay comando para sacársela: si el espejo se corta solo, hay que devolverlo
            // al mundo o queda de fantasma para siempre.
            if (u != null) { u.flags.Invisible = 0; u.flags.InvisibleExpira = 0; }
            ServerPackets.ConsoleMsg(espia, motivo, 4);
            RestaurarPantalla(idx);
        }
    }

    /// <summary>
    /// Limpieza al desconectarse cualquiera. Hay que llamarla SIEMPRE que se cierra una
    /// conexión: si no, una conexión muerta queda en el diccionario y, peor, los objetos
    /// Connection se reciclan, así que el espejo podría quedar apuntando a un jugador
    /// nuevo que reusó ese objeto.
    ///
    /// Si el que se fue era el ESPIADO, al Dios hay que devolverle su pantalla: quedaría
    /// mirando un mundo congelado y sin forma de volver (el /espiar off ya no encontraría
    /// el espejo).
    /// </summary>
    public static void Olvidar(Connection quien)
    {
        if (quien == null) return;
        // Observadores de bots: no son espejos (no dependen de _hayEspejos), pero igual hay que
        // sacarlos o la conexión muerta queda en el diccionario y se refresca para siempre.
        lock (_candado) _obsNpc.Remove(quien);
        if (!_hayEspejos) return;
        Connection espiaHuerfano = null, espiadoSuelto = null;
        lock (_candado)
        {
            // Se fue el espiado → el Dios que lo miraba queda huérfano.
            if (_espiaDe.TryGetValue(quien, out var suEspia)) { QuitarPar(quien, suEspia); espiaHuerfano = suEspia; }
            // Se fue el espía → se apaga su espejo y el otro deja de reportar el mouse.
            if (_mirando.TryGetValue(quien, out var suEspiado)) { QuitarPar(suEspiado, quien); espiadoSuelto = suEspiado; }
        }
        if (espiadoSuelto != null) ServerPackets.EspiaReportar(espiadoSuelto, false);
        if (espiaHuerfano != null)
        {
            ServerPackets.EspiaVista(espiaHuerfano, false, 0, "");
            DevolverPantalla(espiaHuerfano, "El jugador que espiabas se desconectó.");
        }
    }

    /// <summary>
    /// Le devuelve al espía SU pantalla. El cliente quedó con todo lo del otro (mapa, chars,
    /// barras, inventario, hechizos), así que no se parchea pieza por pieza: se le manda de
    /// nuevo la misma ráfaga que arma el mundo al entrar (LoginFlow.EnterWorld), que es el
    /// camino ya probado.
    /// </summary>
    public static void RestaurarPantalla(int idxEspia)
    {
        var u = UserListManager.UserList[idxEspia];
        if (u?.Conn == null) return;
        var conn = u.Conn;

        // Sacarlo de la vista de todos y limpiar los sets: EsconderDelMapa mandó los
        // CharacterRemove pero dejó a los observadores creyendo que lo veían, así que sin
        // esto el AOI no le mandaría a nadie el CharacterCreate de vuelta y quedaría de
        // fantasma para el resto del mapa.
        AreaVisibility.OnUserLeave(idxEspia);

        ServerPackets.ChangeMap(conn, (short)u.Pos.Map, 0);       // el cliente borra char_list
        ServerPackets.UserCharIndexInServer(conn, u.Char.CharIndex);  // volvés a ser vos
        LoginFlow.SendCharCreate(conn, u);
        if (u.flags.Oculto == 1 || u.flags.Invisible == 1)
            ServerPackets.SetInvisible(conn, u.Char.CharIndex, true);

        ServerPackets.UpdateUserStats(conn, u);
        ServerPackets.UpdateHungerAndThirst(conn, u);
        for (byte slot = 1; slot <= Constants.MAX_INVENTORY_SLOTS; slot++)
            ServerPackets.ChangeInventorySlot(conn, u, slot);
        for (byte slot = 1; slot <= Constants.MAXUSERHECHIZOS; slot++)
        {
            short h = u.Stats.UserHechizos[slot];
            ServerPackets.ChangeSpellSlot(conn, slot, h, h > 0 ? SpellData.GetName(h) : "");
        }
        ServerPackets.SendSkills(conn, u);

        AreaVisibility.OnUserEnter(idxEspia);   // su área de vuelta (y él visible para los demás)
        Clima.EnviarClimaAUsuario(idxEspia);
        DayNightCycle.EnviarAUsuario(idxEspia);
    }

    /// <summary>
    /// El espía tocó una flecha (o mandó un giro). Su personaje NO se mueve: se queda parado
    /// e invisible donde está mirando. Pero su cliente ya predijo el paso... sobre el sprite
    /// del ESPIADO, que es el que cree que es "él" — o sea que moverse le desalinea la
    /// pantalla hasta que el otro dé un paso. Así que se le devuelve la posición y el rumbo
    /// reales del espiado y el sprite vuelve a su lugar en el acto.
    ///
    /// Devuelve true si esta conexión estaba espiando (el handler debe cortar ahí).
    /// </summary>
    public static bool AbsorberMovimientoDelEspia(Connection conn)
    {
        var connEspiado = AQuienEspia(conn);
        if (connEspiado == null) return false;
        int idx = IndiceDe(connEspiado);
        var t = idx == 0 ? null : UserListManager.UserList[idx];
        if (t == null) return true;

        var (px, py) = Continuous.Pos(t.Pos.Map, t.Pos.X, t.Pos.Y);
        Directo(conn, c =>
        {
            // PosUpdate va contra el personaje propio del cliente, que acá es el del espiado.
            ServerPackets.PosUpdate(c, px, py);
            ServerPackets.CharacterChange(c, t.Char.CharIndex, t.Char.body, t.Char.Head,
                t.Char.heading, t.Char.WeaponAnim, t.Char.ShieldAnim, t.Char.CascoAnim, 0, 0, 0);
        });
        return true;
    }

    /// <summary>
    /// Llegó la posición del mouse del espiado (ClientPacketID.EspiaMouse): se la pasamos
    /// al Dios tal cual. Si nadie lo espía, se descarta (puede ser un reporte en vuelo de
    /// cuando el espionaje ya se cortó).
    /// </summary>
    public static void MouseDelEspiado(Connection espiado, int xPx, int yPx, short nx, short ny, byte botones)
    {
        var connEspia = DeQuien(espiado);
        if (connEspia == null) return;
        Directo(connEspia, c => ServerPackets.EspiaMousePos(c, xPx, yPx, nx, ny, botones));
    }

    // ===================== TOKEN DEL PANEL (espectar sin loguear) =====================
    // El panel de deploy corre en la máquina del dueño, detrás de su propio token y solo en
    // loopback: ya es un contexto autenticado. Para que desde ahí se pueda mirar a un
    // jugador con un clic —sin escribir cuenta ni contraseña— el servidor guarda un secreto
    // en un archivo al lado de sus datos; el panel lo lee (local: del disco; producción: por
    // SSH) y se lo pasa al cliente en la URL. Quien tenga ese archivo puede espectar, así
    // que vale lo mismo que una credencial de administrador: NO se publica ni se comparte.
    private static string _tokenPanel = "";
    private const string ARCHIVO_TOKEN = "espectador.token";

    /// <summary>Ruta del archivo del token, al lado de los datos del servidor.</summary>
    public static string RutaToken => DataPaths.Root + ARCHIVO_TOKEN;

    /// <summary>
    /// Carga el token del panel; si no existe, genera uno y lo guarda. Se llama al arrancar.
    /// </summary>
    public static void CargarToken()
    {
        try
        {
            string ruta = RutaToken;
            if (File.Exists(ruta)) _tokenPanel = File.ReadAllText(ruta).Trim();
            if (string.IsNullOrWhiteSpace(_tokenPanel))
            {
                _tokenPanel = Crypto.RandomString(40);
                File.WriteAllText(ruta, _tokenPanel);
                Console.WriteLine($"[Espectador] Token nuevo generado en {ruta}");
            }
            Console.WriteLine("[Espectador] Token del panel cargado (espectar sin login habilitado).");
        }
        catch (Exception ex)
        {
            _tokenPanel = "";
            Console.WriteLine($"[Espectador] No se pudo preparar el token ({ex.Message}). " +
                              "Espectar desde el panel queda deshabilitado; el login con cuenta sigue andando.");
        }
    }

    /// <summary>¿Este token es el del panel? (comparación en tiempo constante, sin atajos)</summary>
    public static bool TokenValido(string t)
    {
        if (string.IsNullOrEmpty(_tokenPanel) || string.IsNullOrEmpty(t)) return false;
        if (t.Length != _tokenPanel.Length) return false;
        int dif = 0;
        for (int i = 0; i < t.Length; i++) dif |= t[i] ^ _tokenPanel[i];
        return dif == 0;
    }

    // ======================= MODO ESPECTADOR (sin personaje) =======================
    // Una conexión que se autenticó con una cuenta que tiene un Dios pero NUNCA hizo
    // EnterWorld: no tiene personaje, no ocupa un tile y para el mundo no existe (todo el
    // servidor saltea a los que no tienen flags.UserLogged). Solo recibe el espejo.
    //
    // Por qué no alcanzaba con /espiar: ese comando necesita un personaje de verdad — lo
    // teletransporta al lado del espiado para armar la foto inicial y lo esconde a mano.
    // Acá no hay a quién teletransportar, así que la foto se arma leyendo directamente lo
    // que el objetivo tiene visible (AreaVisibility.CrearVistaDe).

    /// <summary>¿Esta conexión es un espectador puro (mira sin tener personaje en el mundo)?</summary>
    public static bool EsEspectador(Connection conn)
    {
        if (conn == null) return false;
        int idx = IndiceDe(conn);
        var u = idx == 0 ? null : UserListManager.UserList[idx];
        return u != null && u.flags.UserLogged != true;
    }

    /// <summary>
    /// Engancha una conexión espectadora a un jugador: le arma el mundo desde cero (entrada
    /// sintética + la vista del objetivo) y enciende el espejo. Si ya estaba mirando a otro,
    /// se corta ese primero.
    /// </summary>
    public static void EmpezarEspectador(Connection espectador, User objetivo)
    {
        if (espectador == null || objetivo?.Conn == null || espectador == objetivo.Conn) return;
        Terminar(espectador);   // si venía mirando a otro

        // --- Entrada sintética al mundo. Es la misma ráfaga que espera el cliente al
        //     loguear, salvo que el "personaje propio" que se le declara es el del objetivo:
        //     con eso la cámara lo sigue sola y cada paquete copiado cae en su lugar.
        //     SOLO la primera vez: al cambiar de objetivo se le reenvía el mundo pero no el
        //     handshake, porque Logged renegocia la clave XOR (ver Connection.EspectadorEntro). ---
        if (!espectador.EspectadorEntro)
        {
            espectador.EspectadorEntro = true;
            ServerPackets.LoggedSuccessful(espectador);
            byte redundance = (byte)Random.Shared.Next(15, 251);
            ServerPackets.Logged(espectador, redundance);
            espectador.IncomingXorKey = redundance;   // el cliente cambia su clave XOR acá
            ServerPackets.UserIndexInServer(espectador, (short)espectador.UserIndex);
        }
        ServerPackets.ChangeMap(espectador, (short)objetivo.Pos.Map, 0);
        ServerPackets.UserCharIndexInServer(espectador, objetivo.Char.CharIndex);
        LoginFlow.SendCharCreate(espectador, objetivo);        // el propio objetivo
        AreaVisibility.CrearVistaDe(espectador, objetivo);     // todo lo que él ve

        lock (_candado)
        {
            _espiaDe[objetivo.Conn] = espectador;
            _mirando[espectador] = objetivo.Conn;
            _hayEspejos = true;
        }
        Directo(espectador, c => ServerPackets.EspiaVista(c, true, objetivo.Char.CharIndex, objetivo.Name));
        FotoInicialDelEspiado(objetivo);                       // su HUD, por el espejo
        ServerPackets.EspiaReportar(objetivo.Conn, true);      // que reporte mouse + interfaz

        int idxObj = IndiceDe(objetivo.Conn);
        if (idxObj > 0)
        {
            Clima.EnviarClimaAUsuario(idxObj);                 // se copian por el espejo
            DayNightCycle.EnviarAUsuario(idxObj);
        }
        Console.WriteLine($"[Espia] espectador #{espectador.UserIndex} → {objetivo.Name}");
    }

    /// <summary>
    /// Llegó el estado de la interfaz del espiado (qué solapa mira, qué ventanas abrió).
    /// Se reenvía tal cual: el formato es un contrato entre los dos clientes, el server no
    /// lo interpreta. Ver game.html::espiaEstadoUi / espiaAplicarUi.
    /// </summary>
    public static void UiDelEspiado(Connection espiado, string estado)
    {
        var connEspia = DeQuien(espiado);
        if (connEspia == null) return;
        Directo(connEspia, c => ServerPackets.EspiaUi(c, estado));
    }

    /// <summary>
    /// Foto inicial del HUD del espiado: inventario, hechizos, skills, barras, hambre/sed,
    /// créditos y misiones. El espejo copia lo que CAMBIA de acá en adelante, así que sin
    /// esto el Dios se queda mirando SU propio inventario y SUS hechizos mientras el mundo
    /// alrededor ya es el del otro — que es exactamente lo que se veía.
    ///
    /// Truco: en vez de redirigir cada sender hacia el espía (una redirección por cada uno,
    /// y alcanza con olvidarse de una para que quede mal), se le reenvía al ESPIADO lo suyo
    /// y el espejo hace la copia. Para él no cambia nada en pantalla — son exactamente los
    /// datos que su cliente ya tenía — y el espía recibe los mismos bytes que él.
    /// El log de misiones va con abrirVentana:false (origen 2), que es el modo silencioso:
    /// refresca tracker y marcadores sin abrirle la ventana a nadie.
    /// </summary>
    public static void FotoInicialDelEspiado(User t)
    {
        if (t?.Conn == null) return;
        var conn = t.Conn;
        ServerPackets.UpdateUserStats(conn, t);
        ServerPackets.UpdateHungerAndThirst(conn, t);
        ServerPackets.UpdateCreditos(conn, t.CreditoDonador);
        for (byte slot = 1; slot <= Constants.MAX_INVENTORY_SLOTS; slot++)
            ServerPackets.ChangeInventorySlot(conn, t, slot);
        for (byte slot = 1; slot <= Constants.MAXUSERHECHIZOS; slot++)
        {
            short h = t.Stats.UserHechizos[slot];
            ServerPackets.ChangeSpellSlot(conn, slot, h, h > 0 ? SpellData.GetName(h) : "");
        }
        ServerPackets.SendSkills(conn, t);
        int idx = IndiceDe(conn);
        if (idx > 0) QuestSystem.SendQuestLog(idx, abrirVentana: false);
    }

    /// <summary>UserIndex del dueño de esa conexión, o 0 si no está en la lista.</summary>
    private static int IndiceDe(Connection conn)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
            if (UserListManager.UserList[i]?.Conn == conn) return i;
        return 0;
    }

    // ==================== ESPECTAR UN NPC (bots de la guerra de facciones) ====================
    //
    // El espectador normal es un ESPEJO: se le copian los paquetes que el servidor le manda al
    // jugador espiado. Un bot de la guerra no es un jugador —es un NpcInstance, no tiene
    // conexión— así que no hay stream que copiar y todo el mecanismo de arriba no aplica.
    //
    // Acá se arma la vista a mano: se le declara al cliente que "su" personaje es el bot (con lo
    // cual la cámara lo sigue sola) y en cada tick de IA se le manda el diff de lo que hay
    // alrededor: quién apareció, quién se movió y quién salió del área. Es un observador de
    // SOLO LECTURA: el bot no se entera de que lo miran y su comportamiento no cambia en nada.
    private sealed class ObsNpc
    {
        public NpcManager.NpcInstance Bot;
        public bool Auto;                  // modo "seguir la acción": la cámara salta sola a las peleas
        public double ProximoCambioAt;     // no se cambia de cámara antes de esto (evita el zapping)
        public double UltimoCambioAt;      // cuándo se paró la cámara en el bot actual (para la rotación periódica)
        public int MapaEnviado = -1;             // mapa que el cliente ya tiene cargado
        public int UltimoHP = -1, UltimoMana = -1;   // último HP/maná del bot mandado al HUD (-1 = todavía nada)
        public readonly HashSet<int> VistosChars = new();   // charIndex ya creados en el cliente
        public readonly Dictionary<int, (int x, int y)> Pos = new(); // última posición enviada
        // Última APARIENCIA enviada por char. Sin esto el observador no se entera de que un bot
        // se subió a la barca (cuerpo 838) o cambió de arma/heading: se veía a la gente caminando
        // sobre el agua, porque BroadcastNpcAppearance solo le habla a los usuarios del mapa.
        public readonly Dictionary<int, (short body, short head, byte heading, short arma, short escudo, short casco)> Apar = new();
    }

    private static readonly Dictionary<Connection, ObsNpc> _obsNpc = new();

    /// <summary>Área que ve el observador de un bot: los mismos ±2 bloques de 9 tiles del AOI.</summary>
    private const int OBS_RANGO = 22;

    /// <summary>¿Esta conexión está mirando a un bot?</summary>
    public static bool SigueNpc(Connection conn) => conn != null && _obsNpc.ContainsKey(conn);

    /// <summary>Deja de seguir al bot (si seguía a alguno). true si estaba siguiendo.</summary>
    public static bool TerminarNpc(Connection conn)
    {
        if (conn == null) return false;
        bool habia;
        lock (_candado) habia = _obsNpc.Remove(conn);
        if (habia) Directo(conn, c => ServerPackets.EspiaVista(c, false, 0, ""));
        return habia;
    }

    /// <summary>
    /// Engancha una conexión espectadora a un BOT: le arma el mundo desde cero alrededor del bot
    /// y lo deja registrado para que RefrescarObservadores lo vaya actualizando.
    /// </summary>
    public static void EmpezarEspectadorNpc(Connection espectador, NpcManager.NpcInstance bot)
    {
        if (espectador == null || bot == null || bot.Dead) return;
        Terminar(espectador);      // si venía espejando a un jugador
        TerminarNpc(espectador);   // o mirando a otro bot

        // Entrada sintética al mundo: la misma ráfaga que EmpezarEspectador (ver ahí el por qué
        // del EspectadorEntro — Logged renegocia la clave XOR y no se puede repetir).
        if (!espectador.EspectadorEntro)
        {
            espectador.EspectadorEntro = true;
            ServerPackets.LoggedSuccessful(espectador);
            byte redundance = (byte)Random.Shared.Next(15, 251);
            ServerPackets.Logged(espectador, redundance);
            espectador.IncomingXorKey = redundance;
            ServerPackets.UserIndexInServer(espectador, (short)espectador.UserIndex);
        }

        var obs = new ObsNpc { Bot = bot };
        lock (_candado) _obsNpc[espectador] = obs;

        Directo(espectador, c => ServerPackets.EspiaVista(c, true, (short)bot.CharIndex, bot.Name ?? "Bot"));
        // HUD de vida/maná del bot (máximos + actual): sin esto, la barra del espectador quedaba
        // con lo último que tenía su propio personaje (o en cero), no con la vida real del bot.
        Directo(espectador, c => ServerPackets.UpdateUserStatsNpc(c, bot));
        obs.UltimoHP = bot.MinHP; obs.UltimoMana = bot.MinMana;
        RefrescarObs(espectador, obs);
        Console.WriteLine($"[Espia] espectador #{espectador.UserIndex} → BOT {bot.Name} (mapa {bot.Map})");
    }

    /// <summary>
    /// Actualiza la pantalla de todos los que están mirando bots. La llama NpcManager.TickAI
    /// después de mover a los NPCs, así el diff sale con las posiciones ya finales del tick.
    /// </summary>
    public static void RefrescarObservadores()
    {
        if (_obsNpc.Count == 0) return;
        List<KeyValuePair<Connection, ObsNpc>> copia;
        lock (_candado) copia = new List<KeyValuePair<Connection, ObsNpc>>(_obsNpc);
        foreach (var (conn, obs) in copia)
        {
            try
            {
                if (obs.Auto) ElegirCamaraDeAccion(conn, obs);
                RefrescarObs(conn, obs);
            }
            catch (Exception ex) { Console.WriteLine($"[Espia] ERROR refrescando observador de bot: {ex.Message}"); }
        }
    }

    // --- Cámara automática ("seguir la acción") ---
    // Ventana en la que un golpe cuenta como "está peleando ahora".
    private const double ACCION_FRESCA = 4.0;
    // Mínimo que se queda mirando a un bot que NO pelea, cuando tampoco hay pelea en ningún lado
    // (paseo). Si aparece una pelea real, se salta al instante sin esperar esto.
    private const double ACCION_MIN_ESTADIA = 8.0;
    // Aunque el bot que mirás SIGA peleando, cada tanto la cámara rota a OTRO combatiente si hay
    // más de uno peleando — si no, con una batalla grande la cámara se quedaba pegada al primero
    // que encontró y nunca mostraba al resto (queja del usuario: "sigue siempre al mismo personaje").
    private const double ROTACION_MAX_ESTADIA = 15.0;

    /// <summary>Arranca un espectador en modo "seguir la acción": la cámara elige sola a quién mirar.</summary>
    public static void EmpezarEspectadorAccion(Connection espectador)
    {
        if (espectador == null) return;
        var elegido = MejorBotDeAccion(null);
        if (elegido == null)
        {
            ServerPackets.ConsoleMsg(espectador, "No hay ninguna guerra en curso para seguir.", 4);
            return;
        }
        EmpezarEspectadorNpc(espectador, elegido);
        lock (_candado)
            if (_obsNpc.TryGetValue(espectador, out var obs))
            {
                double now = Environment.TickCount64 / 1000.0;
                obs.Auto = true; obs.ProximoCambioAt = now + ACCION_MIN_ESTADIA; obs.UltimoCambioAt = now;
            }
        Directo(espectador, c => ServerPackets.ConsoleMsg(c,
            "Cámara automática: vas a ir saltando de pelea en pelea. Elegí un bot de la lista para fijarla.", 4));
    }

    /// <summary>
    /// Puntúa a los bots y devuelve el mejor para mirar: primero los que están peleando AHORA, y
    /// entre ellos el que tenga más enemigos alrededor (la pelea más grande, no una escaramuza).
    /// </summary>
    private static NpcManager.NpcInstance MejorBotDeAccion(NpcManager.NpcInstance actual)
    {
        double now = Environment.TickCount64 / 1000.0;
        var bots = NpcManager.BotsEspectables();
        if (bots.Count == 0) return null;

        NpcManager.NpcInstance mejor = null;
        int mejorPuntaje = 0;
        foreach (var b in bots)
        {
            if (b.Dead) continue;

            // Cuántos enemigos tiene cerca: es el tamaño de la pelea si ya empezó, y el aviso de
            // que está por empezar si todavía no. Mirar SOLO a los que ya están peleando dejaba la
            // cámara clavada cuando los ejércitos venían marchando (no había ninguno "fresco").
            int enemigos = 0;
            foreach (var o in bots)
            {
                if (o == b || o.Dead || o.Map != b.Map || o.BotFaccion == b.BotFaccion) continue;
                if (Math.Abs(o.X - b.X) <= 12 && Math.Abs(o.Y - b.Y) <= 12) enemigos++;
            }
            bool peleando = now - b.UltimoCombateAt < ACCION_FRESCA;
            int puntaje = enemigos * 10 + (peleando ? 100 : 0) + (b == actual ? 5 : 0);
            if (puntaje > mejorPuntaje) { mejorPuntaje = puntaje; mejor = b; }
        }

        // No hay nadie cerca de nadie (los tres ejércitos todavía marchando): en vez de devolver
        // siempre el mismo —que dejaba la cámara congelada en un bot caminando solo— se elige uno
        // AL AZAR, así el paseo va mostrando distintos frentes hasta que se arme el choque.
        if (mejor == null)
        {
            var vivos = bots.FindAll(b => !b.Dead && b != actual);
            if (vivos.Count > 0) mejor = vivos[System.Random.Shared.Next(vivos.Count)];
        }
        return mejor;
    }

    /// <summary>
    /// Decide si la cámara automática tiene que rotar a otro bot, y rota. A propósito NO
    /// prioriza pelea sobre paseo: con 150+ bots poblando el mundo (Bots.PoblarMundo) casi
    /// siempre hay ALGUNA pelea en curso en algún lado, así que perseguirlas dejaba la cámara
    /// sin mostrar nunca a un bot común caminando/cazando NPCs solo. Ahora el próximo bot sale
    /// al azar entre TODOS los vivos, esté peleando o no — lo único que cambia según pelee o no
    /// es CUÁNTO se queda ahí antes de rotar de nuevo (ROTACION_MAX_ESTADIA si pelea,
    /// ACCION_MIN_ESTADIA si no).
    /// </summary>
    private static void ElegirCamaraDeAccion(Connection conn, ObsNpc obs)
    {
        double now = Environment.TickCount64 / 1000.0;
        var actual = obs.Bot;
        bool muerto = actual == null || actual.Dead;
        bool peleandoActual = !muerto && now - actual.UltimoCombateAt < ACCION_FRESCA;

        // Todavía no toca rotar: peleando se queda más tiempo (ROTACION_MAX_ESTADIA) que
        // paseando/cazando (ACCION_MIN_ESTADIA, guardado en ProximoCambioAt).
        if (!muerto)
        {
            if (peleandoActual) { if (now - obs.UltimoCambioAt < ROTACION_MAX_ESTADIA) return; }
            else { if (now < obs.ProximoCambioAt) return; }
        }

        var candidatos = NpcManager.BotsEspectables().FindAll(b => !b.Dead && b != actual);
        if (candidatos.Count == 0) { obs.ProximoCambioAt = now + 3.0; return; }
        var elegido = candidatos[System.Random.Shared.Next(candidatos.Count)];

        Console.WriteLine($"[Espia] cámara automática: {actual?.Name ?? "-"} → {elegido.Name} (mapa {elegido.Map})");
        obs.Bot = elegido;
        obs.ProximoCambioAt = now + ACCION_MIN_ESTADIA;
        obs.UltimoCambioAt = now;
        // Forzar recarga completa de la pantalla en el próximo refresco (mapa "distinto").
        obs.MapaEnviado = -1;
        Directo(conn, c => ServerPackets.EspiaVista(c, true, (short)elegido.CharIndex, elegido.Name ?? "Bot"));
        Directo(conn, c => ServerPackets.UpdateUserStatsNpc(c, elegido)); // HUD del bot nuevo (otro máximo de HP/maná)
        obs.UltimoHP = elegido.MinHP; obs.UltimoMana = elegido.MinMana;
    }

    /// <summary>Manda a un observador el diff de lo que hay alrededor de su bot.</summary>
    private static void RefrescarObs(Connection conn, ObsNpc obs)
    {
        var bot = obs.Bot;
        double now = Environment.TickCount64 / 1000.0;

        // El bot murió: NUNCA se corta el espectador solo, se salta a otro bot vivo (pedido
        // explícito — antes había que volver a la lista a mano cada vez que el que mirabas caía).
        // Vale tanto en modo manual como en "seguir la acción" (ahí ElegirCamaraDeAccion ya
        // debería haber saltado antes de llegar acá; esto es la red de seguridad si no encontró
        // nada esa vez, y el único camino cuando NO está en modo automático).
        if (bot == null || bot.Dead)
        {
            var reemplazo = MejorBotDeAccion(null);
            if (reemplazo != null)
            {
                obs.Bot = reemplazo;
                obs.MapaEnviado = -1;
                obs.UltimoCambioAt = now;
                obs.UltimoHP = reemplazo.MinHP; obs.UltimoMana = reemplazo.MinMana;
                Console.WriteLine($"[Espia] {bot?.Name ?? "?"} murió — observador reasignado a {reemplazo.Name}");
                Directo(conn, c =>
                {
                    ServerPackets.EspiaVista(c, true, (short)reemplazo.CharIndex, reemplazo.Name ?? "Bot");
                    ServerPackets.UpdateUserStatsNpc(c, reemplazo); // HUD del bot nuevo
                    ServerPackets.ConsoleMsg(c, $"{bot?.Name ?? "Tu bot"} cayó en combate — pasando a {reemplazo.Name}.", 4);
                });
                return;   // el mundo del nuevo bot se arma en el próximo refresco (~380ms)
            }
            TerminarNpc(conn);
            Directo(conn, c => ServerPackets.ConsoleMsg(c, "El bot que mirabas cayó en combate y no queda ningún otro vivo.", 4));
            return;
        }

        // Vida/maná: sin esto la barra del HUD se congelaba en la foto inicial y no bajaba con
        // los golpes ni subía con la regen, aunque el número flotante sobre la cabeza sí se veía.
        if (bot.MinHP != obs.UltimoHP) { obs.UltimoHP = bot.MinHP; Directo(conn, c => ServerPackets.UpdateHP(c, (short)bot.MinHP)); }
        if (bot.MinMana != obs.UltimoMana) { obs.UltimoMana = bot.MinMana; Directo(conn, c => ServerPackets.UpdateMana(c, (short)bot.MinMana)); }

        // Cambió de mapa. OJO con el MUNDO CONTINUO: entre mapas del overworld el cruce es un
        // "seam" (el cliente NO recibe ChangeMap, ver Continuous.IsSeamCross) y el mundo sigue
        // siendo el mismo espacio de coordenadas. Mandar ChangeMap ahí borraría la pantalla y
        // recargaría todo en cada borde. Solo se recarga de cero al entrar a un interior/dungeon.
        if (obs.MapaEnviado != bot.Map)
        {
            bool seam = obs.MapaEnviado > 0 && Continuous.IsSeamCross(obs.MapaEnviado, bot.Map);
            obs.MapaEnviado = bot.Map;
            if (seam)
            {
                // Cruce de borde del mundo continuo: no se recarga nada, pero SÍ hay que avisar el
                // mapa nuevo — el cliente lo usa para el minimapa y el nombre de la zona. Sin esto
                // el bot cruzaba de mapa y el minimapa se quedaba mostrando el anterior.
                var (sgx, sgy) = Continuous.Pos(bot.Map, bot.X, bot.Y);
                Directo(conn, c => ServerPackets.SeamlessCross(c, (short)bot.Map, sgx, sgy));
            }
            else
            {
                obs.VistosChars.Clear();
                obs.Pos.Clear();
                obs.Apar.Clear();
                Directo(conn, c =>
                {
                    ServerPackets.ChangeMap(c, (short)bot.Map, 0);
                    ServerPackets.UserCharIndexInServer(c, (short)bot.CharIndex);
                });
            }
            // Sea seam o recarga completa, el mapa nuevo tiene SU propio piso de objetos — sin esto,
            // un bot que cruza a otro dungeon/piso (o el espectador que salta a uno recién aparecido
            // ahí) no veía los ítems que ya estaban tirados de antes en ese mapa.
            EnviarObjetosDelArea(conn, bot);
        }

        var enArea = new HashSet<int>();
        Directo(conn, c =>
        {
            // El propio bot (su create también fija la cámara, porque es "su" personaje).
            enArea.Add(bot.CharIndex);
            MostrarNpc(c, obs, bot);

            // Todo se compara en GLOBAL: en mundo continuo el bot puede estar pegado al borde y
            // media pantalla es el mapa de al lado (por eso también se recorren los vecinos).
            var (bgx, bgy) = Continuous.Pos(bot.Map, bot.X, bot.Y);
            bool EnArea(int m, int x, int y)
            {
                var (gx, gy) = Continuous.Pos(m, x, y);
                return Math.Abs(gx - bgx) <= OBS_RANGO && Math.Abs(gy - bgy) <= OBS_RANGO;
            }

            var mapas = new List<int> { bot.Map };
            if (Continuous.Enabled && RegionLayout.InRegion(bot.Map))
                foreach (int m in RegionLayout.NearbyMaps(bot.Map))
                    if (m != bot.Map) mapas.Add(m);

            // Los demás NPCs del área (incluidos los bots enemigos, que son la pelea).
            foreach (int m in mapas)
                foreach (var n in NpcManager.GetMapNpcs(m))
                {
                    if (n.Dead || n == bot) continue;
                    if (!EnArea(n.Map, n.X, n.Y)) continue;
                    enArea.Add(n.CharIndex);
                    MostrarNpc(c, obs, n);
                }

            // Y los jugadores del área.
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var u = UserListManager.UserList[i];
                if (u?.flags.UserLogged != true || u.Conn == null) continue;
                if (!mapas.Contains(u.Pos.Map)) continue;
                if (!EnArea(u.Pos.Map, u.Pos.X, u.Pos.Y)) continue;
                int ci = u.Char.CharIndex;
                enArea.Add(ci);
                var (ugx, ugy) = Continuous.Pos(u.Pos.Map, u.Pos.X, u.Pos.Y);   // global, como los NPCs
                if (obs.VistosChars.Add(ci)) { LoginFlow.SendCharCreate(c, u); obs.Pos[ci] = (ugx, ugy); }
                else if (obs.Pos.TryGetValue(ci, out var p) && (p.x != ugx || p.y != ugy))
                { ServerPackets.CharacterMove(c, (short)ci, ugx, ugy); obs.Pos[ci] = (ugx, ugy); }
            }

            // Lo que salió del área: borrarlo del cliente (si no, quedan clones congelados).
            if (obs.VistosChars.Count != enArea.Count)
            {
                var fuera = new List<int>();
                foreach (int ci in obs.VistosChars) if (!enArea.Contains(ci)) fuera.Add(ci);
                foreach (int ci in fuera)
                {
                    ServerPackets.CharacterRemove(c, (short)ci, false);
                    obs.VistosChars.Remove(ci); obs.Pos.Remove(ci); obs.Apar.Remove(ci);
                }
            }
        });
    }

    /// <summary>
    /// Crea el NPC en el cliente del observador la primera vez y después le manda solo lo que
    /// cambió: posición (CharacterMove) y apariencia (CharacterChange — barca, arma, heading).
    /// </summary>
    private static void MostrarNpc(Connection c, ObsNpc obs, NpcManager.NpcInstance n)
    {
        int ci = n.CharIndex;
        var apar = (n.Body, n.Head, n.Heading, n.WeaponAnim, n.ShieldAnim, n.CascoAnim);
        if (obs.VistosChars.Add(ci))
        {
            NpcManager.SendNpcCreate(c, n);
            var (cgx, cgy) = Continuous.Pos(n.Map, n.X, n.Y);
            obs.Pos[ci] = (cgx, cgy);
            obs.Apar[ci] = apar;
            return;
        }
        // Posición en GLOBAL, igual que CharacterCreate (SendOne usa Continuous.Pos) y que
        // AreaVisibility.OnNpcMoved. Mandarla en local era el bug de "los veo caminando en el
        // agua y no veo el mapa": el char se creaba en el lugar correcto y al primer paso saltaba
        // a la esquina del mundo continuo (que es océano), llevándose la cámara con él.
        var (gx, gy) = Continuous.Pos(n.Map, n.X, n.Y);
        if (obs.Pos.TryGetValue(ci, out var p) && (p.x != gx || p.y != gy))
        { ServerPackets.CharacterMove(c, (short)ci, gx, gy); obs.Pos[ci] = (gx, gy); }
        if (!obs.Apar.TryGetValue(ci, out var a) || a != apar)
        {
            ServerPackets.CharacterChange(c, (short)ci, n.Body, n.Head, n.Heading,
                n.WeaponAnim, n.ShieldAnim, n.CascoAnim, 0, 0, 0);
            obs.Apar[ci] = apar;
        }
    }

    /// <summary>
    /// Manda algo a quien esté observando un bot que VE esa posición. Es el canal que le falta a
    /// todo lo que se difunde "por área": hechizos, rayos, partículas, sonidos y carteles de daño
    /// se difunden recorriendo los USUARIOS del mapa, y en el medio del mundo no hay ninguno — así
    /// que mirando por los ojos de un bot no se veía nada de la pelea. Los broadcasters de combate
    /// llaman a esto además de a su loop de usuarios.
    /// </summary>
    public static void ParaObservadoresDe(int map, int x, int y, Action<Connection> enviar)
    {
        if (_obsNpc.Count == 0) return;
        List<KeyValuePair<Connection, ObsNpc>> copia;
        lock (_candado) copia = new List<KeyValuePair<Connection, ObsNpc>>(_obsNpc);
        foreach (var (conn, obs) in copia)
        {
            var b = obs.Bot;
            if (b == null || b.Dead || b.Map != map) continue;
            if (Math.Abs(b.X - x) > OBS_RANGO || Math.Abs(b.Y - y) > OBS_RANGO) continue;
            Directo(conn, enviar);
        }
    }

    /// <summary>Igual que ParaObservadoresDe pero apuntando a un personaje: solo le llega a los
    /// observadores que YA tienen ese charIndex creado en pantalla (si no lo ven, el FX sobra).</summary>
    public static void ParaObservadoresDeChar(int map, int charIndex, Action<Connection> enviar)
    {
        if (_obsNpc.Count == 0) return;
        List<KeyValuePair<Connection, ObsNpc>> copia;
        lock (_candado) copia = new List<KeyValuePair<Connection, ObsNpc>>(_obsNpc);
        foreach (var (conn, obs) in copia)
        {
            var b = obs.Bot;
            if (b == null || b.Dead || b.Map != map) continue;
            if (!obs.VistosChars.Contains(charIndex)) continue;
            Directo(conn, enviar);
        }
    }

    /// <summary>Objetos tirados en el piso alrededor del bot (se mandan al entrar y al cambiar de mapa).</summary>
    private static void EnviarObjetosDelArea(Connection conn, NpcManager.NpcInstance bot)
    {
        var md = MapLoader.Get(bot.Map);
        if (md == null) return;
        Directo(conn, c =>
        {
            for (int x = Math.Max(1, bot.X - OBS_RANGO); x <= Math.Min(100, bot.X + OBS_RANGO); x++)
                for (int y = Math.Max(1, bot.Y - OBS_RANGO); y <= Math.Min(100, bot.Y + OBS_RANGO); y++)
                {
                    short oi = md.FloorObj[x, y];
                    if (oi > 0) ServerPackets.ObjectCreate(c, x, y, oi, (short)md.FloorAmount[x, y]);
                }
        });
    }
}
