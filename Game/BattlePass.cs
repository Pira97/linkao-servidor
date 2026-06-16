using System.Text.Json;
using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Battle Pass / Pase de Temporada (NUEVO, no portado del VB6).
///
/// Dos carriles paralelos por temporada: Gratis (todos) y Premium (donadores). Se sube de nivel
/// del pase ganando "Puntos de Pase" (NO la exp normal). Cada nivel tiene recompensas free y prem
/// que el jugador reclama manualmente. El carril premium se desbloquea con CreditoDonador o
/// compra MercadoPago.
///
/// Configuración: &lt;Dat&gt;/BattlePass.ini (temporada + tabla de recompensas).
/// Persistencia de progreso por personaje: &lt;ServerRoot&gt;/BattlePass/&lt;NOMBRE&gt;.json.
/// Al cambiar el Id de temporada se resetea el progreso automáticamente.
///
/// Tags de recompensa (en BattlePass.ini):
///   ORO:n  OBJ:idx:cant  AURA_ITEM:idx  MOUNT:idx  PART:id
///   BOOST_EXP:%:min  BOOST_ORO:%:min  PASAJE  TITULO:texto
/// </summary>
public static class BattlePass
{
    // ============================================================
    //  Definición de temporada (cargada del .ini)
    // ============================================================
    public sealed class RewardLevel
    {
        public int Level;
        public List<string> Free = new();    // tokens de recompensa del carril gratis
        public List<string> Premium = new(); // tokens del carril premium
    }

    /// <summary>Misión concreta con objetivo y recompensa de puntos.</summary>
    public sealed class Mision
    {
        public int Id;
        public string Desc = "";
        public string Tipo = "";   // npc | tier | trabajo | pvp
        public string Target = ""; // nombre o NpcIndex (tipo npc)
        public int TargetHp;       // tipo tier: HP mínimo
        public int Objetivo = 1;   // cantidad necesaria
        public int Puntos;         // puntos de pase al completar
    }

    public sealed class Season
    {
        public int Id;
        public string Nombre = "";
        public int NivelesMax = 50;
        public int PuntosPorNivel = 1000;
        public int PrecioCredito = 500;
        public int PrecioMercadoPago = 300;
        // Puntos por acción ([PUNTOS] del .ini).
        // NPC por dificultad (según MaxHP).
        public int NpcTrivial = 0; // HP < 500
        public int NpcDebil = 0;   // 500-5000
        public int NpcNormal = 0;  // 5000-20000
        public int NpcFuerte = 0;  // 20000-50000
        public int NpcElite = 0;   // 50000-150000
        public int NpcBoss = 0;    // 150000-500000
        public int NpcRaid = 0;    // > 500000
        public int PorSubirNivel = 0;
        public int PorTrabajo = 0;
        public int PorPvp = 0;
        public int PorEvento = 0;
        public readonly Dictionary<int, RewardLevel> Niveles = new();
        public readonly List<Mision> Misiones = new();
    }

    // ============================================================
    //  Progreso por personaje (persistido a JSON)
    // ============================================================
    public sealed class Progress
    {
        public int Season { get; set; }
        public int Puntos { get; set; }
        public int Nivel { get; set; } = 1;
        public bool Premium { get; set; }
        public List<int> ReclamadosFree { get; set; } = new();
        public List<int> ReclamadosPrem { get; set; } = new();
        public string Titulo { get; set; } = "";
        public Dictionary<int, int> MisionProgreso { get; set; } = new();   // misionId -> avance
        public List<int> MisionesCompletadas { get; set; } = new();
    }

    private static Season _season;
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private static string Dir => string.IsNullOrEmpty(DataPaths.Root)
        ? "BattlePass" + Path.DirectorySeparatorChar
        : DataPaths.Sub("BattlePass");

