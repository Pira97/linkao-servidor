using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Restricciones de acceso a mapas por nivel de personaje.
///
/// Mismo espíritu que la restricción del Dungeon Newbie (Facciones.SalirDungeonNewbie), pero
/// generalizada y con TOPE además de piso: hay zonas pensadas para una franja de niveles, y al
/// pasarse de esa franja el personaje deja de tener nada que hacer ahí.
///
/// Se aplica en tres momentos, y hacen falta los tres:
///   1. Al pisar el TileExit que lleva al mapa  -> Movement.MoveUser (no lo deja entrar).
///   2. Al subir de nivel estando adentro       -> Combat.CheckUserLevel (lo saca a su ciudad).
///   3. Al loguear dentro del mapa              -> Movement.SanearPosicionLogin (reubica la pos
///      ANTES de mandarle el mundo al cliente; sin esto alcanzaría con desloguear adentro,
///      subir de nivel en otro lado y volver, o simplemente quedarse guardado ahí).
///
/// Los GMs (Consejero+) entran y se quedan siempre.
/// </summary>
public static class MapasPorNivel
{
    /// <summary>Franja de niveles permitida en un mapa. Max = SIN_TOPE cuando solo hay piso.</summary>
    public readonly record struct Rango(int Min, int Max);

    public const int SIN_TOPE = int.MaxValue;

    /// <summary>
    /// Mapa -> franja permitida, AMBOS EXTREMOS INCLUSIVE. O sea que un tope de 39 significa
    /// "hasta el 39 inclusive": al llegar al 40 se lo expulsa y ya no puede volver a entrar.
    /// </summary>
    public static readonly Dictionary<int, Rango> Mapas = new()
    {
        [205] = new Rango(25, 39),        // expulsa al llegar a 40
        [207] = new Rango(20, 37),        // expulsa al llegar a 38
        [755] = new Rango(35, SIN_TOPE),
        [756] = new Rango(40, SIN_TOPE),
        [760] = new Rango(45, SIN_TOPE),
    };

    private const byte FONT_INFO = 1;

    /// <summary>Ciudad 15 = Intermundia. Solo se usa si el Hogar del personaje es inválido.</summary>
    private const byte CIUDAD_FALLBACK = 15;

    public static bool EsRestringido(int mapa) => Mapas.ContainsKey(mapa);

    /// <summary>¿Este personaje puede estar en ese mapa? Los GMs siempre pueden.</summary>
    public static bool Permitido(User u, int mapa)
    {
        if (u == null) return true;
        if (u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO) return true;
        if (!Mapas.TryGetValue(mapa, out var r)) return true;
        return u.Stats.ELV >= r.Min && u.Stats.ELV <= r.Max;
    }

    /// <summary>Texto para la consola explicando por qué no puede pasar.</summary>
    public static string MotivoRechazo(int mapa)
    {
        if (!Mapas.TryGetValue(mapa, out var r)) return "No puedes entrar a esa zona.";
        return r.Max == SIN_TOPE
            ? $"Necesitas ser nivel {r.Min} o superior para entrar a esta zona."
            : $"Esta zona es solo para personajes de nivel {r.Min} a {r.Max}.";
    }

    /// <summary>
    /// Si el personaje está parado en un mapa restringido que ya no le corresponde, lo manda a
    /// su ciudad. warpear=true usa WarpUser (en juego, tras subir de nivel); false solo reubica
    /// u.Pos, para el login, antes de que se le mande el mundo al cliente.
    /// Devuelve true si lo movió.
    /// </summary>
    public static bool ExpulsarSiNoCorresponde(User u, bool warpear)
    {
        if (u == null) return false;
        if (Permitido(u, u.Pos.Map)) return false;

        // "Su ciudad" = el Hogar que eligió el personaje. Si quedó en un valor inválido o apunta
        // a un mapa que no existe, se cae a Intermundia en vez de dejarlo en una posición rota.
        var c = CityData.Get(u.Hogar);
        if (c.Map <= 0) c = CityData.Get(CIUDAD_FALLBACK);
        if (c.Map <= 0) return false;

        int mapaViejo = u.Pos.Map;
        var r = Mapas[mapaViejo];
        string aviso = u.Stats.ELV < r.Min
            ? $"No tienes nivel suficiente para estar aquí. Vuelve cuando seas nivel {r.Min}."
            : $"Ya superaste el nivel de esta zona (hasta {r.Max}). Se te devuelve a tu ciudad.";

        if (warpear && u.Conn != null)
        {
            Movement.WarpUser(u.Conn.UserIndex, c.Map, c.X, c.Y);
            ServerPackets.ConsoleMsg(u.Conn, aviso, FONT_INFO);
        }
        else
        {
            u.Pos.Map = c.Map;
            u.Pos.X = c.X;
            u.Pos.Y = c.Y;
        }
        return true;
    }
}
