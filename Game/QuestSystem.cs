using System.Text.Json;
using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sistema de Misiones (NUEVO, no portado del VB6). Sigue el mismo patrón que Achievements:
/// definiciones en &lt;Dat&gt;/Quests.dat, progreso por personaje en &lt;ServerRoot&gt;/Quests/&lt;NOMBRE&gt;.json.
///
/// Objetivos soportados: Matar (NpcIndex de NPCs.dat) y Juntar (ObjIndex de obj.dat; se
/// valida contra el inventario y se descuenta al entregar). Recompensas: EXP:n  ORO:n  OBJ:idx:cant.
///
/// Interacción: doble click sobre el NPC dador abre la ventana de misiones (QuestInfo origen=1).
/// Si el NPC comercia, la ventana solo se abre cuando hay algo accionable (aceptar/entregar);
/// si no, el doble click sigue abriendo el comercio como siempre. El log del jugador se pide
/// con QuestLogRequest o el comando /misiones (QuestInfo origen=0).
/// </summary>
public static class QuestSystem
{
    // Estados que ve el cliente (Byte estado en QuestInfo)
    public const byte EST_DISPONIBLE = 0, EST_EN_CURSO = 1, EST_LISTA = 2, EST_COMPLETADA = 3,
        EST_SIN_NIVEL = 4, EST_BLOQUEADA = 5, EST_COOLDOWN = 6;

    private const int MAX_ACTIVAS = 10;

    public sealed class Objetivo
    {
        public bool EsJuntar;   // false = matar NpcIndex, true = juntar ObjIndex
        public int Target;      // ObjIndex (juntar) o NpcIndex principal (matar: el primero de la lista)
        public int Cantidad;

        // Matar: el mundo tiene bichos duplicados con distinto NpcIndex (ej. "Gallo Salvaje" 506
        // y "Gallina Salvaje" 594, "Murciélago" 500/566/610/615). MatarN acepta lista de índices
        // ("506,594-8") y además cuenta cualquier NPC cuyo nombre coincida exacto con el de un target.
        public HashSet<int> TargetIds = new();
        public HashSet<string> TargetNombres = new(StringComparer.OrdinalIgnoreCase);

        // Zona de ESTE objetivo (DestinoN=mapa-x-y, alineado con MatarN). 0 = usar el
        // Destino general de la quest. Permite que la flecha avance al siguiente lote
        // cuando los objetivos están en mapas distintos.
        public int DestMap, DestX, DestY;

        public bool CuentaKill(int npcIndex, string npcName) =>
            TargetIds.Contains(npcIndex)
            || (!string.IsNullOrEmpty(npcName) && TargetNombres.Contains(npcName));
    }

    public sealed class Quest
    {
        public int Id;
        public string Nombre = "";
        public string Historia = "";
        public int NpcDador;
        public int NivelMin = 1;
        public int Requiere;        // id de quest previa (0 = ninguna)
        public bool Repetible;
        public int CooldownHoras;
        public List<Objetivo> Objetivos = new(); // matar primero, juntar después (orden del .dat)
        public List<string> Reward = new();
        public string RewardTexto = "";          // texto legible para el cliente

        // Zona objetivo (Destino=mapa-x-y): flecha del minimapa y marcador del mapamundi. 0 = sin destino.
        public int DestMap, DestX, DestY;
    }

    /// <summary>Progreso por personaje (persistido a JSON).</summary>
    public sealed class Progress
    {
        // questId -> contadores de caza alineados a los objetivos "matar" de la quest
        public Dictionary<int, List<int>> Activas { get; set; } = new();
        public List<int> Completadas { get; set; } = new();
        // questId -> última entrega (ticks UTC) para el cooldown de repetibles
        public Dictionary<int, long> UltimaEntrega { get; set; } = new();
    }

