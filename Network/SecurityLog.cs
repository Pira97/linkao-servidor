using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ServidorCS.Network;

public enum SecuritySeverity { Info, Warning, Suspicious, Blocked, Critical }

/// <summary>
/// Log de seguridad agregado: evita que un atacante llene la consola/disco generando millones
/// de eventos (p.ej. reintentos de login o packets malformados a repetición). La MISMA clave
/// (categoría+detalle) sólo se escribe una vez por ventana de SecurityLogAgregacionMs; mientras
/// tanto sólo se incrementa un contador en memoria, y al primer log después de la ventana se
/// informa cuántas veces pasó ("(x37 en los últimos 5s)").
/// </summary>
public static class SecurityLog
{
    private sealed class Entry { public long UltimoLog; public int Contador; }

    private static readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>Evento agregado ya escrito (uno por línea de consola real, no por cada
    /// ocurrencia) — lo que expone el panel de monitoreo. Inmutable una vez creado.</summary>
    public readonly struct EventoReciente
    {
        public readonly DateTime Utc;
        public readonly SecuritySeverity Sev;
        public readonly string Categoria;
        public readonly string Detalle;
        public readonly string Ip;
        public readonly string Cuenta;
        public readonly int Repeticiones;

        public EventoReciente(DateTime utc, SecuritySeverity sev, string categoria, string detalle, string ip, string cuenta, int repeticiones)
        { Utc = utc; Sev = sev; Categoria = categoria; Detalle = detalle; Ip = ip; Cuenta = cuenta; Repeticiones = repeticiones; }
    }

    // Buffer circular en memoria (auditoría 24-ago-2026, monitor de seguridad del panel): sólo
    // guarda las líneas que YA pasaron el filtro de agregación de abajo, así que un atacante
    // mandando millones de paquetes sigue generando UNA entrada por ventana, no millones.
    // Tamaño fijo chico: es sólo "lo último que pasó", no una base de datos de auditoría.
    private const int MAX_RECIENTES = 200;
    private static readonly EventoReciente[] _recientes = new EventoReciente[MAX_RECIENTES];
    private static int _recientesIdx;
    private static int _recientesCount;
    private static readonly object _recientesLock = new();

    public static void Log(SecuritySeverity sev, string categoria, string detalle, string ip = null, string cuenta = null)
    {
        string key = $"{categoria}|{detalle}|{ip}|{cuenta}";
        long ahora = Environment.TickCount64;
        var entry = _entries.GetOrAdd(key, _ => new Entry { UltimoLog = long.MinValue });

        int contadorAlEscribir;
        bool debeEscribir;
        lock (entry)
        {
            entry.Contador++;
            debeEscribir = ahora - entry.UltimoLog >= SecurityConfig.SecurityLogAgregacionMs;
            if (debeEscribir)
            {
                contadorAlEscribir = entry.Contador;
                entry.Contador = 0;
                entry.UltimoLog = ahora;
            }
            else
            {
                contadorAlEscribir = 0;
            }
        }
        if (!debeEscribir) return;

        string repeticion = contadorAlEscribir > 1 ? $" (x{contadorAlEscribir} en los últimos {SecurityConfig.SecurityLogAgregacionMs / 1000}s)" : "";
        string quien = ip != null || cuenta != null ? $" ip={ip ?? "?"} cuenta={cuenta ?? "?"}" : "";
        Console.WriteLine($"[SECURITY:{sev.ToString().ToUpperInvariant()}] {categoria}: {detalle}{quien}{repeticion}");

        lock (_recientesLock)
        {
            _recientes[_recientesIdx] = new EventoReciente(DateTime.UtcNow, sev, categoria, detalle, ip, cuenta, contadorAlEscribir);
            _recientesIdx = (_recientesIdx + 1) % MAX_RECIENTES;
            if (_recientesCount < MAX_RECIENTES) _recientesCount++;
        }
    }

    /// <summary>Últimos eventos agregados, más nuevo primero. Para el monitor de seguridad del
    /// panel — de sólo lectura, no expone nada que Log() no haya expuesto ya en consola.</summary>
    public static List<EventoReciente> Recientes(int max = 30)
    {
        lock (_recientesLock)
        {
            var lista = new List<EventoReciente>(Math.Min(max, _recientesCount));
            for (int i = 0; i < _recientesCount && lista.Count < max; i++)
            {
                int idx = (_recientesIdx - 1 - i + MAX_RECIENTES * 2) % MAX_RECIENTES;
                lista.Add(_recientes[idx]);
            }
            return lista;
        }
    }
}
