namespace ServidorCS.Game;

/// <summary>
/// Flag maestro del mundo único (mapa continuo). Gatea la traducción local→global en los senders y
/// el cruce de bordes sin ChangeMap. Se lee de Server.ini (MundoContinuo=0/1), default 0 (APAGADO →
/// el servidor se comporta EXACTAMENTE como ahora). DEBE coincidir con el flag del cliente
/// (RegionLayout.MUNDO_CONTINUO) al desplegar. Ver [[mundo_continuo_analisis_bordes]].
/// </summary>
public static class Continuous
{
    private static bool? _enabled;

    public static bool Enabled
    {
        get
        {
            _enabled ??= Network.ServerConfig.ReadInt("MundoContinuo", 0) == 1;
            return _enabled.Value;
        }
    }

    /// <summary>
    /// Traduce una posición local (map,x,y) a coordenada global de la región SI el mundo continuo está
    /// activo y el mapa es del overworld. Si no, devuelve (x,y) sin cambios. Es el choke point que usan
    /// los senders de posición para enviar coords globales al cliente. Con el flag apagado es identidad.
    /// </summary>
    public static (int x, int y) Pos(int map, int x, int y)
    {
        if (Enabled && RegionLayout.TryLocalToGlobal(map, x, y, out int gx, out int gy))
            return (gx, gy);
        return (x, y);
    }

    /// <summary>
    /// ¿Un cruce oldMap→destMap es un "seam" del mundo continuo? (mundo activo + ambos mapas del
    /// overworld). En ese caso el cruce se hace SIN ChangeMap (SeamlessCross). Los cruces a
    /// interiores/dungeons (destino fuera de la región) siguen usando ChangeMap normal.
    /// </summary>
    public static bool IsSeamCross(int oldMap, int destMap)
        => Enabled && oldMap != destMap && RegionLayout.InRegion(oldMap) && RegionLayout.InRegion(destMap);

    /// <summary>
    /// Coordenada de una posición (srcMap,x,y) en el ESPACIO LOCAL-RELATIVO del observador (obsMap). El
    /// cliente renderiza en ese espacio (local del mapa actual, extendido a vecinos vía NeighborMaps),
    /// así que estos coords se envían TAL CUAL, sin conversión en el cliente. Mismo mapa → (x,y). Mapa
    /// vecino → coord extendida (puede caer fuera de [1,100]). Identidad si el mundo continuo está
    /// apagado o alguno de los mapas no es de la región. Se usa para objetos/sonidos/bloques, cuyos
    /// senders directos ya mandan coords locales del mapa actual (a diferencia de personajes, que van
    /// en global + conversión en el cliente).
    /// </summary>
    public static (int x, int y) Rel(int obsMap, int srcMap, int x, int y)
    {
        if (Enabled && RegionLayout.TryLocalToGlobal(srcMap, x, y, out int gx, out int gy)
                    && RegionLayout.TryGetOffset(obsMap, out var o))
            return (gx - o.X, gy - o.Y);
        return (x, y);
    }
}
