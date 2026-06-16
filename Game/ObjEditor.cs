using System.Text;
using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Editor de objetos en vivo para GMs (NUEVO, no VB6). El GM abre el form con /editobj
/// en el cliente Godot; el server le manda el catálogo (ObjEditorList), el detalle de un
/// objeto como pares clave=valor tal cual obj.dat (ObjEditorDetail), y al guardar:
///  1) persiste los cambios en obj.dat EN DISCO preservando comentarios/estructura (CP1252),
///  2) recarga ese objeto en memoria (ObjData.ReloadOne) → efecto INMEDIATO en combate/uso,
///  3) re-envía los slots de inventario de todos los usuarios online que tengan el objeto
///     (refresca el flag "puede usar" si cambiaron requisitos),
///  4) avisa a los demás GMs online.
/// El obj.dat es la única fuente de verdad: el detalle se lee SIEMPRE de disco, así el
/// round-trip (Faccion=, Genero=, Apuñala=, abierta=...) es byte-exacto y sin conversiones.
/// </summary>
public static class ObjEditor
{
    private const byte MIN_PRIV = AdminLoader.STATUS_SEMIDIOS; // 8: SemiDios o superior

    private static IniFile _cache;          // cache del obj.dat parseado (para detalles)
    private static bool _backupHecho;       // un solo backup de obj.dat por ejecución del server

    // ============================================================
    //  Validación de privilegios
    // ============================================================
    private static User ValidarGM(Connection conn)
    {
        var u = UserListManager.UserList[conn.UserIndex];
        if (u == null || !u.flags.UserLogged) return null;
        if (AdminLoader.GetFaccionStatus(u.Name) < MIN_PRIV)
        {
            ServerPackets.ConsoleMsg(conn, "No tenés privilegios para usar el editor de objetos.", 6);
            Console.WriteLine($"[ObjEditor] RECHAZADO: {u.Name} intentó usar el editor sin privilegios.");
            return null;
        }
        return u;
    }

    private static IniFile Ini()
    {
        if (_cache == null)
        {
            string file = ObjData.FilePath;
            _cache = file != null ? new IniFile(file) : null;
        }
        return _cache;
    }

    // ============================================================
    //  Catálogo (lista resumida de todos los objetos)
    // ============================================================
    public static void SendList(Connection conn)
    {
        var u = ValidarGM(conn);
        if (u == null) return;

        var list = new List<(int, byte, byte, int, string)>();
        for (int i = 1; i <= ObjData.Count; i++)
        {
            var o = ObjData.Get(i);
            if (string.IsNullOrEmpty(o.Name)) continue;
            byte sub = (byte)Math.Clamp(o.SubTipo, 0, 255);
            list.Add((i, (byte)o.Type, sub, o.GrhIndex, o.Name));
        }
        ServerPackets.ObjEditorList(conn, list);
        Console.WriteLine($"[ObjEditor] {u.Name} pidió el catálogo ({list.Count} objetos).");
    }

    // ============================================================
    //  Detalle de un objeto (todas sus claves de obj.dat)
    // ============================================================
    public static void SendDetail(Connection conn, int objIndex)
    {
        var u = ValidarGM(conn);
        if (u == null) return;
        if (objIndex < 1 || objIndex > ObjData.Count)
        {
            ServerPackets.ObjEditorResult(conn, false, objIndex, $"Índice de objeto inválido: {objIndex}.");
            return;
        }

        var ini = Ini();
        if (ini == null || !ini.Loaded)
        {
            ServerPackets.ObjEditorResult(conn, false, objIndex, "No se encontró obj.dat en el servidor.");
            return;
        }

        var fields = new List<(string, string)>();
        foreach (var kv in ini.Section("OBJ" + objIndex))
            fields.Add((kv.Key, kv.Value));

        ServerPackets.ObjEditorDetail(conn, objIndex, fields);
    }

