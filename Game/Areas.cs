namespace ServidorCS.Game;

/// <summary>
/// Difusión por áreas (ModAreas.bas). El mapa se divide en bloques (áreas) de 9 tiles:
/// area = pos \ 9 (PosToArea). Un usuario "recibe" eventos de las áreas dentro de ±2 de la suya
/// (AreasRecive: bitmask que cubre area-2..area+2), tanto en X como en Y. Esto equivale a la
/// ventana visible de 5×5 áreas (~45 tiles) y reemplaza la difusión a todo el mapa
/// (SendData ToPCArea / ToPCAreaButIndex).
/// </summary>
public static class Areas
{
    /// <summary>PosToArea: bloque de área de una coordenada (1:1 con pos \ 9 del VB6).</summary>
    public static int AreaOf(int pos) => pos / 9;

    /// <summary>
    /// ¿El observador está en el área de difusión de la posición (srcX,srcY) del mismo mapa?
    /// 1:1 con AreasRecive (±2 áreas en X e Y).
    /// </summary>
    public static bool InPCArea(int srcX, int srcY, int obsX, int obsY)
    {
        return Math.Abs(AreaOf(obsX) - AreaOf(srcX)) <= 2
            && Math.Abs(AreaOf(obsY) - AreaOf(srcY)) <= 2;
    }
}
