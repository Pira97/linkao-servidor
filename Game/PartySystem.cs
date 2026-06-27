using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sistema de grupos (party) portado 1:1 del VB6 (tParty + handlers de Protocol.bas).
/// Modelo: Parties[1..100] con LeaderIndex, Members[1..5], MemberCount. User.PartyId = índice
/// del grupo (0 = sin grupo). Flujo de invitación: el líder invita (PartyJoin con CharIndex) →
/// el invitado acepta (PartyAccept) o rechaza (PartyReject). El grupo se crea recién al aceptar
/// la primera invitación. Restricciones de facción al invitar, máximo 5 miembros, traspaso de
/// liderazgo y disolución al quedar ≤1. FONTTYPE_GMMSG = 16.
/// </summary>
public static class PartySystem
{
    private const int MAX_PARTIES = 100;
    private const int MAX_MEMBERS = 5;
    private const byte FONT_GMMSG = 16; // FONTTYPE_GMMSG

    private sealed class Party
    {
        public int LeaderIndex;
        public int[] Members = new int[MAX_MEMBERS + 1]; // 1..5
        public byte MemberCount;
    }

    private static readonly Party[] _parties = NewParties();
    // PartyInvitations[targetUserIndex] = inviterUserIndex (invitación pendiente; 0 = ninguna).
    private static readonly int[] _invitations = new int[10001];

    private static Party[] NewParties()
    {
        var a = new Party[MAX_PARTIES + 1];
        for (int i = 1; i <= MAX_PARTIES; i++) a[i] = new Party();
        return a;
    }

