using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// VOZ DE EVENTO (NUEVO, no VB6): un GM escribe un anuncio en el panel y TODOS los jugadores
/// lo ESCUCHAN, no sólo lo leen en la consola. "¡Atención guerreros! ¡El Rey Demonio ha
/// aparecido!" sale por los parlantes de cada uno.
///
/// ── Por qué está armado así ────────────────────────────────────────────────────────────
///
/// 1) NO hay red nueva. El GM lo pide con ClientPacketID.EventVoice (191) y el server lo
///    difunde con ServerPacketID.EventVoice (219), por el MISMO socket TCP de siempre,
///    gateado con su propio bit de ClientCaps (bit5) igual que todo paquete S→C nuevo — un
///    cliente con el JS viejo cacheado no sabría saltear el id y se le rompería el stream.
///
/// 2) El AUDIO NO viaja por el socket. El server genera el .mp3/.wav UNA sola vez, lo guarda
///    en TtsCache/ con el nombre = hash del (texto + voz), y en el paquete manda sólo la RUTA.
///    Cada cliente se lo baja por HTTP (StatusEndpoint sirve /tts/…) y lo cachea el navegador.
///    Un anuncio repetido no se regenera NUNCA: el hash ya está en disco. Meter 40 KB de audio
///    adentro del paquete y multiplicarlo por 150 jugadores sería 6 MB por anuncio saliendo
///    del mismo buffer que usa el combate.
///
/// 3) La generación NO toca el game loop. El pedido del GM entra a un Channel y lo atiende un
///    worker propio (ArrancarWorker): ahí corre el proceso externo de TTS, que puede tardar
///    segundos. Recién con el archivo listo el worker toma GameLock un instante para difundir
///    (mismo patrón que MercadoPago.cs). El tick del mundo nunca espera al TTS.
///
/// 4) Si NO hay TTS configurado (o falla), el anuncio se difunde igual con la ruta vacía y el
///    cliente lo dice con la voz del PROPIO navegador (speechSynthesis, ver event_voice_ui.js).
///    O sea: la función anda sin instalar nada. El TTS del server es la mejora — una sola voz
///    idéntica para todos y sin depender de qué voces tenga instaladas cada jugador.
///
/// 5) La COLA es del server: los anuncios se atienden de a uno y en orden de llegada, así dos
///    GMs escribiendo a la vez no se pisan. El cliente además encola su propia reproducción,
///    que es lo que garantiza que dos voces no suenen encimadas en los parlantes.
///
/// ── Configuración (Server.ini, todo opcional) ──────────────────────────────────────────
///   TtsComando=              ejecutable de TTS ("" = desactivado, se usa la voz del navegador)
///   TtsArgs=                 argumentos; {out} = archivo destino, {voz} = voz del preset,
///                            {texto} = el texto (si no aparece, el texto va por stdin)
///   TtsExt=mp3               extensión que produce el comando (mp3 | wav | ogg)
///   TtsTimeoutSeg=25         corte duro de la generación
/// Ejemplo con edge-tts:
///   TtsComando=edge-tts
///   TtsArgs=--voice {voz} --write-media {out} --text {texto}
/// Ejemplo con piper (texto por stdin):
///   TtsComando=/usr/local/bin/piper
///   TtsArgs=--model /opt/voces/es_AR-daniela-high.onnx --output_file {out}
///   TtsExt=wav
/// </summary>
public static class EventVoice
{
    /// <summary>Mismo piso que ObjEditor/SpellEditor/BalanceEditor: Semidiós.</summary>
    private const byte MIN_PRIV = AdminLoader.STATUS_SEMIDIOS;

    public const int MAX_TEXTO = 300;          // caracteres; el resto se recorta
    private const int MIN_TEXTO = 2;
    private const int COOLDOWN_MS = 3000;      // por GM, para que no se pueda ametrallar
    private const int COLA_MAX = 8;            // pedidos pendientes de generar
    private const int CACHE_BYTES_MAX = 8;     // clips que quedan en RAM para servir por HTTP

