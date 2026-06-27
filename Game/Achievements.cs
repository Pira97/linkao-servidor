using System.Text.Json;
using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sistema de Logros (NUEVO, no portado del VB6). Versión básica inicial.
///
/// Logros configurables en &lt;Dat&gt;/Logros.ini. Tipos soportados:
///   nivel  — alcanzar el nivel de personaje Objetivo (retroactivo al loguear).
///   tiempo — acumular Objetivo minutos conectado (Repetible=1 lo re-otorga cada ciclo).
///   minar  — extraer Objetivo unidades del mineral Target (ObjIndex, ej. 194=Oro, 192=Hierro).
///   npc    — matar Objetivo NPCs cuyo NpcIndex esté en Target (lista separada por coma)
///            o cuyo nombre contenga Target si no es numérico.
///
/// Recompensas (Reward=token;token): OBJ:idx:cant  ORO:n  (mismo formato que BattlePass).
/// Persistencia por personaje: &lt;ServerRoot&gt;/Logros/&lt;NOMBRE&gt;.json.
/// Comando /logros: lista progreso y completados por consola.
/// </summary>
public static class Achievements
{
    public sealed class Logro
    {
        public int Id;
        public string Desc = "";
        public string Tipo = "";       // nivel | tiempo | minar | npc
        public string Target = "";     // según tipo (ver arriba)
        public long Objetivo = 1;      // cantidad necesaria
        public bool Repetible;         // solo tiene sentido para "tiempo"
        public List<string> Reward = new();
        public HashSet<int> TargetNpcIds = new(); // pre-parseado si Target es lista numérica
    }

    /// <summary>Progreso por personaje (persistido a JSON).</summary>
    public sealed class Progress
    {
        public Dictionary<int, long> Progreso { get; set; } = new(); // logroId -> avance
        public List<int> Completados { get; set; } = new();
        public Dictionary<int, int> Repeticiones { get; set; } = new(); // logroId -> veces otorgado (repetibles)

        // Acumulador de segundos online (no persiste fracciones de minuto).
        internal int _segundosParciales;
        internal long _ultimoTickSeg; // TickCount64 del último segundo contado
    }