    /// <summary>HandlePartyCreate: solo informa; el grupo se crea al aceptar la primera invitación.</summary>
    public static void Create(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.PartyId > 0) { Msg(userIndex, ">> Ya estás en un grupo."); return; }
        Msg(userIndex, ">> Grupo creado. Puedes invitar jugadores.");
    }

    /// <summary>HandlePartyJoin (Protocol.bas:20900): el líder invita al PJ con ese CharIndex.</summary>
    public static void Join(int userIndex, short targetCharIndex)
    {
        int target = FindUserByCharIndex(targetCharIndex);
        if (target == 0) { Msg(userIndex, ">> El jugador no está disponible."); return; }
        JoinTarget(userIndex, target);
    }

    /// <summary>
    /// (NUEVO, no VB6) Invitar al grupo por NOMBRE (desde el panel de Amigos). El server resuelve
    /// el userIndex global igual que Join por CharIndex (no requiere cercanía).
    /// </summary>
    public static void JoinByName(int userIndex, string name)
    {
        int target = UserListManager.NameIndex(name);
        if (target == 0) { Msg(userIndex, $">> {name} no está conectado."); return; }
        JoinTarget(userIndex, target);
    }

    /// <summary>Lógica común de invitación (1:1 HandlePartyJoin) ya resuelto el target userIndex.</summary>
    private static void JoinTarget(int userIndex, int target)
    {
        var u = UserListManager.UserList[userIndex];
        var t = UserListManager.UserList[target];
        if (!t.flags.UserLogged) { Msg(userIndex, ">> El jugador no está conectado."); return; }
        if (target == userIndex) { Msg(userIndex, ">> No puedes invitarte a ti mismo al grupo."); return; }
        if (t.PartyId > 0) { Msg(userIndex, $">> {t.Name} ya está en un grupo."); return; }

        // Si el invitador ya está en un grupo: debe ser líder y haber lugar.
        if (u.PartyId > 0)
        {
            var p = _parties[u.PartyId];
            if (p.LeaderIndex != userIndex) { Msg(userIndex, ">> Solo el líder puede invitar miembros al grupo."); return; }
            if (p.MemberCount >= MAX_MEMBERS) { Msg(userIndex, ">> El grupo está lleno. No puedes invitar a más jugadores (máximo 5 miembros)."); return; }
        }

        if (!FaccionesCompatibles(u.Faccion.Status, t.Faccion.Status, userIndex)) return; // ya avisó

        _invitations[target] = userIndex;
        Msg(userIndex, $">> Invitación enviada a {t.Name}.");
        ServerPackets.PartyInvitation(t.Conn, u.Name);
    }

    /// <summary>HandlePartyAccept (Protocol.bas:14382): acepta la invitación pendiente.</summary>
    public static void Accept(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        int inviter = _invitations[userIndex];
        if (inviter <= 0) { Msg(userIndex, ">> No tienes invitaciones pendientes."); return; }
        _invitations[userIndex] = 0;
        if (u.PartyId > 0) { Msg(userIndex, ">> Ya estás en un grupo."); return; }

        var inv = UserListManager.UserList[inviter];
        int pidx;
        if (inv.PartyId == 0)
        {
            // Crear grupo con el invitador como líder.
            pidx = 0;
            for (int i = 1; i <= MAX_PARTIES; i++) if (_parties[i].LeaderIndex == 0) { pidx = i; break; }
            if (pidx == 0) { Msg(userIndex, ">> No hay espacio para más grupos."); return; }
            var np = _parties[pidx];
            np.LeaderIndex = inviter; np.Members[1] = inviter; np.MemberCount = 1;
            inv.PartyId = pidx;
            Msg(inviter, ">> Has creado un grupo.");
        }
        else pidx = inv.PartyId;

        var p = _parties[pidx];
        if (p.MemberCount >= MAX_MEMBERS)
        {
            Msg(userIndex, ">> El grupo está lleno. No puedes unirte (máximo 5 miembros).");
            if (inv.flags.UserLogged) Msg(inviter, $">> El grupo está lleno. {u.Name} no pudo unirse.");
            return;
        }

        p.MemberCount++;
        p.Members[p.MemberCount] = userIndex;
        u.PartyId = pidx;

        for (int i = 1; i <= p.MemberCount; i++)
        {
            int m = p.Members[i];
            if (m <= 0) continue;
            Msg(m, m == userIndex ? ">> Te has unido al grupo." : $">> {u.Name} se ha unido al grupo.");
        }
        SendPartyUpdateToAll(pidx);
        for (int i = 1; i <= p.MemberCount; i++) if (p.Members[i] > 0) SendPartyMemberHP(p.Members[i]);
    }

    /// <summary>HandlePartyReject: solo informa.</summary>
    public static void Reject(int userIndex)
    {
        _invitations[userIndex] = 0;
        ServerPackets.ConsoleMsg(UserListManager.UserList[userIndex].Conn, "Has rechazado la invitación al grupo.", 3);
    }

    /// <summary>HandlePartyLeave (Protocol.bas:21049): abandona el grupo.</summary>
    public static void Leave(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.PartyId == 0) { Msg(userIndex, ">> No estás en ningún grupo."); return; }
        int pidx = u.PartyId;
        RemoveMember(pidx, userIndex);
        u.PartyId = 0;
        Msg(userIndex, ">> Has abandonado el grupo.");
        DisolverOTraspasar(pidx, userIndex, "ha abandonado el grupo");
        SendEmptyList(userIndex);
    }

    /// <summary>HandlePartyKick (Protocol.bas:21171): el líder expulsa a un miembro por nombre.</summary>
    public static void Kick(int userIndex, string memberName)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.PartyId == 0) { Msg(userIndex, ">> No estás en ningún grupo."); return; }
        int pidx = u.PartyId;
        var p = _parties[pidx];
        if (p.LeaderIndex != userIndex) { Msg(userIndex, ">> Solo el líder puede expulsar miembros."); return; }

        int target = 0;
        for (int i = 1; i <= p.MemberCount; i++)
        {
            int m = p.Members[i];
            if (m > 0 && string.Equals(UserListManager.UserList[m].Name, memberName, StringComparison.OrdinalIgnoreCase))
            { target = m; break; }
        }
        if (target == 0) { Msg(userIndex, ">> El jugador no está en tu grupo."); return; }
        if (target == userIndex) { Msg(userIndex, ">> No puedes expulsarte a ti mismo. Usa abandonar."); return; }

        RemoveMember(pidx, target);
        UserListManager.UserList[target].PartyId = 0;
        Msg(userIndex, $">> Has expulsado a {memberName} del grupo.");
        Msg(target, ">> Has sido expulsado del grupo.");
        SendEmptyList(target);
        for (int i = 1; i <= p.MemberCount; i++) if (p.Members[i] > 0) Msg(p.Members[i], $">> {memberName} ha sido expulsado del grupo.");
        SendPartyUpdateToAll(pidx);
    }

    /// <summary>HandlePartyMessage (Protocol.bas:21286): chat al grupo (PartyMessage a cada miembro).</summary>
    public static void Message(int userIndex, string message)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.PartyId == 0) { Msg(userIndex, ">> No estás en ningún grupo."); return; }
        var p = _parties[u.PartyId];
        for (int i = 1; i <= p.MemberCount; i++)
        {
            int m = p.Members[i];
            if (m > 0 && UserListManager.UserList[m].Conn != null)
                ServerPackets.PartyMessage(UserListManager.UserList[m].Conn, u.Name, message);
        }
    }

    /// <summary>HandlePartyOnline (Protocol.bas:21341): reenvía lista + HP de todos al solicitante.</summary>
    public static void Online(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.PartyId <= 0) return;
        SendPartyUpdateToAll(u.PartyId);
        var p = _parties[u.PartyId];
        for (int i = 1; i <= p.MemberCount; i++)
        {
            int m = p.Members[i];
            if (m > 0 && UserListManager.UserList[m].flags.UserLogged)
            {
                var mu = UserListManager.UserList[m];
                ServerPackets.PartyMemberHP(u.Conn, mu.Char.CharIndex, mu.Stats.MinHP, mu.Stats.MaxHP);
            }
        }
    }

    /// <summary>SendPartyMemberHP (Protocol.bas:21450): envía el HP del usuario al resto del grupo.</summary>
    public static void SendPartyMemberHP(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.PartyId <= 0) return;
        var p = _parties[u.PartyId];
        for (int i = 1; i <= p.MemberCount; i++)
        {
            int m = p.Members[i];
            if (m > 0 && m != userIndex && UserListManager.UserList[m].flags.UserLogged)
                ServerPackets.PartyMemberHP(UserListManager.UserList[m].Conn, u.Char.CharIndex, u.Stats.MinHP, u.Stats.MaxHP);
        }
    }

    /// <summary>CleanupUserParty (Protocol.bas:20760): saca al usuario que se desconecta del grupo.</summary>
    public static void Cleanup(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.PartyId == 0) return;
        int pidx = u.PartyId;
        RemoveMember(pidx, userIndex);
        u.PartyId = 0;
        DisolverOTraspasar(pidx, userIndex, "se ha desconectado");
    }

    /// <summary>
    /// Devuelve los userIndex de los miembros del grupo del usuario (el líder primero), o lista
    /// vacía si no tiene grupo. Usado por el sistema de torneos para inscribir equipos completos.
    /// </summary>
    public static List<int> GetPartyMembers(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        var res = new List<int>();
        if (u.PartyId <= 0 || u.PartyId > MAX_PARTIES) return res;
        var p = _parties[u.PartyId];
        if (p.LeaderIndex > 0) res.Add(p.LeaderIndex);
        for (int i = 1; i <= p.MemberCount; i++)
            if (p.Members[i] > 0 && p.Members[i] != p.LeaderIndex) res.Add(p.Members[i]);
        return res;
    }

    /// <summary>True si el usuario es líder de su grupo.</summary>
    public static bool IsLeader(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.PartyId <= 0 || u.PartyId > MAX_PARTIES) return false;
        return _parties[u.PartyId].LeaderIndex == userIndex;
    }

    // ---------------- helpers ----------------

    /// <summary>Saca un miembro del array compactando (mueve los siguientes hacia atrás).</summary>
    private static void RemoveMember(int pidx, int userIndex)
    {
        var p = _parties[pidx];
        for (int i = 1; i <= p.MemberCount; i++)
        {
            if (p.Members[i] == userIndex)
            {
                for (int j = i; j < p.MemberCount; j++) p.Members[j] = p.Members[j + 1];
                p.Members[p.MemberCount] = 0;
                p.MemberCount--;
                break;
            }
        }
    }

    /// <summary>
    /// Tras sacar un miembro: si queda ≤1 disuelve; si se fue el líder traspasa al primer
    /// miembro conectado (o disuelve si no hay), y notifica. 1:1 con VB6.
    /// </summary>
    private static void DisolverOTraspasar(int pidx, int salienteIndex, string accion)
    {
        var p = _parties[pidx];
        string nombreSaliente = UserListManager.UserList[salienteIndex].Name;

        if (p.MemberCount <= 1)
        {
            for (int i = 1; i <= MAX_MEMBERS; i++)
            {
                int m = p.Members[i];
                if (m > 0)
                {
                    UserListManager.UserList[m].PartyId = 0;
                    Msg(m, ">> El grupo se ha disuelto.");
                    SendEmptyList(m);
                }
            }
            p.LeaderIndex = 0; p.MemberCount = 0;
            return;
        }

        if (p.LeaderIndex == salienteIndex)
        {
            int nuevoLider = 0;
            for (int i = 1; i <= p.MemberCount; i++)
            {
                int m = p.Members[i];
                if (m > 0 && UserListManager.UserList[m].flags.UserLogged) { nuevoLider = m; p.LeaderIndex = m; break; }
            }
            if (nuevoLider > 0)
            {
                for (int i = 1; i <= p.MemberCount; i++)
                {
                    int m = p.Members[i];
                    if (m <= 0) continue;
                    Msg(m, m == nuevoLider
                        ? $">> {nombreSaliente} {accion}. Ahora eres el líder del grupo."
                        : $">> {nombreSaliente} {accion}. {UserListManager.UserList[nuevoLider].Name} es ahora el líder del grupo.");
                }
            }
            else
            {
                for (int i = 1; i <= p.MemberCount; i++)
                {
                    int m = p.Members[i];
                    if (m > 0) { UserListManager.UserList[m].PartyId = 0; Msg(m, ">> El grupo se ha disuelto porque no hay líder disponible."); SendEmptyList(m); }
                }
                p.LeaderIndex = 0; p.MemberCount = 0;
                return;
            }
        }
        else
        {
            for (int i = 1; i <= p.MemberCount; i++)
                if (p.Members[i] > 0) Msg(p.Members[i], $">> {nombreSaliente} {accion}.");
        }
        SendPartyUpdateToAll(pidx);
    }

    /// <summary>SendPartyUpdateToAll (Protocol.bas:14346): manda la lista de miembros a todos.</summary>
    private static void SendPartyUpdateToAll(int pidx)
    {
        if (pidx <= 0 || pidx > MAX_PARTIES) return;
        var p = _parties[pidx];
        var names = new string[MAX_MEMBERS + 1];
        string leader = p.LeaderIndex > 0 ? UserListManager.UserList[p.LeaderIndex].Name : "";
        for (int i = 1; i <= p.MemberCount; i++)
            if (p.Members[i] > 0) names[i] = UserListManager.UserList[p.Members[i]].Name;
        for (int i = 1; i <= p.MemberCount; i++)
            if (p.Members[i] > 0) ServerPackets.PartyMemberList(UserListManager.UserList[p.Members[i]].Conn, names, p.MemberCount, leader);
    }

    private static void SendEmptyList(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.Conn != null) ServerPackets.PartyMemberList(u.Conn, new string[MAX_MEMBERS + 1], 0, "");
    }

    /// <summary>
    /// Restricciones de facción al invitar (HandlePartyJoin 1:1). Imperiales(5) solo con 5;
    /// Caos(4)/Renegados(1) entre sí; Repu(3)/Milicia(6) entre sí; Ciudadanos(2) entre sí;
    /// resto solo con su mismo status. Avisa al invitador si no son compatibles.
    /// </summary>
    private static bool FaccionesCompatibles(byte inv, byte tgt, int userIndex)
    {
        switch (inv)
        {
            case 5:
                if (tgt == 5) return true;
                Msg(userIndex, ">> Los Imperiales solo pueden hacer grupo con otros Imperiales."); return false;
            case 4:
                if (tgt == 4 || tgt == 1) return true;
                Msg(userIndex, ">> Los miembros del Caos solo pueden hacer grupo con Caos o Renegados."); return false;
            case 1:
                if (tgt == 4 || tgt == 1) return true;
                Msg(userIndex, ">> Los Renegados solo pueden hacer grupo con Caos o Renegados."); return false;
            case 3:
                if (tgt == 3 || tgt == 6) return true;
                Msg(userIndex, ">> Los Republicanos solo pueden hacer grupo con Republicanos o Milicia."); return false;
            case 6:
                if (tgt == 3 || tgt == 6) return true;
                Msg(userIndex, ">> La Milicia solo puede hacer grupo con Republicanos o Milicia."); return false;
            case 2:
                if (tgt == 2) return true;
                Msg(userIndex, ">> Los Ciudadanos solo pueden hacer grupo con otros Ciudadanos."); return false;
            default:
                if (tgt == inv) return true;
                Msg(userIndex, ">> No puedes hacer grupo con jugadores de diferente facción."); return false;
        }
    }

    private static void Msg(int userIndex, string text)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn != null) ServerPackets.ConsoleMsg(u.Conn, text, FONT_GMMSG);
    }

    private static int FindUserByCharIndex(short charIndex)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Char.CharIndex == charIndex) return i;
        }
        return 0;
    }

    /// <summary>GuildMessage: chat del clan, a todos los del mismo GuildIndex (>0).</summary>
    public static void GuildMessage(int userIndex, string msg)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.GuildIndex == 0) { ServerPackets.ConsoleMsg(u.Conn, "No perteneces a un clan.", 1); return; }
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.GuildIndex == u.GuildIndex)
                ServerPackets.GuildChat(o.Conn, $"{u.Name}> {msg}");
        }
    }
}
