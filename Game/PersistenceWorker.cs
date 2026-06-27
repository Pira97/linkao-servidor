using System.Collections.Concurrent;

namespace ServidorCS.Game;

/// <summary>
/// Único hilo de persistencia en segundo plano (ver [[b3_autosave_sin_lock]]). El tick periódico
/// de autosave/backup (GameServer.cs) captura un snapshot inmutable de los usuarios online DENTRO
/// del GameLock (rápido: solo copia de memoria, sin I/O) y lo encola acá; este worker hace el I/O
/// de disco (leer/escribir .chr, escribir JSON de progreso, copiar backups) en su propio hilo,
/// SIN el GameLock tomado, así que nunca bloquea la simulación del mundo ni los handlers de
/// paquetes entrantes.
///
/// Un único hilo consumidor procesa la cola en orden FIFO: dos jobs nunca corren en paralelo entre
/// sí (evita que autosave y backup se pisen). Además, EnqueueAutosave/EnqueueBackup descartan un
/// nuevo job del mismo tipo si el anterior todavía está en la cola o ejecutándose, para no
/// acumular trabajo atrasado si el disco está lento — se loguea y se lo toma en el próximo ciclo.
///
/// Los snapshots (CharSaveSnapshot, y los Dictionary&lt;string,string&gt; de JSON de BattlePass/
/// Achievements/QuestSystem) son inmutables y ya están desacoplados del User vivo, así que este
/// hilo nunca lee ni escribe ningún estado mutable del mundo: no hace falta ningún lock aparte del
/// per-archivo que ya toma CharSaver.ApplyAndSave (protege contra un logout guardando el MISMO
/// personaje al mismo tiempo que un job de este worker).
/// </summary>
public static class PersistenceWorker
{
    private abstract class Job { }

    private sealed class AutosaveJob : Job
    {
        public required List<CharSaveSnapshot> Chars;
    }

    /// <summary>Guardado puntual de un solo personaje (fix H4), fuera del ciclo de autosave
    /// periódico — no toca _autosavePending, así que nunca interfiere con ese guard.</summary>
    private sealed class SingleJob : Job
    {
        public required CharSaveSnapshot Snap;
    }

    private sealed class BackupJob : Job
    {
        public required List<CharSaveSnapshot> Chars;
        public required Dictionary<string, string> BattlePassJson;
        public required Dictionary<string, string> AchievementsJson;
        public required Dictionary<string, string> QuestJson;
    }

    private static readonly BlockingCollection<Job> _queue = new(new ConcurrentQueue<Job>());
    private static int _autosavePending; // 0 = libre, 1 = encolado o ejecutándose
    private static int _backupPending;

    // El hilo arranca al primer uso de la clase (arranque del server, cuando GameServer llama a
    // EnqueueAutosave/EnqueueBackup por primera vez). IsBackground=true: no impide que el proceso
    // cierre si por algún motivo queda un job pendiente sin drenar.
    private static readonly Thread _thread = StartThread();

    private static Thread StartThread()
    {
        var t = new Thread(RunLoop) { IsBackground = true, Name = "PersistenceWorker" };
        t.Start();
        return t;
    }

    /// <summary>Encola un autosave. Se salta (con log) si el autosave anterior todavía no terminó
    /// de procesarse — evita acumular jobs si el disco está lento.</summary>
    public static void EnqueueAutosave(List<CharSaveSnapshot> chars)
    {
        _ = _thread; // fuerza la inicialización estática (arranca el hilo) si todavía no ocurrió
        if (Interlocked.CompareExchange(ref _autosavePending, 1, 0) != 0)
        {
            Console.WriteLine("[PersistenceWorker] Autosave anterior aún en cola/ejecución: se salta este ciclo.");
            return;
        }
        _queue.Add(new AutosaveJob { Chars = chars });
    }