    /// <summary>bit0 del campo flags: mostrar el texto en pantalla mientras se escucha.</summary>
    public const byte FLAG_MOSTRAR_TEXTO = 1;

    // ── Presets de voz ────────────────────────────────────────────────────────────────
    // El id es lo único que viaja en el protocolo. `Motor` es lo que se le pasa al TTS del
    // server ({voz} en TtsArgs); el cliente tiene su propia tabla con el equivalente para
    // speechSynthesis (ver event_voice_ui.js) — las dos tienen que quedar en el mismo orden.
    public readonly record struct VozPreset(byte Id, string Nombre, string Motor);

    // ⚠️ El ORDEN es el contrato: lo que viaja es el índice, y el hash del cache lo incluye.
    // Agregar voces SIEMPRE al final (insertar en el medio le cambia la voz a los clips que ya
    // están cacheados). Tiene que quedar 1:1 con VOCES de mini/event_voice_ui.js.
    // Los nombres de motor son voces neuronales de edge-tts; con otro TTS hay que cambiarlos
    // por los que ese motor entienda (es lo que se sustituye en {voz} de TtsArgs).
    public static readonly VozPreset[] Voces =
    {
        new(0,  "Heraldo",     "es-AR-TomasNeural"),
        new(1,  "Heraldina",   "es-AR-ElenaNeural"),
        new(2,  "Oráculo",     "es-ES-AlvaroNeural"),
        new(3,  "Sacerdotisa", "es-ES-ElviraNeural"),
        new(4,  "Coloso",      "es-MX-JorgeNeural"),
        new(5,  "Rey Demonio", "es-VE-SebastianNeural"),
        new(6,  "Bruja",       "es-CL-CatalinaNeural"),
        new(7,  "Enano",       "es-CO-GonzaloNeural"),
        new(8,  "Elfo",        "es-CL-LorenzoNeural"),
        new(9,  "Espectro",    "es-PE-AlexNeural"),
        new(10, "Bardo",       "es-MX-DaliaNeural"),
        new(11, "Guardián",    "es-CO-SalomeNeural"),

        // --- Voces de PERSONAJE (12-23) ---
        // OJO: el motor de TTS acá es sólo la MATERIA PRIMA. Ni edge-tts ni ningún TTS tienen
        // una voz "demoníaca" o "celestial": todas sus voces son humanas normales. El carácter
        // lo pone el CLIENTE, procesando el clip con Web Audio (bajar el tono, saturar,
        // modular, agregar sala, duplicar la voz) — ver el campo `fx` de VOCES en
        // mini/event_voice_ui.js. Por eso acá se elige la voz base que mejor aguanta cada
        // tratamiento: graves y neutras para lo demoníaco, claras y aireadas para lo celestial.
        new(12, "Voz Demoníaca Profunda",     "es-VE-SebastianNeural"),
        new(13, "Gruñido Demoníaco",          "es-VE-SebastianNeural"),
        new(14, "Villano Oscuro",             "es-ES-AlvaroNeural"),
        new(15, "Voz Masculina Grave",        "es-MX-JorgeNeural"),
        new(16, "Narrador Épico",             "es-ES-AlvaroNeural"),
        new(17, "Narrador de Fantasía Oscura","es-AR-TomasNeural"),
        new(18, "Voz Ancestral",              "es-PE-AlexNeural"),
        new(19, "Voz Ronca",                  "es-CO-GonzaloNeural"),
        new(20, "Voz de Mando",               "es-MX-JorgeNeural"),
        new(21, "Voz Monstruosa",             "es-VE-SebastianNeural"),
        new(22, "Voz Celestial",              "es-ES-ElviraNeural"),
        new(23, "Nigromante",                 "es-CL-LorenzoNeural"),
    };

    private static string MotorDeVoz(byte voz) =>
        voz < Voces.Length ? Voces[voz].Motor : Voces[0].Motor;

    // ── Estado ────────────────────────────────────────────────────────────────────────
    private sealed record Pedido(string Texto, int Mapa, byte Voz, byte Volumen, byte Flags,
                                 string GmNombre, int GmUserIndex);