    private static string ProgressPath(string name)
        => Path.Combine(Dir, SafeName(name) + ".json");

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.ToUpperInvariant();
    }

    // ============================================================
    //  Carga del .ini de temporada (llamado en el arranque)
    // ============================================================
    public static void Load()
    {
        var s = new Season();
        string path = (string.IsNullOrEmpty(DataPaths.Root) ? "Dat" + Path.DirectorySeparatorChar : DataPaths.Sub("Dat")) + "BattlePass.ini";
        var ini = new IniFile(path);
        if (!ini.Loaded)
        {
            Console.WriteLine($"[BattlePass] No se encontró BattlePass.ini en {path}. Pase deshabilitado.");
            _season = s;
            return;
        }

        s.Id = ini.GetInt("SEASON", "Id");
        s.Nombre = ini.Get("SEASON", "Nombre");
        s.NivelesMax = Math.Max(1, ini.GetInt("SEASON", "NivelesMax"));
        s.PuntosPorNivel = Math.Max(1, ini.GetInt("SEASON", "PuntosPorNivel"));
        s.PrecioCredito = ini.GetInt("SEASON", "PrecioCredito");
        s.PrecioMercadoPago = ini.GetInt("SEASON", "PrecioMercadoPago");
        s.NpcTrivial = ini.GetInt("PUNTOS", "NpcTrivial");
        s.NpcDebil = ini.GetInt("PUNTOS", "NpcDebil");
        s.NpcNormal = ini.GetInt("PUNTOS", "NpcNormal");
        s.NpcFuerte = ini.GetInt("PUNTOS", "NpcFuerte");
        s.NpcElite = ini.GetInt("PUNTOS", "NpcElite");
        s.NpcBoss = ini.GetInt("PUNTOS", "NpcBoss");
        s.NpcRaid = ini.GetInt("PUNTOS", "NpcRaid");
        s.PorSubirNivel = ini.GetInt("PUNTOS", "PorSubirNivel");
        s.PorTrabajo = ini.GetInt("PUNTOS", "PorTrabajo");
        s.PorPvp = ini.GetInt("PUNTOS", "PorPvp");
        s.PorEvento = ini.GetInt("PUNTOS", "PorEvento");

        for (int lvl = 1; lvl <= s.NivelesMax; lvl++)
        {
            string sec = "NIVEL" + lvl;
            string free = ini.Get(sec, "Free");
            string prem = ini.Get(sec, "Premium");
            if (free.Length == 0 && prem.Length == 0) continue;
            s.Niveles[lvl] = new RewardLevel
            {
                Level = lvl,
                Free = SplitTokens(free),
                Premium = SplitTokens(prem),
            };
        }

        // Misiones [MISION1], [MISION2], ... (corta al primer hueco).
        for (int i = 1; i <= 50; i++)
        {
            string sec = "MISION" + i;
            string desc = ini.Get(sec, "Desc");
            if (desc.Length == 0) break;
            s.Misiones.Add(new Mision
            {
                Id = i,
                Desc = desc,
                Tipo = ini.Get(sec, "Tipo").Trim().ToLowerInvariant(),
                Target = ini.Get(sec, "Target").Trim(),
                TargetHp = ini.GetInt(sec, "TargetHP"),
                Objetivo = Math.Max(1, ini.GetInt(sec, "Objetivo")),
                Puntos = ini.GetInt(sec, "Puntos"),
            });
        }

        _season = s;
        Console.WriteLine($"[BattlePass] Temporada {s.Id} \"{s.Nombre}\" cargada: {s.Niveles.Count}/{s.NivelesMax} niveles, {s.Misiones.Count} misiones.");
    }

    private static List<string> SplitTokens(string raw)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var t in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(t);
        return list;
    }

    // ============================================================
    //  Persistencia de progreso
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
            Console.WriteLine($"[BattlePass] Error al cargar progreso de {name}: {ex.Message}");
        }
        return new Progress();
    }

    private static void SaveProgress(User u)
    {
        if (u?.BattlePass == null || string.IsNullOrEmpty(u.Name)) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ProgressPath(u.Name), JsonSerializer.Serialize(u.BattlePass, _json));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BattlePass] Error al guardar progreso de {u.Name}: {ex.Message}");
        }
    }

    /// <summary>Guarda el progreso del pase de TODOS los jugadores online. Se llama en el cierre
    /// del server y en el backup periódico, como red de seguridad ante caídas.</summary>
    public static int SaveAll()
    {
        int n = 0;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.BattlePass != null)
            {
                SaveProgress(u);
                n++;
            }
        }
        return n;
    }

    // ============================================================
    //  Login: cargar progreso, resetear si cambió la temporada, enviar estado
    // ============================================================
    public static void OnLogin(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.Conn == null) return;

        var prog = LoadProgress(u.Name);
        // Reset por cambio de temporada (mantiene Premium solo si es la misma temporada).
        if (prog.Season != _season.Id)
        {
            prog = new Progress { Season = _season.Id, Nivel = 1, Puntos = 0 };
            SaveProgressDirect(u.Name, prog);
        }
        u.BattlePass = prog;
        SendInfo(u);
    }

    private static void SaveProgressDirect(string name, Progress prog)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ProgressPath(name), JsonSerializer.Serialize(prog, _json));
        }
        catch { /* ignore */ }
    }

    // ============================================================
    //  Sumar puntos de pase (llamado desde misiones / eventos)
    // ============================================================
    /// <summary>
    /// Suma puntos de pase. silencioso=true (acciones repetitivas tipo matar NPC) actualiza la barra
    /// SIN toast salvo que se suba de nivel del pase; silencioso=false siempre avisa con motivo.
    /// </summary>
    public static void AddPoints(int userIndex, int amount, string motivo = "", bool silencioso = true)
    {
        if (amount <= 0) return;
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.BattlePass == null || !u.flags.UserLogged) return;

        var prog = u.BattlePass;
        if (prog.Nivel >= _season.NivelesMax && prog.Puntos == 0) return; // ya completo

        prog.Puntos += amount;
        int subidos = 0;
        while (prog.Nivel < _season.NivelesMax && prog.Puntos >= _season.PuntosPorNivel)
        {
            prog.Puntos -= _season.PuntosPorNivel;
            prog.Nivel++;
            subidos++;
        }
        if (prog.Nivel >= _season.NivelesMax) prog.Puntos = 0;

        SaveProgress(u);

        // Mensaje (toast): solo si subió de nivel del pase o si la fuente pidió aviso explícito.
        string msg = "";
        if (subidos > 0)
            msg = $"🏆 ¡Subiste {subidos} nivel(es) del Pase! Ya estás en el nivel {prog.Nivel}.";
        else if (!silencioso)
            msg = string.IsNullOrEmpty(motivo) ? $"+{amount} puntos de pase." : $"+{amount} puntos de pase ({motivo}).";
        ServerPackets.BattlePassUpdate(u.Conn, prog.Puntos, (byte)prog.Nivel, msg);
    }

    // ============================================================
    //  Hooks de juego: cada acción otorga puntos según [PUNTOS] del .ini
    // ============================================================
    /// <summary>Mató un NPC/criatura. Los puntos escalan por dificultad (MaxHP): solo los NPCs
    /// fuertes/jefes hacen avanzar el pase; los bichos triviales pueden dar 0. Además avanza misiones.</summary>
    public static void OnNpcKilled(int userIndex, int npcMaxHp, string npcName, int npcIndex)
    {
        if (_season == null) return;
        int pts;
        if (npcMaxHp < 500) pts = _season.NpcTrivial;
        else if (npcMaxHp < 5000) pts = _season.NpcDebil;
        else if (npcMaxHp < 20000) pts = _season.NpcNormal;
        else if (npcMaxHp < 50000) pts = _season.NpcFuerte;
        else if (npcMaxHp < 150000) pts = _season.NpcElite;
        else if (npcMaxHp < 500000) pts = _season.NpcBoss;
        else pts = _season.NpcRaid;
        AddPoints(userIndex, pts, "NPC");
        AvanzarMisiones(userIndex, "npc", npcName, npcIndex, npcMaxHp);
        AvanzarMisiones(userIndex, "tier", npcName, npcIndex, npcMaxHp);
    }
    /// <summary>Subió un nivel de personaje.</summary>
    public static void OnLevelUp(int userIndex) { if (_season != null) AddPoints(userIndex, _season.PorSubirNivel, "subir nivel", silencioso: false); }
    /// <summary>Acción de trabajo exitosa (pesca/minería/tala/botánica).</summary>
    public static void OnWork(int userIndex) { if (_season == null) return; AddPoints(userIndex, _season.PorTrabajo, "trabajo"); AvanzarMisiones(userIndex, "trabajo", "", 0, 0); }
    /// <summary>Mató a otro jugador en PvP (ya pasó el anti-farmeo de facción).</summary>
    public static void OnPvpKill(int userIndex) { if (_season == null) return; AddPoints(userIndex, _season.PorPvp, "PvP", silencioso: false); AvanzarMisiones(userIndex, "pvp", "", 0, 0); }
    /// <summary>Participó/ganó un evento global.</summary>
    public static void OnEvent(int userIndex) { if (_season != null) AddPoints(userIndex, _season.PorEvento, "evento", silencioso: false); }

    /// <summary>Avanza las misiones del tipo dado que correspondan a esta acción; al completarse
    /// otorga sus puntos y refresca el panel.</summary>
    private static void AvanzarMisiones(int userIndex, string tipo, string npcName, int npcIndex, int npcHp)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.BattlePass == null || !u.flags.UserLogged) return;
        var prog = u.BattlePass;
        bool cambio = false;

        foreach (var m in _season.Misiones)
        {
            if (m.Tipo != tipo) continue;
            if (prog.MisionesCompletadas.Contains(m.Id)) continue;

            // ¿esta acción cuenta para esta misión?
            bool cuenta = tipo switch
            {
                "npc" => MatchNpc(m, npcName, npcIndex),
                "tier" => npcHp >= m.TargetHp,
                "trabajo" => true,
                "pvp" => true,
                _ => false,
            };
            if (!cuenta) continue;

            int actual = prog.MisionProgreso.GetValueOrDefault(m.Id, 0) + 1;
            prog.MisionProgreso[m.Id] = actual;
            cambio = true;

            if (actual >= m.Objetivo)
            {
                prog.MisionesCompletadas.Add(m.Id);
                if (u.Conn != null)
                    ServerPackets.BattlePassUpdate(u.Conn, prog.Puntos, (byte)prog.Nivel,
                        $"✅ Misión completada: {m.Desc}  (+{m.Puntos} pts)");
                AddPoints(userIndex, m.Puntos, m.Desc); // esto reenvía progreso/nivel
            }
        }

        if (cambio)
        {
            SaveProgress(u);
            SendInfo(u); // refresca los contadores en el panel
        }
    }

    /// <summary>Tipo npc: Target numérico = NpcIndex exacto; si no, coincidencia parcial por nombre.</summary>
    private static bool MatchNpc(Mision m, string npcName, int npcIndex)
    {
        if (int.TryParse(m.Target, out int idx)) return npcIndex == idx;
        return !string.IsNullOrEmpty(npcName) && !string.IsNullOrEmpty(m.Target)
            && npcName.Contains(m.Target, StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================
    //  Reclamar recompensa de un nivel (carril 0=free, 1=premium)
    // ============================================================
    public static void Claim(int userIndex, int level, byte carril)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.BattlePass == null || u.Conn == null || !u.flags.UserLogged) return;
        var prog = u.BattlePass;

        if (level < 1 || level > _season.NivelesMax || !_season.Niveles.TryGetValue(level, out var rw))
        { ServerPackets.BattlePassUpdate(u.Conn, prog.Puntos, (byte)prog.Nivel, "Ese nivel no tiene recompensa."); return; }

        if (prog.Nivel < level)
        { ServerPackets.BattlePassUpdate(u.Conn, prog.Puntos, (byte)prog.Nivel, $"Todavía no alcanzaste el nivel {level} del pase."); return; }

        bool premium = carril == 1;
        if (premium && !prog.Premium)
        { ServerPackets.BattlePassUpdate(u.Conn, prog.Puntos, (byte)prog.Nivel, "Necesitas el pase Premium para esta recompensa."); return; }

        var reclamados = premium ? prog.ReclamadosPrem : prog.ReclamadosFree;
        if (reclamados.Contains(level))
        { ServerPackets.BattlePassUpdate(u.Conn, prog.Puntos, (byte)prog.Nivel, "Ya reclamaste esa recompensa."); return; }

        var tokens = premium ? rw.Premium : rw.Free;
        if (tokens.Count == 0)
        { ServerPackets.BattlePassUpdate(u.Conn, prog.Puntos, (byte)prog.Nivel, "No hay recompensa en ese carril."); return; }

        foreach (var tok in tokens) GrantReward(u, tok);

        reclamados.Add(level);
        SaveProgress(u);
        ServerPackets.BattlePassUpdate(u.Conn, prog.Puntos, (byte)prog.Nivel,
            $"¡Recompensa del nivel {level} ({(premium ? "Premium" : "Gratis")}) reclamada!");
        SendInfo(u); // refresca los flags de reclamado en la UI
    }

    // ============================================================
    //  Comprar el pase Premium (metodo 0=CreditoDonador, 1=MercadoPago)
    // ============================================================
    public static void BuyPremium(int userIndex, byte metodo)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.BattlePass == null || u.Conn == null || !u.flags.UserLogged) return;
        var prog = u.BattlePass;

        if (prog.Premium)
        { ServerPackets.BattlePassUpdate(u.Conn, prog.Puntos, (byte)prog.Nivel, "Ya tienes el pase Premium de esta temporada."); return; }

        if (metodo == 0)
        {
            // Pago con créditos de donador.
            if (u.CreditoDonador < _season.PrecioCredito)
            {
                ServerPackets.BattlePassUpdate(u.Conn, prog.Puntos, (byte)prog.Nivel,
                    $"No tienes suficientes créditos. Precio: {_season.PrecioCredito}, tienes: {u.CreditoDonador}.");
                return;
            }
            u.CreditoDonador -= _season.PrecioCredito;
            GuardarCreditos(u);
            ServerPackets.UpdateCreditos(u.Conn, u.CreditoDonador);
            prog.Premium = true;
            SaveProgress(u);
            ServerPackets.BattlePassUpdate(u.Conn, prog.Puntos, (byte)prog.Nivel,
                $"¡Pase Premium activado! Se descontaron {_season.PrecioCredito} créditos.");
            SendInfo(u);
        }
        else
        {
            // Compra directa MercadoPago. El otorgamiento real del Premium debe ocurrir cuando
            // MercadoPago confirme el pago (polling) → llamar GrantPremiumPaid(userIndex) desde ahí.
            // Mientras esa confirmación no esté cableada, se informa el estado y se sugiere créditos.
            if (MercadoPago.Habilitado)
                ServerPackets.BattlePassUpdate(u.Conn, prog.Puntos, (byte)prog.Nivel,
                    "La compra directa por MercadoPago estará disponible pronto. Por ahora podés activarlo con créditos de donador.");
            else
                ServerPackets.BattlePassUpdate(u.Conn, prog.Puntos, (byte)prog.Nivel,
                    "MercadoPago no está habilitado en este servidor. Activá el pase con créditos de donador.");
        }
    }

    /// <summary>Activa el Premium tras una confirmación de pago externa (MercadoPago).</summary>
    public static void GrantPremiumPaid(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.BattlePass == null) return;
        u.BattlePass.Premium = true;
        SaveProgress(u);
        if (u.Conn != null)
        {
            ServerPackets.BattlePassUpdate(u.Conn, u.BattlePass.Puntos, (byte)u.BattlePass.Nivel,
                "¡Pase Premium activado! Gracias por tu compra.");
            SendInfo(u);
        }
    }

    private static void GuardarCreditos(User u)
    {
        if (string.IsNullOrEmpty(u.Account)) return;
        try
        {
            string cnt = Path.Combine(AccountManager.AccountPath, u.Account.ToUpperInvariant() + ".cnt");
            var doc = new IniDocument(cnt);
            doc.Set(u.Account.ToUpperInvariant(), "Creditos", u.CreditoDonador.ToString());
            doc.Save(cnt);
        }
        catch (Exception ex) { Console.WriteLine($"[BattlePass] Error guardando créditos: {ex.Message}"); }
    }

    // ============================================================
    //  Entrega de una recompensa individual
    // ============================================================
    private static void GrantReward(User u, string token)
    {
        var parts = token.Split(':');
        string tipo = parts[0].Trim().ToUpperInvariant();
        switch (tipo)
        {
            case "ORO":
                if (parts.Length >= 2 && int.TryParse(parts[1], out int oro))
                {
                    u.Stats.GLD += oro;
                    ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
                    ServerPackets.ConsoleMsg(u.Conn, $"Recibiste {oro} monedas de oro.", 3);
                }
                break;

            case "OBJ":
                if (parts.Length >= 2 && short.TryParse(parts[1], out short idx))
                {
                    int cant = parts.Length >= 3 && int.TryParse(parts[2], out int c) ? c : 1;
                    if (!Inventory.AddItemToInventory(u, idx, cant))
                        ServerPackets.ConsoleMsg(u.Conn, "Tu inventario está lleno; libera espacio y vuelve a reclamar.", 4);
                    else
                        ServerPackets.ConsoleMsg(u.Conn, $"Recibiste {cant}x {ObjName(idx, "objeto " + idx)}.", 3);
                }
                break;

            case "AURA_ITEM": // cosmético con aura: se entrega el ítem y el jugador lo equipa
                if (parts.Length >= 2 && short.TryParse(parts[1], out short aidx))
                {
                    if (Inventory.AddItemToInventory(u, aidx, 1))
                        ServerPackets.ConsoleMsg(u.Conn, $"Recibiste el cosmético: {ObjName(aidx, "objeto " + aidx)}. ¡Equípalo!", 3);
                    else
                        ServerPackets.ConsoleMsg(u.Conn, "Tu inventario está lleno; libera espacio y vuelve a reclamar.", 4);
                }
                break;

            case "MOUNT": // montura: se entrega el ítem de montura (ObjType 44)
                if (parts.Length >= 2 && short.TryParse(parts[1], out short midx))
                {
                    if (Inventory.AddItemToInventory(u, midx, 1))
                        ServerPackets.ConsoleMsg(u.Conn, $"Recibiste la montura: {ObjName(midx, "objeto " + midx)}.", 3);
                    else
                        ServerPackets.ConsoleMsg(u.Conn, "Tu inventario está lleno; libera espacio y vuelve a reclamar.", 4);
                }
                break;

            case "BOOST_EXP":
                ApplyBoost(u, parts, esExp: true);
                break;

            case "BOOST_ORO":
                ApplyBoost(u, parts, esExp: false);
                break;

            case "PART": // partícula cosmética (id de la lista del cliente) — pendiente de mapear ids
                ServerPackets.ConsoleMsg(u.Conn, "Recibiste una partícula cosmética del pase.", 3);
                break;

            case "TITULO":
                if (parts.Length >= 2)
                {
                    u.BattlePass.Titulo = parts[1];
                    ServerPackets.ConsoleMsg(u.Conn, $"¡Obtuviste el título \"{parts[1]}\"!", 3);
                }
                break;

            case "PASAJE":
                ServerPackets.ConsoleMsg(u.Conn, "Obtuviste un pasaje libre del pase.", 3);
                break;

            default:
                Console.WriteLine($"[BattlePass] Token de recompensa desconocido: {token}");
                break;
        }
    }

    /// <summary>BOOST_EXP:%:min / BOOST_ORO:%:min — multiplicador personal temporal.</summary>
    private static void ApplyBoost(User u, string[] parts, bool esExp)
    {
        if (parts.Length < 3) return;
        if (!int.TryParse(parts[1], out int pct) || !int.TryParse(parts[2], out int min)) return;
        double mult = 1.0 + pct / 100.0;
        long until = Environment.TickCount64 / 1000 + (long)min * 60;
        if (esExp) { u.ExpBoostMult = mult; u.ExpBoostUntil = until; }
        else { u.OroBoostMult = mult; u.OroBoostUntil = until; }
        ServerPackets.ConsoleMsg(u.Conn,
            $"¡Boost de {(esExp ? "experiencia" : "oro")} +{pct}% activado por {min} minutos!", 3);
    }

    /// <summary>Nombre del objeto (obj.dat) o un fallback si no existe. Obj es struct, sin null-check.</summary>
    private static string ObjName(int idx, string fallback)
    {
        string n = ObjData.Get(idx).Name;
        return string.IsNullOrEmpty(n) ? fallback : n;
    }

    /// <summary>Multiplicador de exp personal vigente (1.0 si venció o no hay). Lo usa CalcularDarExp.</summary>
    public static double ExpMult(User u)
    {
        if (u == null) return 1.0;
        if (u.ExpBoostUntil > 0 && Environment.TickCount64 / 1000 < u.ExpBoostUntil) return u.ExpBoostMult;
        return 1.0;
    }

    /// <summary>Multiplicador de oro personal vigente (1.0 si venció o no hay).</summary>
    public static double OroMult(User u)
    {
        if (u == null) return 1.0;
        if (u.OroBoostUntil > 0 && Environment.TickCount64 / 1000 < u.OroBoostUntil) return u.OroBoostMult;
        return 1.0;
    }

    // ============================================================
    //  Envío del estado completo al cliente
    // ============================================================
    private static void SendInfo(User u)
    {
        if (u?.Conn == null || u.BattlePass == null || _season == null) return;
        ServerPackets.BattlePassInfo(u.Conn, _season, u.BattlePass, DescribeLevels(), ComoGanarPuntos(), DescribeMisiones(u.BattlePass));
    }

    /// <summary>Misiones con el progreso del jugador, para pintar el indicador de misiones.</summary>
    private static List<(string desc, int actual, int objetivo, bool completada, int puntos)> DescribeMisiones(Progress prog)
    {
        var list = new List<(string, int, int, bool, int)>();
        foreach (var m in _season.Misiones)
        {
            bool comp = prog.MisionesCompletadas.Contains(m.Id);
            int actual = comp ? m.Objetivo : prog.MisionProgreso.GetValueOrDefault(m.Id, 0);
            list.Add((m.Desc, actual, m.Objetivo, comp, m.Puntos));
        }
        return list;
    }

    /// <summary>Texto que explica al jugador CÓMO ganar puntos de pase (según [PUNTOS] del .ini).
    /// Se arma dinámicamente para que siempre coincida con la config real.</summary>
    private static string ComoGanarPuntos()
    {
        var fuentes = new List<string>();
        // NPCs por dificultad (solo tiers que dan puntos).
        var npc = new List<string>();
        if (_season.NpcDebil > 0) npc.Add($"Débiles +{_season.NpcDebil}");
        if (_season.NpcNormal > 0) npc.Add($"Normales +{_season.NpcNormal}");
        if (_season.NpcFuerte > 0) npc.Add($"Fuertes +{_season.NpcFuerte}");
        if (_season.NpcElite > 0) npc.Add($"Élite +{_season.NpcElite}");
        if (_season.NpcBoss > 0) npc.Add($"Jefes +{_season.NpcBoss}");
        if (_season.NpcRaid > 0) npc.Add($"Raid +{_season.NpcRaid}");
        if (npc.Count > 0)
            fuentes.Add("Matá criaturas fuertes: " + string.Join(", ", npc) + ".");
        if (_season.PorPvp > 0) fuentes.Add($"Ganá combates PvP: +{_season.PorPvp}.");
        if (_season.PorSubirNivel > 0) fuentes.Add($"Subí de nivel: +{_season.PorSubirNivel}.");
        if (_season.PorTrabajo > 0) fuentes.Add($"Trabajá (pesca/minería/tala): +{_season.PorTrabajo}.");
        if (_season.PorEvento > 0) fuentes.Add($"Participá de eventos: +{_season.PorEvento}.");
        if (fuentes.Count == 0) return "Ganá puntos jugando para subir de nivel el pase.";
        return string.Join("  ", fuentes) + " Las criaturas débiles no otorgan puntos.";
    }

    /// <summary>Descripciones legibles free/premium por nivel para que la UI las pinte sin tener obj.dat.</summary>
    private static List<(int level, string free, string prem)> DescribeLevels()
    {
        var list = new List<(int, string, string)>();
        for (int lvl = 1; lvl <= _season.NivelesMax; lvl++)
        {
            if (!_season.Niveles.TryGetValue(lvl, out var rw)) { list.Add((lvl, "", "")); continue; }
            list.Add((lvl, DescribeTokens(rw.Free), DescribeTokens(rw.Premium)));
        }
        return list;
    }

    private static string DescribeTokens(List<string> tokens)
    {
        var partsOut = new List<string>();
        foreach (var t in tokens)
        {
            var p = t.Split(':');
            string tipo = p[0].Trim().ToUpperInvariant();
            switch (tipo)
            {
                case "ORO": partsOut.Add($"{p.ElementAtOrDefault(1)} monedas de oro"); break;
                case "OBJ":
                    {
                        string nm = short.TryParse(p.ElementAtOrDefault(1), out short oi) ? (ObjName(oi, "obj " + oi)) : "obj";
                        partsOut.Add($"{nm} (x{p.ElementAtOrDefault(2) ?? "1"})");
                        break;
                    }
                case "AURA_ITEM":
                case "MOUNT":
                    {
                        string nm = short.TryParse(p.ElementAtOrDefault(1), out short oi) ? (ObjName(oi, "obj " + oi)) : "cosmético";
                        partsOut.Add(nm);
                        break;
                    }
                case "BOOST_EXP": partsOut.Add($"Boost EXP +{p.ElementAtOrDefault(1)}% ({p.ElementAtOrDefault(2)}min)"); break;
                case "BOOST_ORO": partsOut.Add($"Boost Oro +{p.ElementAtOrDefault(1)}% ({p.ElementAtOrDefault(2)}min)"); break;
                case "PART": partsOut.Add("Partícula cosmética"); break;
                case "TITULO": partsOut.Add($"Título \"{p.ElementAtOrDefault(1)}\""); break;
                case "PASAJE": partsOut.Add("Pasaje libre"); break;
            }
        }
        return string.Join(", ", partsOut);
    }
}
