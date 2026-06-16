using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sesión de comercio usuario-a-usuario (compartida por los dos jugadores). Cada uno
/// ofrece hasta 5 items + oro; cuando ambos confirman, se realiza el intercambio.
/// </summary>
public sealed class UserTradeSession
{
    public const int MAX_OFFER_SLOTS = 5;
    public int UserA, UserB;                          // userIndex de cada participante
    public readonly (short obj, int amount, byte invSlot)[] OfferA = new (short, int, byte)[MAX_OFFER_SLOTS + 1];
    public readonly (short obj, int amount, byte invSlot)[] OfferB = new (short, int, byte)[MAX_OFFER_SLOTS + 1];
    public int GoldA, GoldB;
    public bool ConfirmA, ConfirmB;
}

/// <summary>
/// Comercio usuario-a-usuario. Porta el flujo UserCommerce (mdlCOmercioConUsuario.bas):
/// Start (clic sobre el otro), OfferGold/OfferItem, Confirm (cuando ambos confirman se
/// concreta), Cancel/End. Versión núcleo: ofertas a slots 1-5, intercambio al doble-confirm.
/// </summary>
public static class UserTrade
{
    public static void Start(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.Trade != null) return;
        if (u.TargetUserCharIndex == 0) { ServerPackets.ConsoleMsg(u.Conn, "Hacé clic sobre el jugador con quien comerciar.", 1); return; }

        int otherIndex = FindByCharIndex(u.TargetUserCharIndex);
        if (otherIndex == 0) { ServerPackets.ConsoleMsg(u.Conn, "No se encontró a ese jugador.", 1); return; }
        var other = UserListManager.UserList[otherIndex];

        if (Math.Abs(other.Pos.X - u.Pos.X) + Math.Abs(other.Pos.Y - u.Pos.Y) > 3)
        { ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos.", 1); return; }
        if (other.Trade != null) { ServerPackets.ConsoleMsg(u.Conn, "Ese jugador ya está comerciando.", 1); return; }

        var session = new UserTradeSession { UserA = userIndex, UserB = otherIndex };
        u.Trade = session;
        other.Trade = session;
        ServerPackets.UserCommerceInit(u.Conn, other.Name);
        ServerPackets.UserCommerceInit(other.Conn, u.Name);
    }

    public static void OfferGold(int userIndex, int amount)
    {
        var u = UserListManager.UserList[userIndex];
        var s = u.Trade; if (s == null || amount < 0) return;
        if (amount > u.Stats.GLD) amount = u.Stats.GLD;
        if (userIndex == s.UserA) s.GoldA = amount; else s.GoldB = amount;
        ResetConfirms(s);
        SendUpdate(s);
    }

    public static void OfferItem(int userIndex, byte invSlot, int amount)
    {
        var u = UserListManager.UserList[userIndex];
        var s = u.Trade; if (s == null) return;
        if (invSlot < 1 || invSlot > Constants.MAX_INVENTORY_SLOTS) return;
        var item = u.Invent.Object[invSlot];
        if (item.ObjIndex == 0) return;
        if (amount <= 0 || amount > item.Amount) amount = item.Amount;

        var offer = userIndex == s.UserA ? s.OfferA : s.OfferB;
        // ANTI-DUPE: un mismo slot de inventario no puede ofertarse en dos huecos a la vez.
        // Si ya estaba ofertado, se reemplaza la cantidad en su hueco (no se agrega otro).
        for (int i = 1; i <= UserTradeSession.MAX_OFFER_SLOTS; i++)
        {
            if (offer[i].obj != 0 && offer[i].invSlot == invSlot)
            {
                offer[i] = (item.ObjIndex, amount, invSlot);
                ResetConfirms(s);
                SendUpdate(s);
                return;
            }
        }
        // Buscar slot de oferta libre (1-5).
        for (int i = 1; i <= UserTradeSession.MAX_OFFER_SLOTS; i++)
        {
            if (offer[i].obj == 0)
            {
                offer[i] = (item.ObjIndex, amount, invSlot);
                ResetConfirms(s);
                SendUpdate(s);
                return;
            }
        }
        ServerPackets.ConsoleMsg(u.Conn, "No hay más espacio en tu oferta.", 1);
    }

    public static void Confirm(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        var s = u.Trade; if (s == null) return;
        if (userIndex == s.UserA) s.ConfirmA = true; else s.ConfirmB = true;

        if (s.ConfirmA && s.ConfirmB)
            Ejecutar(s);
        else
        {
            var otro = UserListManager.UserList[userIndex == s.UserA ? s.UserB : s.UserA];
            if (otro.Conn != null) ServerPackets.ConsoleMsg(otro.Conn, $"{u.Name} confirmó el intercambio.", 1);
        }
    }