    /// <summary>
    /// Fix H4 (auditoría DDoS 24-ago-2026): encola el guardado de UN solo personaje fuera del
    /// ciclo periódico de autosave (p.ej. HandlePetElegir, que antes llamaba a
    /// CharSaver.SaveUser directo y SINCRÓNICO dentro del handler, con GameLock tomado —
    /// convertía cualquier acción que grabara así en I/O de disco bloqueando el mundo). Usa el
    /// mismo hilo/cola FIFO que el autosave, así que nunca corre en paralelo con otro job de
    /// persistencia del mismo o de otro personaje. No usa el flag _autosavePending (ese es sólo
    /// para no duplicar el job periódico global): un guardado puntual siempre se encola.
    /// </summary>
    public static void EnqueueSingle(CharSaveSnapshot snap)
    {
        _ = _thread;
        _queue.Add(new SingleJob { Snap = snap });
    }

    /// <summary>Encola un backup. Se salta (con log) si el backup anterior todavía no terminó.</summary>
    public static void EnqueueBackup(List<CharSaveSnapshot> chars, Dictionary<string, string> battlePassJson,
        Dictionary<string, string> achievementsJson, Dictionary<string, string> questJson)
    {
        _ = _thread;
        if (Interlocked.CompareExchange(ref _backupPending, 1, 0) != 0)
        {
            Console.WriteLine("[PersistenceWorker] Backup anterior aún en cola/ejecución: se salta este ciclo.");
            return;
        }
        _queue.Add(new BackupJob
        {
            Chars = chars,
            BattlePassJson = battlePassJson,
            AchievementsJson = achievementsJson,
            QuestJson = questJson,
        });
    }

    /// <summary>Espera (con timeout) a que se vacíe la cola de persistencia. Se usa en el cierre
    /// del servidor ANTES del guardado final síncrono (Program.cs), para que un job todavía en
    /// vuelo no termine de escribir un .chr justo mientras el guardado final también lo escribe
    /// (ambos toman el mismo file-lock de CharSaver, así que no se corrompería, pero podría hacer
    /// que el guardado final —con los datos más frescos— quede pisado por un snapshot viejo si
    /// terminara después). No es estrictamente necesario para la corrección, pero evita el orden
    /// de escritura ambiguo en el peor caso.</summary>
    public static bool DrainAndWait(TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _autosavePending) != 0 || Volatile.Read(ref _backupPending) != 0)
        {
            if (sw.Elapsed > timeout) return false;
            Thread.Sleep(20);
        }
        return true;
    }

    private static void RunLoop()
    {
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            try
            {
                switch (job)
                {
                    case AutosaveJob a: ProcessAutosave(a); break;
                    case BackupJob b: ProcessBackup(b); break;
                    case SingleJob s: CharSaver.ApplyAndSave(s.Snap); break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PersistenceWorker] Excepción procesando job: {ex}");
                // Blindaje: pase lo que pase, liberar los flags de "pending" para no dejar el
                // autosave/backup bloqueado para siempre por una excepción inesperada.
                Interlocked.Exchange(ref _autosavePending, 0);
                Interlocked.Exchange(ref _backupPending, 0);
            }
        }
    }

    private static void ProcessAutosave(AutosaveJob job)
    {
        int n = 0;
        foreach (var snap in job.Chars) { CharSaver.ApplyAndSave(snap); n++; }
        if (n > 0) Console.WriteLine($"[ServidorCS] Autosave: {n} personaje(s) guardado(s).");
        Interlocked.Exchange(ref _autosavePending, 0);
    }

    private static void ProcessBackup(BackupJob job)
    {
        foreach (var snap in job.Chars) CharSaver.ApplyAndSave(snap);
        foreach (var kv in job.BattlePassJson) BattlePass.WriteProgressJson(kv.Key, kv.Value);
        foreach (var kv in job.AchievementsJson) Achievements.WriteProgressJson(kv.Key, kv.Value);
        foreach (var kv in job.QuestJson) QuestSystem.WriteProgressJson(kv.Key, kv.Value);

        // Recién ahora los .chr/*.json están al día en disco: copiar el snapshot fechado.
        Backup.CopyAndCleanup();

        Interlocked.Exchange(ref _backupPending, 0);
    }
}
