using System.Text.Json;

namespace ServidorCS.Game;

/// <summary>
/// La decoración de un teleport creado con /ct: el nombre que le puso el GM y qué shader lo
/// dibuja en el cliente. NO es el teleport: el teleport son FloorObj + Exits, que ya existían.
/// Esto es lo que se le agregó encima para que el portal tenga cara y cartel.
///
/// Shader es un ÍNDICE, no un nombre: el que tiene ese efecto en mini/portal_shaders.js
/// (constante ORDEN). Por eso ese array se amplía SIEMPRE por el final — reordenarlo le
/// cambia el efecto a todos los portales ya guardados en disco.
///
/// Vel y Escala van ×100 en un byte (0.01..2.55). Un float sería 4 bytes por portal, en el
/// cable y en el archivo, para una diferencia que no se ve.
/// </summary>
public struct PortalDef
{
    public byte Shader;
    public byte R, G, B;
    public byte Vel, Escala;
    public string Nombre;

    public static PortalDef PorDefecto() =>
        new PortalDef { Shader = 0, R = 0x7a, G = 0x5c, B = 0xff, Vel = 100, Escala = 100, Nombre = "" };
}

/// <summary>
/// Los portales del mundo y su archivo.
///
/// EN MEMORIA, como fue siempre: un /ct dura hasta que se reinicia el server. Existe además
/// la maquinaria para guardarlos en Dat/Portales.json (Cargar/Guardar, que recrean el objeto
/// y el Exit al arrancar), pero está APAGADA — ver el flag Persistir y por qué.
///
/// Si se vuelve a prender: el archivo es uno nuevo, NO se toca ningún .dat existente ni el
/// .csm del mapa, y uno corrupto o borrado significa "no hay portales", nunca un server que
/// no levanta.
/// </summary>
public static class Portales
{
    // (mapa, x*101+y) -> definición. Sólo los creados con /ct: los teleports fijos del .csm
    // no están acá y siguen dibujándose como siempre.
    private static readonly Dictionary<(short, int), PortalDef> _defs = new();
    private static string _ruta = "";
    private static bool _sucio = false;

    /// <summary>
    /// ¿Los portales sobreviven al reinicio del server?
    ///
    /// APAGADO (31-ago-2026, pedido del usuario). La persistencia se agregó de más: un /ct
    /// siempre había durado hasta el reinicio, y hacerlo permanente convirtió cada portal de
    /// prueba en mobiliario fijo del mundo — quedaron treinta desparramados en ocho mapas y
    /// no había forma cómoda de sacarlos (/dt sólo borra el tile que pisás).
    ///
    /// Apagar esto alcanza para las dos mitades: Cargar() no lee nada y, como _ruta queda en
    /// "", Guardar() tampoco escribe. Dat/Portales.json NO se borra — queda ahí con los que
    /// ya estaban, por si algún día se quiere volver a prenderlo.
    /// </summary>
    public static bool Persistir = false;

    private record struct Fila(short Mapa, byte X, byte Y, short DestMapa, byte DestX, byte DestY,
                               byte Shader, byte R, byte G, byte B, byte Vel, byte Escala, string Nombre);

    // Portal -> los tiles VECINOS a los que se les copió el TileExit para que el área que se
    // pisa coincida con el tamaño del efecto. Se guarda la lista exacta que se plantó (y no se
    // recalcula al borrar) porque al plantar se SALTEAN tiles que ya tenían dueño: recalcular
    // borraría salidas ajenas.
    private static readonly Dictionary<(short, int), int[]> _area = new();

    /// <summary>
    /// Por índice de shader: el lado del quad en tiles, y qué fracción de ese quad PINTA de
    /// verdad el efecto. ESPEJO EXACTO de los campos `tiles` y `visible` de PORTALES en
    /// mini/portal_shaders.js, en el orden de ORDEN.
    ///
    /// El segundo número no es cosmético: casi ningún shader llena su cuadro (anillos pinta el
    /// 63%, y las esquinas están vacías siempre). Sin él, el área que teletransporta sale más
    /// grande que el dibujo y se entra al portal desde donde no se ve nada. Cómo se midieron
    /// está documentado en portal_shaders.js, arriba del catálogo.
    ///
    /// 🔴 Si allá se agrega un shader al final, acá va su par en la misma posición.
    /// </summary>
    private static readonly (byte Tiles, double Visible)[] _formaPorShader =
    {
        (3, 0.63),   // anillos
        (3, 0.75),   // tunel
        (4, 0.95),   // singularidad
        (4, 0.94),   // remolino
        (3, 0.89),   // aro_fuego
        (4, 0.88),   // arcoiris
    };

