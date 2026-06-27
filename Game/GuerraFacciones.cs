namespace ServidorCS.Game;

/// <summary>
/// GUERRA MUNDIAL DE FACCIONES (NUEVO, no VB6). Levanta tres ejércitos de bots —Armada,
/// República y Caos— que nacen en su ciudad, marchan por el mundo hacia las ciudades enemigas
/// (cruzando mapas por los TileExits y embarcándose solos al pisar agua) y se matan entre sí al
/// cruzarse. El mundo queda "vivo" sin necesidad de jugadores.
///
/// Reparto de responsabilidades:
///  - <see cref="Bots"/> arma cada bot (clase, sacro, stats, facción, color de nick, estandarte).
///  - <see cref="BotPathing"/> resuelve el camino dentro del mapa y la ruta entre mapas.
///  - <c>NpcManager.TickBotGuerra</c> es la IA por tick (pelear si hay enemigo, si no marchar).
///  - Esta clase decide CUÁNTOS hay, DÓNDE nacen y HACIA DÓNDE van, y repone las bajas.
///
/// Los bots nacen con NoRespawn=true (InitBot), así que al morir desaparecen: la reposición la
/// hace <see cref="Tick"/>, que los hace renacer en su ciudad. La guerra no termina sola.
/// </summary>
public static class GuerraFacciones
{
    // Ciudad base de cada facción (índice eCiudad de Ciudades.dat, ver CityData):
    // Armada → Ullathorpe (hogar imperial), República → Illiandor, Caos → Rinkel.
    // Son las mismas que usa Facciones.RetirarFaccion para reasignar el hogar del jugador.
    private const byte CIUDAD_ARMADA = 3, CIUDAD_REPUBLICA = 2, CIUDAD_CAOS = 5;

    public const byte FACCION_ARMADA = 1, FACCION_REPUBLICA = 2, FACCION_CAOS = 3;
    private static readonly byte[] _facciones = { FACCION_ARMADA, FACCION_REPUBLICA, FACCION_CAOS };

    /// <summary>Tope duro de bots por facción. 300×3 = 900 en total (pedido explícito del usuario).</summary>
    public const int MAX_POR_FACCION = 300;

    private static readonly Random _rng = new();

    /// <summary>true mientras la guerra está en curso (repone bajas).</summary>
    public static bool Activa { get; private set; }

    /// <summary>Cuántos bots debe tener VIVOS cada facción.</summary>
    public static int PorFaccion { get; private set; }

    /// <summary>Bajas acumuladas por facción desde que arrancó la guerra (índice 1..3).</summary>
    private static readonly int[] _bajas = new int[4];

    /// <summary>true durante el alta inicial: esos spawns son el ejército, no bajas repuestas.</summary>
    private static bool _altaInicial;

    private static double _nextReposicionAt;
    private const double REPOSICION_CADA = 6.0;   // segundos entre reposiciones de bajas

    public static byte CiudadDeFaccion(byte faccion) => faccion switch
    {
        FACCION_ARMADA => CIUDAD_ARMADA,
        FACCION_REPUBLICA => CIUDAD_REPUBLICA,
        FACCION_CAOS => CIUDAD_CAOS,
        _ => CIUDAD_ARMADA,
    };

    public static string NombreFaccion(byte faccion) => faccion switch
    {
        FACCION_ARMADA => "Armada Real",
        FACCION_REPUBLICA => "República",
        FACCION_CAOS => "Caos",
        _ => "?",
    };

    // ============================================================
    //  Arranque / parada
    // ============================================================

    /// <summary>
    /// Arranca la guerra con <paramref name="porFaccion"/> bots por bando (50 = los 150 pedidos).
    /// Si ya estaba activa, ajusta el tamaño de los ejércitos. Devuelve cuántos bots spawneó ahora.
    /// </summary>
    public static int Iniciar(int porFaccion)
    {
        porFaccion = Math.Clamp(porFaccion, 1, MAX_POR_FACCION);
        PorFaccion = porFaccion;
        Activa = true;
        Array.Clear(_bajas);

        // El grafo del mundo tarda un par de segundos la primera vez: mejor pagarlo acá y no en el
        // primer tick de IA (que congelaría el loop del juego con los bots ya en el mapa).
        BotPathing.EnsureGrafo();

        _altaInicial = true;
        int spawneados = Reponer();
        _altaInicial = false;
        Console.WriteLine($"[Guerra] Iniciada: {porFaccion} bots por facción ({spawneados} spawneados).");
        return spawneados;
    }

