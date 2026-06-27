using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ServidorCS.Network;

/// <summary>
/// Servidor TCP. Reemplaza el listener Winsock del VB6.
/// Acepta conexiones, asigna un UserIndex y arranca el bucle de recepción de cada una.
/// El parseo real de cada packet vive en PacketHandler (a portar desde Protocol.bas).
/// </summary>
public sealed class GameServer
{
    /// <summary>Tick (ms) en que arrancó el servidor (para UpTime). VB6: tInicioServer.</summary>
    public static readonly long StartTick = Environment.TickCount64;

    private static int _minuteCounter; // cuenta segundos para el tick de 1 minuto (Centinela)
    private const double AutosaveSeconds = 300.0;  // autosave cada 5 minutos
    private const double BackupSeconds = 1800.0;    // snapshot de backup cada 30 minutos

    private readonly int _port;
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<int, Connection> _connections = new();

    public GameServer(int port)
    {
        _port = port;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        // Permitir reusar la dirección: tras matar la instancia previa, su socket puede quedar
        // unos segundos en TIME_WAIT y el bind fallaría con 10048 sin esto.
        try { _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }

        // Reintentar el bind si el puerto todavía no terminó de liberarse (evita el loop de
        // reinicios del launcher por SocketException 10048).
        for (int intento = 1; ; intento++)
        {
            try { _listener.Start(); break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && intento <= 10)
            {
                Console.WriteLine($"[ServidorCS] Puerto {_port} ocupado, reintento {intento}/10 en 1s...");
                await Task.Delay(1000, ct);
            }
        }
        Console.WriteLine($"[ServidorCS] Escuchando en 0.0.0.0:{_port}");
        // Fecha de compilación del exe en ejecución: para verificar qué build está corriendo.
        try { Console.WriteLine($"[ServidorCS] Build del exe: {File.GetLastWriteTime(Environment.ProcessPath):yyyy-MM-dd HH:mm:ss}"); } catch { }

        // Flush periódico de las colas de salida (como el timer del server VB6).
        _ = Task.Run(() => FlushLoopAsync(ct), ct);

        while (!ct.IsCancellationRequested)
        {
            Socket socket = await _listener.AcceptSocketAsync(ct);

            // AntiDos: limitar conexiones simultáneas por IP (clsAntiDos, máx 5).
            string ip = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
            // Rate-limit por IP (SecurityIp, 1 conexión/3s): frena el abrir/cerrar rápido.
            if (!AntiDos.PermiteNuevaConexion(ip))
            {
                Console.WriteLine($"[AntiDos] Conexiones demasiado rápidas desde {ip}, rechazando.");
                GlobalStats.ConexionRechazada();
                socket.Close();
                continue;
            }
            if (!AntiDos.PuedeConectar(ip))
            {
                Console.WriteLine($"[AntiDos] Demasiadas conexiones desde {ip}, rechazando.");
                GlobalStats.ConexionRechazada();
                socket.Close();
                continue;
            }

            // Asignar un slot real del UserList (base-1, igual que VB6).
            int userIndex = Game.UserListManager.NextOpenUser();
            if (userIndex == 0)
            {
                Console.WriteLine("[ServidorCS] Servidor lleno, rechazando conexión");
                AntiDos.Liberar(ip); // devolver el cupo reservado
                GlobalStats.ConexionRechazada();
                socket.Close();
                continue;
            }
            GlobalStats.ConexionAceptada();
            var conn = new Connection(socket, userIndex);
            _connections[userIndex] = conn;
            var u = Game.UserListManager.UserList[userIndex];
            u.Conn = conn;
            u.ConnID = userIndex;
            u.ConnIDValida = true;
            u.ip = conn.RemoteEndPoint;
            if (userIndex > Game.UserListManager.LastUser) Game.UserListManager.LastUser = userIndex;
            Console.WriteLine($"[ServidorCS] Conexión #{userIndex} desde {conn.RemoteEndPoint}");

            _ = conn.ReceiveLoopAsync(
                onData: c => PacketHandler.HandleIncomingData(c),
                onClose: OnClose);
        }
    }