    // ============================================================
    //  Recarga GLOBAL: relee TODO el obj.dat de disco en caliente
    //  (para cuando se editó el archivo desde afuera, p.ej. el editor del cliente).
    // ============================================================
    public static void ReloadAll(Connection conn)
    {
        var u = ValidarGM(conn);
        if (u == null) return;

        // Relee obj.dat completo de disco a RAM (misma semántica de parseo que el arranque).
        _cache = null;          // invalidar el cache de detalles para que relea de disco
        ObjData.Reload();

        // Refrescar todos los inventarios online: flag "puede usar", nombre, gráfico, stats.
        Inventory.RefreshAllInventories();

        string ruta = ObjData.FilePath ?? "(no encontrado)";
        Console.WriteLine($"[ObjEditor] {u.Name} recargó TODO el obj.dat ({ObjData.Count} objetos) desde {ruta}.");

        ServerPackets.ObjEditorResult(conn, true, 0,
            $"obj.dat recargado del disco: {ObjData.Count} objetos. Cambios aplicados en vivo.");
        // Re-enviar el catálogo fresco para que la lista del editor quede sincronizada.
        SendList(conn);

        // Avisar a los demás GMs online.
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var otro = UserListManager.UserList[i];
            if (otro?.flags.UserLogged != true || otro.Conn == null || otro == u) continue;
            if (AdminLoader.GetFaccionStatus(otro.Name) < MIN_PRIV) continue;
            ServerPackets.ConsoleMsg(otro.Conn,
                $"[Editor de objetos] {u.Name} recargó todo el obj.dat del disco ({ObjData.Count} objetos).", 7);
        }
    }

    // ============================================================
    //  Guardado: persistir a disco + recargar en caliente + difundir
    // ============================================================
    public static void Save(int userIndex, int objIndex, List<(string Key, string Value)> cambios)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null) return;
        var conn = u.Conn;
        if (ValidarGM(conn) == null) return;

        if (objIndex < 1 || objIndex > ObjData.Count)
        {
            ServerPackets.ObjEditorResult(conn, false, objIndex, $"Índice de objeto inválido: {objIndex}.");
            return;
        }
        if (cambios == null || cambios.Count == 0)
        {
            ServerPackets.ObjEditorResult(conn, false, objIndex, "No hay cambios para guardar.");
            return;
        }

        // --- Validación de claves/valores ---
        foreach (var (key, value) in cambios)
        {
            if (!ClaveValida(key))
            {
                ServerPackets.ObjEditorResult(conn, false, objIndex, $"Clave inválida: \"{key}\".");
                return;
            }
            if (!ValorValido(value))
            {
                ServerPackets.ObjEditorResult(conn, false, objIndex, $"Valor inválido para \"{key}\".");
                return;
            }
            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(value))
            {
                ServerPackets.ObjEditorResult(conn, false, objIndex, "El nombre no puede quedar vacío.");
                return;
            }
            if (key.Equals("ObjType", StringComparison.OrdinalIgnoreCase) &&
                (!int.TryParse(value, out int t) || t < 1 || t > 60))
            {
                ServerPackets.ObjEditorResult(conn, false, objIndex, "ObjType debe ser un número entre 1 y 60.");
                return;
            }
        }

        string file = ObjData.FilePath;
        if (file == null)
        {
            ServerPackets.ObjEditorResult(conn, false, objIndex, "No se encontró obj.dat en el servidor.");
            return;
        }

        try
        {
            // Backup único por ejecución, antes del primer guardado.
            if (!_backupHecho)
            {
                string bak = file + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                File.Copy(file, bak, overwrite: true);
                _backupHecho = true;
                Console.WriteLine($"[ObjEditor] Backup creado: {bak}");
            }

            var dat = new ObjDatFile(file);
            if (!dat.TieneSeccion(objIndex))
            {
                ServerPackets.ObjEditorResult(conn, false, objIndex, $"La sección [OBJ{objIndex}] no existe en obj.dat.");
                return;
            }
            foreach (var (key, value) in cambios)
                dat.Set(objIndex, key, value);
            dat.Save();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ObjEditor] ERROR guardando obj.dat: {ex}");
            ServerPackets.ObjEditorResult(conn, false, objIndex, "Error al escribir obj.dat: " + ex.Message);
            return;
        }

        // Recarga en caliente: memoria == disco, misma semántica de parseo que el arranque.
        _cache = null;
        ObjData.ReloadOne(objIndex);
        var o = ObjData.Get(objIndex);

        // Refresco en tiempo real del inventario de todos los usuarios que tengan el objeto.
        Inventory.RefreshObjEverywhere((short)objIndex);

        string resumen = string.Join(", ", cambios.Select(c => $"{c.Key}={c.Value}"));
        Console.WriteLine($"[ObjEditor] {u.Name} editó OBJ{objIndex} ({o.Name}): {resumen}");

        ServerPackets.ObjEditorResult(conn, true, objIndex,
            $"Objeto {objIndex} ({o.Name}) guardado: {cambios.Count} campo(s) actualizado(s).");
        // Re-enviar el detalle fresco leído de disco para que el form quede sincronizado.
        SendDetail(conn, objIndex);

        // Avisar a los demás GMs online.
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var otro = UserListManager.UserList[i];
            if (otro?.flags.UserLogged != true || otro.Conn == null || i == userIndex) continue;
            if (AdminLoader.GetFaccionStatus(otro.Name) < MIN_PRIV) continue;
            ServerPackets.ConsoleMsg(otro.Conn,
                $"[Editor de objetos] {u.Name} editó el objeto {objIndex} ({o.Name}): {resumen}", 7);
        }
    }

    // Claves estilo obj.dat: letras (incluye ñ/acentos por "Apuñala"), dígitos y _.
    private static bool ClaveValida(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 32) return false;
        foreach (char c in key)
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    // Valores: una sola línea, sin caracteres de control, largo acotado.
    private static bool ValorValido(string value)
    {
        if (value == null || value.Length > 200) return false;
        foreach (char c in value)
            if (char.IsControl(c)) return false;
        return true;
    }

    // ============================================================
    //  Escritor de obj.dat que preserva la estructura del archivo.
    //  A diferencia de IniDocument, reconoce los headers con comentario
    //  pegado ("[OBJ64]'Hace respawn") que usa este obj.dat.
    // ============================================================
    private sealed class ObjDatFile
    {
        private readonly string _path;
        private readonly List<string> _lines;
        // sección (en MAYÚSCULAS, ej "OBJ64") → índice de línea del header
        private readonly Dictionary<string, int> _headers = new();

        public ObjDatFile(string path)
        {
            _path = path;
            string text = Cp1252.GetString(File.ReadAllBytes(path))
                .Replace("\r\n", "\n").Replace('\r', '\n');
            _lines = new List<string>(text.Split('\n'));
            Reindexar();
        }

        private void Reindexar()
        {
            _headers.Clear();
            for (int i = 0; i < _lines.Count; i++)
            {
                string s = _lines[i].TrimStart();
                if (!s.StartsWith("[")) continue;
                int close = s.IndexOf(']');
                if (close <= 1) continue;
                string sec = s.Substring(1, close - 1).Trim().ToUpperInvariant();
                if (!_headers.ContainsKey(sec)) _headers[sec] = i;
            }
        }

        public bool TieneSeccion(int objIndex) => _headers.ContainsKey("OBJ" + objIndex);

        /// <summary>Setea clave=valor dentro de [OBJn]: reemplaza la línea existente o la inserta al final de la sección.</summary>
        public void Set(int objIndex, string key, string value)
        {
            if (!_headers.TryGetValue("OBJ" + objIndex, out int start)) return;

            // Fin de la sección = línea anterior al próximo header (o EOF).
            int end = _lines.Count - 1;
            for (int i = start + 1; i < _lines.Count; i++)
            {
                string s = _lines[i].TrimStart();
                if (s.StartsWith("[") && s.Contains(']')) { end = i - 1; break; }
            }

            // Buscar la clave dentro de la sección (ignora comentarios con ').
            for (int i = start + 1; i <= end; i++)
            {
                string s = _lines[i].Trim();
                if (s.Length == 0 || s.StartsWith("'") || s.StartsWith(";")) continue;
                int eq = s.IndexOf('=');
                if (eq <= 0) continue;
                if (s.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    _lines[i] = key + "=" + value;
                    return;
                }
            }

            // No existe: insertar después de la última línea con contenido de la sección.
            int insertAt = start;
            for (int i = start + 1; i <= end; i++)
                if (_lines[i].Trim().Length > 0) insertAt = i;
            _lines.Insert(insertAt + 1, key + "=" + value);
            Reindexar();
        }

        public void Save()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _lines.Count; i++)
            {
                sb.Append(_lines[i]);
                if (i < _lines.Count - 1) sb.Append("\r\n");
            }
            // Escritura atómica: tmp + replace, para no dejar un obj.dat a medias si algo falla.
            string tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, Cp1252.GetBytes(sb.ToString()));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
