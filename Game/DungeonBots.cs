namespace ServidorCS.Game;

/// <summary>
/// Puebla dungeons específicos con bots de facción PERMANENTES que nunca marchan (a diferencia
/// de <see cref="GuerraFacciones"/>, que hace marchar ejércitos entre ciudades). Se marcan
/// `BotGuerra=true` (heredan gratis: pelean sin parley vía `CombateDeFaccion`, cazan criaturas
/// hostiles vía `AtacarCriaturaCercana`, son visibles en el panel de espectador vía
/// `NpcManager.BotsDeGuerra()`/`Espia`) y `BotDungeon=true` (nuevo: en `TickBotGuerra`, en vez de
/// `ViajarGuerra`, deambulan localmente con `TryStepRandom`, jamás cruzan de mapa).
///
/// 5+5 bots por mapa, rotando qué 2 de las 3 facciones (1=Armada, 2=Milicia/República, 3=Caos)
/// le tocan a cada dungeon. El mapa 755 (Dungeon Fárzhë, 1er Piso) suma además un trío fijo
/// Paladín+Mago+Cazador de Armada.
/// </summary>
public static class DungeonBots
{
    private const int MAP_ESPECIAL = 755;

    // Guerrero, Paladín, Clérigo, Mago, Cazador (ids de Bots._clases).
    private static readonly byte[] SquadClases = { 3, 9, 1, 2, 10 };

    // 25 mapas de dungeon (uno por piso, no por complejo): Newbie N1/N2, Dragón, Gaugin,
    // Marabel, Veriil (6 pisos), Zero, Cristal, Fárzhë (5 pisos, incluye el especial 755),
    // Krëwh.
    private static readonly int[] Mapas =
    {
        37, 208, 48, 207, 115, 116, 140, 141, 142, 143, 144, 145,
        209, 210, 211, 230, 231, 232, 754, 755, 756, 757, 760, 758, 759,
    };

    // Qué 2 facciones le tocan a cada dungeon, rotando por índice para variar.
    private static readonly (byte a, byte b)[] Rotacion = { (1, 2), (2, 3), (3, 1) };

    private sealed class Entry
    {
        public int Map;
        public byte Clase, Faccion;
        public NpcManager.NpcInstance Bot;
        public double MuertoDesde;     // TickCount/1000 en que se detectó muerto; 0 = vivo o ya repuesto
        public double RespawnDelay;    // segundos de demora ANTES de reponer (sorteado al morir)
    }

    private static readonly List<Entry> _spawned = new();
    private static readonly Dictionary<int, (byte x, byte y)> _anclas = new();
    private static readonly Random _rng = new();

    /// <summary>Spawea la población inicial de los 25 dungeons. Llamar UNA vez al arrancar el server.</summary>
    public static void Init()
    {
        // Grafo de TileExits: los guardianes lo usan para cruzar a otro piso/mapa si el deambulado
        // los lleva justo a una salida (NpcManager.CruzarSiHaySalida). Pagarlo acá (server recién
        // arrancando) y no en el primer tick de IA, que congelaría el loop del juego ya con jugadores.
        BotPathing.EnsureGrafo();

        int ok = 0;
        for (int i = 0; i < Mapas.Length; i++)
        {
            int map = Mapas[i];
            var ancla = Ancla(map);
            if (ancla.x == 0)
            {
                Console.WriteLine($"[DungeonBots] Mapa {map}: sin tile caminable encontrado, salteado.");
                continue;
            }
            _anclas[map] = ancla;

            var (facA, facB) = Rotacion[i % Rotacion.Length];
            SpawnSquad(map, facA);
            SpawnSquad(map, facB);

            // Mapa especial: trío Paladín+Mago+Cazador de Armada, ADEMÁS del 5+5 genérico.
            if (map == MAP_ESPECIAL)
            {
                SpawnUno(map, 9, 1);  // Paladín
                SpawnUno(map, 2, 1);  // Mago
                SpawnUno(map, 10, 1); // Cazador
            }
            ok++;
        }
        Console.WriteLine($"[DungeonBots] {ok}/{Mapas.Length} dungeons poblados, {_spawned.Count} bots iniciales.");
    }

