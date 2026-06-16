namespace ServidorCS.Game;

/// <summary>
/// Persistencia server-side de macros del personaje (mod_Macros.bas). El blob (INI del cliente) se
/// guarda tal cual en &lt;Charfile&gt;\&lt;NOMBRE&gt;.mac (CP1252). El server NO interpreta el contenido:
/// lo recibe (SaveMacrosConfig 136), lo escribe, y al pedirlo (RequestMacrosConfig 135) lo devuelve
/// (MacrosConfig 164). Tope anti-abuso de 4096 bytes.
/// </summary>
public static class Macros
{
    public const int MAX_MACROS_BLOB_SIZE = 4096;

    private static string Path(string charName)
        => System.IO.Path.Combine(CharLoader.CharPath, charName.ToUpperInvariant() + ".mac");

    /// <summary>LoadMacrosFile: devuelve el blob del .mac (o "" si no existe). 1:1 mod_Macros.bas:29.</summary>
    public static string Load(string charName)
    {
        if (string.IsNullOrEmpty(charName)) return "";
        try
        {
            string p = Path(charName);
            if (!System.IO.File.Exists(p)) return "";
            byte[] raw = System.IO.File.ReadAllBytes(p);
            if (raw.Length == 0) return "";
            if (raw.Length > MAX_MACROS_BLOB_SIZE) Array.Resize(ref raw, MAX_MACROS_BLOB_SIZE);
            return Network.Cp1252.GetString(raw);
        }
        catch { return ""; }
    }

    /// <summary>
    /// Borra el .mac del personaje. Se llama al crear un PJ nuevo (por si quedó un .mac huérfano
    /// de un personaje borrado con el mismo nombre — el nuevo heredaría macros ajenos) y al
    /// borrar un personaje (BorrarPersonaje solo eliminaba el .chr).
    /// </summary>
    public static void Delete(string charName)
    {
        if (string.IsNullOrEmpty(charName)) return;
        try
        {
            string p = Path(charName);
            if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
        }
        catch { }
    }

    /// <summary>SaveMacrosFile: persiste el blob (escritura atómica vía .tmp). 1:1 mod_Macros.bas:78.</summary>
    public static void Save(string charName, string content)
    {
        if (string.IsNullOrEmpty(charName)) return;
        content ??= "";
        if (content.Length > MAX_MACROS_BLOB_SIZE) return; // descarta blobs abusivos
        try
        {
            string p = Path(charName);
            string tmp = p + ".tmp";
            System.IO.File.WriteAllBytes(tmp, Network.Cp1252.GetBytes(content));
            if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
            System.IO.File.Move(tmp, p);
        }
        catch { }
    }
}
