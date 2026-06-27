using System.Threading;

namespace ServidorCS.Network;

/// <summary>
/// Contadores globales livianos para monitoreo/detección (auditoría DDoS 24-ago-2026). Todo
/// Interlocked, sin asignar memoria ni tocar disco por evento — el costo es un incremento
/// atómico. No reemplaza SecurityLog (que sigue siendo la fuente de detalle por evento
/// agregado): esto es sólo la foto agregada de "cuánto está pasando" para un panel/comando GM,
/// pensado para poder responder "¿un solo cliente puede parar el GameLoop?" con números, no
/// con logs sueltos. No bloquea nada por sí mismo — es puramente informativo.
/// </summary>
public static class GlobalStats
{
    private static long _paquetesProcesados;
    private static long _bytesEntrantes;
    private static int _conexionesActivas;
    private static long _conexionesNuevas;
    private static long _conexionesRechazadas;
    private static long _paquetesLimitados;
    private static long _clientesDesconectadosPorAbuso;

    // --- Latencia del tick del GameLoop (auditoría 24-ago-2026, monitor de seguridad) ---
    // Cuánto tardó el último ciclo de FlushLoopAsync con GameLock tomado (IA + eventos +
    // snapshot de autosave, ver GameServer.cs). Es la señal más directa de "¿el juego se está
    // trabando?": si esto crece, alguna conexión (o el propio tick) está tardando de más.
    private static long _ultimoTickMsX10; // x10 para guardar un decimal sin usar double en Interlocked
    private static long _maxTickMsVentanaX10;
    private static long _ventanaTickInicio;
    private const long VENTANA_TICK_MS = 60_000; // se resetea el máximo cada minuto

    public static void RegistrarDuracionTick(double ms)
    {
        long ms10 = (long)Math.Round(ms * 10);
        Interlocked.Exchange(ref _ultimoTickMsX10, ms10);

        long ahora = Environment.TickCount64;
        if (ahora - Volatile.Read(ref _ventanaTickInicio) > VENTANA_TICK_MS)
        {
            Volatile.Write(ref _ventanaTickInicio, ahora);
            Interlocked.Exchange(ref _maxTickMsVentanaX10, ms10);
        }
        else
        {
            long actual;
            do { actual = Volatile.Read(ref _maxTickMsVentanaX10); }
            while (ms10 > actual && Interlocked.CompareExchange(ref _maxTickMsVentanaX10, ms10, actual) != actual);
        }
    }

    public static void PaqueteProcesado() => Interlocked.Increment(ref _paquetesProcesados);
    public static void BytesEntrantes(int n) => Interlocked.Add(ref _bytesEntrantes, n);
    public static void ConexionAceptada()
    {
        Interlocked.Increment(ref _conexionesActivas);
        Interlocked.Increment(ref _conexionesNuevas);
    }
    public static void ConexionCerrada() => Interlocked.Decrement(ref _conexionesActivas);
    public static void ConexionRechazada() => Interlocked.Increment(ref _conexionesRechazadas);
    public static void PaqueteLimitado() => Interlocked.Increment(ref _paquetesLimitados);
    public static void ClienteDesconectadoPorAbuso() => Interlocked.Increment(ref _clientesDesconectadosPorAbuso);

    public readonly struct Foto
    {
        public readonly long PaquetesProcesados;
        public readonly long BytesEntrantes;
        public readonly int ConexionesActivas;
        public readonly long ConexionesNuevas;
        public readonly long ConexionesRechazadas;
        public readonly long PaquetesLimitados;
        public readonly long ClientesDesconectadosPorAbuso;
        public readonly double UltimoTickMs;
        public readonly double MaxTickMsUltimoMinuto;

        public Foto(long paquetesProcesados, long bytesEntrantes, int conexionesActivas,
            long conexionesNuevas, long conexionesRechazadas, long paquetesLimitados, long clientesDesconectadosPorAbuso,
            double ultimoTickMs, double maxTickMsUltimoMinuto)
        {
            PaquetesProcesados = paquetesProcesados;
            BytesEntrantes = bytesEntrantes;
            ConexionesActivas = conexionesActivas;
            ConexionesNuevas = conexionesNuevas;
            ConexionesRechazadas = conexionesRechazadas;
            PaquetesLimitados = paquetesLimitados;
            ClientesDesconectadosPorAbuso = clientesDesconectadosPorAbuso;
            UltimoTickMs = ultimoTickMs;
            MaxTickMsUltimoMinuto = maxTickMsUltimoMinuto;
        }
    }

    /// <summary>Foto instantánea de todos los contadores (no resetea nada).</summary>
    public static Foto Snapshot() => new(
        Interlocked.Read(ref _paquetesProcesados),
        Interlocked.Read(ref _bytesEntrantes),
        Volatile.Read(ref _conexionesActivas),
        Interlocked.Read(ref _conexionesNuevas),
        Interlocked.Read(ref _conexionesRechazadas),
        Interlocked.Read(ref _paquetesLimitados),
        Interlocked.Read(ref _clientesDesconectadosPorAbuso),
        Interlocked.Read(ref _ultimoTickMsX10) / 10.0,
        Interlocked.Read(ref _maxTickMsVentanaX10) / 10.0);
}