    private static readonly List<Quest> _quests = new();
    private static readonly Dictionary<int, List<Quest>> _porDador = new(); // NpcIndex -> quests
    private static readonly List<(int Npc, int Map, byte X, byte Y)> _spawns = new(); // [NPCSPAWNS]
    // [ENTRADAS]: dungeon -> (mapa de entrada en el mundo visible, x, y). El cliente usa la
    // entrada cuando el mapa del destino/dador no figura en el mapamundi.
    private static readonly Dictionary<int, (int Map, byte X, byte Y)> _entradas = new();
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private static string Dir => string.IsNullOrEmpty(DataPaths.Root)
        ? "Quests" + Path.DirectorySeparatorChar
        : DataPaths.Sub("Quests");

    /// <summary>Mueve el progreso de un personaje a otro nombre (poción de cambio de nombre).</summary>
    public static void RenombrarProgreso(string viejo, string nuevo)
    {
        try
        {
            string a = ProgressPath(viejo), b = ProgressPath(nuevo);
            if (File.Exists(a)) File.Move(a, b, true);
        }
        catch (Exception ex) { Console.WriteLine($"[RenombrarProgreso] {viejo}->{nuevo}: {ex.Message}"); }
    }

    private static string ProgressPath(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(Dir, name.ToUpperInvariant() + ".json");
    }

