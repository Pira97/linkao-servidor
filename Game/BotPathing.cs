namespace ServidorCS.Game;

/// <summary>
/// Navegación a escala de MUNDO para los bots de la guerra de facciones (NUEVO, no VB6).
///
/// El pathfinding que ya existía (SeekPathHeading en NpcManager) resuelve "dar un paso hacia un
/// tile del MISMO mapa" con un BFS por NPC y por tick, con una ventana chica. No sirve para
/// "cruzar el mundo hasta la ciudad enemiga": ni alcanza el rango, ni es sano correr 150 BFS
/// completos por tick. Acá hay dos piezas:
///
///  1) CAMPO DE FLUJO por mapa+destino: UN BFS inverso desde el destino que deja, en cada tile
///     caminable, el heading del próximo paso. Se cachea y lo comparten TODOS los bots que van
///     al mismo lado (los 50 de una facción marchando a la misma ciudad = 1 solo BFS).
///
///  2) GRAFO DEL MUNDO: qué mapa conecta con qué mapa vía TileExits. Se arma una sola vez
///     leyendo SÓLO la sección TE de cada .csm (parser liviano que saltea con Seek las capas
///     gráficas; NO usa MapLoader.Get para no cargar los 868 mapas enteros en RAM). Un BFS
///     sobre ese grafo dice a qué mapa vecino hay que salir para acercarse al objetivo.
///
/// Todo el estado es de sólo lectura tras armarse (datos estáticos del mapa), así que se calcula
/// perezosamente y se cachea sin invalidación.
/// </summary>
public static class BotPathing
{
    // Headings del juego (mismos que NpcManager): N=1, E=2, S=3, O=4.
    private const byte H_N = 1, H_E = 2, H_S = 3, H_O = 4;

    // ============================================================
    //  1) CAMPO DE FLUJO (dentro de un mapa)
    // ============================================================

    private sealed class FlowField
    {
        public byte[,] Heading = new byte[101, 101];   // 0 = tile sin camino al destino
        public long LastUsed;
    }

    private static readonly Dictionary<(int map, int tx, int ty, bool agua), FlowField> _flows = new();
    private const int MAX_FLOWS = 240;   // ~10KB cada uno; tope para no crecer sin límite

    /// <summary>
    /// ¿Puede un bot pisar este tile? Versión ESTÁTICA de PuedeNpc: sólo bloqueos del mapa y agua.
    /// A propósito NO mira NPCs ni usuarios: el campo de flujo es compartido y cacheado, no puede
    /// depender de quién está parado dónde. Los choques puntuales los resuelve el que camina
    /// (MoveNpcChar no mueve si el tile está ocupado, y el bot se desatasca con un paso al azar).
    /// </summary>
    private static bool Caminable(MapData md, int x, int y, bool agua)
    {
        if (x < 1 || x > 100 || y < 1 || y > 100) return false;
        if (md == null) return false;
        if (md.IsBlocked(x, y)) return false;
        if (!agua && md.HasWater(x, y)) return false;
        return true;
    }

