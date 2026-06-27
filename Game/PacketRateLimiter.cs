using System.Collections.Concurrent;
using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Rate-limit genérico por (usuario, categoría de paquete) — ventana deslizante liviana, mismo
/// patrón que AntiCheat.VerificarLimitePaquetes pero reutilizable para cualquier tipo de acción
/// (movimiento, hechizos, comandos GM, etc.) sin duplicar la lógica en cada handler. NO reemplaza
/// AntiCheat (que además detecta el patrón de autoclicker en el uso de items) ni AntiDos (conexiones
/// por IP): esto cubre el hueco de "N paquetes de este TIPO por segundo", con un límite propio por
/// categoría porque el movimiento, el chat y los comandos GM tienen cadencias legítimas MUY distintas.
///
/// Ante abuso sostenido (muchas violaciones seguidas) devuelve también "Excesivo=true" para que el
/// caller pueda escalar a desconectar la conexión, no sólo descartar el paquete.
/// </summary>
public static class PacketRateLimiter
{
    public readonly struct Resultado
    {
        public readonly bool Permitido;
        public readonly bool Excesivo; // abuso sostenido: el caller puede optar por desconectar
        public Resultado(bool permitido, bool excesivo) { Permitido = permitido; Excesivo = excesivo; }
    }

    private sealed class Bucket
    {
        public long VentanaInicio;
        public int Contador;
        public int ViolacionesSeguidas;
    }

    /// <summary>Violaciones seguidas (ventanas consecutivas por encima del límite) antes de marcar "Excesivo".</summary>
    private const int VIOLACIONES_PARA_EXCESIVO = 8;

    private static readonly ConcurrentDictionary<(int userIndex, string categoria), Bucket> _buckets = new();

    /// <summary>true = dentro del límite. Los checks son O(1) sin asignar memoria en el camino feliz.</summary>
    public static Resultado Permitir(int userIndex, string categoria, int maxPorVentana, long ventanaMs, string ip = null, string cuenta = null)
    {
        var bucket = _buckets.GetOrAdd((userIndex, categoria), _ => new Bucket());
        long ahora = Environment.TickCount64;
        bool permitido;
        bool excesivo = false;
        lock (bucket)
        {
            if (ahora - bucket.VentanaInicio > ventanaMs) { bucket.VentanaInicio = ahora; bucket.Contador = 0; }
            bucket.Contador++;
            permitido = bucket.Contador <= maxPorVentana;
            if (!permitido)
            {
                bucket.ViolacionesSeguidas++;
                if (bucket.ViolacionesSeguidas >= VIOLACIONES_PARA_EXCESIVO) excesivo = true;
            }
            else if (bucket.ViolacionesSeguidas > 0 && bucket.Contador == 1)
            {
                bucket.ViolacionesSeguidas = 0; // una ventana entera limpia: resetear el contador de abuso
            }
        }
        if (!permitido)
        {
            SecurityLog.Log(excesivo ? SecuritySeverity.Blocked : SecuritySeverity.Suspicious,
                $"rate-limit:{categoria}", $"userIndex={userIndex} superó {maxPorVentana}/{ventanaMs}ms", ip, cuenta);
            GlobalStats.PaqueteLimitado();
            if (excesivo) GlobalStats.ClienteDesconectadoPorAbuso();
        }
        return new Resultado(permitido, excesivo);
    }

    /// <summary>Limpiar el estado de un usuario al desconectarse (evita que el diccionario crezca sin límite).</summary>
    public static void Olvidar(int userIndex)
    {
        foreach (var key in _buckets.Keys)
            if (key.userIndex == userIndex) _buckets.TryRemove(key, out _);
    }
}
