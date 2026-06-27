using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// (NUEVO, no VB6) Vida/maná en vivo de CUALQUIER personaje para los Game Masters, sobre el
/// mismo patrón que PartySystem.SendPartyMemberHP/Mana (MEJORA-005) pero sin el filtro de
/// grupo: se manda a toda conexión GM (FaccionStatus>=STATUS_CONSEJERO) que efectivamente
/// ve a ese char (AreaVisibility.VeChar), igual criterio que BroadcastEfectoCharParticula en
/// PacketHandler.cs. Se llama desde los mismos puntos donde ya se refresca la barra de vida/
/// maná del grupo (daño en combate, curas, pociones): son los momentos que importan, no cada
/// tick.
/// </summary>
public static class GmWatch
{
    public static void BroadcastHP(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.flags.UserLogged != true) return;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.id != userIndex
                && o.FaccionStatus >= AdminLoader.STATUS_CONSEJERO
                && AreaVisibility.VeChar(o, u.Pos.Map, u.Char.CharIndex))
                ServerPackets.GmWatchHP(o.Conn, u.Char.CharIndex, u.Stats.MinHP, u.Stats.MaxHP);
        }
    }

    public static void BroadcastMana(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.flags.UserLogged != true) return;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.id != userIndex
                && o.FaccionStatus >= AdminLoader.STATUS_CONSEJERO
                && AreaVisibility.VeChar(o, u.Pos.Map, u.Char.CharIndex))
                ServerPackets.GmWatchMana(o.Conn, u.Char.CharIndex, u.Stats.MinMAN, u.Stats.MaxMAN);
        }
    }
}
