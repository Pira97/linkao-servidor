namespace ServidorCS.Game;

/// <summary>
/// Carga de mapas .csm. Porta CargarMapa (FileIO.bas), versión mínima: lee el header,
/// MapSize, salta MapDat y la capa gráfica L1, y carga los tiles BLOQUEADOS (lo que
/// el server necesita para validar movimiento). Layers gráficos, triggers, luces,
/// objetos y NPCs se leen al portar esas features.
///
/// Layout binario .csm (little-endian, igual que Get# de VB6):
///   tMapHeader  : 10 × Long  (NumeroBloqueados, NumeroLayers[2..4], Triggers, Luces,
///                             Particulas, NPCs, OBJs, TE)          = 40 bytes
///   tMapSize    : 4 × Integer (XMax, XMin, YMax, YMin)             = 8 bytes
///   tMapDat     : struct de strings fijos                          = 182 bytes
///   L1          : (ancho×alto) × Long  (capa gráfica base)
///   Bloqueados  : NumeroBloqueados × (X:Int, Y:Int)
/// </summary>
public struct MapNpc { public short X, Y, NpcIndex; }
public struct MapObj { public short X, Y, ObjIndex, Amount; }
public struct TileExit { public short DestMap, DestX, DestY; }
public struct MapParticle { public short X, Y; public int Particula; }

/// <summary>eTrigger (Declares.bas:279). Valores exactos del VB6.</summary>
public static class eTrigger
{
    public const byte Nada = 0;
    public const byte BAJOTECHO = 1;
    public const byte trigger_2 = 2;
    public const byte POSINVALIDA = 3;   // los NPCs no pueden pisar este tile
    public const byte ZONASEGURA = 4;    // no se puede robar/invocar/apropiar
    public const byte ANTIPIQUETE = 5;   // anti-camping (encarcela)
    public const byte ZONAPELEA = 6;     // arena: no caen items ni cambia estado al pelear
}

/// <summary>eTrigger6 (Declares.bas:299) — resultado de TriggerZonaPelea.</summary>
public enum eTrigger6 { TRIGGER6_PERMITE = 1, TRIGGER6_PROHIBE = 2, TRIGGER6_AUSENTE = 3 }

/// <summary>Info de mapa (propiedades persistibles). Equivale a MapInfo(Map) del VB6.</summary>
public sealed class MapInfo
{
    public bool Pk;
    public bool Backup;
    public bool Restricted;
    public bool NoMagia;
    public bool NoInvi;
    public bool NoResu;
    public bool Land;
    public int Zone;
    public string Zona = ""; // tMapDat.zone (offset 86, 16 bytes). "DUNGEON" → mapa interior. [[clima]]
    public string Terreno = ""; // tMapDat.terrain (offset 102, 16 bytes). "NIEVE" → frío quita vida (EfectoFrio).
}

public sealed class MapData
{
    public int XMin, XMax, YMin, YMax;
    public bool[,] Blocked = new bool[101, 101];
    public TileExit?[,] Exits = new TileExit?[101, 101];
    // Trigger por tile (eTrigger: 0=nada, 1=bajotecho, etc.) — VB6 MapData.Trigger
    public byte[,] Trigger = new byte[101, 101];
    // Agua por tile (HayAgua: Graphic(1) en rango de agua Y Graphic(2)=0). Precalculado al cargar.
    public bool[,] Water = new bool[101, 101];
    public List<MapNpc> Npcs = new();
    public List<MapObj> Objs = new();
    // Partículas ambientales del mapa (fuego, fuentes, etc.) — (X,Y,ParticulaIndex). Se envían al loguear.
    public List<MapParticle> Particles = new();
    public short[,] FloorObj = new short[101, 101];
    public int[,] FloorAmount = new int[101, 101];
    // Info del mapa (PK, backup, etc.)
    public MapInfo Info = new();

    public bool IsBlocked(int x, int y)
    {
        if (x < 1 || x > 100 || y < 1 || y > 100) return true;
        return Blocked[x, y];
    }

    public TileExit? GetExit(int x, int y)
    {
        if (x < 1 || x > 100 || y < 1 || y > 100) return null;
        return Exits[x, y];
    }

