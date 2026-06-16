using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Bóveda / banco. Porta HandleBankStart/HandleBankDeposit/HandleBankExtractItem/HandleBankEnd
/// (Protocol.bas). El jugador selecciona un NPC banquero con LeftClick y deposita/extrae
/// items entre su inventario y la bóveda (40 slots). El oro de banco vive en Stats.Banco.
///
/// Falta al portar más: depósito/extracción de oro (BankDepositGold/ExtractGold),
/// validación de que el NPC sea específicamente banquero (hoy alcanza con que esté cerca).
/// </summary>
public static class Bank
{
    /// <summary>HandleBankStart: abre la bóveda (BankInit + items del banco).</summary>
    public static void BankStart(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.flags.Muerto == 1) return;
        if (u.TargetNpcCharIndex == 0)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Primero seleccioná un banquero (clic sobre él).", 1);
            return;
        }
        var npc = NpcManager.NpcByCharIndex(u.Pos.Map, u.TargetNpcCharIndex);
        if (npc == null || Math.Abs(npc.X - u.Pos.X) + Math.Abs(npc.Y - u.Pos.Y) > 3)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos del banquero.", 1);
            return;
        }

        AbrirBancoNpc(userIndex, npc);
    }

    /// <summary>Abre la bóveda con un NPC banquero ya validado (lo usa Accion/doble-click).</summary>
    public static void AbrirBancoNpc(int userIndex, NpcManager.NpcInstance npc)
    {
        var u = UserListManager.UserList[userIndex];
        u.Comerciando = true; // flag "en ventana" para validar deposit/extract
        SendBankInit(u);
    }

    /// <summary>HandleBankEnd: cierra la bóveda.</summary>
    public static void BankEnd(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        u.Comerciando = false;
        ServerPackets.BankEnd(u.Conn);
    }

    /// <summary>HandleBankDeposit: mueve 'amount' del slot de inventario a la bóveda.</summary>
    public static void Deposit(int userIndex, byte invSlot, int amount)
    {
        var u = UserListManager.UserList[userIndex];
        if (!u.Comerciando || amount <= 0) return;
        if (invSlot < 1 || invSlot > Constants.MAX_INVENTORY_SLOTS) return;
        ref var src = ref u.Invent.Object[invSlot];
        if (src.ObjIndex == 0) return;
        if (amount > src.Amount) amount = src.Amount;

        int bankSlot = FindBankSlot(u, src.ObjIndex);
        if (bankSlot == 0) { ServerPackets.ConsoleMsg(u.Conn, "La bóveda está llena.", 1); return; }

        var bo = u.BancoInvent.Object[bankSlot];
        if (bo.ObjIndex == src.ObjIndex) u.BancoInvent.Object[bankSlot].Amount += amount;
        else
        {
            u.BancoInvent.Object[bankSlot].ObjIndex = src.ObjIndex;
            u.BancoInvent.Object[bankSlot].Amount = amount;
            u.BancoInvent.NroItems++;
        }

        Inventory.QuitarUserInvItem(u, invSlot, amount); // desequipa si se deposita el stack equipado

        SendBankSlot(u, bankSlot);
        SendInvSlot(u, invSlot);
    }

    /// <summary>HandleBankExtractItem: mueve 'amount' del slot de bóveda al inventario.</summary>
    public static void Extract(int userIndex, byte bankSlot, int amount)
    {
        var u = UserListManager.UserList[userIndex];
        if (!u.Comerciando || amount <= 0) return;
        if (bankSlot < 1 || bankSlot > Constants.MAX_BANCOINVENTORY_SLOTS) return;
        ref var src = ref u.BancoInvent.Object[bankSlot];
        if (src.ObjIndex == 0) return;
        if (amount > src.Amount) amount = src.Amount;

        int invSlot = FindInvSlot(u, src.ObjIndex);
        if (invSlot == 0) { ServerPackets.ConsoleMsg(u.Conn, "No tenés espacio en el inventario.", 1); return; }

        if (u.Invent.Object[invSlot].ObjIndex == src.ObjIndex) u.Invent.Object[invSlot].Amount += amount;
        else
        {
            u.Invent.Object[invSlot].ObjIndex = src.ObjIndex;
            u.Invent.Object[invSlot].Amount = amount;
            u.Invent.Object[invSlot].Equipped = false;
            u.Invent.NroItems++;
        }

        src.Amount -= amount;
        if (src.Amount <= 0)
        {
            src.ObjIndex = 0; src.Amount = 0;
            if (u.BancoInvent.NroItems > 0) u.BancoInvent.NroItems--;
        }

        SendBankSlot(u, bankSlot);
        SendInvSlot(u, invSlot);
    }

    /// <summary>HandleBankDepositGold: mueve oro del personaje a la bóveda.</summary>
    public static void DepositGold(int userIndex, int amount)
    {
        var u = UserListManager.UserList[userIndex];
        if (!u.Comerciando || amount <= 0) return;
        if (amount > u.Stats.GLD) amount = u.Stats.GLD;
        u.Stats.GLD -= amount;
        u.Stats.Banco += amount;
        ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
        SendBankInit(u); // refresca el oro mostrado en la bóveda
    }

    /// <summary>HandleBankExtractGold: mueve oro de la bóveda al personaje.</summary>
    public static void ExtractGold(int userIndex, int amount)
    {
        var u = UserListManager.UserList[userIndex];
        if (!u.Comerciando || amount <= 0) return;
        if (amount > u.Stats.Banco) amount = u.Stats.Banco;
        u.Stats.Banco -= amount;
        u.Stats.GLD += amount;
        ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
        SendBankInit(u);
    }

    /// <summary>
    /// HandleMoveBank (Protocol.bas:5254) 1:1. Reordena un item de la bóveda: dir=true sube
    /// (intercambia con slot-1), dir=false baja (intercambia con slot+1). Refresca la ventana.
    /// </summary>
    public static void MoveBank(int userIndex, bool dirUp, byte slot)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged) return;

        int otro = dirUp ? slot - 1 : slot + 1;
        if (slot < 1 || slot > Constants.MAX_BANCOINVENTORY_SLOTS) return;
        if (otro < 1 || otro > Constants.MAX_BANCOINVENTORY_SLOTS) return;

        // Intercambio (VB6: TempItem guarda slot, se copia el vecino encima y se restaura).
        var tmp = u.BancoInvent.Object[slot];
        u.BancoInvent.Object[slot] = u.BancoInvent.Object[otro];
        u.BancoInvent.Object[otro] = tmp;

        SendBankInit(u); // UpdateBanUserInv(True) + UpdateVentanaBanco: refresca toda la bóveda
    }

    // --- helpers ---

    private static void SendBankInit(User u)
    {
        ServerPackets.BankInit(u.Conn, u.Stats.Banco, (byte)u.BancoInvent.NroItems);
        for (int slot = 1; slot <= Constants.MAX_BANCOINVENTORY_SLOTS; slot++)
            SendBankSlot(u, slot);
    }

    private static void SendBankSlot(User u, int slot)
    {
        var o = u.BancoInvent.Object[slot];
        int valor = o.ObjIndex > 0 ? ObjData.Get(o.ObjIndex).Valor : 0;
        ServerPackets.ChangeBankSlot(u.Conn, (byte)slot, o.ObjIndex, o.Amount, valor);
    }

    private static void SendInvSlot(User u, int slot)
    {
        var o = u.Invent.Object[slot];
        ServerPackets.ChangeInventorySlot(u.Conn, (byte)slot, o.ObjIndex, o.Amount, o.Equipped);
    }

    private static int FindBankSlot(User u, short objIndex)
    {
        for (int s = 1; s <= Constants.MAX_BANCOINVENTORY_SLOTS; s++)
            if (u.BancoInvent.Object[s].ObjIndex == objIndex) return s;
        for (int s = 1; s <= Constants.MAX_BANCOINVENTORY_SLOTS; s++)
            if (u.BancoInvent.Object[s].ObjIndex == 0) return s;
        return 0;
    }

    private static int FindInvSlot(User u, short objIndex)
    {
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == objIndex) return s;
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == 0) return s;
        return 0;
    }
}
