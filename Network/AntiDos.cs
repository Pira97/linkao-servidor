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
    // SecurityIp.IntervaloEntreConexiones (VB6): mínimo entre dos conexiones nuevas de la misma IP.
    private const long IntervaloEntreConexionesMs = 3000;
    private static readonly ConcurrentDictionary<string, int> _conexiones = new();
    private static readonly ConcurrentDictionary<string, long> _ultimaConexion = new();
    private static readonly object _lock = new();

    /// <summary>
    /// Loopback = puente WebSocket (websockify) que reenvía a 127.0.0.1:7666. Todos los clientes web
    /// llegarían con la misma IP local, así que se eximen del límite por-IP: su control por IP real
    /// se hace en el firewall del puerto 7668. No se exime ninguna IP pública.
    /// </summary>
    private static bool EsConfiable(string ip) =>
        ip == "127.0.0.1" || ip == "::1" || ip == "::ffff:127.0.0.1";

    /// <summary>
    /// SecurityIp.IpSecurityAceptarNuevaConexion 1:1: rechaza si la misma IP intenta abrir una
    /// conexión nueva antes de IntervaloEntreConexionesMs desde la anterior ACEPTADA. Solo actualiza
    /// el timestamp cuando acepta (un rechazo no reinicia la ventana). Frena el abrir/cerrar rápido
    /// que evade el tope de concurrentes.
    /// </summary>
    public static bool PermiteNuevaConexion(string ip)
    {
        if (string.IsNullOrEmpty(ip) || EsConfiable(ip)) return true;
        long ahora = Environment.TickCount64;
        lock (_lock)
        {
            if (_ultimaConexion.TryGetValue(ip, out long ultima) &&
                ultima + IntervaloEntreConexionesMs > ahora)
            {
                return false;
            }
            _ultimaConexion[ip] = ahora;
            return true;
        }
    }

    /// <summary>
    /// Intenta reservar un cupo para 'ip'. true si se permitió (incrementa el contador);
    /// false si ya alcanzó MaximoConexionesPorIP (no incrementa).
    /// </summary>
    public static bool PuedeConectar(string ip)
    {
        if (string.IsNullOrEmpty(ip) || EsConfiable(ip)) return true;
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
