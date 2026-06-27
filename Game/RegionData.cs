namespace ServidorCS.Game;

/// <summary>
/// Mundo único ensamblado: arrays grandes [1..Width, 1..Height] que fusionan todos los chunks del
/// overworld en una sola superficie continua. Los produce <see cref="RegionLoader"/> con la regla
/// overlay-por-centralidad (cada tile global lo posee el mapa para el que es más central, de modo
/// que el contenido del vecino sobrescribe el margen bloqueado y los seams quedan caminables).
///
/// Convención de índices: [gx, gy] con gx en 1..Width, gy en 1..Height (igual que MapData usa 1..100).
/// global(map,x,y) lo da <see cref="RegionLayout.TryLocalToGlobal"/>.
///
/// Aditivo: nada en el runtime lo usa todavía. Se conectará en la fase de migración de consumidores.
/// </summary>
public sealed class RegionData
{
    public readonly int Width, Height;

    public readonly bool[,] Blocked;
    public readonly bool[,] Water;
    public readonly byte[,] Trigger;
    public readonly short[,] FloorObj;
    public readonly int[,] FloorAmount;
    /// <summary>Número de mapa dueño de cada tile global (0 = vacío/fuera del mundo).</summary>
    public readonly short[,] Owner;

    /// <summary>
    /// TileExits que SALEN de la región (destino = mapa interior/dungeon standalone). Los exits
    /// internos (seam entre dos mapas del overworld) se descartan: caminar los cruza sin transición.
    /// Clave: (gx, gy) global.
    /// </summary>
    public readonly Dictionary<(int, int), TileExit> Exits = new();

    public RegionData(int width, int height)
    {
        Width = width; Height = height;
        Blocked = new bool[width + 1, height + 1];
        Water = new bool[width + 1, height + 1];
        Trigger = new byte[width + 1, height + 1];
        FloorObj = new short[width + 1, height + 1];
        FloorAmount = new int[width + 1, height + 1];
        Owner = new short[width + 1, height + 1];
    }

    public bool InBounds(int gx, int gy) => gx >= 1 && gx <= Width && gy >= 1 && gy <= Height;

    /// <summary>Bloqueado (o fuera de límites / vacío).</summary>
    public bool IsBlocked(int gx, int gy)
    {
        if (!InBounds(gx, gy)) return true;
        return Owner[gx, gy] == 0 || Blocked[gx, gy];
    }
}
