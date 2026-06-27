using System.Linq;
using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sistema de clanes — núcleo (modGuilds.bas + clsClan.cls). Cubre: crear clan, solicitar
/// ingreso, aceptar/rechazar aspirantes, mensaje de clan, salir, expulsar e info básica.
/// Relaciones (guerra/paz/alianza), elecciones, website y codex completos y persistidos.
///
/// Persistencia 1:1 con VB6 (carpeta GUILDS/):
///   guildsinfo.inf          → [INIT] nroGuilds; [GUILDi] GUILDNAME/Founder/Leader/Alineacion/Desc/URL/GuildNews/Codex1..8
///   &lt;Nombre&gt;-members.mem     → [INIT] NroMembers; [Members] Member1..N
///   &lt;Nombre&gt;-solicitudes.sol → [INIT] CantSolicitudes; [Solicitudes] Sol1..N
///   &lt;Nombre&gt;-relaciones.rel  → [RELACIONES] &lt;nClan&gt;=G|A|P (guerra/aliados/paz)
///   &lt;Nombre&gt;-propositions.pro→ [&lt;nClan&gt;] Tipo=P|A, Pendiente=1|0 (propuesta recibida)
/// La pertenencia del jugador vive en su .chr [GUILD] GUILDINDEX (ya cargado por CharLoader).
/// </summary>
public sealed class Guild
{
    public int Number;
    public string Name = "";
    public string Founder = "";
    public string Leader = "";
    public string Alineacion = "Neutral";
    public string Desc = "";
    public string URL = "";
    public string GuildNews = "";
    public List<string> Codex = new();
    public List<string> Members = new();     // nombres en MAYÚSCULA
    public List<string> Aspirantes = new();  // solicitudes de ingreso (MAYÚSCULA)
    public string FechaCreacion = "";
    // Relaciones entre clanes (nombres de clan).
    public List<string> Enemigos = new();           // clanes con guerra declarada
    public List<string> Aliados = new();             // clanes aliados
    public List<string> PropuestasPaz = new();       // clanes que nos ofrecieron paz
    public List<string> PropuestasAlianza = new();   // clanes que nos ofrecieron alianza
    // Elecciones.
    public bool EnElecciones;
    public Dictionary<string, int> Votos = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> YaVotaron = new(StringComparer.OrdinalIgnoreCase);
}

public static class GuildManager
{
    // Requisitos para fundar (PuedeFundarUnClan): nivel 40, Liderazgo 90, 4 gemas.
    private const int NIVEL_MIN = 40, LIDERAZGO_MIN = 90;
    private const byte SKILL_LIDERAZGO = 17; // eSkill.Liderazgo
    private const short GEMA_LUNAR = 406, GEMA_NARANJA = 408, GEMA_GRIS = 409, GEMA_DORADA = 410;
    public const int MAX_GUILDS = 1000;
    private const int MAX_CODEX = 8; // CANTIDADMAXIMACODEX (modGuilds.bas)

    private static readonly Dictionary<int, Guild> _byNumber = new();
    private static readonly Dictionary<string, Guild> _byName = new(StringComparer.OrdinalIgnoreCase);
    private static int _count;
    private static bool _loaded;

    private static string GuildsPath()
    {
        string p = string.IsNullOrEmpty(DataPaths.Root)
            ? Path.Combine(AppContext.BaseDirectory, "GUILDS")
            : DataPaths.Sub("GUILDS");
        Directory.CreateDirectory(p);
        return p;
    }
    private static string InfoFile() => Path.Combine(GuildsPath(), "guildsinfo.inf");
    private static string MembersFile(string name) => Path.Combine(GuildsPath(), name + "-members.mem");
    private static string SolicitudesFile(string name) => Path.Combine(GuildsPath(), name + "-solicitudes.sol");
    private static string RelacionesFile(string name) => Path.Combine(GuildsPath(), name + "-relaciones.rel");
    private static string PropuestasFile(string name) => Path.Combine(GuildsPath(), name + "-propositions.pro");

    public static Guild GetByNumber(int n) { EnsureLoaded(); return _byNumber.TryGetValue(n, out var g) ? g : null; }
    public static Guild GetByName(string name) { EnsureLoaded(); return _byName.TryGetValue(name.Trim(), out var g) ? g : null; }

    /// <summary>Sufijo de clan para el nombre mostrado: " &lt;Clan&gt;" si pertenece a uno, "" si no.
    /// 1:1 con RefreshCharStatus (Modulo_UsUaRiOs.bas): el cliente lo separa por '&lt;'.</summary>
    public static string TagSufijo(User u)
    {
        if (u == null || u.GuildIndex <= 0) return "";
        var g = GetByNumber(u.GuildIndex);
        return g != null ? " <" + g.Name + ">" : "";
    }

    /// <summary>Nombre del personaje con el tag de clan adjunto (lo que ve el cliente sobre la cabeza).</summary>
    public static string NombreConTag(User u) => u.Name + TagSufijo(u);