    /// <summary>Trigger del tile (0 si fuera de límites). 1:1 con MapData(map,x,y).Trigger.</summary>
    public byte GetTrigger(int x, int y)
    {
        if (x < 1 || x > 100 || y < 1 || y > 100) return 0;
        return Trigger[x, y];
    }

    /// <summary>HayAgua (General.bas:297): true si el tile es agua navegable (no puente).</summary>
    public bool HasWater(int x, int y)
    {
        if (x < 1 || x > 100 || y < 1 || y > 100) return false;
        return Water[x, y];
    }
}

public static class MapLoader
{
    private const int MAP_DAT_SIZE = 182; // tamaño fijo de tMapDat

    public static string MapsPath = ResolveMapsPath();
    private static readonly Dictionary<int, MapData> _cache = new();

    /// <summary>Mapas ya cargados en memoria (los únicos que pueden tener objetos tirados en runtime).</summary>
    public static IEnumerable<KeyValuePair<int, MapData>> LoadedMaps => _cache;

    private static string ResolveMapsPath()
    {
        if (!string.IsNullOrEmpty(DataPaths.Root)) return DataPaths.Sub("Maps");
        foreach (var c in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Maps"),
            Path.Combine(Directory.GetCurrentDirectory(), "Maps"),
        })
        {
            if (Directory.Exists(c)) return c + Path.DirectorySeparatorChar;
        }
        return "Maps" + Path.DirectorySeparatorChar;
    }

    /// <summary>Devuelve el mapa (cacheado). null si no se pudo cargar.</summary>
    public static MapData Get(int mapNumber)
    {
        if (_cache.TryGetValue(mapNumber, out var m)) return m;
        m = Load(mapNumber);
        if (m != null) _cache[mapNumber] = m;
        return m;
    }

    private static MapData Load(int mapNumber)
    {
        string file = Path.Combine(MapsPath, "mapa" + mapNumber + ".csm");
        Console.WriteLine($"[MapLoader] Cargando mapa {mapNumber} desde: {file}");
        if (!File.Exists(file)) { Console.WriteLine($"[MapLoader] Archivo NO existe"); return null; }

        try
        {
            byte[] d = File.ReadAllBytes(file);
            int p = 0;
            int Long()  { int v = BitConverter.ToInt32(d, p); p += 4; return v; }
            short Int() { short v = BitConverter.ToInt16(d, p); p += 2; return v; }

            // tMapHeader: 10 longs (NumeroBloqueados, NumeroLayers[2..4], Triggers,
            // Luces, Particulas, NPCs, OBJs, TE).
            int numBloq  = Long();
            int numL2    = Long(); int numL3 = Long(); int numL4 = Long();
            int numTrig  = Long();
            int numLuces = Long();
            int numPart  = Long();
            int numNpcs  = Long();
            int numObjs  = Long();
            int numTE    = Long();

            var map = new MapData();
            map.XMax = Int(); map.XMin = Int(); map.YMax = Int(); map.YMin = Int();

            // tMapDat (182 bytes): map_name(64) + battle_mode(1) + ...
            // VB6 ES.CargarMapa (FileIO.bas:1562): battle_mode==0 → Pk=True (caen cosas);
            // battle_mode!=0 → Pk=False (seguro). Excepción: mapa 457 siempre seguro.
            byte battleMode = d[p + 64];
            map.Info.Pk = (battleMode == 0) && mapNumber != 457;
            // tMapDat.zone: offset 86, String*16 (CP1252, padded). Usado por el clima (EsDungeon).
            map.Info.Zona = Network.Cp1252.GetString(d, p + 86, 16).Replace("\0", "").Trim();
            // tMapDat.terrain: offset 102, String*16. "NIEVE" → EfectoFrio quita vida en vez de stamina.
            map.Info.Terreno = Network.Cp1252.GetString(d, p + 102, 16).Replace("\0", "").Trim();
            p += MAP_DAT_SIZE;        // saltar el resto del tMapDat

            int ancho = map.XMax - map.XMin + 1;
            int alto  = map.YMax - map.YMin + 1;

            // Capa gráfica L1 (Long por tile). VB6: ReDim L1(XMin..XMax, YMin..YMax) leído
            // con Get column-major → X varía primero (interno), Y externo. Guardamos Graphic(1)
            // sólo para detectar agua (HayAgua).
            var graphic1 = new int[101, 101];
            for (int y = map.YMin; y <= map.YMax; y++)
                for (int x = map.XMin; x <= map.XMax; x++)
                {
                    int grh = Long();
                    if (x >= 1 && x <= 100 && y >= 1 && y <= 100) graphic1[x, y] = grh;
                }

            // Bloqueados: N × (X,Y) int
            for (int i = 0; i < numBloq; i++)
            {
                short x = Int(); short y = Int();
                if (x >= 1 && x <= 100 && y >= 1 && y <= 100) map.Blocked[x, y] = true;
            }

            // Layer 2: (X,Y,GrhIndex) = 8 bytes. Si Graphic(2)<>0 el tile NO es agua (puente/borde).
            var graphic2 = new bool[101, 101];
            for (int i = 0; i < numL2; i++)
            {
                short x = Int(); short y = Int(); int grh = Long();
                if (grh != 0 && x >= 1 && x <= 100 && y >= 1 && y <= 100) graphic2[x, y] = true;
            }
            // Layers 3/4: (X,Y,GrhIndex) = 8 bytes. Se saltan.
            p += (numL3 + numL4) * 8;

            // Precalcular agua (HayAgua): Graphic(1) en rango de agua Y sin Graphic(2).
            for (int y = 1; y <= 100; y++)
                for (int x = 1; x <= 100; x++)
                {
                    int g1 = graphic1[x, y];
                    bool agua = (g1 >= 1505 && g1 <= 1520) || (g1 >= 5665 && g1 <= 5680)
                              || (g1 >= 13547 && g1 <= 13562);
                    map.Water[x, y] = agua && !graphic2[x, y];
                }

            // Triggers: (X,Y int, Trigger int) = 6 bytes. Se cargan en map.Trigger (eTrigger por tile).
            for (int i = 0; i < numTrig; i++)
            {
                short tx = Int(); short ty = Int(); short trig = Int();
                if (tx >= 1 && tx <= 100 && ty >= 1 && ty <= 100) map.Trigger[tx, ty] = (byte)trig;
            }

            // Particulas: (X,Y int, Particula long) = 8 bytes. Se cargan para enviarlas al loguear.
            for (int i = 0; i < numPart; i++)
            {
                short px = Int(); short py = Int(); int part = Long();
                if (part > 0 && px >= 1 && px <= 100 && py >= 1 && py <= 100)
                    map.Particles.Add(new MapParticle { X = px, Y = py, Particula = part });
            }

            // Luces: (X,Y,color long,Rango byte) = 9 bytes (VB6 sin padding en arrays). Se saltan.
            p += numLuces * 9;

            // OBJs: (X,Y,ObjIndex,Amount) = 8 bytes.
            int objsCargados = 0;
            for (int i = 0; i < numObjs; i++)
            {
                var o = new MapObj { X = Int(), Y = Int(), ObjIndex = Int(), Amount = Int() };
                if (o.ObjIndex > 0)
                {
                    map.Objs.Add(o);
                    if (o.X >= 1 && o.X <= 100 && o.Y >= 1 && o.Y <= 100)
                    {
                        map.FloorObj[o.X, o.Y] = o.ObjIndex;
                        map.FloorAmount[o.X, o.Y] = o.Amount;
                        objsCargados++;
                    }
                }
            }
            Console.WriteLine($"[MapLoader] Mapa {mapNumber}: {objsCargados} objetos cargados en FloorObj, Pk={map.Info.Pk}");

            // NPCs: (X,Y,npcindex) = 6 bytes.
            for (int i = 0; i < numNpcs; i++)
            {
                var n = new MapNpc { X = Int(), Y = Int(), NpcIndex = Int() };
                if (n.NpcIndex > 0) map.Npcs.Add(n);
            }

            // TE (TileExits): (X,Y,DestMap,DestX,DestY) = 10 bytes. Al pisar → cambio de mapa.
            for (int i = 0; i < numTE; i++)
            {
                short x = Int(); short y = Int();
                short dm = Int(); short dx = Int(); short dy = Int();
                if (x >= 1 && x <= 100 && y >= 1 && y <= 100 && dm > 0)
                    map.Exits[x, y] = new TileExit { DestMap = dm, DestX = dx, DestY = dy };
            }

            return map;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ServidorCS] Error cargando mapa {mapNumber}: {ex.Message}");
            return null;
        }
    }
}
