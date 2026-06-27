using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sistema de clima automático (modClima.bas), retocado a pedido del usuario porque llovía
/// demasiado seguido: original era 50% despejado / 35% lluvia / 15% tormenta cada 2400s (40 min),
/// lo que en la práctica tenía mal tiempo la mitad del tiempo. Ahora: cada IntervaloClima segundos
/// (def 5400 = 90 min) con probabilidades 75% despejado / 18% lluvia / 7% tormenta. Con lluvia o
/// tormenta: oscurece (AmbientLight 40) y sonido de lluvia en loop. Los usuarios en dungeon ven
/// siempre despejado y luz normal. Tick() se llama 1 vez por segundo desde el FlushLoop.
/// Los rayos (daño + partícula 48 + flash de relámpago) fueron removidos del juego.
/// </summary>
public static class Clima
{
    public enum eClima : byte { Despe = 0, Lluvia = 1, Tormenta = 3 }

    public static eClima Queclima { get; private set; } = eClima.Despe;

    // Config (Server.ini [CLIMA]).
    private const int INTERVALO_CLIMA_DEFAULT = 5400;
    private static int _intervaloClima = INTERVALO_CLIMA_DEFAULT;
    private static bool _activo = true;
    private static bool _inicializado;
    private static int _segundos;

    // Probabilidades (sobre 100). La tormenta es el resto: 100 - despejado - lluvia.
    //
    // 📜 Los valores por defecto (75/18/7) NO son los del original (50/35/15): se bajaron a
    // pedido del usuario porque "llovía demasiado seguido". Por eso siguen siendo el default
    // y no se tocan desde el código.
    //
    // 🔴 Pero eso, junto al intervalo de 5400 s, tiene un efecto que se notó el 19-ago-2026:
    // el clima cambia cada 90 minutos y 3 de cada 4 veces sale despejado, así que un jugador
    // que entra **casi nunca** ve mal tiempo, y probar los efectos a mano es imposible.
    // Ahora los tres números se pueden configurar en Server.ini [CLIMA] sin recompilar:
    //   IntervaloClima=1800     cada cuántos segundos se sortea (def 5400)
    //   ProbDespejado=65        (def 75)
    //   ProbLluvia=25           (def 18)  → tormenta = 100 - 65 - 25 = 10
    private const int PROB_DESPEJADO_DEFAULT = 75, PROB_LLUVIA_DEFAULT = 18;
    private static int _probDespejado = PROB_DESPEJADO_DEFAULT;
    private static int _probLluvia = PROB_LLUVIA_DEFAULT;

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
                // Probabilidades. Se validan juntas: si la suma pasa de 100 no habría lugar
                // para la tormenta y el sorteo quedaría sesgado en silencio, así que en ese
                // caso se vuelve a los valores por defecto y se avisa por consola.
                int pd = ini.GetInt("CLIMA", "ProbDespejado");
                int pl = ini.GetInt("CLIMA", "ProbLluvia");
                if (pd > 0) _probDespejado = pd;
                if (pl > 0) _probLluvia = pl;
                if (_probDespejado + _probLluvia > 100)
                {
                    Console.WriteLine($"[Clima] ProbDespejado({_probDespejado}) + ProbLluvia({_probLluvia}) pasa de 100; se usan los valores por defecto.");
                    _probDespejado = PROB_DESPEJADO_DEFAULT; _probLluvia = PROB_LLUVIA_DEFAULT;
                }
            }
        }
        catch
        {
            _intervaloClima = INTERVALO_CLIMA_DEFAULT; _activo = true;
            _probDespejado = PROB_DESPEJADO_DEFAULT; _probLluvia = PROB_LLUVIA_DEFAULT;
        }

        _segundos = 0;
        Queclima = eClima.Despe;
        _sonidoLluviaActivo = false; _contadorSonidoLluvia = 0;
        Console.WriteLine($"[Clima] Sistema {( _activo ? "ACTIVADO" : "DESACTIVADO")}; intervalo {_intervaloClima}s; "
            + $"probabilidades {_probDespejado}% despejado / {_probLluvia}% lluvia / {100 - _probDespejado - _probLluvia}% tormenta; inicial DESPEJADO.");
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
        eClima nuevo = n <= _probDespejado ? eClima.Despe
                     : n <= _probDespejado + _probLluvia ? eClima.Lluvia
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
