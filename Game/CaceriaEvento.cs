using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Evento Cacería por Facción (modEventoCaceriaFaccion.bas) 1:1. Mientras está activo, cuenta los
/// kills PvP por facción del atacante; al finalizar (GM) determina la facción con más kills y reparte
/// un Gran Saco de Créditos (item 1605) a todos sus miembros online. GM-only (Dios). El hook de kill
/// vive en Facciones.ContarMuerte.
/// (El check de "muerto Desnudo" y la notificación a Discord del VB6 se omiten: no portados — igual
/// que la simplificación ya existente en ContarMuerte.)
/// </summary>
public static class CaceriaEvento
{
    private const short ITEM_GRAN_SACO_CREDITOS = 1605;

    public static bool EventoActivo { get; private set; }
    private static long _killsImperial, _killsCaos, _killsRepublicano, _killsMiliciano, _killsRenegado;

    private const byte FONT_INFOBOLD = 4, FONT_GUILD = 5, FONT_WARNING = 2, FONT_INFO = 3;

    /// <summary>SumarKillEventoCaceria: suma 1 a la facción del atacante (llamado desde ContarMuerte).</summary>
    public static void SumarKill(int atacanteIndex, int muertoIndex)
    {
        if (!EventoActivo) return;
        var atk = UserListManager.UserList[atacanteIndex];
        var vic = UserListManager.UserList[muertoIndex];
        if (atk == null || vic == null || !atk.flags.UserLogged || !vic.flags.UserLogged) return;
        if (Facciones.EsNewbie(atk) || Facciones.EsNewbie(vic)) return;
        // VB6 además excluye muerto Desnudo y zonas de pelea; no portados (igual que ContarMuerte).

        switch (atk.Faccion.Status)
        {
            case 2: case 5: _killsImperial++; break;   // Ciudadano Imperial / Armada
            case 4: _killsCaos++; break;
            case 3: _killsRepublicano++; break;
            case 6: _killsMiliciano++; break;
            case 1: _killsRenegado++; break;
            // sin facción: no cuenta
        }
    }

    /// <summary>DeterminarFaccionGanadora: facción con más kills; 0 si empate o sin kills.</summary>
    public static byte DeterminarFaccionGanadora()
    {
        long max = 0; byte ganadora = 0; bool empate = false;
        void Check(long kills, byte status)
        {
            if (kills > max) { max = kills; ganadora = status; empate = false; }
            else if (kills == max && max > 0) empate = true;
        }
        Check(_killsImperial, 2);
        Check(_killsCaos, 4);
        Check(_killsRepublicano, 3);
        Check(_killsMiliciano, 6);
        Check(_killsRenegado, 1);
        return (empate || max == 0) ? (byte)0 : ganadora;
    }

    /// <summary>IniciarEventoCaceria: resetea contadores, activa y anuncia.</summary>
    public static void Iniciar(string activadoPor)
    {
        _killsImperial = _killsCaos = _killsRepublicano = _killsMiliciano = _killsRenegado = 0;
        EventoActivo = true;
        string m = "¡EVENTO DE CACERÍA POR FACCIÓN INICIADO!\nLas facciones competirán por obtener la mayor cantidad de kills.\nLa facción ganadora recibirá el Gran Saco de Créditos.";
        if (!string.IsNullOrWhiteSpace(activadoPor)) m += "\nActivado por: " + activadoPor;
        Broadcast(m, FONT_INFOBOLD);
    }

    /// <summary>FinalizarEventoCaceria: determina ganador, reparte premio y desactiva.</summary>
    public static void Finalizar(string finalizadoPor)
    {
        byte ganadora = DeterminarFaccionGanadora();
        if (ganadora > 0) RepartirPremio(ganadora);
        AnunciarFin(ganadora, finalizadoPor);
        EventoActivo = false;
    }

    /// <summary>Estado actual (para /estadocaceria).</summary>
    public static string Estado()
    {
        if (!EventoActivo) return "Evento de Cacería por Facción: INACTIVO";
        return "Evento de Cacería por Facción: ACTIVO\n\nKills por facción:\n" +
               $"- Imperiales: {_killsImperial}\n- Caóticos: {_killsCaos}\n- Republicanos: {_killsRepublicano}\n" +
               $"- Milicianos: {_killsMiliciano}\n- Renegados: {_killsRenegado}";
    }

    // --- privado ---

    private static void RepartirPremio(byte faccionGanadora)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u == null || !u.flags.UserLogged || u.Conn == null) continue;
            byte s = u.Faccion.Status;
            bool pertenece = faccionGanadora switch
            {
                2 => s == 2 || s == 5,
                3 => s == 3 || s == 6,
                4 => s == 4,
                6 => s == 6,
                1 => s == 1,
                _ => false,
            };
            if (!pertenece) continue;

            if (Inventory.AddItemToInventory(u, ITEM_GRAN_SACO_CREDITOS, 1))
                ServerPackets.ConsoleMsg(u.Conn, "¡Felicidades! Has recibido el Gran Saco de Créditos por la victoria de tu facción en el Evento de Cacería.", FONT_GUILD);
            else
                ServerPackets.ConsoleMsg(u.Conn, "Tu facción ganó el Evento de Cacería, pero tu inventario está lleno. No pudiste recibir el Gran Saco de Créditos.", FONT_WARNING);
        }
    }

    private static void AnunciarFin(byte ganadora, string finalizadoPor)
    {
        string resultados = $"Resultados:\n- Imperiales: {_killsImperial} kills\n- Caóticos: {_killsCaos} kills\n- Republicanos: {_killsRepublicano} kills\n- Milicianos: {_killsMiliciano} kills\n- Renegados: {_killsRenegado} kills";
        string m;
        if (ganadora > 0)
            m = $"¡EVENTO DE CACERÍA POR FACCIÓN FINALIZADO!\n\n¡¡ FACCIÓN GANADORA: {NombreCompleto(ganadora)}\n\n{resultados}\n\nLos jugadores online de {NombreCompleto(ganadora)} han recibido el Gran Saco de Créditos.";
        else
            m = $"¡EVENTO DE CACERÍA POR FACCIÓN FINALIZADO!\n\nNo hubo facción ganadora (empate o sin kills).\n\n{resultados}";
        if (!string.IsNullOrWhiteSpace(finalizadoPor)) m += "\nFinalizado por: " + finalizadoPor;
        Broadcast(m, FONT_INFOBOLD);
    }

    private static string NombreCompleto(byte status) => status switch
    {
        1 => "Renegados", 2 => "Imperiales", 3 => "Republicanos", 4 => "Caóticos", 5 => "Imperiales", 6 => "Milicianos", _ => "Sin Facción",
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
