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

// Herramienta de dev del mundo único: ensambla la región y valida invariantes, luego sale.
// No arranca el servidor. Uso: dotnet run -- --regiontest
if (args.Length > 0 && args[0] == "--regiontest")
{
    ServidorCS.Game.RegionLoader.SelfTest();
    return;
}

// Tabla de tiempos de respawn por NPC según [RESPAWN] de Balance.dat. Para tunear los números
// sin levantar el servidor. Uso: dotnet run -- --respawntest
if (args.Length > 0 && args[0] == "--respawntest")
{
    ServidorCS.Game.NpcManager.RespawnSelfTest();
    return;
}

// Verificación manual de los FIX 1-4 de IA de NPCs (guardias+UsersByMapIndex, cache de pathfinding,
// timers de golpe/hechizo independientes, reacción inmediata a atacante nuevo). Uso: dotnet run -- --fixtest
if (args.Length > 0 && args[0] == "--fixtest")
{
    int fallos = ServidorCS.Game.NpcManager.FixesSelfTest();
    Environment.Exit(fallos == 0 ? 0 : 1);
}

// Benchmark real (Stopwatch) de los FIX 1 y 2: viejo algoritmo vs nuevo, mismo proceso, mismos
// datos sintéticos. Uso: dotnet run -- --benchtest
if (args.Length > 0 && args[0] == "--benchtest")
{
    ServidorCS.Game.NpcManager.FixesBenchmark();
    return;
}

// Qué efecto le da el server a cada consumible de obj.dat (y cuáles quedarían sin efecto,
// que es el bug de "la poción se usa, no hace nada y no se descuenta"). Uso:
// dotnet run -- --pociontest
if (args.Length > 0 && args[0] == "--pociontest")
{
    ServidorCS.Game.ObjData.ConsumiblesSelfTest();
    return;
}

int port = ServerConfig.ReadPort(defaultPort: 7666);

// Versión de cliente exigida: se trae de GitHub ACÁ, antes de escuchar. Si se dejaba para el
// primer login, esa llamada HTTP salía con el GameLock tomado (server congelado hasta 5s) y,
// si fallaba, el número viejo de Server.ini rechazaba a todos por "cliente desactualizado".
ServerConfig.PrecargarVersion();

// Auto-curado del puerto: el launcher de la VM relanza el exe apenas se cae, pero a veces
// la instancia anterior (u otro server viejo) sigue escuchando el puerto y la nueva moría
// con SocketException 10048 ("Only one usage of each socket address"), entrando en un loop
// de reinicios. Resultado visible: el server "no actualizaba" NPCs/objetos porque la copia
// vieja seguía viva. Acá nos aseguramos de ser el único: matamos instancias previas y
// liberamos el puerto antes de escuchar.
PortGuard.EnsurePortFree(port);

ServidorCS.Game.AdminLoader.Load();
ServidorCS.Game.Espia.CargarToken(); // secreto para espectar desde el panel de deploy sin login
ServidorCS.Game.MercadoPago.Init(); // donaciones: catálogo siempre; cobro/polling gateado por token
ServidorCS.Game.PremiumParticles.Init(); // catálogo de partículas premium de meditación (Server.ini [ParticulasPremium])
ServidorCS.Game.CreditItems.Init(); // catálogo de cosméticos comprados con créditos (Server.ini [TiendaCreditosItems])
ServidorCS.Game.ReportManager.Load(); // sistema de reportes / tickets de soporte
ServidorCS.Game.BattlePass.Load(); // pase de temporada (battle pass): temporada + tabla de recompensas
ServidorCS.Game.Achievements.Load(); // sistema de logros (Dat/Logros.ini)
ServidorCS.Game.QuestSystem.Load(); // sistema de misiones (Dat/Quests.dat)
ServidorCS.Game.QuestSystem.SpawnNpcs(); // NPCs dadores dedicados ([NPCSPAWNS] de Quests.dat)
ServidorCS.Game.AmigoRequestStore.Load(); // solicitudes de amistad pendientes (entrega offline)
// DESHABILITADO temporalmente (a pedido): descomentar esta línea para volver a poblar los
// dungeons con los guardianes de facción permanentes.
// ServidorCS.Game.DungeonBots.Init();

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
    // Si quedó un autosave/backup en vuelo en el worker de persistencia, esperarlo antes del
    // guardado final: evita que un snapshot viejo, terminando de escribir en su propio hilo,
    // pise por accidente los datos más frescos que el guardado final está por escribir.
    if (!ServidorCS.Game.PersistenceWorker.DrainAndWait(TimeSpan.FromSeconds(5)))
        Console.WriteLine("[ServidorCS] Aviso: el worker de persistencia no terminó a tiempo, se guarda igual.");

    // Guardado final: sin esto, los jugadores conectados al momento del cierre
    // perdían el progreso desde el último autosave (hasta 5 minutos).
    Console.WriteLine("[ServidorCS] Guardando personajes online...");
    ServidorCS.Game.CharSaver.SaveAllOnline();
    int bp = ServidorCS.Game.BattlePass.SaveAll();
    ServidorCS.Game.Achievements.SaveAll();
    ServidorCS.Game.QuestSystem.SaveAll();
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
