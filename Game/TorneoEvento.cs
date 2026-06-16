using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sistema de torneos PvP por eliminación directa con COLA AUTOMÁTICA (matchmaking), modos
/// 1v1, 2v2 y 3v3. NO es 1:1 del VB6 (sistema nuevo, como Subastas/Reportes).
///
/// Flujo (todo desde la ventana del cliente, packet TORNEO_ACTION):
///   - El jugador entra a la cola de un modo. En 1v1 entra solo; en 2v2/3v3 SOLO el líder de un
///     grupo del tamaño justo (2 o 3) inscribe a todo el equipo.
///   - Cuando una cola junta AUTOSTART_TEAMS equipos arranca el torneo al instante; si junta al
///     menos 2 equipos y pasa WAIT_SECONDS sin completarse, arranca igual con los que haya.
///   - Solo corre UN torneo a la vez. Mientras corre, las colas siguen llenándose y el siguiente
///     torneo arranca solo al terminar el actual.
///   - Los combates se disputan en paralelo (hasta 3 arenas del mapa 859, trigger ZONAPELEA → sin
///     caída de ítems). Un equipo pierde cuando TODOS sus integrantes están muertos.
///   - El campeón recibe Puntos de Arena por integrante.
///
/// La ventana del cliente se refresca con el packet TORNEO_STATE (estado por usuario): el server lo
/// empuja en cada transición y el cliente además lo pide por polling mientras la ventana está abierta.
/// Cableado: Procesar() 1/seg (GameServer); OnUserDeath (Combat.UserDie); CheckDisconnect
/// (UserList.CloseUser); EntrarCola/SalirCola/SolicitarEstado desde PacketHandler.
/// </summary>
public static class TorneoEvento
{
    private const int ARENA_MAP = 859;
    private const int COUNTDOWN = 5;         // segundos congelados en las esquinas antes de pelear
    private const int MATCH_TIMEOUT = 180;   // segundos máximos por combate antes de resolver por HP
    private const int AUTOSTART_TEAMS = 4;   // equipos que disparan el arranque inmediato
    private const int WAIT_SECONDS = 10;     // espera con ≥2 equipos antes de arrancar (avisada en consola)
    private const byte FONT_INFO = 3, FONT_INFOBOLD = 4, FONT_SERVER = 8, FONT_FIGHT = 5;

    // Recompensa de Puntos de Arena al campeón, por integrante, según modo (índices 1/2/3).
    private static readonly int[] _reward = { 0, 100, 150, 200 };

    private sealed class Equipo
    {
        public List<int> Members = new();    // userIndex de los integrantes
        public string Name = "";             // PJ en 1v1, "PJ y N más" en equipos
        public bool Eliminated;
    }

    private sealed class Combate
    {
        public Equipo A = null!, B = null!;
        public int Slot;                     // arena ocupada (-1 = en cola)
        public byte State;                   // 1=cuenta regresiva, 2=peleando
        public int Time;
    }

    private static readonly (int ax, int ay, int bx, int by)[] _arenas =
    {
        (13, 13, 29, 29),
        (42, 13, 58, 29),
        (71, 13, 87, 29),
    };
    private static readonly (int dx, int dy)[] _offsets = { (0, 0), (1, 0), (0, 1) };

    // Colas de matchmaking por modo (índices 1/2/3). _queueCd = cuenta regresiva de arranque (-1 inactiva).
    private static readonly List<Equipo>[] _queues = { new(), new(), new(), new() };
    private static readonly int[] _queueCd = { -1, -1, -1, -1 };

    // Posición original de cada participante (antes de entrar a la arena), para devolverlo al salir.
    private static readonly Dictionary<int, WorldPos> _origin = new();

    private static bool _running;
    private static int _mode;
    private static readonly List<Equipo> _equipos = new();
    private static readonly List<Combate> _combates = new();
    private static readonly bool[] _slotLibre = { true, true, true };
    private static bool _mapPkForzado;

    // ===================== API (desde packets / comandos) =====================