    // ============================================================
    //  Carga de Quests.dat (llamado en el arranque)
    // ============================================================
    public static void Load()
    {
        _quests.Clear();
        _porDador.Clear();
        string path = (string.IsNullOrEmpty(DataPaths.Root) ? "Dat" + Path.DirectorySeparatorChar : DataPaths.Sub("Dat")) + "Quests.dat";
        var ini = new IniFile(path);
        if (!ini.Loaded)
        {
            Console.WriteLine($"[Quests] No se encontró Quests.dat en {path}. Misiones deshabilitadas.");
            return;
        }

        for (int i = 1; i <= 500; i++)
        {
            string sec = "QUEST" + i;
            string nombre = ini.Get(sec, "Nombre");
            if (nombre.Length == 0) break; // corta al primer hueco

            var q = new Quest
            {
                Id = i,
                Nombre = nombre,
                Historia = ini.Get(sec, "Historia"),
                NpcDador = ini.GetInt(sec, "NpcDador"),
                NivelMin = Math.Max(1, ini.GetInt(sec, "NivelMin")),
                Requiere = ini.GetInt(sec, "Requiere"),
                Repetible = ini.GetInt(sec, "Repetible") == 1,
                CooldownHoras = ini.GetInt(sec, "CooldownHoras"),
            };

            // Destino=mapa-x-y (opcional): zona donde se cumple la quest.
            var dp = ini.Get(sec, "Destino").Split('-', StringSplitOptions.TrimEntries);
            if (dp.Length == 3 && int.TryParse(dp[0], out int dm) && int.TryParse(dp[1], out int dx) && int.TryParse(dp[2], out int dy) && dm > 0)
            { q.DestMap = dm; q.DestX = dx; q.DestY = dy; }

            int nMatar = ini.GetInt(sec, "NumMatar");
            for (int m = 1; m <= nMatar; m++)
                if (TryParseMatar(ini.Get(sec, "Matar" + m), out var ids, out int cant))
                {
                    var obj = new Objetivo { EsJuntar = false, Target = ids[0], Cantidad = cant };
                    foreach (int id in ids)
                    {
                        obj.TargetIds.Add(id);
                        string nom = NpcData.Get(id).Name;
                        if (!string.IsNullOrEmpty(nom)) obj.TargetNombres.Add(nom);
                    }
                    // DestinoN (opcional): zona propia de este lote de bichos.
                    var odp = ini.Get(sec, "Destino" + m).Split('-', StringSplitOptions.TrimEntries);
                    if (odp.Length == 3 && int.TryParse(odp[0], out int om) && int.TryParse(odp[1], out int ox) && int.TryParse(odp[2], out int oy) && om > 0)
                    { obj.DestMap = om; obj.DestX = ox; obj.DestY = oy; }
                    q.Objetivos.Add(obj);
                }

            int nJuntar = ini.GetInt(sec, "NumJuntar");
            for (int j = 1; j <= nJuntar; j++)
                if (TryParsePar(ini.Get(sec, "Juntar" + j), out int obj, out int cant))
                    q.Objetivos.Add(new Objetivo { EsJuntar = true, Target = obj, Cantidad = cant });

            foreach (var t in ini.Get(sec, "Reward").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                q.Reward.Add(t);
            q.RewardTexto = RewardLegible(q.Reward);

            if (q.NpcDador <= 0 || q.Objetivos.Count == 0)
            {
                Console.WriteLine($"[Quests] QUEST{i} inválida (sin dador u objetivos), salteada.");
                continue;
            }
            _quests.Add(q);
            if (!_porDador.TryGetValue(q.NpcDador, out var lista)) _porDador[q.NpcDador] = lista = new List<Quest>();
            lista.Add(q);
        }
        // [ENTRADAS]: En=dungeon-mapaEntrada-x-y. El cliente las usa cuando el mapa del
        // destino/dador no figura en el mapamundi: marca la entrada del dungeon.
        _entradas.Clear();
        int nEnt = ini.GetInt("ENTRADAS", "NumEntradas");
        for (int e = 1; e <= nEnt; e++)
        {
            var ep = ini.Get("ENTRADAS", "E" + e).Split('-', StringSplitOptions.TrimEntries);
            if (ep.Length == 4 && int.TryParse(ep[0], out int dung) && int.TryParse(ep[1], out int em)
                && byte.TryParse(ep[2], out byte ex) && byte.TryParse(ep[3], out byte ey))
                _entradas[dung] = (em, ex, ey);
            else
                Console.WriteLine($"[Quests] E{e} inválida en [ENTRADAS], salteada.");
        }

        // [NPCSPAWNS]: dadores dedicados a spawnear al boot (SpawnN=npcIndex-mapa-x-y).
        _spawns.Clear();
        int nSpawns = ini.GetInt("NPCSPAWNS", "NumSpawns");
        for (int s = 1; s <= nSpawns; s++)
        {
            var p = ini.Get("NPCSPAWNS", "Spawn" + s).Split('-', StringSplitOptions.TrimEntries);
            if (p.Length == 4 && int.TryParse(p[0], out int npc) && int.TryParse(p[1], out int mapa)
                && byte.TryParse(p[2], out byte x) && byte.TryParse(p[3], out byte y))
                _spawns.Add((npc, mapa, x, y));
            else
                Console.WriteLine($"[Quests] Spawn{s} inválido en [NPCSPAWNS], salteado.");
        }

        Console.WriteLine($"[Quests] {_quests.Count} misiones cargadas ({_porDador.Count} NPCs dadores, {_spawns.Count} spawns).");
    }

    /// <summary>Spawnea los NPCs dadores de [NPCSPAWNS]. Llamar al boot, después de Load().
    /// GetMapNpcs primero: fuerza la instanciación normal del mapa (SpawnAt sobre un mapa
    /// no instanciado crearía la lista vacía y los NPCs propios del .map no aparecerían).</summary>
    public static void SpawnNpcs()
    {
        int n = 0;
        foreach (var s in _spawns)
        {
            var enMapa = NpcManager.GetMapNpcs(s.Map);
            bool ya = false;
            foreach (var e in enMapa)
                if (e.NpcIndex == s.Npc && e.SpawnX == s.X && e.SpawnY == s.Y) { ya = true; break; }
            if (ya) continue;
            if (NpcManager.SpawnAt(s.Map, s.Npc, s.X, s.Y) != null) n++;
            else Console.WriteLine($"[Quests] No se pudo spawnear el NPC {s.Npc} (¿no existe en NPCs.dat?).");
        }
        if (n > 0) Console.WriteLine($"[Quests] {n} NPCs dadores spawneados en el mundo.");
    }

    private static bool TryParsePar(string s, out int a, out int b)
    {
        a = 0; b = 0;
        var p = s.Split('-', StringSplitOptions.TrimEntries);
        return p.Length == 2 && int.TryParse(p[0], out a) && int.TryParse(p[1], out b) && a > 0 && b > 0;
    }

    /// <summary>MatarN admite varios índices que cuentan para el mismo objetivo: "506,594-8".</summary>
    private static bool TryParseMatar(string s, out List<int> ids, out int cant)
    {
        ids = new List<int>(); cant = 0;
        var p = s.Split('-', StringSplitOptions.TrimEntries);
        if (p.Length != 2 || !int.TryParse(p[1], out cant) || cant <= 0) return false;
        foreach (var t in p[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(t, out int id) && id > 0) ids.Add(id);
        return ids.Count > 0;
    }

    private static string RewardLegible(List<string> tokens)
    {
        var partes = new List<string>();
        foreach (var tok in tokens)
        {
            var p = tok.Split(':');
            switch (p[0].Trim().ToUpperInvariant())
            {
                case "EXP": if (p.Length >= 2) partes.Add($"{FormatoMiles(p[1])} exp"); break;
                case "ORO": if (p.Length >= 2) partes.Add($"{FormatoMiles(p[1])} oro"); break;
                case "OBJ":
                    if (p.Length >= 2 && short.TryParse(p[1], out short idx))
                    {
                        int cant = p.Length >= 3 && int.TryParse(p[2], out int c) ? c : 1;
                        string nom = ObjData.Get(idx).Name;
                        partes.Add(cant > 1 ? $"{nom} x{cant}" : nom);
                    }
                    break;
            }
        }
        return string.Join(" · ", partes);
    }

    private static string FormatoMiles(string n) =>
        long.TryParse(n.Trim(), out long v) ? v.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("es-AR")) : n;

    // ============================================================
    //  Persistencia (mismo esquema que Achievements)
    // ============================================================
    private static Progress LoadProgress(string name)
    {
        try
        {
            string p = ProgressPath(name);
            if (File.Exists(p))
                return JsonSerializer.Deserialize<Progress>(File.ReadAllText(p)) ?? new Progress();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Quests] Error al cargar progreso de {name}: {ex.Message}");
        }
        return new Progress();
    }