    /// <summary>hash → nombre de archivo en TtsCache (ya generado y listo). "" = se intentó y falló.</summary>
    private static readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private static readonly object _cacheLock = new();

    /// <summary>Últimos clips leídos, para no ir al disco una vez por jugador.</summary>
    private static readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
    private static readonly List<string> _bytesOrden = new();

    private static readonly Dictionary<string, long> _ultimoPorGm = new(StringComparer.OrdinalIgnoreCase);

    private static Channel<Pedido> _cola;
    private static bool _workerArrancado;

    // Configuración leída una sola vez (se relee con /reloadsini vía Recargar()).
    private static string _cmd = "";
    private static string _args = "";
    private static string _ext = "mp3";
    private static int _timeoutSeg = 25;
    private static bool _configLeida;

    // ============================================================
    //  Configuración
    // ============================================================
    private static void AsegurarConfig()
    {
        if (_configLeida) return;
        _configLeida = true;
        Recargar();
    }

    /// <summary>Relee las claves Tts* de Server.ini (lo llama /reloadsini).</summary>
    public static void Recargar()
    {
        _cmd = (ServerConfig.ReadString("TtsComando", "") ?? "").Trim();
        _args = (ServerConfig.ReadString("TtsArgs", "") ?? "").Trim();
        var ext = (ServerConfig.ReadString("TtsExt", "mp3") ?? "mp3").Trim().TrimStart('.').ToLowerInvariant();
        _ext = ext is "mp3" or "wav" or "ogg" ? ext : "mp3";
        _timeoutSeg = Math.Clamp(ServerConfig.ReadInt("TtsTimeoutSeg", 25), 3, 120);
        _configLeida = true;
        Console.WriteLine(_cmd.Length == 0
            ? "[EventVoice] Sin TtsComando en Server.ini: los anuncios los va a decir la voz del navegador de cada jugador."
            : $"[EventVoice] TTS del server: '{_cmd}' → .{_ext} (timeout {_timeoutSeg}s)");
    }

