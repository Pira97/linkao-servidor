using System.Text.Json;

namespace ServidorCS.Game;

/// <summary>
/// Mundo único (mapa continuo). Carga region_layout.json: el offset global de cada mapa del
/// overworld, precalculado y validado (0 conflictos) a partir de los TileExits de borde. Es la
/// ÚNICA fuente de verdad de la geometría del mundo — el cliente carga el mismo archivo.
///
/// Convención: global(map,x,y) = (Offset[map].X + x, Offset[map].Y + y), con x,y en 1..100.
/// Los mapas adyacentes se SOLAPAN (pitch ~82 horizontal / ~86 vertical, no 100): el offset ya
/// codifica ese solape, por eso NO se calcula como col*100. Ver [[mundo_continuo_analisis_bordes]].
///
/// Los mapas que NO están en la tabla (dungeons/interiores) NO son parte de la región continua:
/// siguen siendo mapas standalone y su cruce sigue usando ChangeMap.
///
/// Este archivo es ADITIVO: nada lo llama todavía. La carga es perezosa (EnsureLoaded), así que no
/// toca el arranque ni el flujo existente del servidor.
/// </summary>
public static class RegionLayout
{
    public readonly struct Offset
    {
        public readonly int X, Y;
        public Offset(int x, int y) { X = x; Y = y; }
    }

    private static readonly Dictionary<int, Offset> _off = new();
    private static bool _loaded;

    /// <summary>Ancho de la región continua en tiles.</summary>
    public static int Width { get; private set; }
    /// <summary>Alto de la región continua en tiles.</summary>
    public static int Height { get; private set; }
    /// <summary>Cantidad de mapas que forman la región continua.</summary>
    public static int MapCount => _off.Count;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true; // aunque falle, no reintentar en cada acceso
        try
        {
            string file = Path.Combine(MapLoader.MapsPath, "region_layout.json");
            if (!File.Exists(file))
            {
                Console.WriteLine($"[RegionLayout] region_layout.json no encontrado en {MapLoader.MapsPath} — mundo continuo deshabilitado.");
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = doc.RootElement;
            Width = root.GetProperty("width").GetInt32();
            Height = root.GetProperty("height").GetInt32();
            foreach (var prop in root.GetProperty("offsets").EnumerateObject())
            {
                int map = int.Parse(prop.Name);
                var arr = prop.Value;
                _off[map] = new Offset(arr[0].GetInt32(), arr[1].GetInt32());
            }
            Console.WriteLine($"[RegionLayout] Región cargada: {_off.Count} mapas, {Width}x{Height} tiles.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RegionLayout] Error cargando region_layout.json: {ex.Message}");
        }
    }

    /// <summary>¿El mapa forma parte de la región continua (overworld)?</summary>
    public static bool InRegion(int map)
    {
        EnsureLoaded();
        return _off.ContainsKey(map);
    }

    /// <summary>Offset global del mapa. Devuelve false si el mapa no es de la región.</summary>
    public static bool TryGetOffset(int map, out Offset offset)
    {
        EnsureLoaded();
        return _off.TryGetValue(map, out offset);
    }

    /// <summary>
    /// Convierte una posición local (map,x,y) a coordenada global de la región. Devuelve false si el
    /// mapa no es de la región continua (dungeon/interior).
    /// </summary>
    public static bool TryLocalToGlobal(int map, int x, int y, out int gx, out int gy)
    {
        EnsureLoaded();
        if (_off.TryGetValue(map, out var o)) { gx = o.X + x; gy = o.Y + y; return true; }
        gx = 0; gy = 0; return false;
    }

    /// <summary>
    /// Distancia global (en tiles) entre dos posiciones locales, sólo si ambas están en la región.
    /// Útil para AOI/visibilidad a través de bordes. Devuelve false si alguna no es de la región.
    /// </summary>
    public static bool TryGlobalDelta(int mapA, int ax, int ay, int mapB, int bx, int by, out int dx, out int dy)
    {
        EnsureLoaded();
        if (_off.TryGetValue(mapA, out var oa) && _off.TryGetValue(mapB, out var ob))
        {
            dx = (ob.X + bx) - (oa.X + ax);
            dy = (ob.Y + by) - (oa.Y + ay);
            return true;
        }
        dx = 0; dy = 0; return false;
    }

    // Mapas "cercanos" a cada mapa de la región (el propio + los adyacentes cuyo origen global está a
    // <= ~110 tiles = los 8 vecinos de grilla; el 2º anillo está a >=164). Pre-filtro grueso para el
    // AOI cross-map de NPCs/objetos; la visibilidad fina la decide VeContinuo (distancia de bloques).
    private static Dictionary<int, List<int>> _nearby;
    private static readonly List<int> _emptyMaps = new();

    public static IReadOnlyList<int> NearbyMaps(int map)
    {
        EnsureLoaded();
        if (_nearby == null) BuildNearby();
        return _nearby.TryGetValue(map, out var l) ? l : _emptyMaps;
    }

    /// <summary>
    /// Resuelve una coordenada GLOBAL a (mapa, x, y) local, buscando entre el mapa 'hint' y sus
    /// vecinos el que la posee (coord local en 1..100), prefiriendo el más CENTRAL en caso de solape
    /// (misma regla de dueño que RegionData). Devuelve false si ninguno la contiene. Para resolver
    /// clics/warps que caen en el mapa de al lado (mundo continuo).
    /// </summary>
    public static bool TryGlobalToLocal(int hintMap, int gx, int gy, out int map, out int x, out int y)
    {
        EnsureLoaded();
        map = 0; x = 0; y = 0;
        int bestCentr = -1;
        foreach (int m in NearbyMaps(hintMap))
        {
            if (!_off.TryGetValue(m, out var o)) continue;
            int lx = gx - o.X, ly = gy - o.Y;
            if (lx < 1 || lx > 100 || ly < 1 || ly > 100) continue;
            int c = Math.Min(Math.Min(lx - 1, 100 - lx), Math.Min(ly - 1, 100 - ly));
            if (c > bestCentr) { bestCentr = c; map = m; x = lx; y = ly; }
        }
        return bestCentr >= 0;
    }

    private static void BuildNearby()
    {
        const int TH = 110;
        _nearby = new Dictionary<int, List<int>>(_off.Count);
        var keys = new List<int>(_off.Keys);
        foreach (int a in keys)
        {
            var la = _off[a];
            var list = new List<int> { a };
            foreach (int b in keys)
            {
                if (b == a) continue;
                var lb = _off[b];
                if (Math.Abs(la.X - lb.X) <= TH && Math.Abs(la.Y - lb.Y) <= TH)
                    list.Add(b);
            }
            _nearby[a] = list;
        }
    }
}
