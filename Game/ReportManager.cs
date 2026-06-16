using System.Text.Json;
using System.Text.Json.Serialization;
using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sistema de reportes / tickets de soporte (NUEVO, no portado del VB6).
/// Los jugadores abren tickets (Bug / Consulta / Denuncia / Sugerencia / Otro) y los
/// Game Masters y Dioses los gestionan desde un panel: tomar, responder, resolver,
/// rechazar, reabrir, borrar y teletransportarse al reportante.
///
/// Persistencia: JSON en &lt;ServerRoot&gt;/Reports/reports.json (lista + contador de id).
/// El acceso está serializado con un lock para soportar el loop del servidor.
/// </summary>
public static class ReportManager
{
    // ---- Categorías (deben coincidir con el cliente Godot) ----
    public const byte CAT_BUG        = 0;
    public const byte CAT_CONSULTA   = 1;
    public const byte CAT_DENUNCIA   = 2;
    public const byte CAT_SUGERENCIA = 3;
    public const byte CAT_OTRO       = 4;

    // ---- Estados ----
    public const byte ST_ABIERTO   = 0;
    public const byte ST_EN_PROCESO = 1;
    public const byte ST_RESUELTO  = 2;
    public const byte ST_RECHAZADO = 3;

    // ---- Acciones de GM (ReportAction) ----
    public const byte ACT_TAKE    = 1; // asignarse el ticket
    public const byte ACT_REPLY   = 2; // agregar respuesta al hilo
    public const byte ACT_RESOLVE = 3; // marcar resuelto
    public const byte ACT_REJECT  = 4; // marcar rechazado
    public const byte ACT_REOPEN  = 5; // reabrir
    public const byte ACT_DELETE  = 6; // borrar (solo Dios+)
    public const byte ACT_GOTO    = 7; // teletransportar al GM con el reportante

    // ---- Filtros de listado ----
    public const byte FILTER_ALL      = 0;
    public const byte FILTER_OPEN     = 1; // abierto + en proceso
    public const byte FILTER_MINE     = 2; // asignados al GM que pide
    public const byte FILTER_RESOLVED = 3; // resuelto + rechazado

    private const int MAX_OPEN_PER_USER = 5;     // anti-spam: tickets abiertos simultáneos por cuenta
    private const int SUBJECT_MAX = 60;
    private const int BODY_MAX = 1000;
    private const int REPLY_MAX = 1000;

    public class Reply
    {
        public string Author { get; set; } = "";
        public string Date { get; set; } = "";
        public string Text { get; set; } = "";
        public bool IsGm { get; set; }
    }

    public class Report
    {
        public int Id { get; set; }
        public string Account { get; set; } = "";
        public string Reporter { get; set; } = "";
        public byte Category { get; set; }
        public byte Status { get; set; }
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string AssignedGm { get; set; } = "";
        public int Map { get; set; }
        public byte X { get; set; }
        public byte Y { get; set; }
        public List<Reply> Replies { get; set; } = new();
    }

    private class Store
    {
        public int NextId { get; set; } = 1;
        public List<Report> Reports { get; set; } = new();
    }

    private static readonly object _lock = new();
    private static Store _store;
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Dir => string.IsNullOrEmpty(DataPaths.Root)
        ? "Reports" + Path.DirectorySeparatorChar
        : DataPaths.Sub("Reports");
    private static string FilePath => Path.Combine(Dir, "reports.json");

