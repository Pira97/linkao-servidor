using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sistema de clima automático (modClima.bas) 1:1. Cambia el clima global cada IntervaloClima
/// segundos (def 2400) con probabilidades 50% despejado / 35% lluvia / 15% tormenta. Con lluvia o
/// tormenta: oscurece (AmbientLight 40) y sonido de lluvia en loop. Los usuarios en dungeon ven
/// siempre despejado y luz normal. Tick() se llama 1 vez por segundo desde el FlushLoop.
/// Los rayos (daño + partícula 48 + flash de relámpago) fueron removidos del juego.
/// </summary>
public static class Clima
{
    public enum eClima : byte { Despe = 0, Lluvia = 1, Tormenta = 3 }

    public static eClima Queclima { get; private set; } = eClima.Despe;

    // Config (Server.ini [CLIMA]).
    private const int INTERVALO_CLIMA_DEFAULT = 2400;
    private static int _intervaloClima = INTERVALO_CLIMA_DEFAULT;
    private static bool _activo = true;
    private static bool _inicializado;
    private static int _segundos;

    // Probabilidades (sobre 100).
    private const int PROB_DESPEJADO = 50, PROB_LLUVIA = 35; // tormenta = resto

    // Niveles de luz.
    private const byte LUZ_NORMAL = 100, LUZ_TORMENTA = 40;

    // Sonido de lluvia.
    private static bool _sonidoLluviaActivo;
    private static int _contadorSonidoLluvia;
    private const int INTERVALO_SONIDO_LLUVIA = 5;

    private static readonly Random _rng = new();

    private static void Inicializar()
    {
        try
        {
            string iniPath = (string.IsNullOrEmpty(DataPaths.Root) ? AppContext.BaseDirectory : DataPaths.Root) + "Server.ini";
            if (File.Exists(iniPath))
            {
                var ini = new IniFile(iniPath);
                int it = ini.GetInt("CLIMA", "IntervaloClima");
                _intervaloClima = it > 0 ? it : INTERVALO_CLIMA_DEFAULT;
                string act = ini.Get("CLIMA", "Activo");
                _activo = string.IsNullOrEmpty(act) ? true : act.Trim() == "1";
            }
        }
        catch { _intervaloClima = INTERVALO_CLIMA_DEFAULT; _activo = true; }

        _segundos = 0;
        Queclima = eClima.Despe;
        _sonidoLluviaActivo = false; _contadorSonidoLluvia = 0;
        Console.WriteLine($"[Clima] Sistema {( _activo ? "ACTIVADO" : "DESACTIVADO")}; intervalo {_intervaloClima}s; inicial DESPEJADO.");
    }

    /// <summary>ActualizarClima: llamar 1 vez por segundo.</summary>
    public static void Tick()
    {
        if (!_inicializado) { Inicializar(); _inicializado = true; }
        if (!_activo) return;

        _segundos++;

        // Loop de sonido de lluvia.
        if (_sonidoLluviaActivo)
        {
            _contadorSonidoLluvia++;
            if (_contadorSonidoLluvia >= INTERVALO_SONIDO_LLUVIA)
            {
                _contadorSonidoLluvia = 0;
                EnviarSonidoFueraDeDungeons(191); // lluvia
            }
        }

        if (_segundos >= _intervaloClima)
        {
            CambiarClimaAleatorio();
            _segundos = 0;
        }
    }

    private static void CambiarClimaAleatorio()
    {
        int n = _rng.Next(1, 101);
        eClima nuevo = n <= PROB_DESPEJADO ? eClima.Despe
                     : n <= PROB_DESPEJADO + PROB_LLUVIA ? eClima.Lluvia
                     : eClima.Tormenta;
        CambiarClima(nuevo, true);
    }