    private static void SaveProgress(User u)
    {
        if (u?.Quests == null || string.IsNullOrEmpty(u.Name)) return;
        WriteProgressJson(u.Name, JsonSerializer.Serialize(u.Quests, _json));
    }

    /// <summary>Escribe a disco un JSON de progreso ya serializado. No toca ningún User: seguro
    /// para llamar desde el worker de persistencia, fuera del GameLock.</summary>
    public static void WriteProgressJson(string name, string json)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ProgressPath(name), json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Quests] Error al guardar progreso de {name}: {ex.Message}");
        }
    }

    /// <summary>Guarda el progreso de todos los online. Se llama en el cierre del server, de forma
    /// síncrona (el game loop ya paró, no hay lock que liberar rápido).</summary>
    public static int SaveAll()
    {
        int n = 0;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Quests != null) { SaveProgress(u); n++; }
        }
        return n;
    }

    /// <summary>Serializa a JSON (en memoria) el progreso de todos los online, sin tocar disco.
    /// Pensado para llamarse bajo el GameLock desde el backup periódico; el I/O real se hace
    /// después, fuera del lock, con WriteProgressJson.</summary>
    public static Dictionary<string, string> CaptureAllOnlineJson()
    {
        var result = new Dictionary<string, string>();
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Quests != null && !string.IsNullOrEmpty(u.Name))
                result[u.Name] = JsonSerializer.Serialize(u.Quests, _json);
        }
        return result;
    }

    public static void OnLogin(int userIndex)
    {
        if (_quests.Count == 0) return;
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.Conn == null) return;
        u.Quests = LoadProgress(u.Name);
        // Log silencioso: el cliente pinta la flecha del minimapa y los marcadores
        // del mapamundi sin abrir la ventana.
        SendQuestLog(userIndex, abrirVentana: false);
    }

    // ============================================================
    //  Hooks de juego
    // ============================================================
    /// <summary>¿Este NPC da misiones? (para el ruteo del doble click en Accion).</summary>
    public static bool NpcHasQuests(int npcIndex) => _porDador.ContainsKey(npcIndex);

    /// <summary>¿El jugador tiene algo accionable con este NPC (aceptar una quest nueva o
    /// entregar una lista)? Si el NPC comercia y no hay nada accionable, el doble click
    /// debe seguir abriendo el comercio.</summary>
    public static bool TieneAccionable(int userIndex, int npcIndex)
    {
        var u = Get(userIndex);
        if (u == null || !_porDador.TryGetValue(npcIndex, out var lista)) return false;
        foreach (var q in lista)
        {
            byte est = Estado(u, q);
            if (est == EST_DISPONIBLE || est == EST_LISTA) return true;
        }
        return false;
    }

    /// <summary>Mató un NPC: avanza los contadores de caza de las misiones activas.
    /// El progreso se muestra como texto flotante dorado (mismo estilo que Logros).</summary>
    public static void OnNpcKilled(int userIndex, int npcIndex, string npcName)
    {
        var u = Get(userIndex);
        if (u == null || u.Quests.Activas.Count == 0) return;
        bool cambio = false;
        foreach (var (qid, kills) in u.Quests.Activas)
        {
            var q = GetQuest(qid);
            if (q == null) continue;

            bool estabaLista = MatanzaCompleta(q, kills);
            int slot = 0;
            for (int o = 0; o < q.Objetivos.Count; o++)
            {
                var obj = q.Objetivos[o];
                if (obj.EsJuntar) continue;
                if (obj.CuentaKill(npcIndex, npcName) && slot < kills.Count && kills[slot] < obj.Cantidad)
                {
                    kills[slot]++;
                    cambio = true;
                    if (u.Conn != null)
                        ServerPackets.ChatOverHead(u.Conn, $"{npcName}: {kills[slot]}/{obj.Cantidad}", u.Char.CharIndex, 7);
                }
                slot++;
            }

            // Recién completó toda la caza: avisar (los "juntar" se validan al entregar).
            if (cambio && !estabaLista && MatanzaCompleta(q, kills) && u.Conn != null)
                ServerPackets.ConsoleMsg(u.Conn, $"📜 Misión \"{q.Nombre}\" lista: volvé con {NombreDador(q)} para entregarla.", 58);
        }
        if (cambio) SaveProgress(u);
        // Log silencioso en cada kill que cuenta: refresca el tracker del HUD con el progreso
        // y, al completar un lote, la flecha y los marcadores pasan al siguiente objetivo.
        if (cambio) SendQuestLog(userIndex, abrirVentana: false);
    }

    private static bool MatanzaCompleta(Quest q, List<int> kills)
    {
        int slot = 0;
        foreach (var obj in q.Objetivos)
        {
            if (obj.EsJuntar) continue;
            if (slot >= kills.Count || kills[slot] < obj.Cantidad) return false;
            slot++;
        }
        return true;
    }

    // ============================================================
    //  Estado de una quest para un usuario
    // ============================================================
    private static byte Estado(User u, Quest q)
    {
        if (u.Quests.Activas.TryGetValue(q.Id, out var kills))
            return MatanzaCompleta(q, kills) && JuntadoCompleto(u, q) ? EST_LISTA : EST_EN_CURSO;
        if (!q.Repetible && u.Quests.Completadas.Contains(q.Id)) return EST_COMPLETADA;
        if (q.Repetible && u.Quests.UltimaEntrega.TryGetValue(q.Id, out long ticks))
        {
            var desde = new DateTime(ticks, DateTimeKind.Utc);
            if ((DateTime.UtcNow - desde).TotalHours < q.CooldownHoras) return EST_COOLDOWN;
        }
        if (u.Stats.ELV < q.NivelMin) return EST_SIN_NIVEL;
        if (q.Requiere > 0 && !u.Quests.Completadas.Contains(q.Requiere)) return EST_BLOQUEADA;
        return EST_DISPONIBLE;
    }

    private static bool JuntadoCompleto(User u, Quest q)
    {
        foreach (var obj in q.Objetivos)
            if (obj.EsJuntar && Inventory.ContarObjeto(u, obj.Target) < obj.Cantidad) return false;
        return true;
    }

    private static string EstadoExtra(User u, Quest q, byte estado)
    {
        switch (estado)
        {
            case EST_BLOQUEADA:
                var req = GetQuest(q.Requiere);
                return req != null ? $"Requiere completar: {req.Nombre}" : "";
            case EST_COOLDOWN:
                if (u.Quests.UltimaEntrega.TryGetValue(q.Id, out long ticks))
                {
                    var faltan = TimeSpan.FromHours(q.CooldownHoras) - (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc));
                    if (faltan.TotalMinutes > 0)
                        return faltan.TotalHours >= 1
                            ? $"Disponible en {(int)faltan.TotalHours} h {faltan.Minutes} min"
                            : $"Disponible en {Math.Max(1, faltan.Minutes)} min";
                }
                return "";
            case EST_SIN_NIVEL:
                return $"Necesitás nivel {q.NivelMin}";
            default:
                return "";
        }
    }

    // ============================================================
    //  Interacción: ventana del NPC y log del jugador
    // ============================================================
    /// <summary>Doble click sobre un NPC dador: manda sus misiones (QuestInfo origen=1).</summary>
    public static void SendNpcQuests(int userIndex, int npcIndex)
    {
        var u = Get(userIndex);
        if (u?.Conn == null || !_porDador.TryGetValue(npcIndex, out var lista)) return;
        string npcName = NpcData.Get(npcIndex).Name ?? "";
        ServerPackets.QuestInfo(u.Conn, 1, npcName, Entradas(u, lista), new List<ServerPackets.QuestGiver>());
    }

    /// <summary>Log de misiones del jugador. abrirVentana=true → origen 0 (el cliente abre la
    /// ventana); false → origen 2 (silencioso: solo actualiza marcadores de minimapa/mapamundi).
    /// Incluye el catálogo de dadores spawneados para los marcadores del mapamundi.</summary>
    public static void SendQuestLog(int userIndex, bool abrirVentana = true)
    {
        var u = Get(userIndex);
        if (u?.Conn == null) return;
        var lista = new List<Quest>();
        foreach (var qid in u.Quests.Activas.Keys)
        {
            var q = GetQuest(qid);
            if (q != null) lista.Add(q);
        }
        ServerPackets.QuestInfo(u.Conn, (byte)(abrirVentana ? 0 : 2), "", Entradas(u, lista), BuildGivers(u));
    }

    /// <summary>Dadores dedicados ([NPCSPAWNS]) con su posición y si tienen algo disponible
    /// para ESTE jugador (marcador dorado vs gris en el mapamundi).</summary>
    private static List<ServerPackets.QuestGiver> BuildGivers(User u)
    {
        var res = new List<ServerPackets.QuestGiver>();
        foreach (var s in _spawns)
        {
            if (!_porDador.TryGetValue(s.Npc, out var quests)) continue;
            bool disponible = false;
            foreach (var q in quests)
            {
                byte est = Estado(u, q);
                if (est == EST_DISPONIBLE || est == EST_LISTA) { disponible = true; break; }
            }
            var entG = EntranceFor(s.Map);
            res.Add(new ServerPackets.QuestGiver(s.Npc, NpcData.Get(s.Npc).Name ?? ("NPC " + s.Npc),
                s.Map, s.X, s.Y, disponible, entG.Map, entG.X, entG.Y));
        }
        return res;
    }

    /// <summary>Posición del dador si es spawneado ([NPCSPAWNS]); (0,0,0) si no.</summary>
    private static (int Map, byte X, byte Y) GiverPos(int npcIndex)
    {
        foreach (var s in _spawns)
            if (s.Npc == npcIndex) return (s.Map, s.X, s.Y);
        return (0, 0, 0);
    }

    /// <summary>Entrada desde el mundo visible al dungeon 'map' ([ENTRADAS]); (0,0,0) si no aplica.</summary>
    private static (int Map, byte X, byte Y) EntranceFor(int map)
        => map > 0 && _entradas.TryGetValue(map, out var e) ? e : (0, (byte)0, (byte)0);

    private static List<ServerPackets.QuestEntry> Entradas(User u, List<Quest> lista)
    {
        var res = new List<ServerPackets.QuestEntry>();
        foreach (var q in lista)
        {
            byte estado = Estado(u, q);
            string extra = estado == EST_DISPONIBLE || estado == EST_EN_CURSO || estado == EST_LISTA
                ? $"La da: {NombreDador(q)}"
                : EstadoExtra(u, q, estado);

            var objetivos = new List<ServerPackets.QuestObjetivo>();
            u.Quests.Activas.TryGetValue(q.Id, out var kills);
            int slot = 0;
            foreach (var obj in q.Objetivos)
            {
                // Zona del objetivo: la propia (DestinoN) o la general de la quest.
                int oDestMap = obj.DestMap > 0 ? obj.DestMap : q.DestMap;
                byte oDestX = (byte)(obj.DestMap > 0 ? obj.DestX : q.DestX);
                byte oDestY = (byte)(obj.DestMap > 0 ? obj.DestY : q.DestY);
                var oEnt = EntranceFor(oDestMap);
                if (obj.EsJuntar)
                {
                    var od = ObjData.Get((short)obj.Target);
                    objetivos.Add(new ServerPackets.QuestObjetivo(1, od.Name ?? ("objeto " + obj.Target),
                        Math.Min(Inventory.ContarObjeto(u, obj.Target), obj.Cantidad), obj.Cantidad, 0, od.GrhIndex,
                        oDestMap, oDestX, oDestY, oEnt.Map, oEnt.X, oEnt.Y));
                }
                else
                {
                    var nd = NpcData.Get(obj.Target);
                    int actual = kills != null && slot < kills.Count ? kills[slot] : 0;
                    objetivos.Add(new ServerPackets.QuestObjetivo(0, nd.Name ?? ("NPC " + obj.Target),
                        actual, obj.Cantidad, nd.Body, 0,
                        oDestMap, oDestX, oDestY, oEnt.Map, oEnt.X, oEnt.Y));
                    slot++;
                }
            }
            var giver = GiverPos(q.NpcDador);
            // Entrada al dungeon: para el destino y para el dador (el que tenga mapa "invisible").
            var entD = EntranceFor(q.DestMap);
            if (entD.Map == 0) entD = EntranceFor(giver.Map);
            res.Add(new ServerPackets.QuestEntry(q.Id, q.Nombre, q.Historia, q.RewardTexto,
                estado, (byte)Math.Min(q.NivelMin, 255), q.Repetible, extra,
                giver.Map, giver.X, giver.Y, q.DestMap, (byte)q.DestX, (byte)q.DestY,
                entD.Map, entD.X, entD.Y, objetivos));
        }
        return res;
    }

    private static string NombreDador(Quest q) => NpcData.Get(q.NpcDador).Name ?? "el NPC";

    // ============================================================
    //  Acciones del cliente
    // ============================================================
    public static void Accept(int userIndex, int questId)
    {
        var u = Get(userIndex);
        var q = GetQuest(questId);
        if (u?.Conn == null || q == null) return;
        if (Estado(u, q) != EST_DISPONIBLE) return;
        if (u.Quests.Activas.Count >= MAX_ACTIVAS)
        {
            ServerPackets.ConsoleMsg(u.Conn, $"No podés tener más de {MAX_ACTIVAS} misiones activas.", 4);
            return;
        }
        // Solo se acepta cara a cara con el dador (evita aceptar por paquete forjado a distancia).
        if (!DadorCerca(u, q)) { ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos del NPC.", 4); return; }

        int nMatar = 0;
        foreach (var o in q.Objetivos) if (!o.EsJuntar) nMatar++;
        u.Quests.Activas[q.Id] = new List<int>(new int[nMatar]);
        SaveProgress(u);
        ServerPackets.ConsoleMsg(u.Conn, $"📜 Misión aceptada: {q.Nombre}.", 58);
        SendNpcQuests(userIndex, q.NpcDador);     // refresca la ventana
        SendQuestLog(userIndex, abrirVentana: false); // actualiza flecha/marcadores
    }

    public static void TurnIn(int userIndex, int questId)
    {
        var u = Get(userIndex);
        var q = GetQuest(questId);
        if (u?.Conn == null || q == null) return;
        if (!u.Quests.Activas.TryGetValue(q.Id, out var kills)) return;
        if (!DadorCerca(u, q)) { ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos del NPC.", 4); return; }
        if (!MatanzaCompleta(q, kills) || !JuntadoCompleto(u, q))
        {
            ServerPackets.ConsoleMsg(u.Conn, "Todavía no completaste todos los objetivos.", 4);
            return;
        }

        // Descontar los objetos juntados
        foreach (var obj in q.Objetivos)
            if (obj.EsJuntar) Inventory.QuitarObjetos(u, obj.Target, obj.Cantidad);

        u.Quests.Activas.Remove(q.Id);
        if (q.Repetible) u.Quests.UltimaEntrega[q.Id] = DateTime.UtcNow.Ticks;
        else if (!u.Quests.Completadas.Contains(q.Id)) u.Quests.Completadas.Add(q.Id);
        SaveProgress(u);

        ServerPackets.ConsoleMsg(u.Conn, $"🏅 ¡Misión completada: {q.Nombre}!", 58);
        foreach (var tok in q.Reward) GrantReward(u, tok);
        SendNpcQuests(userIndex, q.NpcDador);     // refresca la ventana
        SendQuestLog(userIndex, abrirVentana: false); // actualiza flecha/marcadores
    }

    public static void Abandon(int userIndex, int questId)
    {
        var u = Get(userIndex);
        if (u?.Conn == null) return;
        if (!u.Quests.Activas.Remove(questId)) return;
        SaveProgress(u);
        var q = GetQuest(questId);
        ServerPackets.ConsoleMsg(u.Conn, $"Abandonaste la misión: {q?.Nombre ?? ("#" + questId)}.", 4);
        SendQuestLog(userIndex);
    }

    /// <summary>El dador tiene que estar a distancia de interacción (rango de visión del doble click).</summary>
    private static bool DadorCerca(User u, Quest q)
    {
        foreach (var n in NpcManager.GetMapNpcs(u.Pos.Map))
            if (!n.Dead && n.NpcIndex == q.NpcDador
                && Math.Abs(n.X - u.Pos.X) <= 8 && Math.Abs(n.Y - u.Pos.Y) <= 6)
                return true;
        return false;
    }

    // ============================================================
    //  Recompensas (mismos tokens que Logros + EXP)
    // ============================================================
    private static void GrantReward(User u, string token)
    {
        var parts = token.Split(':');
        switch (parts[0].Trim().ToUpperInvariant())
        {
            case "EXP":
                if (parts.Length >= 2 && long.TryParse(parts[1], out long exp))
                {
                    u.Stats.Exp += exp;
                    if (u.Conn != null)
                    {
                        ServerPackets.ConsoleMsg(u.Conn, $"¡Has ganado {exp:N0} puntos de experiencia!", 62);
                        ServerPackets.UpdateExp(u.Conn, (int)Math.Min(u.Stats.Exp, int.MaxValue));
                    }
                    Combat.CheckUserLevel(u);
                }
                break;

            case "ORO":
                if (parts.Length >= 2 && int.TryParse(parts[1], out int oro))
                {
                    u.Stats.GLD += oro;
                    if (u.Conn != null)
                    {
                        ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
                        ServerPackets.ConsoleMsg(u.Conn, $"Recibiste {oro:N0} monedas de oro.", 3);
                    }
                }
                break;

            case "OBJ":
                if (parts.Length >= 2 && short.TryParse(parts[1], out short idx))
                {
                    int cant = parts.Length >= 3 && int.TryParse(parts[2], out int c) ? c : 1;
                    string nombre = ObjData.Get(idx).Name;
                    if (string.IsNullOrEmpty(nombre)) nombre = "objeto " + idx;
                    if (Inventory.AddItemToInventory(u, idx, cant))
                    { if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, $"Recibiste {cant}x {nombre}.", 3); }
                    else
                    {
                        Work.DropItemAtPos(u.Pos, new UserObj { ObjIndex = idx, Amount = cant });
                        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, $"Tu inventario está lleno: {cant}x {nombre} cayó al suelo.", 4);
                    }
                }
                break;

            default:
                Console.WriteLine($"[Quests] Token de recompensa desconocido: {token}");
                break;
        }
    }

    private static Quest GetQuest(int id)
    {
        foreach (var q in _quests) if (q.Id == id) return q;
        return null;
    }

    private static User Get(int userIndex)
    {
        if (_quests.Count == 0) return null;
        var u = UserListManager.UserList[userIndex];
        return (u != null && u.flags.UserLogged && u.Quests != null) ? u : null;
    }
}
