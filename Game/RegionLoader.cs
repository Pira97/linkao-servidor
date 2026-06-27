namespace ServidorCS.Game;

/// <summary>
/// Ensambla el mundo único (<see cref="RegionData"/>) a partir de los chunks del overworld, usando
/// los offsets de <see cref="RegionLayout"/> y la regla overlay-POR-CENTRALIDAD:
///   cada tile global lo posee el mapa para el que es más central (min distancia a su borde local).
/// Así el contenido caminable del vecino sobrescribe el margen bloqueado (anillo exterior ~6 tiles)
/// y los seams internos quedan continuos. Validado en tools/mundo_continuo (0 islas, 97% seams
/// transitables). Ver [[mundo_continuo_analisis_bordes]].
///
/// Reutiliza <see cref="MapLoader.Get"/> para el parseo de cada .csm (no duplica el lector binario).
/// Aditivo: nada del runtime llama a Build todavía. SelfTest() permite correrlo aislado
/// (Program.cs --regiontest) para verificar contra los mapas reales.
/// </summary>
public static class RegionLoader
{
    /// <summary>Centralidad de un tile local (mayor = más adentro del mapa). 0 en el borde, 49 al centro.</summary>
    private static int Centrality(int x, int y) => Math.Min(Math.Min(x - 1, 100 - x), Math.Min(y - 1, 100 - y));

    private static RegionData _cached;

    /// <summary>Construye (o devuelve cacheada) la RegionData. null si no hay layout cargado.</summary>
    public static RegionData Build()
    {
        if (_cached != null) return _cached;
        if (RegionLayout.Width <= 0 || RegionLayout.MapCount == 0)
        {
            // fuerza carga perezosa del layout
            if (!RegionLayout.InRegion(1) && RegionLayout.MapCount == 0)
            {
                Console.WriteLine("[RegionLoader] Sin layout (region_layout.json) — no se puede ensamblar.");
                return null;
            }
        }

        int W = RegionLayout.Width, H = RegionLayout.Height;
        var rd = new RegionData(W, H);
        var ownCentrality = new short[W + 1, H + 1];
        for (int i = 0; i <= W; i++)
            for (int j = 0; j <= H; j++)
                ownCentrality[i, j] = -1;

        int mapsAssembled = 0;
        // Recorremos todos los mapas del layout. RegionLayout no expone el set; iteramos por número
        // y filtramos con InRegion (los números de mapa del overworld llegan hasta ~749).
        for (int map = 1; map <= 1000; map++)
        {
            if (!RegionLayout.TryGetOffset(map, out var off)) continue;
            var md = MapLoader.Get(map);
            if (md == null) continue;
            mapsAssembled++;

            for (int x = 1; x <= 100; x++)
                for (int y = 1; y <= 100; y++)
                {
                    int gx = off.X + x, gy = off.Y + y;
                    if (gx < 1 || gy < 1 || gx > W || gy > H) continue;
                    int c = Centrality(x, y);
                    if (c <= ownCentrality[gx, gy]) continue; // el dueño actual es igual o más central

                    ownCentrality[gx, gy] = (short)c;
                    rd.Owner[gx, gy] = (short)map;
                    rd.Blocked[gx, gy] = md.Blocked[x, y];
                    rd.Water[gx, gy] = md.Water[x, y];
                    rd.Trigger[gx, gy] = md.Trigger[x, y];
                    rd.FloorObj[gx, gy] = md.FloorObj[x, y];
                    rd.FloorAmount[gx, gy] = md.FloorAmount[x, y];

                    // Exit sólo si SALE de la región (a un interior). Los seams internos se descartan.
                    var e = md.Exits[x, y];
                    if (e.HasValue && !RegionLayout.InRegion(e.Value.DestMap))
                        rd.Exits[(gx, gy)] = e.Value;
                    else
                        rd.Exits.Remove((gx, gy)); // por si un dueño previo (menos central) dejó uno
                }
        }

        Console.WriteLine($"[RegionLoader] Región ensamblada: {mapsAssembled} mapas, {W}x{H}, {rd.Exits.Count} exits a interiores.");
        _cached = rd;
        return rd;
    }

    /// <summary>
    /// Corre el ensamblado contra los mapas reales y valida las invariantes (mismas métricas que
    /// tools/mundo_continuo/assemble_region.py): tiles con dueño, caminables, islas 1x1 (deben ser 0),
    /// y % de cruces de dueño (seams) que quedan transitables. Para Program.cs --regiontest.
    /// </summary>
    public static void SelfTest()
    {
        var rd = Build();
        if (rd == null) { Console.WriteLine("[RegionLoader.SelfTest] Build() devolvió null."); return; }
        int W = rd.Width, H = rd.Height;

        long owned = 0, walkable = 0;
        for (int gx = 1; gx <= W; gx++)
            for (int gy = 1; gy <= H; gy++)
            {
                if (rd.Owner[gx, gy] == 0) continue;
                owned++;
                if (!rd.Blocked[gx, gy]) walkable++;
            }

        // islas 1x1 y seams transitables
        long isolated = 0, seamOk = 0, seamWall = 0;
        for (int gx = 1; gx <= W; gx++)
            for (int gy = 1; gy <= H; gy++)
            {
                if (rd.Owner[gx, gy] == 0 || rd.Blocked[gx, gy]) continue;
                int nb = 0;
                if (!rd.IsBlocked(gx + 1, gy)) nb++;
                if (!rd.IsBlocked(gx - 1, gy)) nb++;
                if (!rd.IsBlocked(gx, gy + 1)) nb++;
                if (!rd.IsBlocked(gx, gy - 1)) nb++;
                if (nb == 0) isolated++;

                // cruces de dueño hacia E y S
                foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
                {
                    int nx = gx + dx, ny = gy + dy;
                    if (!rd.InBounds(nx, ny)) continue;
                    short no = rd.Owner[nx, ny];
                    if (no != 0 && no != rd.Owner[gx, gy])
                    {
                        if (!rd.Blocked[nx, ny]) seamOk++; else seamWall++;
                    }
                }
            }

        long seamTot = seamOk + seamWall;
        Console.WriteLine($"[RegionLoader.SelfTest] tiles con dueño={owned:N0} caminables={walkable:N0} exits-interior={rd.Exits.Count}");
        Console.WriteLine($"[RegionLoader.SelfTest] islas 1x1={isolated} (debe ser ~0) | seams={seamTot:N0} transitables={(seamTot > 0 ? seamOk * 100.0 / seamTot : 0):F1}% pared={seamWall}");
    }
}
