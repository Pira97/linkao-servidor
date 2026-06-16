using ServidorCS.Network;
using System.Runtime.InteropServices;

// Punto de entrada del servidor migrado (reemplaza Sub Main de frmMain/General.bas).
// El puerto se lee de Server.ini (clave Puerto); si no existe usa 7666 (puerto clásico de AO).

// Resolución de timer de 1ms (winmm). Por defecto Windows usa ~15.6ms, lo que hace que
// Task.Delay(20) varíe entre 15 y 31ms (jitter). Ese jitter desincronizaba el envío de
// CharacterMove de los NPCs respecto a la animación del cliente (376ms/tile) → la cola de
// movimiento se vaciaba y la caminata se veía trabada. Con 1ms el loop es preciso.
if (OperatingSystem.IsWindows()) NativeTiming.TimeBeginPeriod(1);

int port = ServerConfig.ReadPort(defaultPort: 7666);
ServidorCS.Game.AdminLoader.Load();
ServidorCS.Game.MercadoPago.Init(); // donaciones: catálogo siempre; cobro/polling gateado por token
ServidorCS.Game.ReportManager.Load(); // sistema de reportes / tickets de soporte

var server = new GameServer(port);

Console.WriteLine("=== ServidorCS (migración VB6 -> C#) ===");
string dataRoot = ServidorCS.Game.DataPaths.Root;
Console.WriteLine(string.IsNullOrEmpty(dataRoot)
    ? "[ADVERTENCIA] No se encontró la carpeta 'Servidor' con los datos (Charfile/Cuentas/Maps/Dat)."
    : $"[ServidorCS] Datos en: {dataRoot}");
Console.WriteLine("Ctrl+C para detener.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("[ServidorCS] Apagado solicitado.");
}
finally
{
    // Guardado final: sin esto, los jugadores conectados al momento del cierre
    // perdían el progreso desde el último autosave (hasta 5 minutos).
    Console.WriteLine("[ServidorCS] Guardando personajes online...");
    ServidorCS.Game.CharSaver.SaveAllOnline();
    Console.WriteLine("[ServidorCS] Personajes guardados. Adiós.");

    if (OperatingSystem.IsWindows()) NativeTiming.TimeEndPeriod(1);
}

// P/Invoke a winmm.dll para subir la resolución del timer del sistema a 1ms.
static class NativeTiming
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint uMilliseconds);
}
