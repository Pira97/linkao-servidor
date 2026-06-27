using System.Linq;
using System.Text;
using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Editor de hechizos en vivo para GMs (NUEVO, no VB6). Mismo mecanismo que ObjEditor:
/// catálogo + detalle genérico clave=valor (tal cual Hechizos.dat) + guardado que persiste
/// a disco preservando comentarios/estructura y recarga en caliente.
/// A DIFERENCIA de ObjEditor: acá SÍ se puede dar de alta un hechizo nuevo — si el índice
/// pedido no tiene sección en Hechizos.dat, Save() la crea (exige el campo "Nombre").
/// El Hechizos.dat es la única fuente de verdad: el detalle se lee SIEMPRE de disco.
/// </summary>
public static class SpellEditor
{
    private const byte MIN_PRIV = AdminLoader.STATUS_SEMIDIOS; // 8: SemiDios o superior, igual que ObjEditor
    private const int MAX_INDEX = 2000; // mismo tope que SpellData.EnsureLoaded

    private static IniFile _cache;         // cache del Hechizos.dat parseado (para detalles)
    private static bool _backupHecho;      // un solo backup de Hechizos.dat por ejecución del server

    // ============================================================
    //  Validación de privilegios
    // ============================================================
    private static User ValidarGM(Connection conn)
    {
        var u = UserListManager.UserList[conn.UserIndex];
        if (u == null || !u.flags.UserLogged) return null;
        if (AdminLoader.GetFaccionStatus(u.Name) < MIN_PRIV)
        {
            ServerPackets.ConsoleMsg(conn, "No tenés privilegios para usar el editor de hechizos.", 6);
            Console.WriteLine($"[SpellEditor] RECHAZADO: {u.Name} intentó usar el editor sin privilegios.");
            return null;
        }
        return u;
    }

    private static IniFile Ini()
    {
        if (_cache == null)
        {
            string file = SpellData.FilePath;
            _cache = file != null ? new IniFile(file) : null;
        }
        return _cache;
    }

    // ============================================================
    //  Catálogo (lista resumida de todos los hechizos)
    // ============================================================
    public static void SendList(Connection conn)
    {
        var u = ValidarGM(conn);
        if (u == null) return;

        var ini = Ini();
        var list = new List<(int, int, string)>();
        if (ini != null && ini.Loaded)
        {
            for (int i = 1; i <= MAX_INDEX; i++)
            {
                string name = ini.Get("HECHIZO" + i, "Nombre");
                if (string.IsNullOrEmpty(name)) continue;
                list.Add((i, ParseIntSafe(ini.Get("HECHIZO" + i, "Tipo")), name));
            }
        }
        ServerPackets.SpellEditorList(conn, list);
        Console.WriteLine($"[SpellEditor] {u.Name} pidió el catálogo ({list.Count} hechizos).");
    }

    // ============================================================
    //  Detalle de un hechizo (todas sus claves de Hechizos.dat)
    // ============================================================
    public static void SendDetail(Connection conn, int spellIndex)
    {
        var u = ValidarGM(conn);
        if (u == null) return;
        if (spellIndex < 1 || spellIndex > MAX_INDEX)
        {
            ServerPackets.SpellEditorResult(conn, false, spellIndex, $"Índice de hechizo inválido: {spellIndex}.");
            return;
        }

        var ini = Ini();
        if (ini == null || !ini.Loaded)
        {
            ServerPackets.SpellEditorResult(conn, false, spellIndex, "No se encontró Hechizos.dat en el servidor.");
            return;
        }

        var fields = new List<(string, string)>();
        foreach (var kv in ini.Section("HECHIZO" + spellIndex))
            fields.Add((kv.Key, kv.Value));

        // Si la sección no existe (el GM eligió un índice libre para crear uno nuevo) se
        // manda igual, vacía — el cliente arma el formulario con su plantilla local.
        ServerPackets.SpellEditorDetail(conn, spellIndex, fields);
    }

