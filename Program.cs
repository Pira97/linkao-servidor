using ServidorCS.Network;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Net.NetworkInformation;

// Punto de entrada del servidor migrado (reemplaza Sub Main de frmMain/General.bas).
// El puerto se lee de Server.ini (clave Puerto); si no existe usa 7666 (puerto clásico de AO).

// Resolución de timer de 1ms (winmm). Por defecto Windows usa ~15.6ms, lo que hace que
// Task.Delay(20) varíe entre 15 y 31ms (jitter). Ese jitter desincronizaba el envío de
// CharacterMove de los NPCs respecto a la animación del cliente (376ms/tile) → la cola de
// movimiento se vaciaba y la caminata se veía trabada. Con 1ms el loop es preciso.
if (OperatingSystem.IsWindows()) NativeTiming.TimeBeginPeriod(1);

int port = ServerConfig.ReadPort(defaultPort: 7666);

// Auto-curado del puerto: el launcher de la VM relanza el exe apenas se cae, pero a veces
// la instancia anterior (u otro server viejo) sigue escuchando el puerto y la nueva moría
// con SocketException 10048 ("Only one usage of each socket address"), entrando en un loop
// de reinicios. Resultado visible: el server "no actualizaba" NPCs/objetos porque la copia
// vieja seguía viva. Acá nos aseguramos de ser el único: matamos instancias previas y
// liberamos el puerto antes de escuchar.
PortGuard.EnsurePortFree(port);

ServidorCS.Game.AdminLoader.Load();
ServidorCS.Game.MercadoPago.Init(); // donaciones: catálogo siempre; cobro/polling gateado por token
ServidorCS.Game.ReportManager.Load(); // sistema de reportes / tickets de soporte
ServidorCS.Game.BattlePass.Load(); // pase de temporada (battle pass): temporada + tabla de recompensas

var server = new GameServer(port);

Console.WriteLine("=== ServidorCS (migración VB6 -> C#) ===");
string dataRoot = ServidorCS.Game.DataPaths.Root;
Console.WriteLine(string.IsNullOrEmpty(dataRoot)
    ? "[ADVERTENCIA] No se encontró la carpeta 'Servidor' con los datos (Charfile/Cuentas/Maps/Dat)."
    : $"[ServidorCS] Datos en: {dataRoot}");
Console.WriteLine("Ctrl+C para detener.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Endpoint de estado (HTTP) para el bot de Discord: publica online/jugadores/versión.
// Puerto separado del juego (StatusPort en Server.ini, default 7667).
int statusPort = ServerConfig.ReadInt("StatusPort", 7667);
ServidorCS.Network.StatusEndpoint.Start(statusPort, cts.Token);

// Actualizador de canales-cartel de Discord (renombra vía API REST con el token
// del bot, configurado en Server.ini). Si no hay DiscordToken, queda desactivado.
ServidorCS.Network.DiscordStatus.Start(cts.Token);

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
    int bp = ServidorCS.Game.BattlePass.SaveAll();
    Console.WriteLine($"[ServidorCS] Personajes guardados ({bp} pases de temporada). Adiós.");

    if (OperatingSystem.IsWindows()) NativeTiming.TimeEndPeriod(1);
}

// Garantiza que el puerto esté libre antes de arrancar, para que el launcher de la VM
// no quede en loop de reinicios por SocketException 10048.
static class PortGuard
{
    public static void EnsurePortFree(int port)
    {
        // 1) Cerrar OTRAS instancias de este mismo exe (no la actual).
        int selfPid = Environment.ProcessId;
        string selfName = Process.GetCurrentProcess().ProcessName;
        foreach (var p in Process.GetProcessesByName(selfName))
        {
            if (p.Id == selfPid) continue;
            try
            {
                p.Kill(true);
                p.WaitForExit(3000);
                Console.WriteLine($"[ServidorCS] Cerré una instancia previa (PID {p.Id}).");
            }
            catch { /* ya murió o sin permisos: seguimos */ }
        }

        // 2) Esperar a que el puerto quede libre. Si lo ocupa otro proceso, matarlo por PID.
        for (int intento = 0; intento < 20; intento++) // ~10s máx
        {
            if (!PuertoOcupado(port)) return;

            int pid = PidEscuchando(port);
            if (pid > 0 && pid != selfPid)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    Console.WriteLine($"[ServidorCS] Puerto {port} ocupado por {p.ProcessName} (PID {pid}); lo cierro.");
                    p.Kill(true);
                    p.WaitForExit(3000);
                }
                catch { /* puede haberse cerrado solo */ }
            }
            Thread.Sleep(500);
        }
        Console.WriteLine($"[ServidorCS] ADVERTENCIA: el puerto {port} sigue ocupado; intento escuchar igual.");
    }

    private static bool PuertoOcupado(int port)
    {
        try
        {
            foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if (ep.Port == port) return true;
        }
        catch { }
        return false;
    }

    // PID que está LISTENING en el puerto (vía netstat -ano). 0 si no se encuentra.
    private static int PidEscuchando(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            string salida = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);

            foreach (var linea in salida.Split('\n'))
            {
                if (!linea.Contains("LISTENING")) continue;
                var cols = linea.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 5) continue;          // Proto Local Foreign Estado PID
                if (!cols[1].EndsWith(":" + port)) continue; // dirección local termina en :puerto
                if (int.TryParse(cols[^1], out int pid)) return pid;
            }
        }
        catch { }
        return 0;
    }
}

// P/Invoke a winmm.dll para subir la resolución del timer del sistema a 1ms.
static class NativeTiming
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint uMilliseconds);
}
