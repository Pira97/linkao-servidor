namespace ServidorCS.Game;

/// <summary>
/// Localiza la carpeta de datos del servidor VB6 (Cuentas, Charfile, Maps, Dat).
/// Busca una carpeta "Servidor" subiendo desde el ejecutable y desde el cwd, para
/// que el exe funcione sin importar desde dónde se lance (publish/, raíz, etc.).
/// </summary>
public static class DataPaths
{
    /// <summary>Raíz de datos (la carpeta "Servidor"), con separador final. "" si no se encontró.</summary>
    public static readonly string Root = FindServerRoot();

    private static string FindServerRoot()
    {
        // Candidatos: subir hasta 6 niveles desde el exe y desde el cwd buscando "Servidor".
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                // ¿este dir ya ES la carpeta Servidor? (tiene Charfile o Cuentas)
                if (Directory.Exists(Path.Combine(dir.FullName, "Charfile")) ||
                    Directory.Exists(Path.Combine(dir.FullName, "Cuentas")))
                    return dir.FullName + Path.DirectorySeparatorChar;

                // ¿tiene una subcarpeta "Servidor"?
                var srv = Path.Combine(dir.FullName, "Servidor");
                if (Directory.Exists(Path.Combine(srv, "Charfile")) ||
                    Directory.Exists(Path.Combine(srv, "Cuentas")))
                    return srv + Path.DirectorySeparatorChar;
            }
        }
        return "";
    }

    /// <summary>Devuelve la ruta a una subcarpeta de datos (ej: "Charfile", "Maps", "Dat").</summary>
    public static string Sub(string name) => Root + name + Path.DirectorySeparatorChar;
}
