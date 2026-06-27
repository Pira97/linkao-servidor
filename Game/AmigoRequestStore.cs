using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ServidorCS.Game;

/// <summary>
/// (NUEVO, no VB6) Solicitudes de amistad pendientes persistidas a disco. Permite enviar una
/// solicitud aunque el destinatario esté OFFLINE; la recibe al conectarse (DeliverPendingAmigoRequest).
/// Un único solicitante pendiente por destinatario (igual que QuienAmigo en memoria).
/// Archivo "amigo_requests.dat" en Charfile: una línea por solicitud "DESTINATARIO=SOLICITANTE".
/// </summary>
public static class AmigoRequestStore
{
    private static readonly Dictionary<string, string> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    private static string FilePath => Path.Combine(CharLoader.CharPath, "amigo_requests.dat");

    public static void Load()
    {
        lock (_lock)
        {
            _pending.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string target = line.Substring(0, eq).Trim();
                    string requester = line.Substring(eq + 1).Trim();
                    if (target.Length > 0 && requester.Length > 0)
                        _pending[target] = requester;
                }
            }
            catch { /* archivo corrupto/ilegible: arrancar vacío */ }
        }
    }

    private static void Save()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var kv in _pending)
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch { /* disco lleno/permisos: no es fatal, queda en memoria */ }
    }

    /// <summary>Registra (o pisa) la solicitud pendiente de 'requester' hacia 'target'.</summary>
    public static void Set(string target, string requester)
    {
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(requester)) return;
        lock (_lock) { _pending[target] = requester; Save(); }
    }

    /// <summary>Nombre del solicitante pendiente para 'target' (null si no hay).</summary>
    public static string Get(string target)
    {
        if (string.IsNullOrEmpty(target)) return null;
        lock (_lock) return _pending.TryGetValue(target, out var r) ? r : null;
    }

    /// <summary>Borra la solicitud pendiente de 'target' (al aceptar/rechazar).</summary>
    public static void Clear(string target)
    {
        if (string.IsNullOrEmpty(target)) return;
        lock (_lock) { if (_pending.Remove(target)) Save(); }
    }
}
