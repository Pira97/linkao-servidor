using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Ruleta de eventos globales (modRuletaEventos.bas) 1:1. Cada INTERVALO_RULETA se sortea uno de
/// 3 eventos que dura DURACION_EVENTO y afecta a todos: montar en dungeon, minería x2, drop x2.
/// Tick() se llama 1/seg. Los multiplicadores los consultan DoMineria y TirarDrops.
/// </summary>
public static class Ruleta
{
    public const byte NINGUNO = 0, MONTAR_DUNGEON = 1, MINERIA_X2 = 2, DROP_X2 = 3;

    public static byte EventoActivo { get; private set; } = NINGUNO;

    private static long _tickFin;
    private static long _proximoTick;
    private static bool _inicializado;

    // TEST: sorteo cada 20s; evento dura 1 hora (igual que el VB6 actual).
    private const long INTERVALO_RULETA_MS = 20000;
    private const long DURACION_EVENTO_MS = 3600000;

    private static long Now => Environment.TickCount64;

    private static void Inicializar()
    {
        EventoActivo = NINGUNO;
        _tickFin = 0;
        _proximoTick = Now + INTERVALO_RULETA_MS;
    }

    /// <summary>TickRuleta: 1 vez por segundo. Finaliza el evento activo o sortea uno nuevo.</summary>
    public static void Tick()
    {
        if (!_inicializado) { Inicializar(); _inicializado = true; }
        if (EventoActivo != NINGUNO)
        {
            if (Now >= _tickFin) Finalizar();
        }
        else if (Now >= _proximoTick) Sortear();
    }

    public static bool EventoMontarEnDungeonActivo() => EventoActivo == MONTAR_DUNGEON;
    public static int MultiplicadorMineria() => EventoActivo == MINERIA_X2 ? 2 : 1;
    public static int MultiplicadorDrop() => EventoActivo == DROP_X2 ? 2 : 1;

    /// <summary>NotificarEventoAlLogin: avisa al que loguea si hay un evento activo.</summary>
    public static void NotificarEventoAlLogin(int userIndex)
    {
        if (EventoActivo == NINGUNO) return;
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null) return;
        long minutosRest = (_tickFin - Now) / 60000;
        if (minutosRest < 0) minutosRest = 0;
        ServerPackets.ConsoleMsg(u.Conn,
            $"*** EVENTO ACTIVO: {Nombre(EventoActivo)} - {Descripcion(EventoActivo)} (quedan {minutosRest} min) ***",
            FONT_INFOBOLD);
    }

    // --- privado ---

    private const byte FONT_INFOBOLD = 4, FONT_INFO = 3;

    private static void Sortear()
    {
        byte evento = (byte)Random.Shared.Next(1, 4); // 1..3
        EventoActivo = evento;
        _tickFin = Now + DURACION_EVENTO_MS;
        Anunciar();
    }

    private static void Anunciar()
    {
        Broadcast($"*** RULETA DE EVENTOS *** {Nombre(EventoActivo)} ACTIVADO! {Descripcion(EventoActivo)} Durante 1 hora.", FONT_INFOBOLD);
        Events.SonidoInicioEvento(); // sonido de inicio de evento (252)
    }

    private static void Finalizar()
    {
        byte terminado = EventoActivo;
        EventoActivo = NINGUNO;
        _tickFin = 0;
        _proximoTick = Now + INTERVALO_RULETA_MS;
        Broadcast($"*** El evento {Nombre(terminado)} ha terminado. ***", FONT_INFO);
    }

    private static string Nombre(byte e) => e switch
    {
        MONTAR_DUNGEON => "MONTURAS EN DUNGEON",
        MINERIA_X2 => "MINERIA x2",
        DROP_X2 => "DROP x2",
        _ => "DESCONOCIDO",
    };

    private static string Descripcion(byte e) => e switch
    {
        MONTAR_DUNGEON => "Se permite pelear montado dentro de los dungeons.",
        MINERIA_X2 => "La mineria rinde el doble de minerales.",
        DROP_X2 => "Los monstruos tiran items con el doble de probabilidad.",
        _ => "",
    };

    private static void Broadcast(string msg, byte font)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u.flags.UserLogged && u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, msg, font);
        }
    }
}
