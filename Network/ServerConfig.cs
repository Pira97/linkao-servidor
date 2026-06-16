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
    private static readonly object _verLock = new();

    public static string UltimaVersion
    {
        get
        {
            lock (_verLock)
            {
                if (_ultimaVersion != null && DateTime.UtcNow - _ultimaVersionAt < VERSION_TTL)
                    return _ultimaVersion;
                string nueva = LeerVersionRequerida();
                // Solo refrescamos timestamp y valor si conseguimos algo válido; si falla todo,
                // conservamos el último bueno (no dejamos pasar a todos por un corte de red).
                if (nueva != null) { _ultimaVersion = nueva; _ultimaVersionAt = DateTime.UtcNow; }
                return _ultimaVersion ?? "1";
            }
        }
    }

    /// <summary>Versión de cliente requerida. Orden: GitHub (client_version.txt) → Server.ini "VersionCliente"
    /// → version.txt local → "1". El cliente manda su número; si no coincide exacto, se lo rechaza con
    /// "Ejecuta el LAUNCHER para actualizar el juego."</summary>
    private static string LeerVersionRequerida()
    {
        // 1) GitHub: misma fuente que publica el launcher al exportar el cliente.
        try
        {
            string remoto = _http.GetStringAsync(CLIENT_VERSION_URL).GetAwaiter().GetResult()?.Trim();
            if (!string.IsNullOrEmpty(remoto) && int.TryParse(remoto, out _)) return remoto;
        }
        catch { /* sin internet o GitHub caído → fallbacks */ }

        // 2) Server.ini (fallback offline).
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
