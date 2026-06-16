using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Arenas de duelo 1v1 (modArenas.bas) 1:1. Cola de espera; al haber 2, se ocupa una de las 3
/// arenas (mapa 859) con cuenta regresiva de 10s, se teletransporta y cura a ambos, y pelean al
/// mejor de 3. El ganador recibe 10 Puntos de Arena. Procesar() 1/seg; OnUserDeath desde
/// Combat.UserDie; CheckArenaDisconnect desde UserList.CloseUser.
/// </summary>
public static class ArenaEvento
{
    private const byte LEVEL_MIN = 40, LEVEL_MAX = 50;
    public const int ARENA_MAP = 859;
    private const byte FONT_INFO = 3, FONT_INFOBOLD = 4, FONT_SERVER = 8, FONT_FIGHT = 5;

    private sealed class Arena
    {
        public int X1, Y1, X2, Y2;
        public int User1, User2;
        public bool Occupied;
        public byte State;      // 0=libre, 1=cuenta regresiva, 2=peleando
        public int TimeLeft;
        public byte Wins1, Wins2;
        public WorldPos Origin1, Origin2;
    }

    private static readonly Arena[] _arenas =
    {
        new() { X1 = 13, Y1 = 13, X2 = 29, Y2 = 29 },
        new() { X1 = 42, Y1 = 13, X2 = 58, Y2 = 29 },
        new() { X1 = 71, Y1 = 13, X2 = 87, Y2 = 29 },
    };
    private static readonly List<int> _cola = new();
    private static bool _inicializado;

    private static void Init()
    {
        // Forzar combate en el mapa de arenas (VB6: MapInfo(859).Pk = True) y marcar las tiles de las
        // 3 arenas como ZONAPELEA (en memoria) para que no caigan ítems al morir ni cambie estado.
        var md = MapLoader.Get(ARENA_MAP);
        if (md != null)
        {
            md.Info.Pk = true;
            foreach (var a in _arenas)
                for (int x = Math.Min(a.X1, a.X2); x <= Math.Max(a.X1, a.X2); x++)
                    for (int y = Math.Min(a.Y1, a.Y2); y <= Math.Max(a.Y1, a.Y2); y++)
                        if (x >= 1 && x <= 100 && y >= 1 && y <= 100)
                            md.Trigger[x, y] = eTrigger.ZONAPELEA;
        }
        _inicializado = true;
    }

    /// <summary>UnirseArena (ArenaJoin): valida e inscribe en la cola; intenta emparejar.</summary>
    public static void UnirseArena(int userIndex)
    {
        if (!_inicializado) Init();
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged) return;

        if (u.Stats.ELV < LEVEL_MIN || u.Stats.ELV > LEVEL_MAX)
        { Msg(u, $"Solo usuarios entre nivel {LEVEL_MIN} y {LEVEL_MAX} pueden entrar.", FONT_INFO); return; }
        if (u.flags.Muerto == 1) { Msg(u, "¡Estás muerto!", FONT_INFO); return; }
        if (_cola.Contains(userIndex)) { Msg(u, "Ya estás en la cola de espera.", FONT_INFO); return; }
        if (u.Pos.Map == ARENA_MAP) { Msg(u, "Ya estás en la arena.", FONT_INFO); return; }