    /// <summary>Carpeta del cache de audio, creada al vuelo la primera vez.</summary>
    public static string CacheDir
    {
        get
        {
            string d = DataPaths.Sub("TtsCache");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    // ============================================================
    //  Entrada: el GM pide un anuncio (corre bajo GameLock, desde PacketHandler)
    // ============================================================
    /// <summary>
    /// mapa: 0 = todo el servidor, &gt;0 = sólo los que están en ese mapa.
    /// volumen: 0-100, lo que el GM eligió; el jugador lo multiplica por SU volumen de anuncios.
    /// Devuelve rápido SIEMPRE: lo caro (generar el audio) queda encolado para el worker.
    /// </summary>
    public static void Anunciar(int userIndex, string texto, int mapa, byte voz, byte volumen, byte flags)
    {
        AsegurarConfig();

        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged || u.Conn == null) return;

        if (AdminLoader.GetFaccionStatus(u.Name) < MIN_PRIV)
        {
            ServerPackets.ConsoleMsg(u.Conn, "No tenés privilegios para usar la Voz de Evento.", 6);
            Console.WriteLine($"[EventVoice] RECHAZADO: {u.Name} intentó anunciar sin privilegios.");
            return;
        }

        texto = Limpiar(texto);
        if (texto.Length < MIN_TEXTO)
        {
            ServerPackets.ConsoleMsg(u.Conn, "El anuncio está vacío.", 6);
            return;
        }

        long ahora = Environment.TickCount64;
        if (_ultimoPorGm.TryGetValue(u.Name, out long ult) && ahora - ult < COOLDOWN_MS)
        {
            ServerPackets.ConsoleMsg(u.Conn,
                $"Esperá {(COOLDOWN_MS - (ahora - ult)) / 1000 + 1}s antes del próximo anuncio.", 6);
            return;
        }
        _ultimoPorGm[u.Name] = ahora;

        voz = voz < Voces.Length ? voz : (byte)0;
        volumen = Math.Clamp(volumen, (byte)0, (byte)100);
        flags &= FLAG_MOSTRAR_TEXTO;
        // mapa 0 = global. Cualquier otro valor se toma tal cual: si no hay nadie ahí, no
        // suena en ningún lado y listo (no hace falta validar contra NumMaps para eso).
        if (mapa < 0) mapa = 0;

        ArrancarWorker();
        var pedido = new Pedido(texto, mapa, voz, volumen, flags, u.Name, userIndex);
        if (!_cola.Writer.TryWrite(pedido))
        {
            ServerPackets.ConsoleMsg(u.Conn, "La cola de anuncios está llena, probá en unos segundos.", 6);
            return;
        }

        ServerPackets.ConsoleMsg(u.Conn,
            $"Voz de evento en cola ({(mapa == 0 ? "todo el servidor" : "mapa " + mapa)}): \"{texto}\"", 2);
        Console.WriteLine($"[EventVoice] {u.Name} → mapa {mapa}, voz {voz}: \"{texto}\"");
    }

    /// <summary>
    /// Deja SÓLO lo que hay que decir. El cliente ya limpia antes de mandar (ver
    /// mini/event_voice_ui.js::limpiarTexto), pero esto es lo que hace que un cliente viejo,
    /// uno con el JS cacheado o un paquete armado a mano no puedan meter basura en el anuncio.
    ///
    /// Qué se saca y por qué:
    ///  · Marcas de markdown (* _ ` ~ y los # / &gt; de principio de renglón): el TTS las LEE
    ///    ("asterisco asterisco EVENTO"), no las interpreta.
    ///  · Rachas de '?': un emoji no existe en CP1252, así que llega convertido en '?' — y como
    ///    en el cable es un par de sustitutos, siempre llega de a dos o más. Un '?' SUELTO se
    ///    respeta: es un signo de pregunta de verdad, y es lo que le da entonación a la voz.
    ///  · Saltos de línea y controles: el cartel y la voz son un solo bloque.
    /// Lo que se RESPETA: palabras, acentos, ñ, ¡ ¿ y la puntuación normal.
    /// </summary>
    private static string Limpiar(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, MAX_TEXTO));
        bool inicioDeRenglon = true;

        for (int i = 0; i < s.Length && sb.Length < MAX_TEXTO; i++)
        {
            char c = s[i];

            if (c == '\r' || c == '\n')
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                inicioDeRenglon = true;
                continue;
            }
            if (c == '\t' || char.IsControl(c))
            {
                if (c == '\t' && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }
            // '#' y '>' sólo se sacan al PRINCIPIO del renglón (títulos y citas de markdown):
            // en el medio pueden ser parte del mensaje ("el mapa #7", "3 > 2").
            if (inicioDeRenglon)
            {
                if (c == ' ' || c == '#' || c == '>') continue;   // sangría y marcas de bloque
                inicioDeRenglon = false;
            }

            if (c == '*' || c == '_' || c == '`' || c == '~') continue;

            if (c == '?')
            {
                // Cuántos '?' seguidos hay: 2 o más = restos de un emoji, se van todos.
                int j = i;
                while (j < s.Length && s[j] == '?') j++;
                int racha = j - i;
                if (racha >= 2) { i = j - 1; if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
            }

            sb.Append(c);
        }

        // La limpieza puede dejar dobles espacios donde había un emoji entre dos palabras.
        var salida = sb.ToString().Trim();
        while (salida.Contains("  ", StringComparison.Ordinal))
            salida = salida.Replace("  ", " ", StringComparison.Ordinal);
        return salida;
    }

