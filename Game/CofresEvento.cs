using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Invasión de Cofres del Tesoro (modEventoInvasionCofres.bas) 1:1. Un GM dispara
/// /INVASION &lt;mapa&gt; &lt;cantidad&gt;: spawnea 'cantidad' cofres (NPC 619) en posiciones legales
/// aleatorias del mapa. Al matar un cofre, el matador recibe 4.000.000 de oro (cap MAXORO) y
/// 30% de chance de un item legendario (402/481/1606). Termina cuando se saquean todos.
/// </summary>
public static class CofresEvento
{
    public const int NPC_ID_COFRE = 619;
    private const int MAXORO = 90000000; // Declares.bas:385

    public static bool InvasionActiva { get; private set; }
    private static int _mapaInvasion;
    private static int _cofresRestantes;
    private static int _cofresTotales;

    private static readonly Random _rng = new();

    /// <summary>IniciarInvasion(mapa, cantidad): spawnea los cofres y anuncia el evento.</summary>
    public static void IniciarInvasion(int mapa, int cantidad)
    {
        if (cantidad <= 0) return;
        if (MapLoader.Get(mapa) == null) return;

        InvasionActiva = true;
        _mapaInvasion = mapa;

        int spawneados = 0;
        for (int i = 0; i < cantidad; i++)
            if (SpawnCofreAleatorio(mapa)) spawneados++;

        if (spawneados > 0)
        {
            _cofresRestantes = spawneados;
            _cofresTotales = spawneados;
            Anunciar(mapa, spawneados);
        }
        else
        {
            InvasionActiva = false;
            Console.WriteLine($"[CofresEvento] No se pudieron spawnear cofres en el mapa {mapa}.");
        }
    }

    private static bool SpawnCofreAleatorio(int mapa)
    {
        var md = MapLoader.Get(mapa);
        if (md == null) return false;
        for (int intentos = 0; intentos < 50; intentos++)
        {
            int x = _rng.Next(10, 91);
            int y = _rng.Next(10, 91);
            if (md.IsBlocked(x, y)) continue;
            if (NpcManager.NpcAt(mapa, x, y) != null) continue;

            var npc = NpcManager.SpawnAt(mapa, NPC_ID_COFRE, (byte)x, (byte)y);
            if (npc != null)
            {
                npc.Movement = 1;       // ESTATICO
                npc.NoRespawn = true;    // los cofres del evento no reviven
                return true;
            }
        }
        return false;
    }

    /// <summary>OnCofreMuere: recompensa al matador y, si era el último, finaliza la invasión.</summary>
    public static void OnCofreMuere(NpcManager.NpcInstance npc, int userIndex)
    {
        if (!InvasionActiva) return;
        _cofresRestantes--;

        if (userIndex > 0)
        {
            var u = UserListManager.UserList[userIndex];
            if (u != null && u.flags.UserLogged && u.Conn != null)
            {
                u.Stats.GLD += 4000000;
                if (u.Stats.GLD > MAXORO) u.Stats.GLD = MAXORO;
                ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
                ServerPackets.ConsoleMsg(u.Conn, "¡Has recibido 4,000,000 de oro del cofre!", 3);

                if (_rng.Next(1, 101) <= 30) // 30% item legendario
                {
                    short item = _rng.Next(1, 4) switch { 1 => 402, 2 => 481, _ => (short)1606 };
                    if (Inventory.AddItemToInventory(u, item, 1))
                        ServerPackets.ConsoleMsg(u.Conn, "¡Has encontrado un objeto legendario en el cofre!", 5);
                    else
                    {
                        DropItemAtUser(u, item, 1);
                        ServerPackets.ConsoleMsg(u.Conn, "Tu inventario estaba lleno. ¡El objeto cayó al suelo!", 2);
                    }
                }

                Broadcast($"¡{u.Name} ha destruido un Cofre del Tesoro! Quedan {_cofresRestantes} cofres.", 3);
            }
        }

        if (_cofresRestantes <= 0) Finalizar();
    }

    private static void Finalizar()
    {
        InvasionActiva = false;
        Broadcast("¡La Invasión de Cofres del Tesoro ha finalizado! Todos los cofres han sido saqueados.", 4);
    }

    private static void Anunciar(int mapa, int cantidad)
        => Broadcast($"¡INVASIÓN DE COFRES DEL TESORO! Se han avistado {cantidad} cofres legendarios en el Mapa {mapa}. ¡Corran antes de que otros se lleven el botín!", 4);

    /// <summary>Deja un item en el piso en la posición del usuario (o adyacente) y lo difunde.</summary>
    private static void DropItemAtUser(User u, short objIndex, int amount)
    {
        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return;
        int x = u.Pos.X, y = u.Pos.Y;
        if (map.FloorObj[x, y] != 0)
        {
            bool libre = false;
            for (int dx = -1; dx <= 1 && !libre; dx++)
                for (int dy = -1; dy <= 1 && !libre; dy++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx is >= 1 and <= 100 && ny is >= 1 and <= 100 && !map.IsBlocked(nx, ny) && map.FloorObj[nx, ny] == 0)
                    { x = nx; y = ny; libre = true; }
                }
            if (!libre) return;
        }
        map.FloorObj[x, y] = objIndex;
        map.FloorAmount[x, y] = amount;
        AreaVisibility.ObjectAppeared(u.Pos.Map, x, y, objIndex, amount);
    }

    private static void Broadcast(string msg, byte font)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u.flags.UserLogged && u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, msg, font);
        }
    }
}
