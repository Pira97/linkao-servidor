using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Lector de archivos INI estilo VB6 (GetVar). Lee en CP1252.
/// Equivale a clsIniManager/GetVar del servidor original.
/// </summary>
public sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public bool Loaded { get; private set; }

    public IniFile(string path)
    {
        if (!File.Exists(path)) return;
        byte[] raw = File.ReadAllBytes(path);
        string text = Cp1252.GetString(raw).Replace("\r\n", "\n").Replace('\r', '\n');

        Dictionary<string, string> current = null;
        foreach (var line in text.Split('\n'))
        {
            var s = line.Trim();
            if (s.Length == 0 || s.StartsWith(";")) continue;
            if (s.StartsWith("["))
            {
                // Formato: [SECCION] o [SECCION]comentario (obj.dat usa esto)
                // Buscar el primer ] para extraer la sección
                int closeBracket = s.IndexOf(']');
                if (closeBracket > 0)
                {
                    string sec = s.Substring(1, closeBracket - 1);
                    if (!_sections.TryGetValue(sec, out current))
                    {
                        current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        _sections[sec] = current;
                    }
                    continue;
                }
            }
            int eq = s.IndexOf('=');
            if (eq < 0 || current == null) continue;
            string key = s.Substring(0, eq).Trim();
            string val = s.Substring(eq + 1).Trim();
            current[key] = val;
        }
        Loaded = true;
    }

    /// <summary>GetVar: devuelve el valor o "" si no existe.</summary>
    public string Get(string section, string key)
        => _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : "";

    public int GetInt(string section, string key)
        => int.TryParse(Get(section, key), out var v) ? v : 0;

    private static readonly Dictionary<string, string> _emptySection = new();

    /// <summary>Todas las claves de una sección (en orden de aparición), o un dict vacío.</summary>
    public IReadOnlyDictionary<string, string> Section(string section)
        => _sections.TryGetValue(section, out var s) ? s : _emptySection;
}

/// <summary>
/// INI editable que PRESERVA la estructura original del archivo (orden de secciones y
/// líneas, comentarios). Permite Set(seccion, clave, valor) y Save() reescribiendo solo
/// lo que cambió. Necesario para guardar el .chr sin perder secciones que el server aún
/// no modela (FLAGS, FACCIONES, GUILD, etc.). Escribe en CP1252 (ver [[vb6_encoding]]).
/// </summary>
public sealed class IniDocument
{
    private sealed class Line { public string Raw; public string Section; public string Key; }
    private readonly List<Line> _lines = new();
    public bool Loaded { get; private set; }

    public IniDocument(string path)
    {
        if (!File.Exists(path)) return;
        byte[] raw = File.ReadAllBytes(path);
        string text = Cp1252.GetString(raw).Replace("\r\n", "\n").Replace('\r', '\n');

        string curSec = "";
        foreach (var line in text.Split('\n'))
        {
            var s = line.Trim();
            if (s.StartsWith("[") && s.EndsWith("]"))
            {
                curSec = s.Substring(1, s.Length - 2);
                _lines.Add(new Line { Raw = line, Section = curSec, Key = null });
            }
            else
            {
                int eq = s.IndexOf('=');
                string key = (eq > 0 && !s.StartsWith(";")) ? s.Substring(0, eq).Trim() : null;
                _lines.Add(new Line { Raw = line, Section = curSec, Key = key });
            }
        }
        Loaded = true;
    }

    /// <summary>Actualiza una clave existente, o la agrega al final de su sección (creándola si falta).</summary>
    public void Set(string section, string key, string value)
    {
        // Buscar clave existente en la sección.
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].Key != null &&
                string.Equals(_lines[i].Section, section, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_lines[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i].Raw = key + "=" + value;
                return;
            }

        // No existe: encontrar el final de la sección para insertar.
        int lastInSection = -1, sectionStart = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (string.Equals(_lines[i].Section, section, StringComparison.OrdinalIgnoreCase))
            {
                if (sectionStart == -1) sectionStart = i;
                lastInSection = i;
            }
        }

        var newLine = new Line { Raw = key + "=" + value, Section = section, Key = key };
        if (lastInSection >= 0)
            _lines.Insert(lastInSection + 1, newLine);
        else
        {
            // La sección no existe: crearla al final.
            _lines.Add(new Line { Raw = "[" + section + "]", Section = section, Key = null });
            _lines.Add(newLine);
        }
    }

    public void Save(string path)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var l in _lines) sb.Append(l.Raw).Append("\r\n");
        File.WriteAllBytes(path, Cp1252.GetBytes(sb.ToString()));
    }
}