    /// <summary>RefreshCharStatus (Modulo_UsUaRiOs.bas): difunde UpdateTagAndStatus con el nombre+tag
    /// a todos los del mapa, para que el tag de clan se vea sin necesidad de reloguear.</summary>
    public static void RefreshCharStatus(User u)
    {
        if (u == null || u.Char.CharIndex <= 0) return;
        string tag = NombreConTag(u);
        byte status = LoginFlow.NickStatus(u);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.UpdateTagAndStatus(o.Conn, u.Char.CharIndex, tag, status, u.Char.Donador);
        }
    }

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        var ini = new IniFile(InfoFile());
        _count = ini.Loaded ? ini.GetInt("INIT", "nroGuilds") : 0;
        for (int i = 1; i <= _count; i++)
        {
            string sec = "GUILD" + i;
            var g = new Guild
            {
                Number = i,
                Name = ini.Get(sec, "GUILDNAME").Trim(),
                Founder = ini.Get(sec, "Founder").Trim(),
                Leader = ini.Get(sec, "Leader").Trim(),
                Alineacion = ini.Get(sec, "Alineacion").Trim(),
                Desc = ini.Get(sec, "Desc"),
                URL = ini.Get(sec, "URL"),
                GuildNews = ini.Get(sec, "GuildNews"),
            };
            if (string.IsNullOrEmpty(g.Name)) continue;
            // Codex (clsClan.SetCodex/GetCodex: GUILDi Codex1..8, máx CANTIDADMAXIMACODEX=8).
            for (int c = 1; c <= MAX_CODEX; c++)
            {
                string cx = ini.Get(sec, "Codex" + c);
                if (!string.IsNullOrEmpty(cx)) g.Codex.Add(cx);
            }
            LoadMembers(g);
            LoadAspirantes(g);
            _byNumber[i] = g;
            _byName[g.Name] = g;
        }

        // Segunda pasada: relaciones y propuestas (necesitan todos los clanes ya cargados
        // para resolver número→nombre). Formato VB6: <Nombre>-relaciones.rel / -propositions.pro.
        foreach (var g in _byNumber.Values)
        {
            var rel = new IniFile(RelacionesFile(g.Name));
            var pro = new IniFile(PropuestasFile(g.Name));
            for (int n = 1; n <= _count; n++)
            {
                if (n == g.Number) continue;
                var otro = GetByNumber(n);
                if (otro == null) continue;
                // Relación persistida ("G"=guerra, "A"=aliados, vacío/"P"=paz).
                switch (rel.Get("RELACIONES", n.ToString()).Trim().ToUpperInvariant())
                {
                    case "G": AgregarUnico(g.Enemigos, otro.Name); break;
                    case "A": AgregarUnico(g.Aliados, otro.Name); break;
                }
                // Propuesta pendiente que el clan n nos hizo a nosotros.
                if (pro.Get(n.ToString(), "Pendiente").Trim() == "1")
                {
                    if (pro.Get(n.ToString(), "Tipo").Trim().ToUpperInvariant() == "A")
                        AgregarUnico(g.PropuestasAlianza, otro.Name);
                    else
                        AgregarUnico(g.PropuestasPaz, otro.Name);
                }
            }
        }
        Console.WriteLine($"[GuildManager] {_byNumber.Count} clan(es) cargado(s).");
    }

    private static void LoadMembers(Guild g)
    {
        var ini = new IniFile(MembersFile(g.Name));
        if (!ini.Loaded) return;
        int n = ini.GetInt("INIT", "NroMembers");
        for (int i = 1; i <= n; i++)
        {
            string m = ini.Get("Members", "Member" + i).Trim();
            if (!string.IsNullOrEmpty(m)) g.Members.Add(m.ToUpperInvariant());
        }
    }

    private static void LoadAspirantes(Guild g)
    {
        var ini = new IniFile(SolicitudesFile(g.Name));
        if (!ini.Loaded) return;
        int n = ini.GetInt("INIT", "CantSolicitudes");
        for (int i = 1; i <= n; i++)
        {
            string a = ini.Get("Solicitudes", "Sol" + i).Trim();
            if (!string.IsNullOrEmpty(a)) g.Aspirantes.Add(a.ToUpperInvariant());
        }
    }

    // === Persistencia ===
    private static void SaveInfo()
    {
        var doc = new IniDocument(InfoFile());
        doc.Set("INIT", "nroGuilds", _count.ToString());
        foreach (var g in _byNumber.Values)
        {
            string sec = "GUILD" + g.Number;
            doc.Set(sec, "GUILDNAME", g.Name);
            doc.Set(sec, "Founder", g.Founder);
            doc.Set(sec, "Leader", g.Leader);
            doc.Set(sec, "Alineacion", g.Alineacion);
            doc.Set(sec, "Desc", g.Desc);
            doc.Set(sec, "URL", g.URL);
            doc.Set(sec, "GuildNews", g.GuildNews);
            // Codex (GUILDi Codex1..8). Se reescriben las 8 ranuras: las sobrantes en blanco.
            for (int c = 1; c <= MAX_CODEX; c++)
                doc.Set(sec, "Codex" + c, c <= g.Codex.Count ? g.Codex[c - 1] : "");
        }
        try { doc.Save(InfoFile()); } catch (Exception ex) { Console.WriteLine($"[GuildManager] Error guardando info: {ex.Message}"); }
    }

    /// <summary>Persiste las relaciones del clan (clsClan.SetRelacion → &lt;Nombre&gt;-relaciones.rel,
    /// [RELACIONES] keyed por número de clan: "G"=guerra, "A"=aliados, "P"=paz por defecto).</summary>
    private static void SaveRelaciones(Guild g)
    {
        var doc = new IniDocument(RelacionesFile(g.Name));
        for (int n = 1; n <= _count; n++)
        {
            if (n == g.Number) continue;
            var otro = GetByNumber(n);
            string val = otro != null && g.Enemigos.Contains(otro.Name, StringComparer.OrdinalIgnoreCase) ? "G"
                       : otro != null && g.Aliados.Contains(otro.Name, StringComparer.OrdinalIgnoreCase) ? "A"
                       : "P";
            doc.Set("RELACIONES", n.ToString(), val);
        }
        try { doc.Save(RelacionesFile(g.Name)); } catch { }
    }

    /// <summary>Persiste las propuestas pendientes que recibió el clan (clsClan.SetPropuesta/AnularPropuestas
    /// → &lt;Nombre&gt;-propositions.pro, sección = número del clan proponente: Tipo "P"/"A", Pendiente "1"/"0").</summary>
    private static void SaveProposals(Guild g)
    {
        var doc = new IniDocument(PropuestasFile(g.Name));
        for (int n = 1; n <= _count; n++)
        {
            if (n == g.Number) continue;
            var otro = GetByNumber(n);
            if (otro == null) { doc.Set(n.ToString(), "Pendiente", "0"); continue; }
            bool alianza = g.PropuestasAlianza.Contains(otro.Name, StringComparer.OrdinalIgnoreCase);
            bool paz = g.PropuestasPaz.Contains(otro.Name, StringComparer.OrdinalIgnoreCase);
            doc.Set(n.ToString(), "Tipo", alianza ? "A" : "P");
            doc.Set(n.ToString(), "Pendiente", (alianza || paz) ? "1" : "0");
        }
        try { doc.Save(PropuestasFile(g.Name)); } catch { }
    }

    private static void SaveMembers(Guild g)
    {
        var doc = new IniDocument(MembersFile(g.Name));
        doc.Set("INIT", "NroMembers", g.Members.Count.ToString());
        for (int i = 0; i < g.Members.Count; i++)
            doc.Set("Members", "Member" + (i + 1), g.Members[i]);
        try { doc.Save(MembersFile(g.Name)); } catch { }
    }

    private static void SaveAspirantes(Guild g)
    {
        var doc = new IniDocument(SolicitudesFile(g.Name));
        doc.Set("INIT", "CantSolicitudes", g.Aspirantes.Count.ToString());
        for (int i = 0; i < g.Aspirantes.Count; i++)
            doc.Set("Solicitudes", "Sol" + (i + 1), g.Aspirantes[i]);
        try { doc.Save(SolicitudesFile(g.Name)); } catch { }
    }

    /// <summary>
    /// CloseGuild (Protocol.bas:19537) 1:1. El líder disuelve su clan (debe ser el único miembro):
    /// borra los archivos del clan, lo quita de memoria/guildsinfo, limpia el GUILD del .chr del líder.
    /// Las validaciones de muerto/zona segura las hace el caller. error = motivo del fallo.
    /// </summary>
    public static bool CerrarClan(User u, out string error)
    {
        EnsureLoaded();
        error = "";
        if (u.GuildIndex == 0) { error = "No perteneces a ningún clan!"; return false; }
        var g = GetByNumber(u.GuildIndex);
        if (g == null) { error = "No perteneces a ningún clan!"; return false; }
        if (!string.Equals(g.Leader, u.Name, StringComparison.OrdinalIgnoreCase)) { error = "¡No eres el líder del clan!"; return false; }
        if (g.Members.Count > 1) { error = "Debes echar a todos los miembros del clan para cerrarlo!"; return false; }

        string nombre = g.Name;
        try { File.Delete(MembersFile(nombre)); } catch { }
        try { File.Delete(SolicitudesFile(nombre)); } catch { }
        _byNumber.Remove(g.Number);
        _byName.Remove(nombre);
        SaveInfo();
        SetGuildIndexChar(u.Name, 0);
        u.GuildIndex = 0;
        RefreshCharStatus(u); // quitar el tag <Clan> de su cabeza

        // Aviso global (VB6: "Servidor> X ha cerrado el clan llamado: N").
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null)
                ServerPackets.ConsoleMsg(o.Conn, $"Servidor> {u.Name} ha cerrado el clan llamado: {nombre}", 1);
        }
        return true;
    }

    // ============================================================
    //  Crear clan (CrearNuevoClan + PuedeFundarUnClan)
    // ============================================================
    /// <summary>
    /// PuedeFundarUnClan (modGuilds): condiciones para ABRIR el formulario de fundación
    /// (nivel 40, Liderazgo 90, las 4 gemas, sin clan previo). No crea nada.
    /// </summary>
    public static bool PuedeFundarUnClan(User u, out string error)
    {
        error = "";
        if (u.GuildIndex > 0) { error = "Ya perteneces a un clan, no puedes fundar otro."; return false; }
        if (u.Stats.ELV < NIVEL_MIN || u.Stats.UserSkills[SKILL_LIDERAZGO] < LIDERAZGO_MIN)
        { error = "Para fundar un clan debes ser nivel 40 y tener 90 en Liderazgo."; return false; }
        if (!TieneGema(u, GEMA_LUNAR)) { error = "Necesitas una Gema Lunar para fundar un clan."; return false; }
        if (!TieneGema(u, GEMA_NARANJA)) { error = "Necesitas una Gema Naranja para fundar un clan."; return false; }
        if (!TieneGema(u, GEMA_DORADA)) { error = "Necesitas una Gema Dorada para fundar un clan."; return false; }
        if (!TieneGema(u, GEMA_GRIS)) { error = "Necesitas una Gema Gris para fundar un clan."; return false; }
        return true;
    }

    public static bool CrearNuevoClan(User u, string desc, string guildName, string url, string[] codex, string alineacion, out string error)
    {
        EnsureLoaded();
        error = "";
        guildName = guildName.Trim();

        if (u.GuildIndex > 0) { error = "Ya perteneces a un clan, no puedes fundar otro."; return false; }
        if (u.Stats.ELV < NIVEL_MIN || u.Stats.UserSkills[SKILL_LIDERAZGO] < LIDERAZGO_MIN)
        { error = "Para fundar un clan debes ser nivel 40 y tener 90 en Liderazgo."; return false; }
        if (!TieneGema(u, GEMA_LUNAR)) { error = "Necesitas una Gema Lunar para fundar un clan."; return false; }
        if (!TieneGema(u, GEMA_NARANJA)) { error = "Necesitas una Gema Naranja para fundar un clan."; return false; }
        if (!TieneGema(u, GEMA_DORADA)) { error = "Necesitas una Gema Dorada para fundar un clan."; return false; }
        if (!TieneGema(u, GEMA_GRIS)) { error = "Necesitas una Gema Gris para fundar un clan."; return false; }

        if (string.IsNullOrEmpty(guildName) || !GuildNameValido(guildName)) { error = "Nombre de clan inválido."; return false; }
        if (_byName.ContainsKey(guildName)) { error = "Ya existe un clan con ese nombre."; return false; }
        if (_count >= MAX_GUILDS) { error = "No hay más slots para fundar clanes."; return false; }

        _count++;
        var g = new Guild
        {
            Number = _count, Name = guildName, Founder = u.Name, Leader = u.Name,
            Alineacion = alineacion, Desc = desc, URL = url,
            GuildNews = "Clan creado con alineación: " + alineacion,
            Codex = new List<string>(codex ?? Array.Empty<string>()),
            FechaCreacion = DateTime.Now.ToString("dd/MM/yyyy"),
        };
        g.Members.Add(u.Name.ToUpperInvariant());
        _byNumber[g.Number] = g;
        _byName[g.Name] = g;

        // Quitar las gemas del inventario (costo de fundar).
        QuitarGema(u, GEMA_LUNAR); QuitarGema(u, GEMA_NARANJA);
        QuitarGema(u, GEMA_DORADA); QuitarGema(u, GEMA_GRIS);

        u.GuildIndex = g.Number;
        SaveInfo(); SaveMembers(g);

        ServerPackets.ConsoleMsg(u.Conn, $"Has fundado el clan {g.Name}.", 5);
        // Fanfarria al fundar el clan (sonido 3) al fundador.
        ServerPackets.PlayWave(u.Conn, Sounds.FUNDAR_CLAN, (byte)u.Pos.X, (byte)u.Pos.Y);
        // Mostrar el tag <Clan> sobre el personaje sin necesidad de reloguear.
        RefreshCharStatus(u);
        return true;
    }

    // ============================================================
    //  Ingreso de miembros (solicitud / aceptar / rechazar)
    // ============================================================
    public static void SolicitarIngreso(User u, string guildName, string solicitud)
    {
        EnsureLoaded();
        if (u.GuildIndex > 0) { ServerPackets.ConsoleMsg(u.Conn, "Ya perteneces a un clan.", 1); return; }
        var g = GetByName(guildName);
        if (g == null) { ServerPackets.ConsoleMsg(u.Conn, "No existe ese clan.", 1); return; }
        string up = u.Name.ToUpperInvariant();
        if (g.Aspirantes.Contains(up)) { ServerPackets.ConsoleMsg(u.Conn, "Ya tienes una solicitud pendiente en ese clan.", 1); return; }
        g.Aspirantes.Add(up);
        SaveAspirantes(g);
        ServerPackets.ConsoleMsg(u.Conn, $"Has solicitado ingreso al clan {g.Name}.", 5);
        // Avisar al líder si está online.
        NotificarLider(g, $"{u.Name} solicitó ingreso al clan.");
    }

    public static void AceptarAspirante(User lider, string nombre)
    {
        var g = GuildDe(lider);
        if (g == null || !EsLider(lider, g)) return;
        string up = nombre.Trim().ToUpperInvariant();
        if (!g.Aspirantes.Remove(up)) { ServerPackets.ConsoleMsg(lider.Conn, "Esa persona no solicitó ingreso.", 1); return; }
        SaveAspirantes(g);
        g.Members.Add(up);
        SaveMembers(g);
        // Persistir GUILDINDEX en el .chr del aceptado y, si está online, en memoria.
        SetGuildIndexChar(nombre, g.Number);
        var nuevo = UserListManager.GetByName(nombre);
        if (nuevo != null) { nuevo.GuildIndex = g.Number; ServerPackets.ConsoleMsg(nuevo.Conn, $"Has sido aceptado en el clan {g.Name}.", 5); RefreshCharStatus(nuevo); }
        ServerPackets.ConsoleMsg(lider.Conn, $"Aceptaste a {nombre} en el clan.", 5);
    }

    public static void RechazarAspirante(User lider, string nombre)
    {
        var g = GuildDe(lider);
        if (g == null || !EsLider(lider, g)) return;
        string up = nombre.Trim().ToUpperInvariant();
        if (g.Aspirantes.Remove(up))
        {
            SaveAspirantes(g);
            ServerPackets.ConsoleMsg(lider.Conn, $"Rechazaste la solicitud de {nombre}.", 5);
            var asp = UserListManager.GetByName(nombre);
            if (asp != null) ServerPackets.ConsoleMsg(asp.Conn, $"Tu solicitud al clan {g.Name} fue rechazada.", 1);
        }
    }

    // ============================================================
    //  Mensaje de clan / salir / expulsar
    // ============================================================
    public static void MensajeClan(User u, string msg)
    {
        var g = GuildDe(u);
        if (g == null) { ServerPackets.ConsoleMsg(u.Conn, "No perteneces a ningún clan.", 1); return; }
        if (string.IsNullOrWhiteSpace(msg)) return;
        string texto = $"{u.Name}> {msg}";
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.GuildIndex == g.Number)
                ServerPackets.GuildChat(o.Conn, texto);
        }
    }

    public static void SalirDeClan(User u)
    {
        var g = GuildDe(u);
        if (g == null) { ServerPackets.ConsoleMsg(u.Conn, "No perteneces a ningún clan.", 1); return; }
        string up = u.Name.ToUpperInvariant();
        g.Members.Remove(up);
        SaveMembers(g);
        u.GuildIndex = 0;
        ServerPackets.ConsoleMsg(u.Conn, $"Has abandonado el clan {g.Name}.", 5);
        RefreshCharStatus(u); // quitar el tag <Clan> de su cabeza
        NotificarLider(g, $"{u.Name} abandonó el clan.");
    }

    public static void ExpulsarMiembro(User lider, string nombre)
    {
        var g = GuildDe(lider);
        if (g == null || !EsLider(lider, g)) return;
        string up = nombre.Trim().ToUpperInvariant();
        if (up == g.Founder.ToUpperInvariant()) { ServerPackets.ConsoleMsg(lider.Conn, "No puedes expulsar al fundador.", 1); return; }
        if (!g.Members.Remove(up)) { ServerPackets.ConsoleMsg(lider.Conn, "Esa persona no es del clan.", 1); return; }
        SaveMembers(g);
        SetGuildIndexChar(nombre, 0);
        var ech = UserListManager.GetByName(nombre);
        if (ech != null) { ech.GuildIndex = 0; ServerPackets.ConsoleMsg(ech.Conn, $"Has sido expulsado del clan {g.Name}.", 1); RefreshCharStatus(ech); }
        ServerPackets.ConsoleMsg(lider.Conn, $"Expulsaste a {nombre} del clan.", 5);
    }

    // ============================================================
    //  Info para el panel del clan (SendGuildLeaderInfo)
    // ============================================================
    public static void EnviarLeaderInfo(User u)
    {
        // 1:1 SendGuildLeaderInfo (modGuilds.bas:1314): la respuesta depende de la pertenencia/rango.
        var g = GuildDe(u);

        // No pertenece a ningún clan (Gi <= 0) → mandar la lista de TODOS los clanes (browser),
        // para poder explorar y solicitar ingreso. (VB6: WriteGuildList)
        if (g == null) { EnviarGuildList(u); return; }

        // Miembro pero NO líder → info de miembro (lista de clanes + miembros del propio).
        // (VB6: If Not m_EsGuildLeader → WriteGuildMemberInfo)
        if (!string.Equals(g.Leader.Trim(), u.Name.Trim(), StringComparison.OrdinalIgnoreCase))
        { EnviarMemberInfo(u); return; }

        // Líder → info completa de administración. (VB6: WriteGuildLeaderInfo)
        string members = string.Join("|", g.Members);
        string aspirantes = string.Join("|", g.Aspirantes);
        ServerPackets.GuildLeaderInfo(u.Conn, members, (short)g.Members.Count,
            g.Name, g.Founder, aspirantes, g.GuildNews);
    }

    /// <summary>Lista de todos los clanes (browser): pares nombre/líder separados por '\0'.</summary>
    public static void EnviarGuildList(User u)
    {
        EnsureLoaded();
        var sb = new System.Text.StringBuilder();
        foreach (var g in _byNumber.Values)
        {
            if (sb.Length > 0) sb.Append('\0');
            sb.Append(g.Name).Append('\0').Append(g.Leader);
        }
        ServerPackets.GuildList(u.Conn, sb.ToString());
    }

    /// <summary>GuildMemberInfo: lista de todos los clanes + miembros del clan propio (separados '|').</summary>
    public static void EnviarMemberInfo(User u)
    {
        EnsureLoaded();
        string allGuilds = string.Join("|", _byNumber.Values.Select(g => g.Name));
        var mine = GuildDe(u);
        string members = mine != null ? string.Join("|", mine.Members) : "";
        ServerPackets.GuildMemberInfo(u.Conn, allGuilds, members);
    }

    /// <summary>GuildNews del clan propio + listas de guerra/alianza.</summary>
    public static void EnviarNews(User u)
    {
        var g = GuildDe(u);
        if (g == null) { ServerPackets.GuildNews(u.Conn, "No perteneces a ningún clan.", "", ""); return; }
        ServerPackets.GuildNews(u.Conn, g.GuildNews, string.Join(", ", g.Enemigos), string.Join(", ", g.Aliados));
    }

    /// <summary>Detalles de un clan (panel de info al elegir un clan de la lista).</summary>
    public static void EnviarDetails(User u, string guildName)
    {
        var g = GetByName(guildName);
        if (g == null) { ServerPackets.ConsoleMsg(u.Conn, "No existe ese clan.", 1); return; }
        ServerPackets.GuildDetails(u.Conn, g.Name, g.Founder, g.FechaCreacion, g.Desc, g.URL,
            (short)g.Members.Count, string.Join(", ", g.Aliados), (short)g.Enemigos.Count,
            (short)g.PropuestasAlianza.Count, (short)g.PropuestasPaz.Count);
    }

    /// <summary>Lista de miembros del clan que están online (por consola).</summary>
    public static void EnviarOnline(User u)
    {
        var g = GuildDe(u);
        if (g == null) { ServerPackets.ConsoleMsg(u.Conn, "No perteneces a ningún clan.", 1); return; }
        var online = new List<string>();
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.GuildIndex == g.Number) online.Add(o.Name);
        }
        ServerPackets.ConsoleMsg(u.Conn, $"Miembros online ({online.Count}): {string.Join(", ", online)}", 5);
    }

    /// <summary>Info de un aspirante (al revisar una solicitud de ingreso).</summary>
    public static void EnviarJoinerInfo(User u, string nombre)
    {
        var asp = UserListManager.GetByName(nombre);
        string info = asp != null
            ? $"{nombre} — Nivel {asp.Stats.ELV}, Clase {asp.Clase}"
            : $"{nombre} (offline)";
        ServerPackets.ConsoleMsg(u.Conn, info, 5);
    }

    public static void ActualizarNews(User u, string news)
    {
        var g = GuildDe(u);
        if (g == null || !EsLider(u, g)) return;
        g.GuildNews = news;
        SaveInfo();
        ServerPackets.ConsoleMsg(u.Conn, "Noticias del clan actualizadas.", 5);
    }

    public static void ActualizarWebsite(User u, string url)
    {
        var g = GuildDe(u);
        if (g == null || !EsLider(u, g)) return;
        g.URL = url;
        SaveInfo();
        ServerPackets.ConsoleMsg(u.Conn, "Web del clan actualizada.", 5);
    }

    public static void ActualizarCodex(User u, string[] codex)
    {
        var g = GuildDe(u);
        if (g == null || !EsLider(u, g)) return;
        g.Codex = new List<string>(codex);
        SaveInfo();
        ServerPackets.ConsoleMsg(u.Conn, "Códex del clan actualizado.", 5);
    }

    // ============================================================
    //  Elecciones de líder (v_AbrirElecciones / v_UsuarioVota)
    // ============================================================
    public static void AbrirElecciones(User u)
    {
        var g = GuildDe(u);
        if (g == null || !EsLider(u, g)) return;
        if (g.EnElecciones) { ServerPackets.ConsoleMsg(u.Conn, "Ya hay elecciones abiertas.", 1); return; }
        g.EnElecciones = true;
        g.Votos.Clear();
        g.YaVotaron.Clear();
        foreach (var m in g.Members)
        {
            var mu = UserListManager.GetByName(m);
            if (mu?.flags.UserLogged == true) ServerPackets.ConsoleMsg(mu.Conn, "Se abrieron elecciones de líder. Usá el panel para votar.", 5);
        }
    }

    public static void Votar(User u, string candidato)
    {
        var g = GuildDe(u);
        if (g == null) { ServerPackets.ConsoleMsg(u.Conn, "No perteneces a ningún clan.", 1); return; }
        if (!g.EnElecciones) { ServerPackets.ConsoleMsg(u.Conn, "No hay elecciones abiertas.", 1); return; }
        string votante = u.Name.ToUpperInvariant();
        if (g.YaVotaron.Contains(votante)) { ServerPackets.ConsoleMsg(u.Conn, "Ya votaste.", 1); return; }
        string cand = candidato.Trim().ToUpperInvariant();
        if (!g.Members.Contains(cand)) { ServerPackets.ConsoleMsg(u.Conn, "Ese personaje no es del clan.", 1); return; }
        g.YaVotaron.Add(votante);
        g.Votos[cand] = g.Votos.GetValueOrDefault(cand) + 1;
        ServerPackets.ConsoleMsg(u.Conn, $"Votaste por {candidato}.", 5);

        // Si votaron todos los miembros, cerrar y proclamar al más votado.
        if (g.YaVotaron.Count >= g.Members.Count)
        {
            string ganador = g.Votos.OrderByDescending(kv => kv.Value).First().Key;
            g.Leader = ganador;
            g.EnElecciones = false;
            SaveInfo();
            foreach (var m in g.Members)
            {
                var mu = UserListManager.GetByName(m);
                if (mu?.flags.UserLogged == true) ServerPackets.ConsoleMsg(mu.Conn, $"{ganador} es el nuevo líder del clan.", 5);
            }
        }
    }

    // ============================================================
    //  Relaciones entre clanes (r_DeclararGuerra / propuestas)
    // ============================================================
    public static void DeclararGuerra(User u, string otroClan)
    {
        var g = GuildDe(u);
        if (g == null || !EsLider(u, g)) return;
        var enemigo = GetByName(otroClan);
        if (enemigo == null || enemigo.Number == g.Number) { ServerPackets.ConsoleMsg(u.Conn, "Clan inválido.", 1); return; }
        // AnularPropuestas en ambos sentidos (clsClan.AnularPropuestas) antes de fijar la guerra.
        g.PropuestasPaz.Remove(enemigo.Name); g.PropuestasAlianza.Remove(enemigo.Name);
        enemigo.PropuestasPaz.Remove(g.Name); enemigo.PropuestasAlianza.Remove(g.Name);
        AgregarUnico(g.Enemigos, enemigo.Name);
        AgregarUnico(enemigo.Enemigos, g.Name);
        g.Aliados.Remove(enemigo.Name); enemigo.Aliados.Remove(g.Name);
        SaveRelaciones(g); SaveRelaciones(enemigo);
        SaveProposals(g); SaveProposals(enemigo);
        AvisarClan(g, $"Tu clan declaró la guerra a {enemigo.Name}.");
        AvisarClan(enemigo, $"¡El clan {g.Name} te declaró la guerra!");
    }

    public static void OfrecerPaz(User u, string otroClan)    => Ofrecer(u, otroClan, paz: true);
    public static void OfrecerAlianza(User u, string otroClan) => Ofrecer(u, otroClan, paz: false);

    private static void Ofrecer(User u, string otroClan, bool paz)
    {
        var g = GuildDe(u);
        if (g == null || !EsLider(u, g)) return;
        var otro = GetByName(otroClan);
        if (otro == null || otro.Number == g.Number) { ServerPackets.ConsoleMsg(u.Conn, "Clan inválido.", 1); return; }
        AgregarUnico(paz ? otro.PropuestasPaz : otro.PropuestasAlianza, g.Name);
        SaveProposals(otro);
        ServerPackets.ConsoleMsg(u.Conn, $"Propuesta de {(paz ? "paz" : "alianza")} enviada a {otro.Name}.", 5);
        AvisarClan(otro, $"El clan {g.Name} te propone {(paz ? "paz" : "una alianza")}.");
    }

    public static void AceptarPaz(User u, string otroClan)     => Resolver(u, otroClan, paz: true,  aceptar: true);
    public static void RechazarPaz(User u, string otroClan)    => Resolver(u, otroClan, paz: true,  aceptar: false);
    public static void AceptarAlianza(User u, string otroClan) => Resolver(u, otroClan, paz: false, aceptar: true);
    public static void RechazarAlianza(User u, string otroClan)=> Resolver(u, otroClan, paz: false, aceptar: false);

    private static void Resolver(User u, string otroClan, bool paz, bool aceptar)
    {
        var g = GuildDe(u);
        if (g == null || !EsLider(u, g)) return;
        var otro = GetByName(otroClan);
        if (otro == null) return;
        var lista = paz ? g.PropuestasPaz : g.PropuestasAlianza;
        if (!lista.Remove(otro.Name)) { ServerPackets.ConsoleMsg(u.Conn, "No hay tal propuesta.", 1); return; }
        if (aceptar)
        {
            // Paz: dejar de ser enemigos. Alianza: además aliarse.
            g.Enemigos.Remove(otro.Name); otro.Enemigos.Remove(g.Name);
            if (!paz) { AgregarUnico(g.Aliados, otro.Name); AgregarUnico(otro.Aliados, g.Name); }
            SaveRelaciones(g); SaveRelaciones(otro);
            AvisarClan(g, $"Tu clan aceptó {(paz ? "la paz" : "la alianza")} con {otro.Name}.");
            AvisarClan(otro, $"El clan {g.Name} aceptó {(paz ? "la paz" : "la alianza")}.");
        }
        else
        {
            ServerPackets.ConsoleMsg(u.Conn, $"Rechazaste la propuesta de {otro.Name}.", 5);
            AvisarClan(otro, $"El clan {g.Name} rechazó tu propuesta.");
        }
        SaveProposals(g); // la propuesta se consumió (aceptada o rechazada)
    }

    public static void EnviarPropList(User u, bool paz)
    {
        var g = GuildDe(u);
        if (g == null) return;
        var lista = paz ? g.PropuestasPaz : g.PropuestasAlianza;
        ServerPackets.ConsoleMsg(u.Conn, $"Propuestas de {(paz ? "paz" : "alianza")}: {(lista.Count > 0 ? string.Join(", ", lista) : "ninguna")}", 5);
    }

    private static void AgregarUnico(List<string> lista, string v)
    { if (!lista.Contains(v, StringComparer.OrdinalIgnoreCase)) lista.Add(v); }

    private static void AvisarClan(Guild g, string msg)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.GuildIndex == g.Number)
                ServerPackets.ConsoleMsg(o.Conn, msg, 5);
        }
    }

    // ============================================================
    //  Helpers
    // ============================================================
    public static Guild GuildDe(User u) => u.GuildIndex > 0 ? GetByNumber(u.GuildIndex) : null;
    private static bool EsLider(User u, Guild g)
    {
        if (string.Equals(u.Name, g.Leader, StringComparison.OrdinalIgnoreCase)) return true;
        ServerPackets.ConsoleMsg(u.Conn, "No eres el líder del clan.", 1);
        return false;
    }

    private static void NotificarLider(Guild g, string msg)
    {
        var lider = UserListManager.GetByName(g.Leader);
        if (lider?.flags.UserLogged == true && lider.Conn != null)
            ServerPackets.ConsoleMsg(lider.Conn, msg, 5);
    }

    private static bool TieneGema(User u, short gema)
    {
        for (int i = 1; i <= Constants.MAX_INVENTORY_SLOTS; i++)
            if (u.Invent.Object[i].ObjIndex == gema && u.Invent.Object[i].Amount > 0) return true;
        return false;
    }

    private static void QuitarGema(User u, short gema)
    {
        for (int i = 1; i <= Constants.MAX_INVENTORY_SLOTS; i++)
        {
            if (u.Invent.Object[i].ObjIndex == gema && u.Invent.Object[i].Amount > 0)
            {
                u.Invent.Object[i].Amount--;
                if (u.Invent.Object[i].Amount <= 0)
                {
                    u.Invent.Object[i] = new UserObj();
                    if (u.Invent.NroItems > 0) u.Invent.NroItems--;
                }
                var o = u.Invent.Object[i];
                if (u.Conn != null) ServerPackets.ChangeInventorySlot(u.Conn, (byte)i, o.ObjIndex, o.Amount, o.Equipped);
                return;
            }
        }
    }

    /// <summary>Escribe GUILD/GUILDINDEX en el .chr del personaje (online u offline).</summary>
    private static void SetGuildIndexChar(string nombre, int guildIndex)
    {
        string file = Path.Combine(CharLoader.CharPath, nombre.ToUpperInvariant() + ".chr");
        if (!File.Exists(file)) return;
        var doc = new IniDocument(file);
        if (!doc.Loaded) return;
        doc.Set("GUILD", "GUILDINDEX", guildIndex.ToString());
        if (guildIndex > 0) doc.Set("GUILD", "AspiranteA", "0");
        try { doc.Save(file); } catch { }
    }

    private static bool GuildNameValido(string name)
    {
        if (name.Length is < 3 or > 30) return false;
        foreach (char c in name)
            if (!char.IsLetterOrDigit(c) && c != ' ') return false;
        return true;
    }
}