    private void OnClose(Connection conn)
    {
        _connections.TryRemove(conn.UserIndex, out _);
        GlobalStats.ConexionCerrada();
        AntiDos.Liberar(conn.RemoteIp); // liberar el cupo de la IP
        Game.PacketRateLimiter.Olvidar(conn.UserIndex); // limpiar sus buckets de rate-limit
        // Modo espía: sacar esta conexión del espejo, esté de un lado o del otro. Los
        // objetos Connection se RECICLAN, así que dejarla adentro haría que el espejo
        // apunte a un jugador nuevo que reusó el objeto.
        Game.Espia.Olvidar(conn);
        // CloseUser muta UserList/visibilidad de otros (CharacterRemove, AreaVisibility.OnUserLeave):
        // bajo GameLock para no pisarse con un handler o con el tick de IA corriendo en otro hilo.
        lock (Game.UserListManager.GameLock)
            Game.UserListManager.CloseUser(conn.UserIndex);
        conn.Close();
        Console.WriteLine($"[ServidorCS] Conexión #{conn.UserIndex} cerrada");
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        try
        {
            int tick = 0;
            double nextAutosave = Environment.TickCount64 / 1000.0 + AutosaveSeconds;
            double nextBackup = Environment.TickCount64 / 1000.0 + BackupSeconds;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // --- Lógica del mundo bajo GameLock: misma semántica monohilo que el VB6. Mientras
                    //     corre el tick, ningún handler de packets ni OnClose puede mutar el mundo. No se
                    //     puede mantener un lock a través de un await, así que el flush de red y el
                    //     Task.Delay quedan FUERA del lock.
                    var _swTick = System.Diagnostics.Stopwatch.StartNew();
                    lock (Game.UserListManager.GameLock)
                    {
                        // IA de NPCs en cada ciclo (~10ms). El ritmo real de cada NPC lo limita
                        // NextAiAt (~376ms); el muestreo fino (10ms) evita que el redondeo del ciclo
                        // empuje el intervalo muy por encima de los 376ms de animación del cliente.
                        Game.NpcManager.TickAI();

                        // Evento "El Barrido": criatura de movimiento rápido (su propio ritmo lo limita
                        // MOVE_INTERVAL_MS internamente; el muestreo fino de ~10ms le da fluidez).
                        Game.BarridoEvento.Tick();

                        // Veneno/incineración a ~500ms (50×10ms): el VB6 los aplica cada IntervaloVeneno=500ms (2Hz).
                        if (tick % 50 == 0) Game.GameTimer.TickEfectosDanio();

                        // Respawn de NPCs, efectos de estado y eventos ~1 vez por segundo (100×10ms).
                        if (++tick >= 100) { tick = 0; Game.NpcManager.TickRespawns(); Game.Combat.TickEstados(); Game.Events.Tick(); Game.GameTimer.Tick(); Game.Clima.Tick(); Game.DayNightCycle.Tick(); Game.InframundoEvento.Verificar(); Game.ArenaEvento.Procesar(); Game.TorneoEvento.Procesar(); Game.Subastas.CheckExpirations();
                            Game.Centinela.CallUserAttention();
                            if (++_minuteCounter >= 60) { _minuteCounter = 0; Game.Centinela.PasarMinuto(); Game.WorldCleanup.PasarMinuto(); Game.Jail.PurgarPenas(); } }

                        // Autosave / backup: se toma el snapshot (copia de memoria, sin I/O) bajo el
                        // lock para tener una foto consistente del mundo, igual que antes — pero el
                        // I/O de disco en sí (leer/escribir .chr, copiar backups) YA NO corre acá:
                        // se encola para el PersistenceWorker, que lo procesa en su propio hilo sin
                        // el GameLock tomado. Ver [[b3_autosave_sin_lock]] / Game/PersistenceWorker.cs.
                        double now = Environment.TickCount64 / 1000.0;
                        if (now >= nextAutosave)
                        {
                            nextAutosave = now + AutosaveSeconds;
                            Game.PersistenceWorker.EnqueueAutosave(Game.CharSaver.CaptureAllOnline());
                        }
                        if (now >= nextBackup)
                        {
                            nextBackup = now + BackupSeconds;
                            Game.PersistenceWorker.EnqueueBackup(
                                Game.CharSaver.CaptureAllOnline(),
                                Game.BattlePass.CaptureAllOnlineJson(),
                                Game.Achievements.CaptureAllOnlineJson(),
                                Game.QuestSystem.CaptureAllOnlineJson());
                        }
                    }
                    // Monitor de seguridad (auditoría 24-ago-2026): cuánto tardó este ciclo con
                    // GameLock tomado. Es la señal de "GameLoop latency" que expone /security —
                    // si esto crece de forma sostenida, algo (IA, un handler lento, contención)
                    // está demorando al mundo entero, sea por carga legítima o por abuso.
                    GlobalStats.RegistrarDuracionTick(_swTick.Elapsed.TotalMilliseconds);

                    // Flush DESPUÉS de generar los moves: así el CharacterMove se envía en el mismo
                    // ciclo en que se genera, no en el siguiente. Antes, hacer flush primero metía
                    // 0-10ms de latencia variable en la LLEGADA al cliente (jitter) aunque el move
                    // se generara a 375ms regular → micro-gaps irregulares en la caminata.
                    foreach (var conn in _connections.Values)
                        await conn.FlushAsync();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Blindaje: una excepción suelta en el tick NO debe matar el bucle. Antes la tarea
                    // moría callada (sólo capturaba OperationCanceledException) y el server quedaba
                    // "congelado" sin ningún error visible. Ahora se loguea y se sigue al ciclo siguiente.
                    Console.WriteLine($"[ServidorCS][FlushLoop] excepción en el tick (se ignora y continúa): {ex}");
                }

                await Task.Delay(10, ct); // ~100 flush/seg (muestreo fino para la IA de NPCs)
            }
        }
        catch (OperationCanceledException) { }
    }
}