    // ============================================================
    //  Carga / guardado
    // ============================================================
    public static void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string raw = File.ReadAllText(FilePath);
                    _store = JsonSerializer.Deserialize<Store>(raw) ?? new Store();
                }
                else _store = new Store();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ReportManager] Error al cargar: {ex.Message}. Se empieza vacío.");
                _store = new Store();
            }
            Console.WriteLine($"[ReportManager] {_store.Reports.Count} reportes cargados (NextId={_store.NextId}).");
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_store, _json));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ReportManager] Error al guardar: {ex.Message}");
        }
    }

    private static void EnsureLoaded()
    {
        if (_store == null) Load();
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm");

    // ============================================================
    //  Jugador: crear ticket
    // ============================================================
    public static void Create(int userIndex, byte category, string subject, string body)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged || u.Conn == null) return;

        subject = (subject ?? "").Trim();
        body = (body ?? "").Trim();

        if (category > CAT_OTRO) category = CAT_OTRO;
        if (subject.Length == 0 || body.Length == 0)
        {
            ServerPackets.ReportSubmitted(u.Conn, false, 0, "El asunto y la descripción no pueden estar vacíos.");
            return;
        }
        if (subject.Length > SUBJECT_MAX) subject = subject.Substring(0, SUBJECT_MAX);
        if (body.Length > BODY_MAX) body = body.Substring(0, BODY_MAX);

        Report rep;
        lock (_lock)
        {
            EnsureLoaded();
            int abiertos = _store.Reports.Count(r =>
                string.Equals(r.Account, u.Account, StringComparison.OrdinalIgnoreCase)
                && (r.Status == ST_ABIERTO || r.Status == ST_EN_PROCESO));
            if (abiertos >= MAX_OPEN_PER_USER)
            {
                ServerPackets.ReportSubmitted(u.Conn, false, 0,
                    $"Ya tienes {MAX_OPEN_PER_USER} reportes abiertos. Espera a que sean atendidos.");
                return;
            }

            rep = new Report
            {
                Id = _store.NextId++,
                Account = u.Account ?? "",
                Reporter = u.Name ?? "",
                Category = category,
                Status = ST_ABIERTO,
                Subject = subject,
                Body = body,
                CreatedAt = Now(),
                UpdatedAt = Now(),
                Map = u.Pos.Map,
                X = (byte)u.Pos.X,
                Y = (byte)u.Pos.Y,
            };
            _store.Reports.Add(rep);
            Save();
        }

        ServerPackets.ReportSubmitted(u.Conn, true, rep.Id,
            $"Reporte #{rep.Id} enviado. Un Game Master lo revisará pronto.");
        NotifyGms($"Nuevo reporte #{rep.Id} [{CatName(category)}] de {rep.Reporter}: {subject}");
    }

    // ============================================================
    //  Listado (GM ve global filtrado; jugador ve solo lo suyo)
    // ============================================================
    public static void SendList(int userIndex, byte filter)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged || u.Conn == null) return;
        bool esGm = u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO;

        List<Report> result;
        lock (_lock)
        {
            EnsureLoaded();
            IEnumerable<Report> q = _store.Reports;
            if (!esGm)
            {
                // Jugador: solo sus propios tickets, más recientes primero.
                q = q.Where(r => string.Equals(r.Account, u.Account, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                q = filter switch
                {
                    FILTER_OPEN     => q.Where(r => r.Status == ST_ABIERTO || r.Status == ST_EN_PROCESO),
                    FILTER_MINE     => q.Where(r => string.Equals(r.AssignedGm, u.Name, StringComparison.OrdinalIgnoreCase)),
                    FILTER_RESOLVED => q.Where(r => r.Status == ST_RESUELTO || r.Status == ST_RECHAZADO),
                    _ => q,
                };
            }
            result = q.OrderByDescending(r => r.Id).Take(200).ToList();
        }
        ServerPackets.ReportList(u.Conn, result);
    }

    // ============================================================
    //  Detalle de un ticket
    // ============================================================
    public static void SendDetail(int userIndex, int reportId)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged || u.Conn == null) return;

        Report rep;
        lock (_lock)
        {
            EnsureLoaded();
            rep = _store.Reports.FirstOrDefault(r => r.Id == reportId);
        }
        if (rep == null) return;

        bool esGm = u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO;
        bool esDueno = string.Equals(rep.Account, u.Account, StringComparison.OrdinalIgnoreCase);
        if (!esGm && !esDueno) return; // un jugador no puede ver tickets ajenos

        ServerPackets.ReportDetail(u.Conn, rep);
    }

    // ============================================================
    //  Acciones (GM gestiona; el dueño puede responder su propio ticket)
    // ============================================================
    public static void DoAction(int userIndex, int reportId, byte action, string message)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged || u.Conn == null) return;
        bool esGm = u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO;
        bool esDios = u.FaccionStatus >= AdminLoader.STATUS_DIOS;
        message = (message ?? "").Trim();

        Report rep;
        lock (_lock)
        {
            EnsureLoaded();
            rep = _store.Reports.FirstOrDefault(r => r.Id == reportId);
        }
        if (rep == null)
        {
            ServerPackets.ReportNotify(u.Conn, 0, "Ese reporte ya no existe.");
            return;
        }

        bool esDueno = string.Equals(rep.Account, u.Account, StringComparison.OrdinalIgnoreCase);

        // El dueño no-GM solo puede responder su propio ticket.
        if (!esGm)
        {
            if (action != ACT_REPLY || !esDueno) return;
        }

        lock (_lock)
        {
            switch (action)
            {
                case ACT_TAKE:
                    if (!esGm) return;
                    rep.AssignedGm = u.Name;
                    if (rep.Status == ST_ABIERTO) rep.Status = ST_EN_PROCESO;
                    Touch(rep);
                    ServerPackets.ReportNotify(u.Conn, 1, $"Tomaste el reporte #{rep.Id}.");
                    NotifyReporter(rep, $"Un Game Master ({u.Name}) está revisando tu reporte #{rep.Id}.");
                    break;

                case ACT_REPLY:
                    if (message.Length == 0) return;
                    if (message.Length > REPLY_MAX) message = message.Substring(0, REPLY_MAX);
                    rep.Replies.Add(new Reply { Author = u.Name, Date = Now(), Text = message, IsGm = esGm });
                    if (esGm && rep.Status == ST_ABIERTO) rep.Status = ST_EN_PROCESO;
                    Touch(rep);
                    if (esGm)
                    {
                        ServerPackets.ReportNotify(u.Conn, 1, $"Respuesta agregada al reporte #{rep.Id}.");
                        NotifyReporter(rep, $"Tu reporte #{rep.Id} tiene una nueva respuesta de un Game Master.");
                    }
                    else
                    {
                        ServerPackets.ReportNotify(u.Conn, 1, $"Respuesta agregada al reporte #{rep.Id}.");
                        NotifyGms($"El jugador {u.Name} respondió en el reporte #{rep.Id}.");
                    }
                    break;

                case ACT_RESOLVE:
                    if (!esGm) return;
                    rep.Status = ST_RESUELTO;
                    if (string.IsNullOrEmpty(rep.AssignedGm)) rep.AssignedGm = u.Name;
                    if (message.Length > 0)
                        rep.Replies.Add(new Reply { Author = u.Name, Date = Now(), Text = message, IsGm = true });
                    Touch(rep);
                    ServerPackets.ReportNotify(u.Conn, 1, $"Reporte #{rep.Id} marcado como RESUELTO.");
                    NotifyReporter(rep, $"Tu reporte #{rep.Id} fue marcado como RESUELTO por {u.Name}.");
                    break;

                case ACT_REJECT:
                    if (!esGm) return;
                    rep.Status = ST_RECHAZADO;
                    if (string.IsNullOrEmpty(rep.AssignedGm)) rep.AssignedGm = u.Name;
                    if (message.Length > 0)
                        rep.Replies.Add(new Reply { Author = u.Name, Date = Now(), Text = message, IsGm = true });
                    Touch(rep);
                    ServerPackets.ReportNotify(u.Conn, 1, $"Reporte #{rep.Id} marcado como RECHAZADO.");
                    NotifyReporter(rep, $"Tu reporte #{rep.Id} fue rechazado por {u.Name}.");
                    break;

                case ACT_REOPEN:
                    if (!esGm) return;
                    rep.Status = ST_EN_PROCESO;
                    Touch(rep);
                    ServerPackets.ReportNotify(u.Conn, 1, $"Reporte #{rep.Id} reabierto.");
                    break;

                case ACT_DELETE:
                    if (!esDios)
                    {
                        ServerPackets.ReportNotify(u.Conn, 0, "Solo los Dioses pueden borrar reportes.");
                        return;
                    }
                    _store.Reports.RemoveAll(r => r.Id == rep.Id);
                    Save();
                    ServerPackets.ReportNotify(u.Conn, 2, $"Reporte #{reportId} borrado.");
                    return; // no enviar detalle

                case ACT_GOTO:
                    if (!esGm) return;
                    GotoReporter(userIndex, u, rep);
                    return; // no reenviar detalle

                default:
                    return;
            }
            Save();
        }

        // Reenviar el detalle actualizado al que ejecutó la acción.
        ServerPackets.ReportDetail(u.Conn, rep);
    }

    private static void Touch(Report rep) => rep.UpdatedAt = Now();

    // ============================================================
    //  Teletransporte del GM a la posición del reportante (su última pos online,
    //  si está conectado; si no, a la posición guardada en el ticket).
    // ============================================================
    private static void GotoReporter(int gmIndex, User gm, Report rep)
    {
        int map = rep.Map; byte x = rep.X; byte y = rep.Y;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged
                && string.Equals(o.Name, rep.Reporter, StringComparison.OrdinalIgnoreCase))
            {
                map = o.Pos.Map; x = (byte)o.Pos.X; y = (byte)o.Pos.Y;
                break;
            }
        }
        if (map <= 0)
        {
            ServerPackets.ReportNotify(gm.Conn, 0, "No hay una ubicación registrada para ese reporte.");
            return;
        }
        Movement.WarpUser(gmIndex, (short)map, x, y);
        ServerPackets.ReportNotify(gm.Conn, 1, $"Teletransportado al reporte #{rep.Id} ({map}-{x}-{y}).");
    }

    // ============================================================
    //  Notificaciones
    // ============================================================
    private static void NotifyGms(string msg)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Conn != null
                && o.FaccionStatus >= AdminLoader.STATUS_CONSEJERO)
            {
                ServerPackets.ReportNotify(o.Conn, 1, msg);
            }
        }
    }

    private static void NotifyReporter(Report rep, string msg)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Conn != null
                && string.Equals(o.Account, rep.Account, StringComparison.OrdinalIgnoreCase))
            {
                ServerPackets.ReportNotify(o.Conn, 0, msg);
            }
        }
    }

    // ============================================================
    //  Helpers de nombres (para mensajes; el cliente igual traduce por índice)
    // ============================================================
    public static string CatName(byte c) => c switch
    {
        CAT_BUG        => "Bug",
        CAT_CONSULTA   => "Consulta",
        CAT_DENUNCIA   => "Denuncia",
        CAT_SUGERENCIA => "Sugerencia",
        _ => "Otro",
    };
}