    // ============================================================
    //  Recarga GLOBAL: relee TODO el Hechizos.dat de disco en caliente
    // ============================================================
    public static void ReloadAll(Connection conn)
    {
        var u = ValidarGM(conn);
        if (u == null) return;

        _cache = null; // invalidar el cache de detalles para que relea de disco
        SpellData.Reload();

        string ruta = SpellData.FilePath ?? "(no encontrado)";
        Console.WriteLine($"[SpellEditor] {u.Name} recargó TODO el Hechizos.dat desde {ruta}.");

        ServerPackets.SpellEditorResult(conn, true, 0, "Hechizos.dat recargado del disco. Cambios aplicados en vivo.");
        SendList(conn);

        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var otro = UserListManager.UserList[i];
            if (otro?.flags.UserLogged != true || otro.Conn == null || otro == u) continue;
            if (AdminLoader.GetFaccionStatus(otro.Name) < MIN_PRIV) continue;
            ServerPackets.ConsoleMsg(otro.Conn,
                $"[Editor de hechizos] {u.Name} recargó todo el Hechizos.dat del disco.", 7);
        }
    }

    // ============================================================
    //  Guardado: persistir a disco (creando la sección si hace falta) + recargar + difundir
    // ============================================================
    public static void Save(int userIndex, int spellIndex, List<(string Key, string Value)> cambios)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null) return;
        var conn = u.Conn;
        if (ValidarGM(conn) == null) return;

        if (spellIndex < 1 || spellIndex > MAX_INDEX)
        {
            ServerPackets.SpellEditorResult(conn, false, spellIndex, $"Índice de hechizo inválido: {spellIndex}.");
            return;
        }
        if (cambios == null || cambios.Count == 0)
        {
            ServerPackets.SpellEditorResult(conn, false, spellIndex, "No hay cambios para guardar.");
            return;
        }

        foreach (var (key, value) in cambios)
        {
            if (!ClaveValida(key))
            {
                ServerPackets.SpellEditorResult(conn, false, spellIndex, $"Clave inválida: \"{key}\".");
                return;
            }
            if (!ValorValido(value))
            {
                ServerPackets.SpellEditorResult(conn, false, spellIndex, $"Valor inválido para \"{key}\".");
                return;
            }
        }

        string file = SpellData.FilePath;
        if (file == null)
        {
            ServerPackets.SpellEditorResult(conn, false, spellIndex, "No se encontró Hechizos.dat en el servidor.");
            return;
        }

        try
        {
            if (!_backupHecho)
            {
                string bak = file + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                File.Copy(file, bak, overwrite: true);
                _backupHecho = true;
                Console.WriteLine($"[SpellEditor] Backup creado: {bak}");
            }

            var dat = new HechizoDatFile(file);
            if (!dat.TieneSeccion(spellIndex))
            {
                // Alta de hechizo nuevo (a diferencia de ObjEditor, acá SÍ está permitido).
                string nombreNuevo = cambios.FirstOrDefault(c =>
                    c.Key.Equals("Nombre", StringComparison.OrdinalIgnoreCase)).Value;
                if (string.IsNullOrWhiteSpace(nombreNuevo))
                {
                    ServerPackets.SpellEditorResult(conn, false, spellIndex,
                        "Para crear un hechizo nuevo hace falta completar el campo \"Nombre\".");
                    return;
                }
                dat.CrearSeccion(spellIndex);
            }
            else
            {
                var nombreCambio = cambios.FirstOrDefault(c => c.Key.Equals("Nombre", StringComparison.OrdinalIgnoreCase));
                if (nombreCambio.Key != null && string.IsNullOrWhiteSpace(nombreCambio.Value))
                {
                    ServerPackets.SpellEditorResult(conn, false, spellIndex, "El nombre no puede quedar vacío.");
                    return;
                }
            }

            foreach (var (key, value) in cambios)
                dat.Set(spellIndex, key, value);
            dat.Save();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SpellEditor] ERROR guardando Hechizos.dat: {ex}");
            ServerPackets.SpellEditorResult(conn, false, spellIndex, "Error al escribir Hechizos.dat: " + ex.Message);
            return;
        }

        // Recarga en caliente. No hay ReloadOne para hechizos (tabla chica, sin costo real
        // de recargar todo) — misma semántica de parseo que el arranque.
        _cache = null;
        SpellData.Reload();
        string nombreFinal = SpellData.GetName(spellIndex);

        string resumen = string.Join(", ", cambios.Select(c => $"{c.Key}={c.Value}"));
        Console.WriteLine($"[SpellEditor] {u.Name} editó HECHIZO{spellIndex} ({nombreFinal}): {resumen}");

        ServerPackets.SpellEditorResult(conn, true, spellIndex,
            $"Hechizo {spellIndex} ({nombreFinal}) guardado: {cambios.Count} campo(s) actualizado(s).");
        // Re-enviar el detalle fresco leído de disco y la lista (por si fue un alta nueva).
        SendDetail(conn, spellIndex);
        SendList(conn);

        // Avisar a los demás GMs online.
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var otro = UserListManager.UserList[i];
            if (otro?.flags.UserLogged != true || otro.Conn == null || i == userIndex) continue;
            if (AdminLoader.GetFaccionStatus(otro.Name) < MIN_PRIV) continue;
            ServerPackets.ConsoleMsg(otro.Conn,
                $"[Editor de hechizos] {u.Name} editó el hechizo {spellIndex} ({nombreFinal}): {resumen}", 7);
        }
    }

    private static int ParseIntSafe(string s) => int.TryParse(s, out var v) ? v : 0;

    // Claves estilo Hechizos.dat: letras, dígitos y _.
    private static bool ClaveValida(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 40) return false;
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
    //  Escritor de Hechizos.dat que preserva la estructura del archivo — idéntico al
    //  ObjDatFile de ObjEditor.cs, salvo que TAMBIÉN sabe crear una sección nueva al final.
    // ============================================================
    private sealed class HechizoDatFile
    {
        private readonly string _path;
        private readonly List<string> _lines;
        // sección (en MAYÚSCULAS, ej "HECHIZO64") → índice de línea del header
        private readonly Dictionary<string, int> _headers = new();

        public HechizoDatFile(string path)
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

        public bool TieneSeccion(int spellIndex) => _headers.ContainsKey("HECHIZO" + spellIndex);

        /// <summary>Agrega "[HECHIZOn]" al final del archivo (alta de hechizo nuevo).</summary>
        public void CrearSeccion(int spellIndex)
        {
            if (TieneSeccion(spellIndex)) return;
            if (_lines.Count > 0 && _lines[_lines.Count - 1].Trim().Length > 0) _lines.Add("");
            _lines.Add("[HECHIZO" + spellIndex + "]");
            Reindexar();
        }

        /// <summary>Setea clave=valor dentro de [HECHIZOn]: reemplaza la línea existente o la inserta al final de la sección.</summary>
        public void Set(int spellIndex, string key, string value)
        {
            if (!_headers.TryGetValue("HECHIZO" + spellIndex, out int start)) return;

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
            // Escritura atómica: tmp + replace, para no dejar un Hechizos.dat a medias si algo falla.
            string tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, Cp1252.GetBytes(sb.ToString()));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
