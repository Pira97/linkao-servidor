using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Eventos globales del servidor (subset de modRuletaEventos / eventos exp-oro).
/// Versión núcleo: evento de EXP multiplicada por tiempo limitado. El multiplicador
/// afecta a Combat (reparto de EXP). Se anuncia con EventoExpBonus.
/// </summary>
/// <summary>Estado global del servidor (flags de configuración dinámica).</summary>
public static class GameState
{
    public static bool ServerSoloGMs { get; set; } = false;
}

public static class Events
{
    /// <summary>Multiplicador de EXP activo (1 = normal). Combat lo aplica al dar exp.</summary>
    public static int ExpMultiplicador { get; private set; } = 1;
    private static double _expEventoExpira; // 0 = sin límite de tiempo (toggle GM)

    /// <summary>Multiplicador de ORO activo (1 = normal). Combat lo aplica al oro que suelta el NPC.</summary>
    public static int OroMultiplicador { get; private set; } = 1;
    private static double _oroEventoExpira; // 0 = sin límite de tiempo (toggle GM)

    /// <summary>Activa el evento EXP xN. segundos &lt;= 0 = sin límite (hasta desactivarlo un GM). Anuncia a todos los online.</summary>
    public static void ActivarEventoExp(int multiplicador, int segundos)
    {
        ExpMultiplicador = Math.Max(1, multiplicador);
        _expEventoExpira = segundos > 0 ? Environment.TickCount64 / 1000.0 + segundos : 0;
        string msg = segundos > 0
            ? $"¡Evento EXP x{ExpMultiplicador} activado por {segundos} segundos!"
            : $"¡Evento EXP x{ExpMultiplicador} activado!";
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Conn != null)
            {
                if (segundos > 0) ServerPackets.EventoExpBonus(u.Conn, segundos); // banner con countdown solo si hay tiempo
                ServerPackets.ConsoleMsg(u.Conn, msg, 58); // 58 = ámbar dorado (FONTTYPE_NPCDIALOG)
                ServerPackets.PlayWave(u.Conn, Sounds.EVENTO_INICIO, (byte)u.Pos.X, (byte)u.Pos.Y); // sonido de inicio (252)
            }
        }
    }

    /// <summary>Desactiva el evento EXP y lo anuncia a todos los online.</summary>
    public static void DesactivarEventoExp()
    {
        ExpMultiplicador = 1;
        _expEventoExpira = 0;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Conn != null)
            {
                ServerPackets.EventoExpBonus(u.Conn, 0);
                ServerPackets.ConsoleMsg(u.Conn, "El evento de EXP ha terminado.", 58);
            }
        }
    }

    /// <summary>Activa el evento ORO xN. segundos &lt;= 0 = sin límite (hasta desactivarlo un GM). Anuncia a todos los online.</summary>
    public static void ActivarEventoOro(int multiplicador, int segundos)
    {
        OroMultiplicador = Math.Max(1, multiplicador);
        _oroEventoExpira = segundos > 0 ? Environment.TickCount64 / 1000.0 + segundos : 0;
        string msg = segundos > 0
            ? $"¡Evento ORO x{OroMultiplicador} activado por {segundos} segundos!"
            : $"¡Evento ORO x{OroMultiplicador} activado!";
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Conn != null)
            {
                ServerPackets.ConsoleMsg(u.Conn, msg, 58); // 58 = ámbar dorado (FONTTYPE_NPCDIALOG)
                ServerPackets.PlayWave(u.Conn, Sounds.EVENTO_INICIO, (byte)u.Pos.X, (byte)u.Pos.Y);
            }
        }
    }

    /// <summary>Desactiva el evento ORO y lo anuncia a todos los online.</summary>
    public static void DesactivarEventoOro()
    {
        OroMultiplicador = 1;
        _oroEventoExpira = 0;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Conn != null)
                ServerPackets.ConsoleMsg(u.Conn, "El evento de ORO ha terminado.", 58);
        }
    }

    /// <summary>Líneas con los eventos globales en curso (vacía si no hay ninguno).
    /// Las usan /eventos y el aviso al loguear.</summary>
    public static List<string> ResumenEventos()
    {
        var lineas = new List<string>();
        if (ExpMultiplicador > 1) lineas.Add($"Experiencia x{ExpMultiplicador}{TiempoTxt(_expEventoExpira)}");
        if (OroMultiplicador > 1) lineas.Add($"Oro x{OroMultiplicador}{TiempoTxt(_oroEventoExpira)}");
        if (BarridoEvento.EventoActivo) lineas.Add("El Barrido");
        if (CaceriaEvento.EventoActivo) lineas.Add("Cacería por Facción");
        if (InframundoEvento.EventoActivo) lineas.Add("Inframundo");
        return lineas;
    }

    private static string TiempoTxt(double expira)
    {
        if (expira <= 0) return " (sin límite de tiempo)";
        int seg = Math.Max(0, (int)(expira - Environment.TickCount64 / 1000.0));
        return $" ({seg / 60}m {seg % 60:00}s restantes)";
    }

    /// <summary>Sonido de inicio/curso de un evento del juego (252): se difunde a TODOS los online.
    /// Lo llaman los distintos eventos (cacería, barrido, cofres, inframundo, torneo) al arrancar.</summary>
    public static void SonidoInicioEvento()
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Conn != null)
                ServerPackets.PlayWave(u.Conn, Sounds.EVENTO_INICIO, (byte)u.Pos.X, (byte)u.Pos.Y);
        }
    }

    /// <summary>Tick periódico: desactiva los eventos cuando expiran (expira 0 = sin límite, no vence solo). Lo llama el timer del server.</summary>
    public static void Tick()
    {
        double ahora = Environment.TickCount64 / 1000.0;
        if (ExpMultiplicador > 1 && _expEventoExpira > 0 && ahora >= _expEventoExpira)
            DesactivarEventoExp();
        if (OroMultiplicador > 1 && _oroEventoExpira > 0 && ahora >= _oroEventoExpira)
            DesactivarEventoOro();
        TickAutoDobleEvento(ahora);
    }

    // --- Ciclo automático EXP x2 / ORO x2 (reemplaza la vieja ruleta): 1 hora activo, 30 minutos
    // apagado, y así en loop mientras el server esté arriba. Arranca apenas prende el server. ---
    private const int AUTO_DURACION_SEG = 3600;
    private const int AUTO_DESCANSO_SEG = 1800;
    private static bool _autoInicializado;
    private static double _autoProximaActivacion;

    private static void TickAutoDobleEvento(double ahora)
    {
        if (!_autoInicializado)
        {
            _autoInicializado = true;
            _autoProximaActivacion = ahora; // primer evento arranca ya al levantar el server
        }
        if (ExpMultiplicador == 1 && OroMultiplicador == 1 && ahora >= _autoProximaActivacion)
        {
            ActivarEventoExp(2, AUTO_DURACION_SEG);
            ActivarEventoOro(2, AUTO_DURACION_SEG);
            _autoProximaActivacion = ahora + AUTO_DURACION_SEG + AUTO_DESCANSO_SEG;
        }
    }
}
