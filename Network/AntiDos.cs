using System.Collections.Concurrent;

namespace ServidorCS.Network;

/// <summary>
/// Anti-DoS por IP (clsAntiDos.cls) 1:1: limita la cantidad de conexiones simultáneas desde una
/// misma IP (MaximoConexionesPorIP=5). PuedeConectar reserva un cupo; Liberar lo devuelve al cerrar.
/// Evita que un cliente sature el servidor abriendo cientos de sockets.
/// </summary>
public static class AntiDos
{
    private const int MaximoConexionesPorIP = 5;
    private static readonly ConcurrentDictionary<string, int> _conexiones = new();
    private static readonly object _lock = new();

    /// <summary>
    /// Intenta reservar un cupo para 'ip'. true si se permitió (incrementa el contador);
    /// false si ya alcanzó MaximoConexionesPorIP (no incrementa).
    /// </summary>
    public static bool PuedeConectar(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return true;
        lock (_lock)
        {
            _conexiones.TryGetValue(ip, out int n);
            if (n >= MaximoConexionesPorIP) return false;
            _conexiones[ip] = n + 1;
            return true;
        }
    }

    /// <summary>RestarConexion: libera un cupo de 'ip' al cerrarse la conexión.</summary>
    public static void Liberar(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return;
        lock (_lock)
        {
            if (!_conexiones.TryGetValue(ip, out int n)) return;
            if (n <= 1) _conexiones.TryRemove(ip, out _);
            else _conexiones[ip] = n - 1;
        }
    }
}
