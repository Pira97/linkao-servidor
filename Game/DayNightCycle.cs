using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sistema de ciclo Día/Noche (NUEVO, no VB6). El servidor es la autoridad del tiempo de juego:
/// usa el reloj REAL del PC servidor (modo "hora real") para derivar la hora del mundo, de forma
/// que TODOS los jugadores ven la misma fase (madrugada/amanecer/mañana/mediodía/tarde/atardecer/noche).
///
/// El servidor solo difunde la hora y minuto autoritativos (+ un flag de dungeon); el cliente Godot
/// posee la rampa de color y la interpola suavemente fotograma a fotograma, avanzando su propio reloj
/// entre actualizaciones. Esto da transiciones "mantequilla" sin saturar la red.
///
/// REGLA: el efecto NO aplica en dungeons. A un usuario dentro de un dungeon se le envía inDungeon=1
/// y el cliente fuerza luz neutra (los dungeons ya tienen su propia oscuridad vía LightSystem).
///
/// Tick() se llama 1 vez por segundo desde el FlushLoop (junto a Clima.Tick).
/// </summary>
public static class DayNightCycle
{
    public enum eFase : byte
    {
        Madrugada = 0, // 00-05
        Amanecer  = 1, // 06-07
        Mañana    = 2, // 08-11
        Mediodia  = 3, // 12-13
        Tarde     = 4, // 14-18
        Atardecer = 5, // 19-20
        Noche     = 6, // 21-23
    }

    private static bool _activo = true;
    private static bool _inicializado;

    // Cadencia de difusión. El cliente avanza su reloj localmente, así que con 30s alcanza
    // de sobra para mantener la sincronía global sin tráfico innecesario.
    private const int INTERVALO_BROADCAST_SEG = 30;
    private static int _segundos;

    // Última fase difundida globalmente, para anunciar el cambio de momento del día en consola.
    private static eFase _ultimaFaseAnunciada = (eFase)255;

    private static void Inicializar()
    {
        try
        {
            string iniPath = (string.IsNullOrEmpty(DataPaths.Root) ? AppContext.BaseDirectory : DataPaths.Root) + "Server.ini";
            if (File.Exists(iniPath))
            {
                var ini = new IniFile(iniPath);
                string act = ini.Get("DIANOCHE", "Activo");
                _activo = string.IsNullOrEmpty(act) ? true : act.Trim() == "1";
            }
        }
        catch { _activo = true; }

        _segundos = 0;
        var (h, _) = HoraActual();
        _ultimaFaseAnunciada = FaseDe(h);
        Console.WriteLine($"[DiaNoche] Sistema {(_activo ? "ACTIVADO (hora real del servidor)" : "DESACTIVADO")}; hora inicial {h:00}h ({_ultimaFaseAnunciada}).");
    }

    /// <summary>Hora y minuto autoritativos del mundo (reloj real del servidor).</summary>
    public static (byte hora, byte minuto) HoraActual()
    {
        var now = DateTime.Now;
        return ((byte)now.Hour, (byte)now.Minute);
    }

    public static eFase FaseDe(int hora) => hora switch
    {
        >= 6 and <= 7   => eFase.Amanecer,
        >= 8 and <= 11  => eFase.Mañana,
        >= 12 and <= 13 => eFase.Mediodia,
        >= 14 and <= 18 => eFase.Tarde,
        >= 19 and <= 20 => eFase.Atardecer,
        >= 21           => eFase.Noche,
        _               => eFase.Madrugada, // 0-5
    };

    /// <summary>ActualizarCiclo: llamar 1 vez por segundo desde el FlushLoop.</summary>
    public static void Tick()
    {
        if (!_inicializado) { Inicializar(); _inicializado = true; }
        if (!_activo) return;

        // Anuncio en consola cuando cambia el momento del día (no en dungeons; mensaje global).
        var (h, _) = HoraActual();
        eFase faseAhora = FaseDe(h);
        if (faseAhora != _ultimaFaseAnunciada)
        {
            _ultimaFaseAnunciada = faseAhora;
            AnunciarFase(faseAhora);
        }

        if (++_segundos >= INTERVALO_BROADCAST_SEG)
        {
            _segundos = 0;
            DifundirATodos();
        }
    }

    /// <summary>Difunde la hora actual a cada usuario logueado (cada uno con su flag de dungeon).</summary>
    private static void DifundirATodos()
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u == null || !u.flags.UserLogged || u.Conn == null) continue;
            EnviarAUsuarioInterno(u);
        }
    }

    /// <summary>Envía el estado día/noche a un usuario (login / cambio de mapa).</summary>
    public static void EnviarAUsuario(int userIndex)
    {
        if (!_activo) return;
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged || u.Conn == null) return;
        EnviarAUsuarioInterno(u);
    }

    private static void EnviarAUsuarioInterno(User u)
    {
        var (h, m) = HoraActual();
        bool dungeon = EsDungeon(u.Pos.Map);
        ServerPackets.DayNightInfo(u.Conn, h, m, (byte)(dungeon ? 1 : 0));
    }

    private static void AnunciarFase(eFase fase)
    {
        string msg = fase switch
        {
            eFase.Amanecer  => "El sol comienza a asomar en el horizonte...",
            eFase.Mañana    => "Ha amanecido sobre las tierras de LinkAO.",
            eFase.Mediodia  => "El sol está en lo más alto del cielo.",
            eFase.Tarde     => "La tarde cae lentamente sobre el mundo.",
            eFase.Atardecer => "El cielo se tiñe de naranja, el atardecer ha llegado.",
            eFase.Noche     => "La noche cubre las tierras de LinkAO. Ten cuidado.",
            eFase.Madrugada => "Es plena madrugada, la oscuridad lo envuelve todo.",
            _               => "",
        };
        if (string.IsNullOrEmpty(msg)) return;

        const byte FONT_INFO = 4;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u == null || !u.flags.UserLogged || u.Conn == null) continue;
            if (EsDungeon(u.Pos.Map)) continue; // los de dungeon no perciben el ciclo
            ServerPackets.ConsoleMsg(u.Conn, msg, FONT_INFO);
        }
    }

    /// <summary>Mismo criterio que Clima.EsDungeon: mapa 37 (dungeon newbie) o Zona == "DUNGEON".</summary>
    private static bool EsDungeon(int map)
    {
        if (map <= 0) return false;
        if (map == 37) return true;
        var md = MapLoader.Get(map);
        return md != null && string.Equals(md.Info.Zona.Trim(), "DUNGEON", StringComparison.OrdinalIgnoreCase);
    }
}
