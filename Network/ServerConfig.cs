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
    private static string _ultimaVersion;
    public static string UltimaVersion => _ultimaVersion ??= LeerVersionDesdeArchivo();

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