    // ============================================================
    //  Worker: genera el audio FUERA del game loop y recién ahí difunde
    // ============================================================
    private static void ArrancarWorker()
    {
        if (_workerArrancado) return;
        _workerArrancado = true;
        _cola = Channel.CreateBounded<Pedido>(new BoundedChannelOptions(COLA_MAX)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

        _ = Task.Run(async () =>
        {
            await foreach (var p in _cola.Reader.ReadAllAsync())
            {
                string url = "";
                try { url = await GenerarOCache(p.Texto, p.Voz); }
                catch (Exception ex) { Console.WriteLine($"[EventVoice] TTS falló: {ex.Message}"); }

                // Único momento en que se toca el mundo, y es un instante: encolar bytes en las
                // conexiones. Mismo patrón que MercadoPago (Task.Run → lock puntual).
                try
                {
                    lock (UserListManager.GameLock) { Difundir(p, url); }
                }
                catch (Exception ex) { Console.WriteLine($"[EventVoice] difusión falló: {ex.Message}"); }
            }
        });
    }

    /// <summary>
    /// Devuelve la ruta HTTP del clip ("/tts/&lt;hash&gt;.mp3"), generándolo si hace falta.
    /// "" = no hay TTS del server, que el cliente lo diga con la voz del navegador.
    /// Corre SIEMPRE en el worker, nunca en el hilo del juego.
    /// </summary>
    private static async Task<string> GenerarOCache(string texto, byte voz)
    {
        if (_cmd.Length == 0) return "";

        string hash = Hash(texto, voz);
        string archivo = hash + "." + _ext;

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(hash, out string ya))
                return ya.Length == 0 ? "" : "/tts/" + ya;
        }

        string destino = Path.Combine(CacheDir, archivo);

        // El archivo puede seguir en disco de una corrida anterior: el nombre es el contenido.
        if (File.Exists(destino) && new FileInfo(destino).Length > 0)
        {
            lock (_cacheLock) _cache[hash] = archivo;
            return "/tts/" + archivo;
        }

        // Se genera en un temporal y recién al final se renombra: si el proceso muere a mitad,
        // no queda un .mp3 truncado con el nombre bueno (y el hash lo daría por válido para siempre).
        string tmp = destino + ".parcial";
        bool ok = await CorrerTts(texto, voz, tmp);
        if (ok && File.Exists(tmp) && new FileInfo(tmp).Length > 0)
        {
            try { File.Move(tmp, destino, true); }
            catch (Exception ex) { Console.WriteLine($"[EventVoice] no se pudo guardar el clip: {ex.Message}"); ok = false; }
        }
        else ok = false;

        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