    /// <summary>Tile caminable cerca del centro del mapa (mismo criterio que GuerraFacciones.DestinoEn).</summary>
    private static (byte x, byte y) Ancla(int map)
    {
        var t = GuerraFacciones.TileLibreCerca(map, 50, 50, 45);
        if (t.x != 0) return t;
        var md = MapLoader.Get(map);
        if (md == null) return (0, 0);
        for (int y = 1; y <= 100; y++)
            for (int x = 1; x <= 100; x++)
                if (!md.IsBlocked(x, y) && !md.HasWater(x, y)) return ((byte)x, (byte)y);
        return (0, 0);
    }

    private static void SpawnSquad(int map, byte faccion)
    {
        foreach (var clase in SquadClases) SpawnUno(map, clase, faccion);
    }

    private static void SpawnUno(int map, byte clase, byte faccion)
    {
        if (!_anclas.TryGetValue(map, out var ancla)) return;
        var (fx, fy) = TileDisperso(map, ancla);
        byte heading = (byte)(1 + _rng.Next(4));
        var bot = Bots.Spawn(map, fx, fy, clase, raza: 0, owner: 0, faccion: faccion, heading: heading);
        if (bot == null) return;
        bot.BotGuerra = true;
        bot.BotDungeon = true;
        _spawned.Add(new Entry { Map = map, Clase = clase, Faccion = faccion, Bot = bot });
    }

    /// <summary>
    /// Tile caminable disperso por el dungeon: prueba puntos al azar en un radio grande alrededor
    /// del ancla (no siempre el mismo lugar) y cae de vuelta cerca del ancla si el mapa es chico o
    /// muy tapado. Se usa tanto para el spawn inicial como para cada respawn.
    /// </summary>
    private static (byte x, byte y) TileDisperso(int map, (byte x, byte y) ancla)
    {
        for (int intento = 0; intento < 8; intento++)
        {
            int ox = Math.Clamp(ancla.x + _rng.Next(-25, 26), 1, 99);
            int oy = Math.Clamp(ancla.y + _rng.Next(-25, 26), 1, 99);
            var t = GuerraFacciones.TileLibreCerca(map, (byte)ox, (byte)oy, 5);
            if (t.x != 0) return t;
        }
        var cerca = GuerraFacciones.TileLibreCerca(map, ancla.x, ancla.y, 15);
        return cerca.x != 0 ? cerca : ancla;
    }

    // Demora antes de reponer un bot caído: así no revive al instante en la cara de quien lo mató.
    private const double RESPAWN_DELAY_MIN = 12.0, RESPAWN_DELAY_MAX = 30.0;

    private static double _nextCheckAt;
    private const double CheckIntervalSeconds = 2.0; // chequeo fino; el respawn en sí lo pausa RespawnDelay

    /// <summary>Repone cualquier bot caído (con demora aleatoria y en un punto disperso del mapa).
    /// Llamado desde NpcManager.TickAI (tiene su propio cooldown).</summary>
    public static void Tick()
    {
        double now = Environment.TickCount64 / 1000.0;
        if (now < _nextCheckAt) return;
        _nextCheckAt = now + CheckIntervalSeconds;

        foreach (var e in _spawned)
        {
            if (e.Bot != null && !e.Bot.Dead) { e.MuertoDesde = 0; continue; } // vivo, nada que hacer

            // Recién detectado muerto: sortear cuánto va a tardar en volver, todavía no reponer.
            if (e.MuertoDesde == 0) { e.MuertoDesde = now; e.RespawnDelay = RESPAWN_DELAY_MIN + _rng.NextDouble() * (RESPAWN_DELAY_MAX - RESPAWN_DELAY_MIN); continue; }
            if (now - e.MuertoDesde < e.RespawnDelay) continue; // todavía esperando

            if (!_anclas.TryGetValue(e.Map, out var ancla)) continue;
            var (fx, fy) = TileDisperso(e.Map, ancla);
            byte heading = (byte)(1 + _rng.Next(4));
            var bot = Bots.Spawn(e.Map, fx, fy, e.Clase, raza: 0, owner: 0, faccion: e.Faccion, heading: heading);
            if (bot == null) continue; // reintenta en el próximo chequeo (queda con MuertoDesde ya seteado)
            bot.BotGuerra = true;
            bot.BotDungeon = true;
            e.Bot = bot;
            e.MuertoDesde = 0;
        }
    }
}
