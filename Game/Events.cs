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
    private static double _expEventoExpira;

    /// <summary>Activa el evento EXP xN por 'segundos'. Anuncia a todos los online.</summary>
    public static void ActivarEventoExp(int multiplicador, int segundos)
    {
        ExpMultiplicador = Math.Max(1, multiplicador);
        _expEventoExpira = Environment.TickCount64 / 1000.0 + segundos;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u.flags.UserLogged && u.Conn != null)
            {
                ServerPackets.EventoExpBonus(u.Conn, segundos);
                ServerPackets.ConsoleMsg(u.Conn, $"¡Evento EXP x{ExpMultiplicador} activado por {segundos} segundos!", 1);
                ServerPackets.PlayWave(u.Conn, Sounds.EVENTO_INICIO, (byte)u.Pos.X, (byte)u.Pos.Y); // sonido de inicio (252)
            }
        }
    }

    /// <summary>Sonido de inicio/curso de un evento del juego (252): se difunde a TODOS los online.
    /// Lo llaman los distintos eventos (cacería, barrido, cofres, inframundo, torneo, ruleta) al arrancar.</summary>
    public static void SonidoInicioEvento()
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Conn != null)
                ServerPackets.PlayWave(u.Conn, Sounds.EVENTO_INICIO, (byte)u.Pos.X, (byte)u.Pos.Y);
        }
    }

    /// <summary>Tick periódico: desactiva el evento cuando expira. Lo llama el timer del server.</summary>
    public static void Tick()
    {
        if (ExpMultiplicador > 1 && Environment.TickCount64 / 1000.0 >= _expEventoExpira)
        {
            ExpMultiplicador = 1;
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var u = UserListManager.UserList[i];
                if (u.flags.UserLogged && u.Conn != null)
                {
                    ServerPackets.EventoExpBonus(u.Conn, 0);
                    ServerPackets.ConsoleMsg(u.Conn, "El evento de EXP ha terminado.", 1);
                }
            }
        }
    }
}