        _cola.Add(userIndex);
        Broadcast($"Arenas> {u.Name} se ha inscripto y busca oponente (Nivel {u.Stats.ELV}).", FONT_SERVER);
        Msg(u, "Te has inscripto. Buscando oponente...", FONT_INFOBOLD);
        CheckMatch();
    }

    private static void CheckMatch()
    {
        // Limpiar de la cola los que ya no estén online.
        _cola.RemoveAll(i => { var x = UserListManager.UserList[i]; return x == null || !x.flags.UserLogged; });
        if (_cola.Count < 2) return;

        int slot = GetFreeArena();
        if (slot < 0) return;

        int u1 = _cola[0], u2 = _cola[1];
        _cola.RemoveAt(0); _cola.RemoveAt(0);

        var a = _arenas[slot];
        a.Occupied = true; a.State = 1; a.TimeLeft = 10;
        a.User1 = u1; a.User2 = u2; a.Wins1 = 0; a.Wins2 = 0;
        a.Origin1 = UserListManager.UserList[u1].Pos;
        a.Origin2 = UserListManager.UserList[u2].Pos;

        Msg(UserListManager.UserList[u1], "¡Oponente encontrado! La arena comienza en 10 segundos...", FONT_INFOBOLD);
        Msg(UserListManager.UserList[u2], "¡Oponente encontrado! La arena comienza en 10 segundos...", FONT_INFOBOLD);
    }

    private static int GetFreeArena()
    {
        for (int i = 0; i < _arenas.Length; i++) if (!_arenas[i].Occupied) return i;
        return -1;
    }

    /// <summary>Arena_Procesar: 1/seg. Avanza la cuenta regresiva y arranca el duelo.</summary>
    public static void Procesar()
    {
        if (!_inicializado) return;
        foreach (var a in _arenas)
        {
            if (!a.Occupied || a.State != 1) continue;
            a.TimeLeft--;
            if (a.TimeLeft is 5 or 3 or 2 or 1)
            {
                Msg(UserListManager.UserList[a.User1], $"Arena en {a.TimeLeft}...", FONT_INFO);
                Msg(UserListManager.UserList[a.User2], $"Arena en {a.TimeLeft}...", FONT_INFO);
            }
            if (a.TimeLeft <= 0) StartArena(a);
        }
    }

    private static void StartArena(Arena a)
    {
        a.State = 2;
        var u1 = UserListManager.UserList[a.User1];
        var u2 = UserListManager.UserList[a.User2];
        if (u1 == null || !u1.flags.UserLogged || u2 == null || !u2.flags.UserLogged)
        { a.Occupied = false; a.User1 = 0; a.User2 = 0; a.State = 0; return; }

        Movement.WarpUser(a.User1, ARENA_MAP, (short)a.X1, (short)a.Y1);
        Movement.WarpUser(a.User2, ARENA_MAP, (short)a.X2, (short)a.Y2);
        HealFull(u1); HealFull(u2);
        Msg(u1, $"¡PELEA! Round {a.Wins1 + a.Wins2 + 1}", FONT_FIGHT);
        Msg(u2, $"¡PELEA! Round {a.Wins1 + a.Wins2 + 1}", FONT_FIGHT);
    }

    /// <summary>OnUserDeath: si el muerto estaba en un duelo, resuelve el round/match.</summary>
    public static void OnUserDeath(int userIndex)
    {
        foreach (var a in _arenas)
            if (a.Occupied && (a.User1 == userIndex || a.User2 == userIndex))
            { FinalizarArena(0, userIndex, a); return; }
    }

    private static void FinalizarArena(int winner, int loser, Arena a)
    {
        if (winner == 0) winner = (a.User1 == loser) ? a.User2 : a.User1;
        bool u1Gana = a.User1 == winner;
        if (u1Gana) a.Wins1++; else a.Wins2++;

        var w = UserListManager.UserList[winner];
        var l = UserListManager.UserList[loser];
        Msg(w, "¡Ganaste el round!", FONT_INFOBOLD);
        Msg(l, "Perdiste el round.", FONT_FIGHT);

        if (a.Wins1 >= 2 || a.Wins2 >= 2)
        {
            Broadcast($"Arenas> {w.Name} venció a {l.Name}!", FONT_SERVER);
            w.Stats.ArenaPoints += 10;
            Msg(w, "¡Has ganado 10 Puntos de Arena!", FONT_INFOBOLD);
            Msg(l, "¡Has perdido la arena!", FONT_INFO);
            ReviveYCura(winner); ReviveYCura(loser);
            Movement.WarpUser(winner, (short)a.Origin1.Map, a.Origin1.X, a.Origin1.Y);
            Movement.WarpUser(loser, (short)a.Origin2.Map, a.Origin2.X, a.Origin2.Y);
            ResetArena(a);
            CheckMatch();
        }
        else
        {
            ReviveYCura(a.User1); ReviveYCura(a.User2);
            Movement.WarpUser(a.User1, ARENA_MAP, (short)a.X1, (short)a.Y1);
            Movement.WarpUser(a.User2, ARENA_MAP, (short)a.X2, (short)a.Y2);
            Msg(UserListManager.UserList[a.User1], "¡Siguiente Round!", FONT_INFOBOLD);
            Msg(UserListManager.UserList[a.User2], "¡Siguiente Round!", FONT_INFOBOLD);
        }
    }

    /// <summary>CheckArenaDisconnect: saca de la cola y, si estaba en duelo, da la victoria al rival.</summary>
    public static void CheckArenaDisconnect(int userIndex)
    {
        _cola.Remove(userIndex);
        foreach (var a in _arenas)
        {
            if (!a.Occupied || (a.User1 != userIndex && a.User2 != userIndex)) continue;
            int loser = userIndex;
            int winner = (a.User1 == userIndex) ? a.User2 : a.User1;
            if (winner > 0)
            {
                var w = UserListManager.UserList[winner];
                if (w != null && w.flags.UserLogged)
                {
                    Msg(w, "Tu oponente se ha desconectado. ¡Ganaste el duelo!", FONT_INFOBOLD);
                    Broadcast($"Arenas> {w.Name} ganó por abandono de {UserListManager.UserList[loser]?.Name}.", FONT_SERVER);
                    w.Stats.ArenaPoints += 10;
                    Msg(w, "¡Has ganado 10 Puntos de Arena!", FONT_INFOBOLD);
                    ReviveYCura(winner);
                    var ow = (a.User1 == winner) ? a.Origin1 : a.Origin2;
                    Movement.WarpUser(winner, (short)ow.Map, ow.X, ow.Y);
                }
            }
            // Al que se desconecta se le fija la pos de origen (se guardará en CloseUser).
            var lu = UserListManager.UserList[loser];
            if (lu != null)
            {
                if (lu.flags.Muerto == 1) Combat.Resucitar(loser);
                lu.Pos = (a.User1 == loser) ? a.Origin1 : a.Origin2;
            }
            ResetArena(a);
            CheckMatch();
            return;
        }
    }

    // --- helpers ---

    private static void ResetArena(Arena a)
    { a.Occupied = false; a.User1 = 0; a.User2 = 0; a.State = 0; a.Wins1 = 0; a.Wins2 = 0; }

    private static void ReviveYCura(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null) return;
        if (u.flags.Muerto == 1) Combat.Resucitar(userIndex);
        HealFull(u);
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
            if (u.flags.UserLogged && u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, m, font);
        }
    }
}