    /// <summary>Termina la guerra y borra los tres ejércitos. Devuelve cuántos bots sacó.</summary>
    public static int Detener()
    {
        Activa = false;
        PorFaccion = 0;
        int n = 0;
        foreach (var b in NpcManager.BotsDeGuerra()) { NpcManager.RemoveNpc(b); n++; }
        BroadcastPosiciones();   // lista vacía: limpia los marcadores del Mapamundi de los GMs
        Console.WriteLine($"[Guerra] Detenida: {n} bots eliminados.");
        return n;
    }

    /// <summary>Resumen para el GM: vivos por facción, bajas y en qué mapas andan.</summary>
    public static string Estado()
    {
        if (!Activa) return "La guerra de facciones NO está activa. Usá /guerra [cantidad] para iniciarla.";
        var vivos = new int[4];
        var mapas = new Dictionary<int, int>();
        foreach (var b in NpcManager.BotsDeGuerra())
        {
            if (b.BotFaccion >= 1 && b.BotFaccion <= 3) vivos[b.BotFaccion]++;
            mapas[b.Map] = mapas.GetValueOrDefault(b.Map) + 1;
        }
        string top = string.Join(", ", mapas.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"mapa {kv.Key}: {kv.Value}"));
        return $"Guerra ACTIVA ({PorFaccion} por facción). " +
               $"Armada {vivos[1]} (bajas {_bajas[1]}) | República {vivos[2]} (bajas {_bajas[2]}) | Caos {vivos[3]} (bajas {_bajas[3]}). " +
               $"Frentes: {top}";
    }

    // ============================================================
    //  Reposición de bajas
    // ============================================================

    /// <summary>Lo llama NpcManager.TickAI: repone bajas y manda las posiciones a los GMs.</summary>
    public static void Tick()
    {
        if (!Activa) return;
        double now = Environment.TickCount64 / 1000.0;

        if (now >= _nextPosicionesAt)
        {
            _nextPosicionesAt = now + POSICIONES_CADA;
            BroadcastPosiciones();
        }

        if (now < _nextReposicionAt) return;
        _nextReposicionAt = now + REPOSICION_CADA;
        Reponer();
    }

    private static double _nextPosicionesAt;
    private const double POSICIONES_CADA = 2.0;   // segundos entre fotos de posiciones al Mapamundi

    /// <summary>
    /// Manda a cada GM conectado la posición de TODOS los bots de la guerra, para que el Mapamundi
    /// del cliente web los dibuje en vivo. Sólo GMs: son ~1 KB cada 2s por GM y, sobre todo, la
    /// posición de los tres ejércitos es información que un jugador no debería tener gratis.
    /// </summary>
    private static void BroadcastPosiciones()
    {
        // Armar la lista UNA vez y reusarla para todos los destinatarios.
        var bots = NpcManager.BotsDeGuerra();
        var datos = new List<(int map, byte x, byte y, byte faccion)>(bots.Count);
        foreach (var b in bots) datos.Add((b.Map, b.X, b.Y, b.BotFaccion));

        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u == null || !u.flags.UserLogged || u.Conn == null) continue;
            if (u.FaccionStatus < AdminLoader.STATUS_SEMIDIOS) continue;
            Network.ServerPackets.GuerraBots(u.Conn, datos);
        }
    }

    /// <summary>Spawnea los que falten para llegar a PorFaccion en cada bando. Devuelve cuántos creó.</summary>
    private static int Reponer()
    {
        var vivosPorFaccion = new Dictionary<byte, List<NpcManager.NpcInstance>>();
        foreach (var b in NpcManager.BotsDeGuerra())
        {
            if (b.BotFaccion < 1 || b.BotFaccion > 3) continue;
            if (!vivosPorFaccion.TryGetValue(b.BotFaccion, out var l)) { l = new(); vivosPorFaccion[b.BotFaccion] = l; }
            l.Add(b);
        }

        int creados = 0;
        foreach (byte f in _facciones)
        {
            var vivos = vivosPorFaccion.TryGetValue(f, out var l) ? l : new List<NpcManager.NpcInstance>();
            int faltan = PorFaccion - vivos.Count;

            if (faltan > 0)
            {
                if (!_altaInicial) _bajas[f] += faltan;   // los que faltan cayeron en combate
                for (int i = 0; i < faltan; i++)
                    if (SpawnGuerrero(f) != null) creados++;
            }
            else if (faltan < 0)
            {
                // El GM bajó el tamaño del ejército con /guerra: sobran bots vivos — se eliminan
                // los que sobren (no es "muerte en combate", así que no suma a las bajas).
                for (int i = 0; i < -faltan; i++) NpcManager.RemoveNpc(vivos[i]);
            }
        }
        return creados;
    }

    /// <summary>Crea un guerrero de la facción en su ciudad, de clase al azar, y le da un objetivo.</summary>
    private static NpcManager.NpcInstance SpawnGuerrero(byte faccion)
    {
        var ciudad = CityData.Get(CiudadDeFaccion(faccion));
        if (ciudad.Map <= 0) return null;

        // Dispersarlos por la ciudad: un tile caminable al azar en un radio chico del centro.
        var (sx, sy) = TileLibreCerca(ciudad.Map, (byte)ciudad.X, (byte)ciudad.Y, 10);
        if (sx == 0) { sx = (byte)ciudad.X; sy = (byte)ciudad.Y; }

        var clases = Bots.Clases.ToList();
        byte clase = clases[_rng.Next(clases.Count)].Clase;
        byte heading = (byte)(1 + _rng.Next(4));

        var bot = Bots.Spawn(ciudad.Map, sx, sy, clase, raza: 0, owner: 0, faccion: faccion, heading: heading);
        if (bot == null) return null;
        bot.BotGuerra = true;
        DarMonturaOAlas(bot);
        AsignarObjetivo(bot);
        return bot;
    }

    // ============================================================
    //  Monturas y alas
    // ============================================================

    // Bodies (NumRopaje) de las 26 monturas reales de obj.dat (ObjType=44), extraídos del propio
    // .dat. El cliente reconoce montura/alas mirando SOLO el body/escudo de cualquier personaje
    // (MOUNT_BODIES / SHIELDS_WINGS en game.html), así que alcanza con ponerle el body: se dibuja
    // montado y hasta se le anima la velocidad correcta, sin ningún paquete nuevo.
    private static readonly short[] _monturaBodies =
    {
        291, 292, 317, 357, 358, 381, 382, 383, 384, 412, 413, 415, 416,
        688, 689, 690, 691, 692, 693, 723, 724, 725, 860, 862,
    };
    /// <summary>Body de la única montura VOLADORA (Montura de Sombras, obj 1669, Vuela=1).</summary>
    private const short MONTURA_VOLADORA = 888;
    /// <summary>ShieldAnim de las "Alas de Ángel" (obj 2035, Anim=88) — el cliente las dibuja y las hace volar.</summary>
    public const short ALAS_ANIM = 88;

    private const int PROB_MONTURA = 30;   // % de bots a caballo
    private const int PROB_ALAS = 12;      // % de bots con alas (vuelan: cruzan el agua sin barca)

    /// <summary>
    /// Le da al bot montura o alas al nacer (o nada). La apariencia de TIERRA se actualiza también
    /// (LandBody/LandShield): así, si más adelante se sube a una barca y vuelve a bajar, recupera
    /// su montura en vez del cuerpo desnudo.
    /// </summary>
    public static void DarMonturaOAlas(NpcManager.NpcInstance bot)
    {
        int r = _rng.Next(100);
        if (r < PROB_ALAS)
        {
            bot.GuerraMontura = 2;
            bot.ShieldAnim = ALAS_ANIM; bot.LandShield = ALAS_ANIM;
        }
        else if (r < PROB_ALAS + PROB_MONTURA)
        {
            bot.GuerraMontura = 1;
            // 1 de cada 12 montados lleva la montura voladora (es la rara del juego).
            short body = _rng.Next(12) == 0 ? MONTURA_VOLADORA : _monturaBodies[_rng.Next(_monturaBodies.Length)];
            bot.Body = body; bot.LandBody = body;
        }
        else bot.GuerraMontura = 0;
    }

    // ============================================================
    //  Objetivos
    // ============================================================

    /// <summary>
    /// DUNGEONS que los ejércitos también recorren (además de asaltar ciudades enemigas). Son los
    /// mapas de entrada de cada uno, leídos del nombre en el header de los .csm:
    ///   Cristal 230-232 · Marabel 115-116 · Veriil 140-145 · Fárzhë 754-757/760 · Zero 209-211.
    /// Se apunta al PRIMER mapa de cada complejo; adentro siguen andando por los TileExits como en
    /// cualquier otro lado. Si desde donde está el bot no hay ruta, AsignarObjetivo elige otra cosa.
    /// </summary>
    private static readonly (int map, string nombre)[] _dungeons =
    {
        (230, "Dungeon Cristal"),
        (115, "Dungeon Marabel"),
        (140, "Dungeon Veriil"),
        (755, "Dungeon Fárzhë"),
        (209, "Dungeon Zero"),
    };

    /// <summary>Probabilidad de que un objetivo nuevo sea un dungeon en vez de una ciudad enemiga.</summary>
    private const int PROB_DUNGEON = 40;   // %

    // Tile de destino dentro de cada dungeon (uno caminable cerca del centro), cacheado.
    private static readonly Dictionary<int, (byte x, byte y)> _destinoDungeon = new();

    private static (byte x, byte y) DestinoEn(int map)
    {
        if (_destinoDungeon.TryGetValue(map, out var t)) return t;
        // Centro del mapa primero; si ahí no hay nada pisable (dungeons con formas raras), se
        // barre el mapa entero hasta encontrar el primer tile caminable.
        t = TileLibreCerca(map, 50, 50, 45);
        if (t.x == 0)
        {
            var md = MapLoader.Get(map);
            if (md != null)
                for (int y = 1; y <= 100 && t.x == 0; y++)
                    for (int x = 1; x <= 100; x++)
                        if (!md.IsBlocked(x, y) && !md.HasWater(x, y)) { t = ((byte)x, (byte)y); break; }
        }
        _destinoDungeon[map] = t;
        return t;
    }

    /// <summary>
    /// Le da al bot un objetivo de marcha: una ciudad ENEMIGA (la de otra facción, al azar entre
    /// las dos) o, con probabilidad PROB_DUNGEON, uno de los dungeons de la lista. El tile destino
    /// es siempre el MISMO punto por objetivo a propósito: así todos los bots que van al mismo lado
    /// comparten UN campo de flujo cacheado en vez de calcular uno cada uno.
    /// </summary>
    public static void AsignarObjetivo(NpcManager.NpcInstance bot)
    {
        if (bot == null) return;

        if (_dungeons.Length > 0 && _rng.Next(100) < PROB_DUNGEON)
        {
            var d = _dungeons[_rng.Next(_dungeons.Length)];
            var (dx, dy) = DestinoEn(d.map);
            if (dx != 0)
            {
                bot.GuerraDestMap = d.map;
                bot.GuerraDestX = dx; bot.GuerraDestY = dy;
                bot.GuerraLlegadaAt = 0; bot.GuerraStuck = 0;
                return;
            }
        }

        byte mia = bot.BotFaccion;
        var enemigas = _facciones.Where(f => f != mia).ToArray();
        if (enemigas.Length == 0) return;

        byte objetivo = enemigas[_rng.Next(enemigas.Length)];
        var ciudad = CityData.Get(CiudadDeFaccion(objetivo));
        if (ciudad.Map <= 0) return;

        bot.GuerraDestMap = ciudad.Map;
        // Jitter del punto de llegada: si TODOS apuntan al mismo tile exacto, el campo de flujo les
        // da a todos el mismo camino y la tropa marcha en fila india. Con ±2 hay 25 destinos
        // posibles (25 campos de flujo cacheados como mucho, ver BotPathing) y los caminos se abren.
        bot.GuerraDestX = (byte)Math.Clamp(ciudad.X + _rng.Next(-2, 3), 1, 99);
        bot.GuerraDestY = (byte)Math.Clamp(ciudad.Y + _rng.Next(-2, 3), 1, 99);
        bot.GuerraLlegadaAt = 0;
        bot.GuerraStuck = 0;
    }

    // ============================================================
    //  Utilidades de mapa
    // ============================================================

    /// <summary>
    /// Busca un tile CAMINABLE (no bloqueado, no agua) y sin NPC encima, en anillos crecientes
    /// alrededor de (x,y). Devuelve (0,0) si no encontró nada en el radio dado.
    /// Ojo: NpcManager.FreeTileNear sólo mira si hay un NPC, no si el tile es pisable — por eso
    /// esta versión, para no dejar bots naciendo dentro de una pared.
    /// </summary>
    public static (byte x, byte y) TileLibreCerca(int map, byte x, byte y, int radio)
    {
        var md = MapLoader.Get(map);
        if (md == null) return (0, 0);
        for (int r = 1; r <= radio; r++)
        {
            // Recorrer el anillo en orden al azar para que no se apilen todos del mismo lado.
            var candidatos = new List<(int nx, int ny)>();
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 1 || nx > 99 || ny < 1 || ny > 99) continue;
                    if (md.IsBlocked(nx, ny) || md.HasWater(nx, ny)) continue;
                    if (NpcManager.NpcAt(map, nx, ny) != null) continue;
                    candidatos.Add((nx, ny));
                }
            if (candidatos.Count == 0) continue;
            var (cx, cy) = candidatos[_rng.Next(candidatos.Count)];
            return ((byte)cx, (byte)cy);
        }
        return (0, 0);
    }
}
