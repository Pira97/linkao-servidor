using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Limpieza automática del mundo (ModLimpieza / LimpiezaAutomaticaMundo, Protocol.bas:11023 +
/// frmMain timer). Cada IntervaloLimpiarMundo minutos avisa (a los 3 y 1 min) y borra los objetos
/// tirados en el piso (no estructurales, con cantidad > 0). Evita que los drops se acumulen sin fin.
/// </summary>
public static class WorldCleanup
{
    // Minutos entre limpiezas (Server.ini [INTERVALOS] IntervaloLimpiarMundo). 0 = desactivado.
    private static readonly int Intervalo = ServerConfig.ReadInt("IntervaloLimpiarMundo", 30);
    private static int _minutos;

    // FontIndex del cliente (FONT_COLOR/fontIndexToTab en hud_ui.js): 45 = gris azulado apagado
    // (#646478), sin uso en ningún otro mensaje del server y ausente de la lista de Combate,
    // así que estos avisos caen siempre en la pestaña Chat con un único color discreto.
    private const byte FONT_LIMPIEZA = 45;

    /// <summary>Llamar una vez por minuto (desde el scheduler). Anuncia y ejecuta la limpieza.</summary>
    public static void PasarMinuto()
    {
        if (Intervalo <= 0) return;
        _minutos++;

        if (Intervalo >= 3 && _minutos == Intervalo - 3)
        {
            Anunciar("ATENCIÓN: La limpieza automática del mundo se ejecutará en 3 minutos.", FONT_LIMPIEZA);
            Anunciar("Recoge tus items del suelo ahora.", FONT_LIMPIEZA);
        }
        if (Intervalo >= 2 && _minutos == Intervalo - 1)
        {
            Anunciar("ADVERTENCIA: La limpieza automática se ejecutará en 1 MINUTO.", FONT_LIMPIEZA);
            Anunciar("¡RECOGE TUS ITEMS AHORA!", FONT_LIMPIEZA);
        }
        if (_minutos >= Intervalo)
        {
            Anunciar("INICIANDO LIMPIEZA DEL MUNDO", FONT_LIMPIEZA);
            LimpiezaAutomaticaMundo();
            _minutos = 0;
        }
    }

    /// <summary>
    /// LimpiezaAutomaticaMundo (Protocol.bas:11023): recorre los mapas cargados y borra los objetos
    /// del piso que NO sean estructurales y tengan cantidad > 0 (= fueron tirados por un jugador).
    /// </summary>
    public static int LimpiezaAutomaticaMundo()
    {
        int limpios = 0;
        foreach (var kv in MapLoader.LoadedMaps)
        {
            int mapNum = kv.Key;
            var md = kv.Value;
            for (int x = 1; x <= 100; x++)
                for (int y = 1; y <= 100; y++)
                {
                    short oi = md.FloorObj[x, y];
                    if (oi <= 0 || md.FloorAmount[x, y] <= 0) continue;
                    if (EsEstructural(ObjData.Get(oi).Type)) continue;
                    md.FloorObj[x, y] = 0;
                    md.FloorAmount[x, y] = 0;
                    AreaVisibility.ObjectRemoved(mapNum, x, y);
                    limpios++;
                }
        }
        Anunciar($"Limpieza del mundo completada. Items eliminados: {limpios}", FONT_LIMPIEZA);
        return limpios;
    }

    // Tipos estructurales que NO se limpian (Protocol.bas:11039-11048).
    private static bool EsEstructural(ObjType t) =>
        t == ObjType.Puertas || t == ObjType.Carteles || t == ObjType.Arboles || t == ObjType.Teleport ||
        t == ObjType.Muebles || t == ObjType.Yacimiento || t == ObjType.Yunque || t == ObjType.Fragua ||
        t == ObjType.Pozos || t == ObjType.Puestos;

    private static void Anunciar(string msg, byte font)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u?.flags.UserLogged == true && u.Conn != null)
                ServerPackets.ConsoleMsg(u.Conn, msg, font);
        }
    }
}