    /// <summary>El jugador (o su grupo) entra a la cola del modo indicado (1, 2 o 3).</summary>
    public static void EntrarCola(int userIndex, int mode)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged) return;
        if (mode < 1 || mode > 3) { Msg(u, "Modo de torneo inválido.", FONT_INFO); return; }
        if (u.flags.Muerto == 1) { Msg(u, "Estás muerto. Resucita antes de entrar a la cola.", FONT_INFO); return; }
        if (EnTorneo(userIndex)) { Msg(u, "Ya estás participando en el torneo en curso.", FONT_INFO); return; }
        if (EnAlgunaCola(userIndex)) { Msg(u, "Ya estás en una cola de torneo. Sal primero si quieres cambiar.", FONT_INFO); return; }

        List<int> roster;
        string nombre;
        if (mode == 1)
        {
            roster = new List<int> { userIndex };
            nombre = u.Name;
        }
        else
        {
            if (u.PartyId <= 0) { Msg(u, $"Para el {mode}v{mode} debes estar en un grupo de {mode}.", FONT_INFO); return; }
            if (!PartySystem.IsLeader(userIndex)) { Msg(u, "Solo el líder del grupo puede inscribir al equipo.", FONT_INFO); return; }
            roster = PartySystem.GetPartyMembers(userIndex);
            if (roster.Count != mode)
            { Msg(u, $"Tu grupo debe tener exactamente {mode} integrantes (tiene {roster.Count}).", FONT_INFO); return; }
            foreach (int m in roster)
            {
                var mu = UserListManager.UserList[m];
                if (mu == null || !mu.flags.UserLogged) { Msg(u, "Un integrante del grupo no está disponible.", FONT_INFO); return; }
                if (mu.flags.Muerto == 1) { Msg(u, $"{mu.Name} está muerto y no puede inscribirse.", FONT_INFO); return; }
                if (EnTorneo(m) || EnAlgunaCola(m)) { Msg(u, $"{mu.Name} ya está en una cola o torneo.", FONT_INFO); return; }
            }
            nombre = $"{u.Name} y {roster.Count - 1} más";
        }

        var e = new Equipo { Members = roster, Name = nombre };
        _queues[mode].Add(e);
        foreach (int m in roster) Msg(UserListManager.UserList[m], $">> En cola para el torneo {mode}v{mode}. ({_queues[mode].Count} equipos esperando)", FONT_INFOBOLD);

        // Arranque inmediato si ya hay suficientes; si no, refrescar estado de todos.
        if (!_running && _queues[mode].Count >= AUTOSTART_TEAMS) IniciarTorneo(mode);
        else PushEstadoGlobal();
    }

    /// <summary>El jugador sale de la cola (saca a todo su equipo). No se puede salir si ya empezó.</summary>
    public static void SalirCola(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null) return;
        for (int mode = 1; mode <= 3; mode++)
        {
            var eq = _queues[mode].FirstOrDefault(e => e.Members.Contains(userIndex));
            if (eq == null) continue;
            _queues[mode].Remove(eq);
            if (_queues[mode].Count < 2) _queueCd[mode] = -1;
            foreach (int m in eq.Members) Msg(UserListManager.UserList[m], ">> Has salido de la cola del torneo.", FONT_INFO);
            PushEstadoGlobal();
            return;
        }
        Msg(u, "No estás en ninguna cola de torneo.", FONT_INFO);
    }

    /// <summary>El cliente pide su estado (polling de la ventana abierta).</summary>
    public static void SolicitarEstado(int userIndex) => SendEstado(userIndex);

    /// <summary>
    /// True si ambos usuarios son COMPAÑEROS del mismo equipo en el torneo en curso (2v2/3v3).
    /// Lo usa Combat.PuedeAtacar: en arenas de torneo se permite pelear entre facciones/grupos,
    /// pero los compañeros de equipo NO se atacan. En 1v1 cada equipo tiene 1 integrante, así que
    /// dos jugadores nunca son "compañeros" → pueden enfrentarse aunque estén en el mismo grupo.
    /// </summary>
    public static bool SonCompanerosDeEquipo(int a, int b)
    {
        if (!_running || a == b) return false;
        foreach (var e in _equipos)
            if (e.Members.Contains(a) && e.Members.Contains(b)) return true;
        return false;
    }

    /// <summary>
    /// True si A y B son RIVALES de un combate de torneo activo (equipos opuestos). Lo usa
    /// Combat.PuedeAtacar para permitir el combate SIEMPRE, sin depender del trigger ZONAPELEA del
    /// mapa (ignora facción/clan/grupo). Robusto aunque las tiles de la arena no estén marcadas.
    /// </summary>
    public static bool SonRivalesEnCombate(int a, int b)
    {
        if (!_running) return false;
        foreach (var c in _combates)
        {
            if (c.State != 2) continue;
            bool aA = c.A.Members.Contains(a), bA = c.A.Members.Contains(b);
            bool aB = c.B.Members.Contains(a), bB = c.B.Members.Contains(b);
            if ((aA && bB) || (aB && bA)) return true;
        }
        return false;
    }

    /// <summary>True si el usuario está en un combate de torneo en curso (State 2). Lo usa
    /// Combat.UserDie para no soltar los ítems al morir aunque la tile no sea ZONAPELEA.</summary>
    public static bool EstaPeleando(int userIndex)
    {
        if (!_running) return false;
        foreach (var c in _combates)
            if (c.State == 2 && (c.A.Members.Contains(userIndex) || c.B.Members.Contains(userIndex))) return true;
        return false;
    }

    // ===================== TICK / MATCHMAKING =====================

    /// <summary>Procesar (1/seg): avanza colas (auto-arranque) y combates en curso.</summary>
    public static void Procesar()
    {
        if (!_running)
        {
            // Matchmaking: arrancar la primera cola que esté lista.
            for (int mode = 1; mode <= 3; mode++)
            {
                int n = _queues[mode].Count(EquipoDisponible);
                if (n >= AUTOSTART_TEAMS) { IniciarTorneo(mode); return; }
                if (n >= 2)
                {
                    if (_queueCd[mode] < 0)
                    {
                        _queueCd[mode] = WAIT_SECONDS;
                        AvisarCola(mode, $">> ¡Hay {n} equipos en la cola {mode}v{mode}! El torneo comienza en {WAIT_SECONDS} segundos...", FONT_INFOBOLD);
                        PushEstadoGlobal();
                    }
                    else
                    {
                        _queueCd[mode]--;
                        if (_queueCd[mode] is 5 or 3 or 2 or 1)
                            AvisarCola(mode, $">> El torneo {mode}v{mode} comienza en {_queueCd[mode]}...", FONT_INFO);
                        if (_queueCd[mode] <= 0) { IniciarTorneo(mode); return; }
                    }
                }
                else _queueCd[mode] = -1;
            }
            return;
        }

        // Asignar arena a combates en cola: al conseguir slot, warpea a las esquinas y congela.
        foreach (var c in _combates)
        {
            if (c.Slot >= 0) continue;
            for (int i = 0; i < _slotLibre.Length; i++)
                if (_slotLibre[i])
                {
                    _slotLibre[i] = false;
                    c.Slot = i;
                    ComenzarCuentaRegresiva(c);
                    break;
                }
        }

        foreach (var c in _combates.ToList())
        {
            if (c.Slot < 0) continue;
            c.Time--;
            if (c.State == 1)
            {
                // Contador grande en pantalla + congelados en las esquinas.
                if (c.Time > 0) MandarContador(c, (byte)c.Time);
                if (c.Time <= 0) ArrancarCombate(c);
            }
            else if (c.State == 2)
            {
                // Abandono: si un equipo se quedó sin nadie peleando (todos muertos, FUERA de la
                // arena o desconectados), el rival gana automáticamente.
                if (!c.A.Members.Any(EnArenaVivo)) ResolverAbandono(c, c.B, c.A);
                else if (!c.B.Members.Any(EnArenaVivo)) ResolverAbandono(c, c.A, c.B);
                else if (c.Time <= 0) ResolverPorTimeout(c);
            }
        }

        if (_combates.Count == 0) IniciarRonda();
    }

    private static void IniciarTorneo(int mode)
    {
        var equipos = _queues[mode].Where(EquipoDisponible).ToList();
        _queues[mode].Clear();
        _queueCd[mode] = -1;
        if (equipos.Count < 2) { PushEstadoGlobal(); return; }

        if (!_mapPkForzado)
        {
            MarcarArenas();
            _mapPkForzado = true;
        }

        _running = true;
        _mode = mode;
        _equipos.Clear();
        _equipos.AddRange(equipos);
        _combates.Clear();
        for (int i = 0; i < _slotLibre.Length; i++) _slotLibre[i] = true;

        Broadcast($"Torneo> ¡Comienza un torneo {mode}v{mode} con {_equipos.Count} equipos!", FONT_SERVER);
        IniciarRonda();
        PushEstadoGlobal();
    }

    /// <summary>
    /// Fuerza el mapa de arenas a PK y marca como ZONAPELEA todas las tiles de los 3 rectángulos de
    /// arena (en memoria; se re-aplica cada vez al iniciar). Así el trigger es correcto para no soltar
    /// ítems y para cualquier lógica que mire ZONAPELEA, además del chequeo por equipos del torneo.
    /// </summary>
    private static void MarcarArenas()
    {
        var md = MapLoader.Get(ARENA_MAP);
        if (md == null) return;
        md.Info.Pk = true;
        foreach (var (ax, ay, bx, by) in _arenas)
            for (int x = Math.Min(ax, bx); x <= Math.Max(ax, bx); x++)
                for (int y = Math.Min(ay, by); y <= Math.Max(ay, by); y++)
                    if (x >= 1 && x <= 100 && y >= 1 && y <= 100)
                        md.Trigger[x, y] = eTrigger.ZONAPELEA;
    }

    /// <summary>Arma los enfrentamientos de la ronda con los equipos que siguen en pie.</summary>
    private static void IniciarRonda()
    {
        foreach (var e in _equipos) if (!e.Eliminated && !EquipoDisponible(e)) e.Eliminated = true;
        var enPie = _equipos.Where(e => !e.Eliminated).ToList();

        if (enPie.Count <= 1) { FinalizarTorneo(enPie.Count == 1 ? enPie[0] : null); return; }

        var rnd = new Random();
        for (int i = enPie.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); (enPie[i], enPie[j]) = (enPie[j], enPie[i]); }

        Broadcast($"Torneo> ¡Nueva ronda! {enPie.Count} equipos en competencia.", FONT_SERVER);

        for (int i = 0; i + 1 < enPie.Count; i += 2) CrearCombate(enPie[i], enPie[i + 1]);

        if (enPie.Count % 2 == 1)
        {
            var bye = enPie[^1];
            foreach (int m in bye.Members) Msg(UserListManager.UserList[m], ">> Tu equipo pasa de ronda directamente (BYE).", FONT_INFOBOLD);
        }
        if (_combates.Count == 0) IniciarRonda();
        else PushEstadoGlobal();
    }

    private static void CrearCombate(Equipo a, Equipo b)
    {
        int slot = -1;
        for (int i = 0; i < _slotLibre.Length; i++) if (_slotLibre[i]) { slot = i; break; }
        var c = new Combate { A = a, B = b, Slot = slot, State = 1, Time = COUNTDOWN };
        _combates.Add(c);
        if (slot >= 0) { _slotLibre[slot] = false; AvisarEmparejamiento(c); ComenzarCuentaRegresiva(c); }
        else AvisarCombate(c, "Tu combate está en cola: esperando que se libere una arena...");
    }

    /// <summary>Warpea a ambos equipos a las esquinas de la arena, los CONGELA (no se mueven ni
    /// atacan) y arranca la cuenta regresiva con número grande en pantalla.</summary>
    private static void ComenzarCuentaRegresiva(Combate c)
    {
        c.State = 1;
        c.Time = COUNTDOWN;
        var (ax, ay, bx, by) = _arenas[c.Slot];
        WarpEquipo(c.A, ax, ay);
        WarpEquipo(c.B, bx, by);
        Congelar(c.A, true);
        Congelar(c.B, true);
        MandarContador(c, (byte)COUNTDOWN);
        AvisarCombate(c, $"¡A tus posiciones! El combate comienza en {COUNTDOWN} segundos...");
        PushEstadoGlobal();
    }

    private static void ArrancarCombate(Combate c)
    {
        if (!EquipoDisponible(c.A) || !EquipoDisponible(c.B))
        {
            Equipo ganador = EquipoDisponible(c.A) ? c.A : (EquipoDisponible(c.B) ? c.B : null!);
            Equipo perdedor = ganador == c.A ? c.B : c.A;
            if (ganador != null) { perdedor.Eliminated = true; AvisarEquipo(ganador, ">> Tu rival no se presentó. ¡Avanzas de ronda!", FONT_INFOBOLD); }
            else { c.A.Eliminated = true; c.B.Eliminated = true; }
            CerrarCombate(c);
            return;
        }
        // Descongelar y ocultar el contador: ¡a pelear! (ya están en las esquinas).
        c.State = 2;
        c.Time = MATCH_TIMEOUT;
        Congelar(c.A, false);
        Congelar(c.B, false);
        MandarContador(c, 0);
        AvisarCombate(c, "¡PELEA!");
        PushEstadoGlobal();
    }

    /// <summary>OnUserDeath: si murió dentro de un combate de torneo, evalúa si su equipo cayó.</summary>
    public static void OnUserDeath(int userIndex)
    {
        if (!_running) return;
        foreach (var c in _combates.ToList())
        {
            if (c.State != 2) continue;
            if (!c.A.Members.Contains(userIndex) && !c.B.Members.Contains(userIndex)) continue;
            if (EquipoTodosMuertos(c.A)) { ResolverCombate(c, c.B, c.A); return; }
            if (EquipoTodosMuertos(c.B)) { ResolverCombate(c, c.A, c.B); return; }
            return;
        }
    }

    private static void ResolverPorTimeout(Combate c)
    {
        int vivosA = c.A.Members.Count(EstaVivo), vivosB = c.B.Members.Count(EstaVivo);
        Equipo ganador = vivosA != vivosB ? (vivosA > vivosB ? c.A : c.B) : (HpRatio(c.A) >= HpRatio(c.B) ? c.A : c.B);
        AvisarCombate(c, "¡Tiempo agotado! Se decide por integrantes vivos y vida restante.");
        ResolverCombate(c, ganador, ganador == c.A ? c.B : c.A);
    }

    /// <summary>Abandono (salir de la arena / desconexión): el rival avanza por incomparecencia.</summary>
    private static void ResolverAbandono(Combate c, Equipo ganador, Equipo perdedor)
    {
        AvisarEquipo(ganador, ">> El equipo rival abandonó el combate. ¡Avanzan de ronda!", FONT_INFOBOLD);
        AvisarEquipo(perdedor, ">> Abandonaste el combate y quedas eliminado del torneo.", FONT_FIGHT);
        Broadcast($"Torneo> {perdedor.Name} abandonó. {ganador.Name} avanza.", FONT_INFO);
        ResolverCombate(c, ganador, perdedor, avisar: false);
    }

    private static void ResolverCombate(Combate c, Equipo ganador, Equipo perdedor)
        => ResolverCombate(c, ganador, perdedor, avisar: true);

    private static void ResolverCombate(Combate c, Equipo ganador, Equipo perdedor, bool avisar)
    {
        perdedor.Eliminated = true;
        if (avisar)
        {
            AvisarEquipo(ganador, ">> ¡Ganaron el combate y avanzan de ronda!", FONT_INFOBOLD);
            AvisarEquipo(perdedor, ">> Han sido eliminados del torneo.", FONT_FIGHT);
            Broadcast($"Torneo> {ganador.Name} venció a {perdedor.Name}.", FONT_INFO);
        }
        SacarEquipo(ganador);
        SacarEquipo(perdedor);
        CerrarCombate(c);
    }

    /// <summary>Congela/descongela a un equipo (no se mueve ni ataca durante la cuenta regresiva).</summary>
    private static void Congelar(Equipo e, bool congelar)
    {
        foreach (int m in e.Members)
        {
            var u = UserListManager.UserList[m];
            if (u != null && u.flags.UserLogged) u.flags.TorneoCongelado = congelar;
        }
    }

    /// <summary>Envía el número grande de cuenta regresiva a ambos equipos (0 = ocultar).</summary>
    private static void MandarContador(Combate c, byte secs)
    {
        foreach (int m in c.A.Members.Concat(c.B.Members))
        {
            var u = UserListManager.UserList[m];
            if (u?.Conn != null) ServerPackets.TorneoCountdown(u.Conn, secs);
        }
    }

    private static void CerrarCombate(Combate c)
    {
        // Seguridad: descongelar y ocultar el contador por si se resolvió durante la cuenta regresiva.
        Congelar(c.A, false); Congelar(c.B, false);
        MandarContador(c, 0);
        if (c.Slot >= 0) { _slotLibre[c.Slot] = true; c.Slot = -1; }
        _combates.Remove(c);
        PushEstadoGlobal();
        if (_running && _combates.Count == 0) IniciarRonda();
    }

    private static void FinalizarTorneo(Equipo campeon)
    {
        if (campeon != null)
        {
            int premio = _reward[Math.Clamp(_mode, 1, 3)];
            Broadcast($"Torneo> ¡{campeon.Name} se consagra CAMPEÓN del torneo {_mode}v{_mode}!", FONT_SERVER);
            foreach (int m in campeon.Members)
            {
                var u = UserListManager.UserList[m];
                if (u == null) continue;
                u.Stats.ArenaPoints += premio;
                Msg(u, $"¡Eres campeón del torneo! Recibes {premio} Puntos de Arena.", FONT_INFOBOLD);
            }
            SacarEquipo(campeon);
        }
        else Broadcast("Torneo> El torneo finalizó sin campeón.", FONT_SERVER);
        ResetTorneo();
    }

    // ===================== GM (chat) =====================

    /// <summary>/torneoforce — GM: arranca ya la cola con más equipos (aunque no llegue al umbral).</summary>
    public static void ForzarInicio(int gmUserIndex)
    {
        var gm = UserListManager.UserList[gmUserIndex];
        if (gm != null && gm.FaccionStatus < AdminLoader.STATUS_SEMIDIOS) { Msg(gm, "No tienes privilegios.", FONT_INFO); return; }
        if (_running) { if (gm != null) Msg(gm, "Ya hay un torneo en curso.", FONT_INFO); return; }
        int best = 0, bestN = 0;
        for (int mode = 1; mode <= 3; mode++) { int n = _queues[mode].Count(EquipoDisponible); if (n > bestN) { bestN = n; best = mode; } }
        if (bestN < 2) { if (gm != null) Msg(gm, "No hay ninguna cola con al menos 2 equipos.", FONT_INFO); return; }
        IniciarTorneo(best);
    }

    /// <summary>/torneocancel — GM: cancela el torneo en curso y devuelve a todos.</summary>
    public static void Cancelar(int gmUserIndex)
    {
        var gm = UserListManager.UserList[gmUserIndex];
        if (gm != null && gm.FaccionStatus < AdminLoader.STATUS_SEMIDIOS) { Msg(gm, "No tienes privilegios.", FONT_INFO); return; }
        if (!_running) { if (gm != null) Msg(gm, "No hay ningún torneo en curso.", FONT_INFO); return; }
        foreach (var c in _combates) { SacarEquipo(c.A); SacarEquipo(c.B); }
        Broadcast("Torneo> El torneo ha sido cancelado por un administrador.", FONT_SERVER);
        ResetTorneo();
    }

    /// <summary>Texto de estado para el comando /torneoestado.</summary>
    public static void Estado(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null) return;
        if (_running)
            Msg(u, $"Torneo> {_mode}v{_mode} en curso. Equipos en pie: {_equipos.Count(e => !e.Eliminated)}. Combates activos: {_combates.Count}.", FONT_INFO);
        else
            Msg(u, $"Torneo> Colas: 1v1={_queues[1].Count}, 2v2={_queues[2].Count}, 3v3={_queues[3].Count}. (abre la ventana de Torneos para unirte)", FONT_INFO);
    }

    // ===================== DESCONEXIÓN =====================

    /// <summary>
    /// CheckDisconnect (desde UserList.CloseUser, con el usuario aún UserLogged=true): lo saca de la
    /// cola o, si estaba peleando, lo devuelve a su posición original y, si su equipo se quedó sin
    /// nadie, da la victoria por abandono al rival. Fijar Pos aquí se persiste en el SaveUser posterior.
    /// </summary>
    public static void CheckDisconnect(int userIndex)
    {
        // Quitar de cualquier cola (descarta el equipo entero).
        for (int mode = 1; mode <= 3; mode++)
        {
            var eq = _queues[mode].FirstOrDefault(e => e.Members.Contains(userIndex));
            if (eq != null) { _queues[mode].Remove(eq); if (_queues[mode].Count < 2) _queueCd[mode] = -1; PushEstadoGlobal(); }
        }
        if (!_running) return;

        foreach (var c in _combates.ToList())
        {
            bool enA = c.A.Members.Contains(userIndex);
            if (!enA && !c.B.Members.Contains(userIndex)) continue;
            var team = enA ? c.A : c.B;
            var rival = enA ? c.B : c.A;

            // Restaurar la posición original del que se va (fijar Pos; el SaveUser posterior la guarda).
            if (_origin.TryGetValue(userIndex, out var op))
            {
                _origin.Remove(userIndex);
                var lu = UserListManager.UserList[userIndex];
                if (lu != null && lu.flags.Muerto == 1) Combat.Resucitar(userIndex);
                if (lu != null) lu.Pos = op;
            }

            // ¿Queda algún compañero todavía en juego? (peleando = vivo+en arena; en cuenta regresiva = conectado)
            bool peleando = c.State == 2;
            bool quedaEquipo = team.Members.Any(m => m != userIndex &&
                (peleando ? EnArenaVivo(m)
                          : UserListManager.UserList[m] is { } mu && mu.flags.UserLogged));
            if (!quedaEquipo) ResolverAbandono(c, rival, team);
            return;
        }
    }

    // ===================== ESTADO AL CLIENTE =====================

    /// <summary>Empuja el estado a todos los que están en cola o en el torneo (transiciones).</summary>
    private static void PushEstadoGlobal()
    {
        var vistos = new HashSet<int>();
        for (int mode = 1; mode <= 3; mode++)
            foreach (var e in _queues[mode]) foreach (int m in e.Members) vistos.Add(m);
        foreach (var e in _equipos) foreach (int m in e.Members) vistos.Add(m);
        foreach (int m in vistos) SendEstado(m);
    }

    private static void SendEstado(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null) return;

        byte yourMode = 0, yourStatus = 0;
        for (int mode = 1; mode <= 3; mode++)
            if (_queues[mode].Any(e => e.Members.Contains(userIndex))) { yourMode = (byte)mode; yourStatus = 1; break; }

        if (yourStatus == 0 && _running)
        {
            var eq = _equipos.FirstOrDefault(e => e.Members.Contains(userIndex));
            if (eq != null)
            {
                if (eq.Eliminated) yourStatus = 4;
                else if (_combates.Any(c => c.State == 2 && (c.A == eq || c.B == eq))) yourStatus = 3;
                else yourStatus = 2;
            }
        }

        byte activeTeams = (byte)Math.Clamp(_running ? _equipos.Count(e => !e.Eliminated) : 0, 0, 255);
        string info = BuildInfo(yourStatus, yourMode);

        ServerPackets.TorneoState(u.Conn,
            yourMode,
            (byte)Math.Clamp(_queues[1].Count, 0, 255),
            (byte)Math.Clamp(_queues[2].Count, 0, 255),
            (byte)Math.Clamp(_queues[3].Count, 0, 255),
            (byte)Math.Clamp(_queueCd[1] < 0 ? 0 : _queueCd[1], 0, 255),
            (byte)Math.Clamp(_queueCd[2] < 0 ? 0 : _queueCd[2], 0, 255),
            (byte)Math.Clamp(_queueCd[3] < 0 ? 0 : _queueCd[3], 0, 255),
            (byte)(_running ? 1 : 0),
            (byte)(_running ? _mode : 0),
            activeTeams,
            yourStatus,
            info);
    }

    private static string BuildInfo(byte status, byte mode) => status switch
    {
        1 => $"En cola para {mode}v{mode}. Esperando equipos...",
        2 => "En el torneo. Esperando tu próximo combate...",
        3 => "¡Peleando! Derrota al equipo rival.",
        4 => "Has sido eliminado del torneo.",
        _ => _running ? $"Torneo {_mode}v{_mode} en curso." : "Elige un modo y entra a la cola.",
    };

    // ===================== HELPERS =====================

    private static void ResetTorneo()
    {
        // Devolver a su posición original a cualquiera que haya quedado en la arena.
        foreach (int m in _origin.Keys.ToList()) RestaurarOrigen(m);
        _origin.Clear();
        _running = false;
        _mode = 0;
        _equipos.Clear();
        _combates.Clear();
        for (int i = 0; i < _slotLibre.Length; i++) _slotLibre[i] = true;
        PushEstadoGlobal();
    }

    private static bool EnAlgunaCola(int userIndex)
    {
        for (int mode = 1; mode <= 3; mode++) if (_queues[mode].Any(e => e.Members.Contains(userIndex))) return true;
        return false;
    }

    private static bool EnTorneo(int userIndex) => _running && _equipos.Any(e => e.Members.Contains(userIndex));

    private static bool EquipoDisponible(Equipo e)
    {
        if (e.Members.Count == 0) return false;
        foreach (int m in e.Members) { var u = UserListManager.UserList[m]; if (u == null || !u.flags.UserLogged) return false; }
        return true;
    }

    private static bool EstaVivo(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        return u != null && u.flags.UserLogged && u.flags.Muerto == 0;
    }

    private static bool EquipoTodosMuertos(Equipo e) => e.Members.All(m => !EstaVivo(m));

    private static double HpRatio(Equipo e)
    {
        double sum = 0; int n = 0;
        foreach (int m in e.Members)
        {
            var u = UserListManager.UserList[m];
            if (u == null || !u.flags.UserLogged) continue;
            sum += u.Stats.MaxHP > 0 ? (double)u.Stats.MinHP / u.Stats.MaxHP : 0;
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    private static void WarpEquipo(Equipo e, int ax, int ay)
    {
        for (int i = 0; i < e.Members.Count; i++)
        {
            int m = e.Members[i];
            var u = UserListManager.UserList[m];
            if (u == null || !u.flags.UserLogged) continue;
            // Guardar la posición original ANTES del primer warp a la arena.
            if (!_origin.ContainsKey(m)) _origin[m] = u.Pos;
            var (dx, dy) = _offsets[Math.Min(i, _offsets.Length - 1)];
            if (u.flags.Muerto == 1) Combat.Resucitar(m);
            HealFull(u);
            Movement.WarpUser(m, ARENA_MAP, (short)(ax + dx), (short)(ay + dy));
        }
    }

    /// <summary>Revive, cura y devuelve a cada integrante a su posición original (fuera de la arena).</summary>
    private static void SacarEquipo(Equipo e)
    {
        foreach (int m in e.Members)
        {
            var u = UserListManager.UserList[m];
            if (u == null || !u.flags.UserLogged) { _origin.Remove(m); continue; }
            u.flags.TorneoCongelado = false;
            if (u.Conn != null) ServerPackets.TorneoCountdown(u.Conn, 0);
            if (u.flags.Muerto == 1) Combat.Resucitar(m);
            HealFull(u);
            RestaurarOrigen(m);
        }
    }

    /// <summary>Devuelve al usuario a su posición original guardada (si está conectado).</summary>
    private static void RestaurarOrigen(int m)
    {
        if (!_origin.TryGetValue(m, out var p)) return;
        _origin.Remove(m);
        var u = UserListManager.UserList[m];
        if (u != null && u.flags.UserLogged && u.Pos.Map == ARENA_MAP)
            Movement.WarpUser(m, (short)p.Map, p.X, p.Y);
    }

    /// <summary>True si el integrante sigue peleando: conectado, vivo y dentro de la arena.</summary>
    private static bool EnArenaVivo(int m)
    {
        var u = UserListManager.UserList[m];
        return u != null && u.flags.UserLogged && u.flags.Muerto == 0 && u.Pos.Map == ARENA_MAP;
    }

    private static void AvisarEmparejamiento(Combate c)
    {
        AvisarEquipo(c.A, $">> ¡Emparejado contra {c.B.Name}! El combate comienza en {COUNTDOWN} segundos...", FONT_INFO);
        AvisarEquipo(c.B, $">> ¡Emparejado contra {c.A.Name}! El combate comienza en {COUNTDOWN} segundos...", FONT_INFO);
    }

    private static void AvisarCombate(Combate c, string msg)
    {
        AvisarEquipo(c.A, ">> " + msg, FONT_INFO);
        AvisarEquipo(c.B, ">> " + msg, FONT_INFO);
    }

    private static void AvisarEquipo(Equipo e, string msg, byte font)
    {
        foreach (int m in e.Members)
        {
            var u = UserListManager.UserList[m];
            if (u?.Conn != null) ServerPackets.ConsoleMsg(u.Conn, msg, font);
        }
    }

    /// <summary>Aviso por consola a todos los integrantes de los equipos en cola de un modo.</summary>
    private static void AvisarCola(int mode, string msg, byte font)
    {
        foreach (var e in _queues[mode]) AvisarEquipo(e, msg, font);
    }

    private static void HealFull(User u)
    {
        u.Stats.MinHP = u.Stats.MaxHP;
        u.Stats.MinMAN = u.Stats.MaxMAN;
        u.Stats.MinSta = u.Stats.MaxSta;
        if (u.Conn != null) ServerPackets.UpdateUserStats(u.Conn, u);
    }

    private static void Msg(User u, string m, byte font)
    { if (u?.Conn != null) ServerPackets.ConsoleMsg(u.Conn, m, font); }

    private static void Broadcast(string m, byte font)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, m, font);
        }
    }
}