    /// <summary>
    /// Radio de lo que el cliente DIBUJA, en tiles: medio lado del quad por la fracción que el
    /// efecto ocupa. Es el radio con el que hay que comparar, no el del quad.
    /// </summary>
    public static double RadioVisibleTiles(PortalDef d)
    {
        var f = _formaPorShader[d.Shader < _formaPorShader.Length ? d.Shader : 0];
        return f.Tiles * Math.Max(0.01, d.Escala / 100.0) / 2.0 * f.Visible;
    }

    public static int Cantidad => _defs.Count;

    public static bool TryGet(short mapa, int x, int y, out PortalDef d) =>
        _defs.TryGetValue((mapa, x * 101 + y), out d);

    public static void Set(short mapa, int x, int y, PortalDef d)
    {
        _defs[(mapa, x * 101 + y)] = d;
        _sucio = true;
    }

    public static void Remove(short mapa, int x, int y)
    {
        if (_defs.Remove((mapa, x * 101 + y))) _sucio = true;
    }

    /// <summary>
    /// Copia el TileExit del portal a los tiles que el efecto TAPA, para que se entre por donde
    /// se lo ve y no sólo por el tile del medio. El objeto 378 se queda SOLO en el centro: si se
    /// pusiera en los vecinos, cada uno sería un teleport aparte y el cliente dibujaría un shader
    /// encima de cada uno.
    ///
    /// El área es el DISCO de lo que se dibuja, nunca el cuadrado del quad: los shaders dibujan
    /// un anillo o un remolino inscripto y las esquinas están vacías. Un tile entra si su CENTRO
    /// cae dentro del disco. El área no se pasa NUNCA del dibujo.
    ///
    /// Y encima se mete UN TILE para adentro del borde (MARGEN). Pegado al borde el portal se
    /// disparaba al rozarlo: caminando en diagonal por al lado te tragaba sin haber apuntado.
    /// Un tile de colchón hace que haya que entrar, no rozar.
    ///
    /// Consecuencia: los portales chicos entran sólo por su tile, como antes. Para que se pisen
    /// desde más lejos hay que agrandarlos con el slider de TAMAÑO — que es justamente lo que
    /// los hace verse más grandes. El área sigue al dibujo, nunca al revés.
    ///
    /// Nunca pisa un tile que ya tiene dueño (objeto, otra salida) ni una pared.
    /// </summary>
    private const double MARGEN = 1.0;   // tiles de colchón contra el borde del efecto

    public static void PlantarArea(short mapa, int cx, int cy, PortalDef d, TileExit ex)
    {
        var clave = (mapa, cx * 101 + cy);
        _area.Remove(clave);
        var md = MapLoader.Get(mapa);
        if (md == null) return;

        double r = RadioVisibleTiles(d) - MARGEN;
        if (r < 1.0) return;                    // no llega ni al vecino: entra sólo por su tile
        int R = (int)Math.Floor(r);
        var puestos = new List<int>();
        for (int dx = -R; dx <= R; dx++)
            for (int dy = -R; dy <= R; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                if (dx * dx + dy * dy > r * r) continue;
                int x = cx + dx, y = cy + dy;
                if (x < 1 || x > 100 || y < 1 || y > 100) continue;
                if (md.FloorObj[x, y] != 0) continue;
                if (md.Exits[x, y].HasValue) continue;
                if (md.Blocked[x, y]) continue;
                md.Exits[x, y] = ex;
                puestos.Add(x * 101 + y);
            }
        if (puestos.Count > 0) _area[clave] = puestos.ToArray();
    }

    /// <summary>Saca los TileExit satélite que plantó PlantarArea. Sólo los suyos.</summary>
    public static void QuitarArea(short mapa, int cx, int cy)
    {
        var clave = (mapa, cx * 101 + cy);
        if (!_area.TryGetValue(clave, out var tiles)) return;
        var md = MapLoader.Get(mapa);
        if (md != null)
            foreach (int k in tiles)
            {
                int x = k / 101, y = k % 101;
                // Si mientras tanto alguien puso un objeto ahí, la salida ya no es sólo nuestra.
                if (md.FloorObj[x, y] == 0) md.Exits[x, y] = null;
            }
        _area.Remove(clave);
    }