        lock (_cacheLock) _cache[hash] = ok ? archivo : "";
        return ok ? "/tts/" + archivo : "";
    }

    /// <summary>Lanza el ejecutable de TTS. true si terminó con código 0 dentro del timeout.</summary>
    private static async Task<bool> CorrerTts(string texto, byte voz, string destino)
    {
        string motor = MotorDeVoz(voz);
        bool textoEnArgs = _args.Contains("{texto}", StringComparison.Ordinal);

        var psi = new ProcessStartInfo
        {
            FileName = _cmd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = !textoEnArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // ArgumentList (y no una string armada a mano) es lo que hace que un anuncio con
        // comillas o con && no pueda convertirse en otro comando: .NET escapa cada argumento
        // por separado y nunca pasa por un shell (UseShellExecute=false).
        foreach (var a in PartirArgs(_args))
        {
            psi.ArgumentList.Add(a
                .Replace("{out}", destino, StringComparison.Ordinal)
                .Replace("{voz}", motor, StringComparison.Ordinal)
                .Replace("{texto}", texto, StringComparison.Ordinal));
        }

        using var proc = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        if (!proc.Start()) return false;

        if (!textoEnArgs)
        {
            await proc.StandardInput.WriteAsync(texto);
            proc.StandardInput.Close();
        }
        // Hay que vaciar las dos salidas o un comando verborrágico llena el pipe y se cuelga.
        var salida = proc.StandardOutput.ReadToEndAsync();
        var errores = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeg));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            Console.WriteLine($"[EventVoice] TTS cortado por timeout ({_timeoutSeg}s).");
            return false;
        }

        await Task.WhenAll(salida, errores);
        if (proc.ExitCode != 0)
        {
            Console.WriteLine($"[EventVoice] TTS salió con código {proc.ExitCode}: {errores.Result?.Trim()}");
            return false;
        }
        Console.WriteLine($"[EventVoice] clip generado en {sw.ElapsedMilliseconds} ms → {Path.GetFileName(destino)}");
        return true;
    }

    /// <summary>Parte la línea de TtsArgs respetando las comillas dobles (un {texto} con espacios es UN argumento).</summary>
    private static List<string> PartirArgs(string linea)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(linea)) return res;
        var sb = new StringBuilder();
        bool comillas = false;
        foreach (char c in linea)
        {
            if (c == '"') { comillas = !comillas; continue; }
            if (c == ' ' && !comillas) { if (sb.Length > 0) { res.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) res.Add(sb.ToString());
        return res;
    }

    /// <summary>Nombre del clip = contenido. Mismo texto + misma voz ⇒ mismo archivo, para siempre.</summary>
    private static string Hash(string texto, byte voz)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(voz + "|" + texto));
        var sb = new StringBuilder(32);
        for (int i = 0; i < 16; i++) sb.Append(h[i].ToString("x2"));
        return sb.ToString();
    }

    // ============================================================
    //  Difusión (bajo GameLock)
    // ============================================================
    private static void Difundir(Pedido p, string url)
    {
        int llegaron = 0;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u == null || !u.flags.UserLogged || u.Conn == null) continue;
            if (p.Mapa != 0 && u.Pos.Map != p.Mapa) continue;
            // Candado de ClientCaps: el cliente que no declaró el bit5 no sabe saltear el id 219
            // y leería basura desde ahí. Ver Connection.SoportaVozEvento.
            if (!u.Conn.SoportaVozEvento) continue;
            ServerPackets.EventVoice(u.Conn, p.Texto, p.Voz, p.Volumen, p.Flags, url);
            llegaron++;
        }

        var gm = UserListManager.UserList[p.GmUserIndex];
        if (gm?.Conn != null && gm.flags.UserLogged && gm.Name == p.GmNombre)
        {
            ServerPackets.ConsoleMsg(gm.Conn,
                url.Length == 0
                    ? $"Anuncio enviado a {llegaron} jugador(es) — sin TTS del server, cada cliente lo dice con la voz de su navegador."
                    : $"Anuncio enviado a {llegaron} jugador(es) con voz del servidor.", 2);
        }
        Console.WriteLine($"[EventVoice] difundido a {llegaron} jugador(es){(url.Length == 0 ? " (voz del navegador)" : " (" + url + ")")}.");
    }

    // ============================================================
    //  Servido por HTTP (lo llama StatusEndpoint en GET /tts/<archivo>)
    // ============================================================
    /// <summary>
    /// Devuelve los bytes de un clip del cache, o null si no existe. `archivo` viene de la URL:
    /// se valida a mano (32 hex + extensión conocida) para que no pueda salirse de la carpeta.
    /// </summary>
    public static byte[] LeerClip(string archivo, out string contentType)
    {
        contentType = "application/octet-stream";
        if (string.IsNullOrEmpty(archivo) || archivo.Length > 40) return null;

        int punto = archivo.LastIndexOf('.');
        if (punto <= 0) return null;
        string nombre = archivo[..punto], ext = archivo[(punto + 1)..].ToLowerInvariant();
        if (nombre.Length != 32) return null;
        foreach (char c in nombre) if (!Uri.IsHexDigit(c)) return null;
        contentType = ext switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            _ => null,
        };
        if (contentType == null) return null;

        lock (_cacheLock)
        {
            if (_bytes.TryGetValue(archivo, out var enRam)) return enRam;
        }

        string ruta = Path.Combine(CacheDir, archivo);
        byte[] datos;
        try
        {
            if (!File.Exists(ruta)) return null;
            datos = File.ReadAllBytes(ruta);
        }
        catch { return null; }

        lock (_cacheLock)
        {
            _bytes[archivo] = datos;
            _bytesOrden.Add(archivo);
            while (_bytesOrden.Count > CACHE_BYTES_MAX)
            {
                _bytes.Remove(_bytesOrden[0]);
                _bytesOrden.RemoveAt(0);
            }
        }
        return datos;
    }
}
