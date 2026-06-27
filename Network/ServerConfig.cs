namespace ServidorCS.Network;

/// <summary>
/// Lectura mínima de Server.ini (formato INI clásico de AO, CP1252).
/// Se irá ampliando al portar clsIniManager / clsIniReader.
/// </summary>
public static class ServerConfig
{
    public static int ReadPort(int defaultPort)
    {
        string path = FindServerIni();
        if (path == null) return defaultPort;

        try
        {
            // Server.ini en CP1252; leemos bytes y decodificamos con nuestro codec.
            byte[] data = File.ReadAllBytes(path);
            foreach (var raw in Cp1252.GetString(data).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("Puerto", StringComparison.OrdinalIgnoreCase))
                {
                    int eq = line.IndexOf('=');
                    if (eq >= 0 && int.TryParse(line[(eq + 1)..].Trim(), out int p))
                        return p;
                }
            }
        }
        catch { /* usa default */ }

        return defaultPort;
    }

    /// <summary>Lee un string de Server.ini buscando "Clave=valor" (ignora la sección, como ReadInt).</summary>
    public static string ReadString(string clave, string def = "")
    {
        string path = FindServerIni();
        if (path == null) return def;
        try
        {
            byte[] data = File.ReadAllBytes(path);
            foreach (var raw in Cp1252.GetString(data).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith(clave, StringComparison.OrdinalIgnoreCase))
                {
                    int eq = line.IndexOf('=');
                    if (eq >= 0 && line[..eq].Trim().Equals(clave, StringComparison.OrdinalIgnoreCase))
                        return line[(eq + 1)..].Trim();
                }
            }
        }
        catch { }
        return def;
    }

    /// <summary>Lee un entero de Server.ini buscando "Clave=valor" (ignora la sección, como ReadPort).</summary>
    public static int ReadInt(string clave, int def)
    {
        string path = FindServerIni();
        if (path == null) return def;
        try
        {
            byte[] data = File.ReadAllBytes(path);
            foreach (var raw in Cp1252.GetString(data).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith(clave, StringComparison.OrdinalIgnoreCase))
                {
                    int eq = line.IndexOf('=');
                    if (eq >= 0 && line[..eq].Trim().Equals(clave, StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(line[(eq + 1)..].Trim(), out int v))
                        return v;
                }
            }
        }
        catch { }
        return def;
    }

    /// <summary>
    /// Versión requerida del cliente. Port 1:1 de ULTIMAVERSION/LeerVersionDesdeArchivo (FileIO.bas:2884):
    /// se lee de version.txt (un número simple en la primera línea) junto al ejecutable del server.
    /// Si el archivo no existe o no es numérico, devuelve "1" como el VB6. Se cachea en el primer acceso.
    /// </summary>
    // Fuente de verdad ÚNICA de la versión: el mismo repo de updates que publica Actualizar.bat.
    // Así el server (en la VM) se entera del número nuevo SIN redeploy ni reinicio: lo refresca de
    // GitHub cada VERSION_TTL. Si no hay internet, cae a Server.ini "VersionCliente" y luego version.txt.
    private const string CLIENT_VERSION_URL = "https://raw.githubusercontent.com/Pira97/LinkAO-Updates/main/client_version.txt";
    private static readonly TimeSpan VERSION_TTL = TimeSpan.FromSeconds(120);
    private static readonly System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static string _ultimaVersion;
    private static DateTime _ultimaVersionAt = DateTime.MinValue;
    private static bool _refrescoEnVuelo;
    private static readonly object _verLock = new();

    /// <summary>
    /// Versión de cliente exigida AHORA. Nunca hace red en el camino del login (ver abajo):
    /// devuelve el último valor bueno y, si venció el TTL, dispara el refresco en SEGUNDO PLANO.
    ///
    /// Dos bugs de producción que arregla, los dos vistos el 3-ago-2026 (cuenta 'class' rechazada
    /// ~100 veces seguidas con "Servidor: 9, Cliente: 28", y entrando bien 2 minutos después):
    ///
    /// 1. UN fallo transitorio de GitHub dejaba a TODOS afuera 2 minutos. El fallback local
    ///    (Server.ini/version.txt) se cacheaba como si fuera un valor bueno, y en la VM ese
    ///    archivo tenía un número VIEJO (VersionCliente=9 contra la 28 real) → "cliente
    ///    desactualizado" para todo el mundo hasta que vencía el TTL. Peor: el portal muestra
    ///    CUALQUIER rechazo como "Cuenta o contraseña incorrectas", así que el jugador ve un
    ///    problema de contraseña que no existe. Ahora el valor local es SEMILLA, no reemplazo:
    ///    una vez que se conoce un número remoto bueno, un fallo de red conserva ESE.
    /// 2. La llamada HTTP (hasta 5s de timeout) salía desde el handler de ConnectAccount, o sea
    ///    con el GameLock TOMADO: GitHub lento = servidor entero congelado. Ahora el refresco
    ///    corre en su propia tarea y el login contesta con lo que ya tiene.
    /// </summary>
    public static string UltimaVersion
    {
        get
        {
            lock (_verLock)
            {
                bool vencido = DateTime.UtcNow - _ultimaVersionAt >= VERSION_TTL;
                if (_ultimaVersion != null && !vencido) return _ultimaVersion;

                if (!_refrescoEnVuelo)
                {
                    _refrescoEnVuelo = true;
                    Task.Run(RefrescarVersionRemota);
                }

                // Todavía sin número remoto (arranque con GitHub caído): semilla local. Es el
                // único caso en que manda Server.ini/version.txt.
                return _ultimaVersion ?? LeerVersionLocal();
            }
        }
    }

    /// <summary>
    /// Trae el número de GitHub y lo cachea. Si falla, NO toca el valor vigente ni el timestamp:
    /// el próximo login vuelve a intentar y mientras tanto se sigue usando el último bueno.
    /// </summary>
    private static void RefrescarVersionRemota()
    {
        string remoto = LeerVersionRemota();
        lock (_verLock)
        {
            _refrescoEnVuelo = false;
            if (remoto == null)
            {
                // Log una sola vez por ventana, no por intento: si GitHub está caído esto se
                // llama en cada login y llenaría la consola.
                if (_ultimaVersion == null)
                    Console.WriteLine("[ServidorCS] No se pudo leer la versión de cliente desde GitHub; " +
                                      $"usando la local ({LeerVersionLocal()}). Revisá Server.ini si rechaza logins.");
                return;
            }
            if (remoto != _ultimaVersion)
                Console.WriteLine($"[ServidorCS] Versión de cliente requerida: {remoto}");
            _ultimaVersion = remoto;
            _ultimaVersionAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Primer valor, ANTES de aceptar conexiones (Program.cs). Acá sí conviene esperar la red:
    /// no hay nadie jugando, no hay ningún lock tomado, y evita que los primeros logins tras un
    /// reinicio se coman el número local por llegar antes que el refresco.
    /// </summary>
    public static void PrecargarVersion()
    {
        string remoto = LeerVersionRemota();
        lock (_verLock)
        {
            if (remoto != null) { _ultimaVersion = remoto; _ultimaVersionAt = DateTime.UtcNow; }
        }
        Console.WriteLine(remoto != null
            ? $"[ServidorCS] Versión de cliente requerida: {remoto} (GitHub)"
            : $"[ServidorCS] GitHub no respondió: versión de cliente {LeerVersionLocal()} (local). " +
              "Si Server.ini está viejo, se rechazan TODOS los logins por 'cliente desactualizado'.");
    }

    /// <summary>GitHub (client_version.txt), misma fuente que publica el launcher al exportar el
    /// cliente. null si no hay red, GitHub falla o el contenido no es un número.</summary>
    private static string LeerVersionRemota()
    {
        try
        {
            string remoto = _http.GetStringAsync(CLIENT_VERSION_URL).GetAwaiter().GetResult()?.Trim();
            if (!string.IsNullOrEmpty(remoto) && int.TryParse(remoto, out _)) return remoto;
        }
        catch { /* sin internet o GitHub caído */ }
        return null;
    }

    /// <summary>Fallback offline: Server.ini "VersionCliente" → version.txt → "1".</summary>
    private static string LeerVersionLocal()
    {
        string v = ReadString("VersionCliente", "");
        if (v.Length > 0 && int.TryParse(v, out _)) return v;
        return LeerVersionDesdeArchivo();
    }

    private static string LeerVersionDesdeArchivo()
    {
        string path = FindVersionTxt();
        if (path == null)
        {
            Console.WriteLine("[ServidorCS] Advertencia: no se encontro version.txt, usando version por defecto 1.");
            return "1";
        }
        try
        {
            string version = File.ReadLines(path).FirstOrDefault()?.Trim() ?? "";
            if (version.Length == 0 || !int.TryParse(version, out _))
            {
                Console.WriteLine("[ServidorCS] Advertencia: version.txt debe contener un numero, usando version por defecto 1.");
                return "1";
            }
            return version;
        }
        catch
        {
            return "1";
        }
    }

    /// <summary>VersionOK (Admin.bas:95): comparación estricta del entero recibido contra ULTIMAVERSION.</summary>
    public static bool VersionOk(short version) => version.ToString() == UltimaVersion;

    private static string FindVersionTxt()
    {
        // Prioridad a la carpeta del .exe: es lo que se deploya a la VM (version.txt va al lado del binario).
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "version.txt"),
            Path.Combine(Directory.GetCurrentDirectory(), "version.txt"),
        };
        if (!string.IsNullOrEmpty(Game.DataPaths.Root)) candidates.Add(Path.Combine(Game.DataPaths.Root, "version.txt"));
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static string FindServerIni()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(Game.DataPaths.Root)) candidates.Add(Path.Combine(Game.DataPaths.Root, "Server.ini"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Server.ini"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "Server.ini"));
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        return null;
    }
}