    /// <summary>
    /// Lee Dat/Portales.json y vuelve a poner cada portal en su mapa (objeto + Exit + alta en
    /// DynamicTeleports, que es lo que hace que se reenvíe en cada FullUpdate).
    /// Se llama UNA vez al arrancar, después de que los mapas estén cargados.
    /// </summary>
    public static void Cargar()
    {
        if (!Persistir)
        {
            // _ruta se queda vacía a propósito: es lo que deja mudo también a Guardar().
            Console.WriteLine("[Portales] persistencia APAGADA: los /ct duran hasta el reinicio.");
            return;
        }
        _ruta = Path.Combine(DataPaths.Sub("Dat"), "Portales.json");
        if (!File.Exists(_ruta)) { Console.WriteLine("[Portales] sin archivo: arranca vacío."); return; }
        List<Fila> filas;   // sin '?': el proyecto no tiene nullable habilitado (CS8632)
        try
        {
            filas = JsonSerializer.Deserialize<List<Fila>>(File.ReadAllText(_ruta));
        }
        catch (Exception e)
        {
            // Un JSON roto NO puede impedir que el server levante: se avisa fuerte y se sigue
            // sin portales. El archivo queda intacto para poder mirarlo a mano.
            Console.WriteLine($"[Portales] ⚠ {Path.GetFileName(_ruta)} ilegible ({e.Message}). Se arranca SIN portales y NO se sobrescribe.");
            _ruta = "";
            return;
        }
        if (filas == null) return;

        int puestos = 0, saltados = 0;
        foreach (var f in filas)
        {
            var md = MapLoader.Get(f.Mapa);
            if (md == null || f.X < 1 || f.X > 100 || f.Y < 1 || f.Y > 100) { saltados++; continue; }
            // Si alguien puso otra cosa en ese tile editando el mapa, el portal no se planta
            // encima: se descarta y se avisa. Es la misma regla que aplica /ct en vivo.
            if (md.FloorObj[f.X, f.Y] != 0 && md.FloorObj[f.X, f.Y] != 378) { saltados++; continue; }
            md.FloorObj[f.X, f.Y] = 378;
            md.FloorAmount[f.X, f.Y] = 1;
            md.Exits[f.X, f.Y] = new TileExit { DestMap = f.DestMapa, DestX = f.DestX, DestY = f.DestY };
            md.DynamicTeleports.Add(f.X * 101 + f.Y);
            _defs[(f.Mapa, f.X * 101 + f.Y)] = new PortalDef
            {
                Shader = f.Shader, R = f.R, G = f.G, B = f.B,
                Vel = f.Vel, Escala = f.Escala, Nombre = f.Nombre ?? "",
            };
            puestos++;
        }
        Console.WriteLine($"[Portales] {puestos} restaurados" + (saltados > 0 ? $", {saltados} descartados (mapa o tile ocupado)" : "") + ".");
    }

    /// <summary>
    /// Vuelca a disco si cambió algo. Escribe a un .tmp y recién ahí reemplaza: un corte de
    /// luz a mitad de la escritura deja el archivo VIEJO entero, no uno a medias.
    /// </summary>
    public static void Guardar()
    {
        if (!_sucio || _ruta.Length == 0) return;
        var filas = new List<Fila>();
        foreach (var kv in _defs)
        {
            var (mapa, clave) = kv.Key;
            int x = clave / 101, y = clave % 101;
            var md = MapLoader.Get(mapa);
            var ex = md?.Exits[x, y];
            if (ex == null) continue;   // el teleport ya no está: no se guarda un portal huérfano
            var d = kv.Value;
            filas.Add(new Fila(mapa, (byte)x, (byte)y, ex.Value.DestMap, (byte)ex.Value.DestX,
                (byte)ex.Value.DestY, d.Shader, d.R, d.G, d.B, d.Vel, d.Escala, d.Nombre ?? ""));
        }
        try
        {
            var tmp = _ruta + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(filas, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _ruta, true);
            _sucio = false;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Portales] ⚠ no se pudo guardar: {e.Message}");
        }
    }
}