    public static void Cancel(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        var s = u.Trade; if (s == null) return;
        EndSession(s, "El comercio fue cancelado.");
    }

    public static void RequestUpdate(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.Trade != null) SendUpdate(u.Trade);
    }

    // --- interno ---

    private static void Ejecutar(UserTradeSession s)
    {
        var a = UserListManager.UserList[s.UserA];
        var b = UserListManager.UserList[s.UserB];

        // Transferir oro.
        a.Stats.GLD -= s.GoldA; b.Stats.GLD += s.GoldA;
        b.Stats.GLD -= s.GoldB; a.Stats.GLD += s.GoldB;

        // Transferir items: quitar del oferente y dar al receptor.
        TransferItems(a, b, s.OfferA);
        TransferItems(b, a, s.OfferB);

        if (a.Conn != null) { ServerPackets.UpdateGold(a.Conn, a.Stats.GLD); RefreshInv(a); }
        if (b.Conn != null) { ServerPackets.UpdateGold(b.Conn, b.Stats.GLD); RefreshInv(b); }
        EndSession(s, "Comercio realizado con éxito.");
    }

    private static void TransferItems(User from, User to, (short obj, int amount, byte invSlot)[] offer)
    {
        for (int i = 1; i <= UserTradeSession.MAX_OFFER_SLOTS; i++)
        {
            if (offer[i].obj == 0) continue;
            short obj = offer[i].obj; int amt = offer[i].amount; byte slot = offer[i].invSlot;
            // ANTI-DUPE: solo se entrega lo que el oferente realmente tiene en ese slot.
            // Si el slot ya no contiene el objeto (p.ej. ofertado dos veces), no se transfiere nada.
            if (from.Invent.Object[slot].ObjIndex != obj) continue;
            int give = Math.Min(amt, from.Invent.Object[slot].Amount);
            if (give <= 0) continue;
            // Quitar del oferente (desequipa si entrega el stack equipado).
            Inventory.QuitarUserInvItem(from, slot, give);
            // Dar al receptor.
            int dest = FindInvSlot(to, obj);
            if (dest == 0) continue; // sin espacio: se pierde (simplificación)
            if (to.Invent.Object[dest].ObjIndex == obj) to.Invent.Object[dest].Amount += give;
            else { to.Invent.Object[dest].ObjIndex = obj; to.Invent.Object[dest].Amount = give; to.Invent.NroItems++; }
        }
    }

    private static void EndSession(UserTradeSession s, string msg)
    {
        var a = UserListManager.UserList[s.UserA];
        var b = UserListManager.UserList[s.UserB];
        a.Trade = null; b.Trade = null;
        if (a.Conn != null) { ServerPackets.UserCommerceEnd(a.Conn); ServerPackets.ConsoleMsg(a.Conn, msg, 1); }
        if (b.Conn != null) { ServerPackets.UserCommerceEnd(b.Conn); ServerPackets.ConsoleMsg(b.Conn, msg, 1); }
    }

    private static void ResetConfirms(UserTradeSession s) { s.ConfirmA = false; s.ConfirmB = false; }

    private static void SendUpdate(UserTradeSession s)
    {
        var a = UserListManager.UserList[s.UserA];
        var b = UserListManager.UserList[s.UserB];
        // A ve su oferta como side 0 y la de B como side 1; B al revés.
        SendOfferTo(a, s.OfferA, 0); SendOfferTo(a, s.OfferB, 1);
        SendOfferTo(b, s.OfferB, 0); SendOfferTo(b, s.OfferA, 1);
    }

    private static void SendOfferTo(User u, (short obj, int amount, byte invSlot)[] offer, byte side)
    {
        if (u.Conn == null) return;
        for (byte i = 1; i <= UserTradeSession.MAX_OFFER_SLOTS; i++)
            ServerPackets.UserCommerceUpdate(u.Conn, side, i, offer[i].obj, offer[i].amount);
    }

    private static void RefreshInv(User u)
    {
        for (int slot = 1; slot <= Constants.MAX_INVENTORY_SLOTS; slot++)
        {
            var o = u.Invent.Object[slot];
            ServerPackets.ChangeInventorySlot(u.Conn, (byte)slot, o.ObjIndex, o.Amount, o.Equipped);
        }
    }

    private static int FindInvSlot(User u, short obj)
    {
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == obj) return s;
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == 0) return s;
        return 0;
    }

    private static int FindByCharIndex(short charIndex)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Char.CharIndex == charIndex) return i;
        }
        return 0;
    }
}