    /// <summary>CambiarClima: fija el clima global y, si cambió, lo difunde + notifica/sonidos.</summary>
    public static void CambiarClima(eClima nuevo, bool notificar = true)
    {
        eClima anterior = Queclima;
        Queclima = nuevo;
        if (anterior == nuevo) return;

        EnviarCambioClima();
        if (!notificar) return;

        const byte FONT_VENENO = 6, FONT_EJECUCION = 5;
        if (anterior != eClima.Despe && nuevo == eClima.Despe)
        {
            Broadcast("El clima ha mejorado, la lluvia ha cesado", FONT_VENENO);
            _sonidoLluviaActivo = false; _contadorSonidoLluvia = 0;
        }
        else if (nuevo == eClima.Lluvia)
        {
            Broadcast("Ha comenzado a llover, busca refugio si lo necesitas", FONT_VENENO);
            EnviarSonidoFueraDeDungeons(62);
            _sonidoLluviaActivo = true; _contadorSonidoLluvia = INTERVALO_SONIDO_LLUVIA;
            EnviarSonidoFueraDeDungeons(191);
        }
        else if (nuevo == eClima.Tormenta)
        {
            Broadcast("Una tormenta se aproxima, ten cuidado", FONT_EJECUCION);
            EnviarSonidoFueraDeDungeons(62);
            _sonidoLluviaActivo = true; _contadorSonidoLluvia = INTERVALO_SONIDO_LLUVIA;
            EnviarSonidoFueraDeDungeons(191);
        }
    }

    /// <summary>EnviarCambioClima: RainToggle + AmbientLight a cada usuario (dungeon → despejado/normal).</summary>
    private static void EnviarCambioClima()
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (!u.flags.UserLogged || u.Conn == null) continue;
            byte clima; byte luz;
            if (EsDungeon(u.Pos.Map)) { clima = (byte)eClima.Despe; luz = LUZ_NORMAL; }
            else
            {
                clima = (byte)Queclima;
                luz = (Queclima == eClima.Lluvia || Queclima == eClima.Tormenta) ? LUZ_TORMENTA : LUZ_NORMAL;
            }
            ServerPackets.RainToggle(u.Conn, clima);
            ServerPackets.AmbientLight(u.Conn, luz);
        }
    }

    /// <summary>
    /// EnviarClimaAUsuario (modClima.bas:490): manda el clima actual a un usuario (login/warp).
    /// mapaAnterior se usa solo para el sonido de "salir de un dungeon a la lluvia".
    /// </summary>
    public static void EnviarClimaAUsuario(int userIndex, int mapaAnterior = 0)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || u.Conn == null) return;

        byte clima; byte luz;
        if (EsDungeon(u.Pos.Map)) { clima = (byte)eClima.Despe; luz = LUZ_NORMAL; }
        else
        {
            clima = (byte)Queclima;
            luz = (Queclima == eClima.Lluvia || Queclima == eClima.Tormenta) ? LUZ_TORMENTA : LUZ_NORMAL;
            // Si venía de un dungeon hacia la lluvia, reproducir el sonido para "ponerse al día".
            if (mapaAnterior > 0 && EsDungeon(mapaAnterior) &&
                (Queclima == eClima.Lluvia || Queclima == eClima.Tormenta))
                ServerPackets.PlayWave(u.Conn, 191, 0, 0);
        }
        ServerPackets.RainToggle(u.Conn, clima);
        ServerPackets.AmbientLight(u.Conn, luz);
    }

    // --- helpers ---

    private static void EnviarSonidoFueraDeDungeons(short sonido)
        => ForEachOnlineFueraDungeon((i, u) => ServerPackets.PlayWave(u.Conn, sonido, 0, 0));

    /// <summary>EsDungeon (modClima.bas:451): mapa 37 (dungeon newbie) o Zona == "DUNGEON".</summary>
    private static bool EsDungeon(int map)
    {
        if (map <= 0) return false;
        if (map == 37) return true;
        var md = MapLoader.Get(map);
        return md != null && string.Equals(md.Info.Zona.Trim(), "DUNGEON", StringComparison.OrdinalIgnoreCase);
    }

    private static void ForEachOnlineFueraDungeon(Action<int, User> action)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u.flags.UserLogged && u.Conn != null && !EsDungeon(u.Pos.Map)) action(i, u);
        }
    }

    private static void Broadcast(string msg, byte font)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u.flags.UserLogged && u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, msg, font);
        }
    }
}