    private static FlowField GetFlow(int map, int tx, int ty, bool agua)
    {
        var key = (map, tx, ty, agua);
        if (_flows.TryGetValue(key, out var f)) { f.LastUsed = Environment.TickCount64; return f; }

        var md = MapLoader.Get(map);
        if (md == null) return null;
        if (tx < 1 || tx > 100 || ty < 1 || ty > 100) return null;

        f = new FlowField { LastUsed = Environment.TickCount64 };
        var visited = new bool[101, 101];
        var q = new Queue<(int x, int y)>();
        visited[tx, ty] = true;
        q.Enqueue((tx, ty));

        // BFS INVERSO desde el destino: al descubrir un vecino, se le anota el heading que lo
        // lleva HACIA el tile desde el que se lo descubrió (o sea, un paso más cerca del destino).
        (int dx, int dy, byte hHaciaOrigen)[] dirs =
        {
            (0, -1, H_S),   // el vecino de arriba llega al actual yendo al SUR
            (1,  0, H_O),   // el de la derecha, yendo al OESTE
            (0,  1, H_N),
            (-1, 0, H_E),
        };

        while (q.Count > 0)
        {
            var (cx, cy) = q.Dequeue();
            foreach (var (dx, dy, h) in dirs)
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < 1 || nx > 100 || ny < 1 || ny > 100) continue;
                if (visited[nx, ny]) continue;
                if (!Caminable(md, nx, ny, agua)) continue;
                visited[nx, ny] = true;
                f.Heading[nx, ny] = h;
                q.Enqueue((nx, ny));
            }
        }

        if (_flows.Count >= MAX_FLOWS) PurgarFlows();
        _flows[key] = f;
        return f;
    }

    /// <summary>Descarta la mitad más vieja de los campos de flujo cacheados.</summary>
    private static void PurgarFlows()
    {
        var viejos = _flows.OrderBy(kv => kv.Value.LastUsed).Take(_flows.Count / 2).Select(kv => kv.Key).ToList();
        foreach (var k in viejos) _flows.Remove(k);
    }

    /// <summary>
    /// Descarta TODO lo cacheado de un mapa: campos de flujo y componentes conexas. Hay que
    /// llamarlo cuando algo cambia el mapa de verdad (una puerta se abre) — si no, el campo de
    /// flujo sigue creyendo bloqueado un tile que ya no lo está, y el bot de guerra que chocó con
    /// esa puerta la abre una y otra vez sin que el camino "rápido" (BotPathing) se entere nunca.
    /// </summary>
    public static void InvalidarMapa(int map)
    {
        foreach (var k in _flows.Keys.Where(k => k.map == map).ToList()) _flows.Remove(k);
        foreach (var k in _componentes.Keys.Where(k => k.map == map).ToList()) _componentes.Remove(k);
        // Las distancias entre mapas (BFS sobre el grafo de TileExits) no cambian por una puerta
        // que se abre DENTRO de un mapa —esas rutas son a nivel de mapa, no de tile—, así que no
        // hace falta tocar _distancias acá.
    }

    /// <summary>
    /// Heading del próximo paso desde (x,y) hacia (tx,ty) en el mismo mapa. 0 = ya llegó o no hay
    /// camino. El primer llamado a un destino nuevo calcula el BFS; los siguientes son O(1).
    /// </summary>
    public static byte NextHeading(int map, int x, int y, int tx, int ty, bool agua)
    {
        if (x == tx && y == ty) return 0;
        var f = GetFlow(map, tx, ty, agua);
        if (f == null) return 0;
        if (x < 1 || x > 100 || y < 1 || y > 100) return 0;
        return f.Heading[x, y];
    }

    /// <summary>true si desde (x,y) existe camino terrestre/naval hasta (tx,ty) en el mismo mapa.</summary>
    public static bool HayCamino(int map, int x, int y, int tx, int ty, bool agua)
        => (x == tx && y == ty) || NextHeading(map, x, y, tx, ty, agua) != 0;

    // ---- Componentes conexas (¿estos dos tiles se comunican caminando?) ----
    //
    // Preguntar "¿puedo llegar a esta salida?" con un campo de flujo por salida era carísimo: un
    // mapa tiene ~300 TileExits. Con UN etiquetado de componentes por mapa la respuesta es O(1)
    // y sirve para todas las salidas a la vez.
    private static readonly Dictionary<(int map, bool agua), int[,]> _componentes = new();
    // 40 KB por entrada: con ejércitos recorriendo el mundo entero esto podría crecer a cientos
    // de mapas, así que se vacía de golpe al pasar el tope (recalcular una es 1 BFS, barato).
    private const int MAX_COMPONENTES = 300;

    private static int[,] Componentes(int map, bool agua)
    {
        if (_componentes.TryGetValue((map, agua), out var c)) return c;
        if (_componentes.Count >= MAX_COMPONENTES) _componentes.Clear();
        var md = MapLoader.Get(map);
        if (md == null) return null;

        c = new int[101, 101];
        int label = 0;
        var q = new Queue<(int x, int y)>();
        for (int sy = 1; sy <= 100; sy++)
            for (int sx = 1; sx <= 100; sx++)
            {
                if (c[sx, sy] != 0 || !Caminable(md, sx, sy, agua)) continue;
                label++;
                c[sx, sy] = label;
                q.Enqueue((sx, sy));
                while (q.Count > 0)
                {
                    var (cx, cy) = q.Dequeue();
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = cx + (d == 1 ? 1 : d == 3 ? -1 : 0);
                        int ny = cy + (d == 0 ? -1 : d == 2 ? 1 : 0);
                        if (nx < 1 || nx > 100 || ny < 1 || ny > 100) continue;
                        if (c[nx, ny] != 0 || !Caminable(md, nx, ny, agua)) continue;
                        c[nx, ny] = label;
                        q.Enqueue((nx, ny));
                    }
                }
            }
        _componentes[(map, agua)] = c;
        return c;
    }

    /// <summary>true si (x1,y1) y (x2,y2) están en la misma zona caminable del mapa.</summary>
    public static bool MismaZona(int map, int x1, int y1, int x2, int y2, bool agua)
    {
        var c = Componentes(map, agua);
        if (c == null) return false;
        if (x1 < 1 || x1 > 100 || y1 < 1 || y1 > 100 || x2 < 1 || x2 > 100 || y2 < 1 || y2 > 100) return false;
        int a = c[x1, y1], b = c[x2, y2];
        return a != 0 && a == b;
    }

    // ============================================================
    //  2) GRAFO DEL MUNDO (mapa → mapa por TileExits)
    // ============================================================

    /// <summary>Una salida: el tile (X,Y) del mapa origen que lleva a (DestMap, DestX, DestY).</summary>
    public readonly struct Salida
    {
        public readonly int DestMap;
        public readonly byte X, Y, DestX, DestY;
        public Salida(int destMap, byte x, byte y, byte dx, byte dy)
        { DestMap = destMap; X = x; Y = y; DestX = dx; DestY = dy; }
    }

    private static Dictionary<int, List<Salida>> _grafo;
    private static Dictionary<int, List<int>> _grafoInverso;   // mapa → mapas desde los que se llega a él
    private static readonly object _grafoLock = new();

    /// <summary>Cantidad de mapas con salidas conocidas (0 = grafo no armado).</summary>
    public static int MapasEnGrafo => _grafo?.Count ?? 0;

    /// <summary>
    /// Arma el grafo del mundo si hace falta. Lee la sección TE de TODOS los .csm de Maps/.
    /// Tarda un par de segundos la primera vez y queda en memoria (unos pocos MB).
    /// </summary>
    public static void EnsureGrafo()
    {
        if (_grafo != null) return;
        lock (_grafoLock)
        {
            if (_grafo != null) return;
            var g = new Dictionary<int, List<Salida>>();
            long t0 = Environment.TickCount64;
            int mapas = 0, salidas = 0;
            try
            {
                foreach (string file in Directory.GetFiles(MapLoader.MapsPath, "mapa*.csm"))
                {
                    string nombre = Path.GetFileNameWithoutExtension(file);
                    if (!int.TryParse(nombre.AsSpan(4), out int num)) continue;
                    var lista = LeerSalidas(file);
                    if (lista == null || lista.Count == 0) continue;
                    g[num] = lista; mapas++; salidas += lista.Count;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[BotPathing] Error armando el grafo del mundo: {ex.Message}"); }

            // Grafo inverso: para saber "a qué distancia en mapas está CADA mapa del objetivo" se
            // hace UN BFS hacia atrás desde el objetivo (las salidas son dirigidas), en vez de un
            // BFS por candidato. Lo usa DistanciasHacia.
            var inv = new Dictionary<int, List<int>>();
            foreach (var (origen, salidas2) in g)
                foreach (var s in salidas2)
                {
                    if (!inv.TryGetValue(s.DestMap, out var l)) { l = new List<int>(); inv[s.DestMap] = l; }
                    if (!l.Contains(origen)) l.Add(origen);
                }
            _grafoInverso = inv;
            _grafo = g;
            Console.WriteLine($"[BotPathing] Grafo del mundo: {mapas} mapas, {salidas} salidas ({Environment.TickCount64 - t0}ms).");
        }
    }

    /// <summary>
    /// Parser LIVIANO de un .csm: lee el header, calcula el tamaño de cada sección y saltea con
    /// Seek hasta los TileExits. Mismo layout que MapLoader.Load (ver ahí el detalle del formato);
    /// acá sólo interesa la última sección, así que no se materializa nada más.
    /// </summary>
    private static List<Salida> LeerSalidas(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            using var br = new BinaryReader(fs);

            int numBloq = br.ReadInt32();
            int numL2 = br.ReadInt32(), numL3 = br.ReadInt32(), numL4 = br.ReadInt32();
            int numTrig = br.ReadInt32();
            int numLuces = br.ReadInt32();
            int numPart = br.ReadInt32();
            int numNpcs = br.ReadInt32();
            int numObjs = br.ReadInt32();
            int numTE = br.ReadInt32();
            if (numTE <= 0) return null;

            short xMax = br.ReadInt16(), xMin = br.ReadInt16(), yMax = br.ReadInt16(), yMin = br.ReadInt16();
            int ancho = xMax - xMin + 1, alto = yMax - yMin + 1;
            if (ancho <= 0 || alto <= 0) return null;

            long pos = fs.Position;
            pos += 182;                       // tMapDat
            pos += (long)ancho * alto * 4;    // capa gráfica L1
            pos += (long)numBloq * 4;         // bloqueados (X,Y int)
            pos += (long)(numL2 + numL3 + numL4) * 8;
            pos += (long)numTrig * 6;
            pos += (long)numPart * 8;
            pos += (long)numLuces * 9;
            pos += (long)numObjs * 8;
            pos += (long)numNpcs * 6;
            if (pos + (long)numTE * 10 > fs.Length) return null;   // layout inesperado: no inventar
            fs.Position = pos;

            var lista = new List<Salida>(numTE);
            for (int i = 0; i < numTE; i++)
            {
                short x = br.ReadInt16(), y = br.ReadInt16();
                short dm = br.ReadInt16(), dx = br.ReadInt16(), dy = br.ReadInt16();
                if (dm <= 0 || x < 1 || x > 100 || y < 1 || y > 100) continue;
                if (dx < 1 || dx > 100 || dy < 1 || dy > 100) continue;
                lista.Add(new Salida(dm, (byte)x, (byte)y, (byte)dx, (byte)dy));
            }
            return lista;
        }
        catch { return null; }
    }

    /// <summary>Salidas conocidas de un mapa (vacío si no tiene o no está en el grafo).</summary>
    public static IReadOnlyList<Salida> SalidasDe(int map)
    {
        EnsureGrafo();
        return _grafo.TryGetValue(map, out var l) ? l : (IReadOnlyList<Salida>)Array.Empty<Salida>();
    }

    // Distancia EN MAPAS de cada mapa al destino, cacheada por destino.
    private static readonly Dictionary<int, Dictionary<int, int>> _distancias = new();

    /// <summary>
    /// Cuántos cambios de mapa faltan desde cada mapa hasta <paramref name="destMap"/> (BFS sobre
    /// el grafo INVERSO, una sola vez por destino). Los mapas que no aparecen no llegan al destino.
    /// </summary>
    private static Dictionary<int, int> DistanciasHacia(int destMap)
    {
        EnsureGrafo();
        if (_distancias.TryGetValue(destMap, out var d)) return d;

        d = new Dictionary<int, int> { [destMap] = 0 };
        var q = new Queue<int>();
        q.Enqueue(destMap);
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            if (!_grafoInverso.TryGetValue(cur, out var origenes)) continue;
            foreach (int o in origenes)
            {
                if (d.ContainsKey(o)) continue;
                d[o] = d[cur] + 1;
                q.Enqueue(o);
            }
        }
        _distancias[destMap] = d;
        return d;
    }

    /// <summary>
    /// Salida del mapa actual por la que conviene irse para acercarse a <paramref name="destMap"/>.
    ///
    /// Sólo considera salidas que están en la MISMA zona caminable que el bot: seguir "la ruta más
    /// corta del grafo" a ciegas dejaba ejércitos enteros encerrados (caso real: los bots del Caos
    /// no salían nunca de Rinkel porque la ruta óptima pasaba por una salida al mapa 16 que desde
    /// el centro de la ciudad es inalcanzable). Entre las alcanzables elige la que deja más cerca
    /// del objetivo en cantidad de mapas, y a igualdad la más próxima a pie.
    /// false = desde acá no se llega al objetivo (el que llama debería cambiar de objetivo).
    /// </summary>
    public static bool SalidaHacia(int map, int x, int y, int destMap, bool agua, out Salida salida)
    {
        salida = default;
        if (map == destMap) return false;

        var dist = DistanciasHacia(destMap);
        if (!dist.TryGetValue(map, out int distActual)) return false;   // desde este mapa no se llega

        Salida mejor = default; int mejorCosto = int.MaxValue, mejorD = int.MaxValue; bool hay = false;
        foreach (var s in SalidasDe(map))
        {
            if (!dist.TryGetValue(s.DestMap, out int costo)) continue;   // esa salida no acerca a nada
            if (costo >= distActual) continue;                           // no acerca: alejarse no sirve
            int d = Math.Abs(s.X - x) + Math.Abs(s.Y - y);
            if (costo > mejorCosto || (costo == mejorCosto && d >= mejorD)) continue;
            if (d > 0 && !MismaZona(map, x, y, s.X, s.Y, agua)) continue; // encerrada: no es opción
            mejor = s; mejorCosto = costo; mejorD = d; hay = true;
        }
        salida = mejor;
        return hay;
    }
}