    private static readonly List<Logro> _logros = new();
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private static string Dir => string.IsNullOrEmpty(DataPaths.Root)
        ? "Logros" + Path.DirectorySeparatorChar
        : DataPaths.Sub("Logros");

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
    //  Carga de Logros.ini (llamado en el arranque)
    // ============================================================
    public static void Load()
    {
        _logros.Clear();
        string path = (string.IsNullOrEmpty(DataPaths.Root) ? "Dat" + Path.DirectorySeparatorChar : DataPaths.Sub("Dat")) + "Logros.ini";
        var ini = new IniFile(path);
        if (!ini.Loaded)
        {
            Console.WriteLine($"[Logros] No se encontró Logros.ini en {path}. Logros deshabilitados.");
            return;
        }

        for (int i = 1; i <= 200; i++)
        {
            string sec = "LOGRO" + i;
            string desc = ini.Get(sec, "Desc");
            if (desc.Length == 0) break; // corta al primer hueco

            var l = new Logro
            {
                Id = i,
                Desc = desc,
                Tipo = ini.Get(sec, "Tipo").Trim().ToLowerInvariant(),
                Target = ini.Get(sec, "Target").Trim(),
                Objetivo = Math.Max(1, ini.GetInt(sec, "Objetivo")),
                Repetible = ini.GetInt(sec, "Repetible") == 1,
            };
            foreach (var t in ini.Get(sec, "Reward").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                l.Reward.Add(t);
            // Target numérico (uno o varios NpcIndex separados por coma) → set de ids.
            foreach (var p in l.Target.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(p, out int id)) l.TargetNpcIds.Add(id);
            _logros.Add(l);
        }
        Console.WriteLine($"[Logros] {_logros.Count} logros cargados.");
    }

    // ============================================================
    //  Persistencia
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
            Console.WriteLine($"[Logros] Error al cargar progreso de {name}: {ex.Message}");
        }
        return new Progress();
    }

    private static void SaveProgress(User u)
    {
        if (u?.Logros == null || string.IsNullOrEmpty(u.Name)) return;
        WriteProgressJson(u.Name, JsonSerializer.Serialize(u.Logros, _json));
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
            Console.WriteLine($"[Logros] Error al guardar progreso de {name}: {ex.Message}");
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
            if (u != null && u.flags.UserLogged && u.Logros != null) { SaveProgress(u); n++; }
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
            if (u != null && u.flags.UserLogged && u.Logros != null && !string.IsNullOrEmpty(u.Name))
                result[u.Name] = JsonSerializer.Serialize(u.Logros, _json);
        }
        return result;
    }

    // ============================================================
    //  Login: cargar progreso y chequear logros de nivel retroactivos
    // ============================================================
    public static void OnLogin(int userIndex)
    {
        if (_logros.Count == 0) return;
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.Conn == null) return;
        u.Logros = LoadProgress(u.Name);
        u.Logros._ultimoTickSeg = Environment.TickCount64;
        // Los logros de nivel ya alcanzado se completan al loguear (retroactivo).
        OnLevelUp(userIndex, u.Stats.ELV);
    }

    // ============================================================
    //  Hooks de juego
    // ============================================================
    /// <summary>Subió de nivel (o logueó): completa todos los logros de nivel ya alcanzados.</summary>
    public static void OnLevelUp(int userIndex, int nivel)
    {
        var u = Get(userIndex);
        if (u == null) return;
        bool cambio = false;
        foreach (var l in _logros)
        {
            if (l.Tipo != "nivel" || u.Logros.Completados.Contains(l.Id)) continue;
            u.Logros.Progreso[l.Id] = nivel;
            cambio = true;
            if (nivel >= l.Objetivo) Completar(u, l);
        }
        if (cambio) SaveProgress(u);
    }

    /// <summary>Mató un NPC: avanza los logros tipo npc que matcheen por NpcIndex o nombre.
    /// El avance se muestra como texto flotante dorado sobre el personaje (ChatOverHead
    /// modo 7 sobre el propio char = FloatingText del cliente, igual que la subida de skills).</summary>
    public static void OnNpcKilled(int userIndex, int npcIndex, string npcName)
    {
        var u = Get(userIndex);
        if (u == null) return;
        bool cambio = false;
        foreach (var l in _logros)
        {
            if (l.Tipo != "npc" || u.Logros.Completados.Contains(l.Id)) continue;
            bool match = l.TargetNpcIds.Count > 0
                ? l.TargetNpcIds.Contains(npcIndex)
                : !string.IsNullOrEmpty(l.Target) && !string.IsNullOrEmpty(npcName)
                    && npcName.Contains(l.Target, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;

            long actual = u.Logros.Progreso.GetValueOrDefault(l.Id, 0) + 1;
            u.Logros.Progreso[l.Id] = actual;
            cambio = true;
            if (actual >= l.Objetivo)
            {
                Completar(u, l); // el completado ya avisa con cartel dorado + sonido
            }
            else if (u.Conn != null)
            {
                // Progreso sobre la cabeza: "Lobos Invernales: 4/10"
                string nombre = string.IsNullOrEmpty(npcName) ? "Logro" : npcName;
                ServerPackets.ChatOverHead(u.Conn, $"{nombre}: {actual}/{l.Objetivo}", u.Char.CharIndex, 7);
            }
        }
        if (cambio) SaveProgress(u);
    }

    /// <summary>Extrajo minerales: avanza los logros tipo minar del mineral Target (ObjIndex).</summary>
    public static void OnMinar(int userIndex, short mineralObjIndex, int cantidad)
    {
        var u = Get(userIndex);
        if (u == null || cantidad <= 0) return;
        bool cambio = false;
        foreach (var l in _logros)
        {
            if (l.Tipo != "minar" || u.Logros.Completados.Contains(l.Id)) continue;
            if (!l.TargetNpcIds.Contains(mineralObjIndex)) continue;

            long actual = u.Logros.Progreso.GetValueOrDefault(l.Id, 0) + cantidad;
            u.Logros.Progreso[l.Id] = actual;
            cambio = true;
            if (actual >= l.Objetivo) Completar(u, l);
        }
        if (cambio) SaveProgress(u);
    }

    /// <summary>Llamado ~1/seg por usuario desde GameTimer.Tick: acumula tiempo online y avanza
    /// los logros tipo tiempo (en minutos). Los repetibles se re-otorgan cada ciclo completo.</summary>
    public static void TickOnline(int userIndex, long now)
    {
        var u = Get(userIndex);
        if (u == null) return;
        var prog = u.Logros;
        if (now - prog._ultimoTickSeg < 1000) return;
        prog._segundosParciales += (int)((now - prog._ultimoTickSeg) / 1000);
        prog._ultimoTickSeg = now;
        if (prog._segundosParciales < 60) return;

        int minutos = prog._segundosParciales / 60;
        prog._segundosParciales %= 60;
        bool cambio = false;
        foreach (var l in _logros)
        {
            if (l.Tipo != "tiempo") continue;
            if (!l.Repetible && u.Logros.Completados.Contains(l.Id)) continue;

            long actual = prog.Progreso.GetValueOrDefault(l.Id, 0) + minutos;
            cambio = true;
            if (actual >= l.Objetivo)
            {
                if (l.Repetible)
                {
                    actual -= l.Objetivo; // arranca el ciclo siguiente con el sobrante
                    prog.Repeticiones[l.Id] = prog.Repeticiones.GetValueOrDefault(l.Id, 0) + 1;
                    Entregar(u, l, anunciar: true);
                }
                else Completar(u, l);
            }
            prog.Progreso[l.Id] = actual;
        }
        if (cambio) SaveProgress(u);
    }

    // ============================================================
    //  Completar / entregar recompensa
    // ============================================================
    private static void Completar(User u, Logro l)
    {
        u.Logros.Completados.Add(l.Id);
        Entregar(u, l, anunciar: true);
    }

    private static void Entregar(User u, Logro l, bool anunciar)
    {
        if (anunciar && u.Conn != null)
        {
            ServerPackets.ConsoleMsg(u.Conn, $"🏆 ¡Logro completado: {l.Desc}!", 58); // 58 = ámbar dorado
            BroadcastWave(u, Sounds.NIVEL_NUEVO);
        }
        foreach (var tok in l.Reward) GrantReward(u, tok);
    }

    private static void GrantReward(User u, string token)
    {
        var parts = token.Split(':');
        switch (parts[0].Trim().ToUpperInvariant())
        {
            case "ORO":
                if (parts.Length >= 2 && int.TryParse(parts[1], out int oro))
                {
                    u.Stats.GLD += oro;
                    if (u.Conn != null)
                    {
                        ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
                        ServerPackets.ConsoleMsg(u.Conn, $"Recibiste {oro} monedas de oro.", 3);
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
                        // Inventario lleno: cae al piso para no perder la recompensa.
                        Work.DropItemAtPos(u.Pos, new UserObj { ObjIndex = idx, Amount = cant });
                        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, $"Tu inventario está lleno: {cant}x {nombre} cayó al suelo.", 4);
                    }
                }
                break;

            default:
                Console.WriteLine($"[Logros] Token de recompensa desconocido: {token}");
                break;
        }
    }

    private static void BroadcastWave(User u, short wave)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.PlayWave(o.Conn, wave, (byte)u.Pos.X, (byte)u.Pos.Y);
        }
    }

    private static User Get(int userIndex)
    {
        if (_logros.Count == 0) return null;
        var u = UserListManager.UserList[userIndex];
        return (u != null && u.flags.UserLogged && u.Logros != null) ? u : null;
    }

    // ============================================================
    //  /logros — envío del estado para la ventana del cliente
    // ============================================================
    /// <summary>Manda el packet LogrosInfo con todos los logros y el progreso del jugador.
    /// El cliente abre/refresca la ventana de Logros al recibirlo.</summary>
    public static void SendInfo(User u)
    {
        if (u?.Conn == null) return;
        if (_logros.Count == 0) { ServerPackets.ConsoleMsg(u.Conn, "No hay logros configurados.", 1); return; }
        var prog = u.Logros ?? new Progress();

        var list = new List<(string, string, string, long, long, bool, bool, int, int, int)>();
        foreach (var l in _logros)
        {
            bool comp = !l.Repetible && prog.Completados.Contains(l.Id);
            long actual = comp ? l.Objetivo : prog.Progreso.GetValueOrDefault(l.Id, 0);
            int veces = l.Repetible ? prog.Repeticiones.GetValueOrDefault(l.Id, 0) : 0;
            list.Add((l.Tipo, l.Desc, DescribeReward(l), actual, l.Objetivo, comp, l.Repetible, veces, BodyDeLogro(l), GrhDeLogro(l)));
        }
        ServerPackets.LogrosInfo(u.Conn, list);
    }

    /// <summary>Texto legible de la recompensa (nombres de obj.dat).</summary>
    private static string DescribeReward(Logro l)
    {
        var partes = new List<string>();
        foreach (var tok in l.Reward)
        {
            var p = tok.Split(':');
            switch (p[0].Trim().ToUpperInvariant())
            {
                case "ORO":
                    partes.Add($"{p.ElementAtOrDefault(1)} monedas de oro");
                    break;
                case "OBJ":
                    if (short.TryParse(p.ElementAtOrDefault(1), out short oi))
                    {
                        string nm = ObjData.Get(oi).Name;
                        if (string.IsNullOrEmpty(nm)) nm = "objeto " + oi;
                        string cant = p.ElementAtOrDefault(2) ?? "1";
                        partes.Add($"{cant}x {nm}");
                    }
                    break;
            }
        }
        return string.Join(" + ", partes);
    }

    /// <summary>Body del primer NPC objetivo (tipo npc), para que el cliente dibuje su sprite. 0 = sin sprite.</summary>
    private static int BodyDeLogro(Logro l)
    {
        if (l.Tipo != "npc" || l.TargetNpcIds.Count == 0) return 0;
        foreach (var id in l.TargetNpcIds)
        {
            var npc = NpcData.Get(id);
            if (npc.Body > 0) return npc.Body;
        }
        return 0;
    }

    /// <summary>GrhIndex de un objeto representativo para el ícono de la fila:
    /// tipo minar → el mineral Target; resto → el primer OBJ de la recompensa. 0 = sin ícono.</summary>
    private static int GrhDeLogro(Logro l)
    {
        if (l.Tipo == "minar")
            foreach (var id in l.TargetNpcIds)
            { int g = ObjData.Get(id).GrhIndex; if (g > 0) return g; }
        foreach (var tok in l.Reward)
        {
            var p = tok.Split(':');
            if (p[0].Trim().ToUpperInvariant() == "OBJ" && short.TryParse(p.ElementAtOrDefault(1), out short oi))
            { int g = ObjData.Get(oi).GrhIndex; if (g > 0) return g; }
        }
        return 0;
    }

    // ============================================================
    //  /logros — listado por consola (fallback / debug)
    // ============================================================
    public static void ListarLogros(User u)
    {
        if (u?.Conn == null) return;
        if (_logros.Count == 0) { ServerPackets.ConsoleMsg(u.Conn, "No hay logros configurados.", 1); return; }
        if (u.Logros == null) { ServerPackets.ConsoleMsg(u.Conn, "Progreso de logros no cargado.", 1); return; }

        ServerPackets.ConsoleMsg(u.Conn, "Logros:", 58);
        foreach (var l in _logros)
        {
            bool comp = u.Logros.Completados.Contains(l.Id);
            long actual = comp ? l.Objetivo : u.Logros.Progreso.GetValueOrDefault(l.Id, 0);
            string extra = "";
            if (l.Repetible)
            {
                int veces = u.Logros.Repeticiones.GetValueOrDefault(l.Id, 0);
                actual = u.Logros.Progreso.GetValueOrDefault(l.Id, 0);
                comp = false;
                if (veces > 0) extra = $" (obtenido x{veces})";
            }
            string estado = comp ? "✔" : $"{actual}/{l.Objetivo}";
            ServerPackets.ConsoleMsg(u.Conn, $"» {l.Desc} — {estado}{extra}", comp ? (byte)3 : (byte)1);
        }
    }
}
