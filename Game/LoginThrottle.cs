using System.Collections.Concurrent;
using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Protección de fuerza bruta en el login (CONNECT_ACCOUNT). No existía ningún contador de
/// intentos fallidos: AntiDos sólo limita conexiones simultáneas/por segundo, no reintentos de
/// password dentro de la misma conexión ni entre reconexiones. Bloquea por CUENTA (protege a la
/// víctima aunque el atacante rote de IP) y por IP (protege contra probar muchas cuentas desde el
/// mismo origen), por separado, con backoff que escala x2 en cada bloqueo consecutivo.
/// </summary>
public static class LoginThrottle
{
    private sealed class Estado
    {
        public long VentanaInicio;
        public int Fallos;
        public long BloqueadoHasta;
        public long BloqueoActualMs;
    }

    private static readonly ConcurrentDictionary<string, Estado> _porCuenta = new();
    private static readonly ConcurrentDictionary<string, Estado> _porIp = new();

    /// <summary>true si NO está bloqueado (puede intentar). Si está bloqueado, informa el motivo.</summary>
    public static bool PuedeIntentar(string cuenta, string ip, out string motivo)
    {
        motivo = null;
        long ahora = Environment.TickCount64;

        if (Bloqueado(_porCuenta, cuenta, ahora, out long restanteCuenta))
        { motivo = $"Demasiados intentos fallidos. Probá de nuevo en {Segundos(restanteCuenta)}s."; return false; }

        if (Bloqueado(_porIp, ip, ahora, out long restanteIp))
        { motivo = $"Demasiados intentos fallidos desde tu conexión. Probá de nuevo en {Segundos(restanteIp)}s."; return false; }

        return true;
    }

    /// <summary>Registrar un intento fallido: puede iniciar o extender el bloqueo.</summary>
    public static void RegistrarFallo(string cuenta, string ip)
    {
        long ahora = Environment.TickCount64;
        RegistrarFallo(_porCuenta, cuenta, ahora);
        RegistrarFallo(_porIp, ip, ahora);
        SecurityLog.Log(SecuritySeverity.Warning, "login", "password incorrecta", ip, cuenta);
    }

    /// <summary>Login exitoso: limpia el historial de fallos (no el de bloqueo activo, si lo hubiera).</summary>
    public static void RegistrarExito(string cuenta, string ip)
    {
        _porCuenta.TryRemove(cuenta, out _);
        _porIp.TryRemove(ip, out _);
    }

    private static bool Bloqueado(ConcurrentDictionary<string, Estado> tabla, string clave, long ahora, out long restanteMs)
    {
        restanteMs = 0;
        if (string.IsNullOrEmpty(clave)) return false;
        if (!tabla.TryGetValue(clave, out var e)) return false;
        lock (e)
        {
            if (e.BloqueadoHasta > ahora) { restanteMs = e.BloqueadoHasta - ahora; return true; }
            return false;
        }
    }

    private static void RegistrarFallo(ConcurrentDictionary<string, Estado> tabla, string clave, long ahora)
    {
        if (string.IsNullOrEmpty(clave)) return;
        var e = tabla.GetOrAdd(clave, _ => new Estado());
        lock (e)
        {
            if (ahora - e.VentanaInicio > SecurityConfig.LoginVentanaMs) { e.VentanaInicio = ahora; e.Fallos = 0; }
            e.Fallos++;
            if (e.Fallos >= SecurityConfig.LoginMaxIntentosFallidos)
            {
                long baseMs = e.BloqueoActualMs > 0 ? Math.Min(e.BloqueoActualMs * 2, SecurityConfig.LoginBloqueoMaxMs) : SecurityConfig.LoginBloqueoBaseMs;
                e.BloqueoActualMs = baseMs;
                e.BloqueadoHasta = ahora + baseMs;
                e.Fallos = 0;
                e.VentanaInicio = ahora;
            }
        }
    }

    private static long Segundos(long ms) => Math.Max(1, (ms + 999) / 1000);
}
