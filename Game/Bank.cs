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
        SendBankInitPremium(u); // estado de desbloqueo + contenido de las 2 solapas premium
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

    // --- Bóvedas premium (NUEVO, no VB6) ---
    // 2 bóvedas extra de 80 slots, por personaje, en paralelo a la Normal (no comparten
    // límite ni slots con BancoInvent). Requieren u.BovedaPremiumDesbloqueada; se compran
    // gastando CreditoDonador (mismo saldo que ya carga MercadoPago.cs con dinero real,
    // igual patrón que PremiumParticles.Comprar / BattlePass.ComprarPasePremium).

    /// <summary>HandleBankDepositPremium: mueve 'amount' del inventario a la bóveda premium 1|2.</summary>
    public static void DepositPremium(int userIndex, byte vaultId, byte invSlot, int amount)
    {
        var u = UserListManager.UserList[userIndex];
        if (!u.Comerciando || !u.BovedaPremiumDesbloqueada || amount <= 0) return;
        if (vaultId != 1 && vaultId != 2) return;
        if (invSlot < 1 || invSlot > Constants.MAX_INVENTORY_SLOTS) return;
        ref var src = ref u.Invent.Object[invSlot];
        if (src.ObjIndex == 0) return;
        if (amount > src.Amount) amount = src.Amount;

        var vault = VaultOf(u, vaultId);
        int bankSlot = FindBankSlotPremium(vault, src.ObjIndex);
        if (bankSlot == 0) { ServerPackets.ConsoleMsg(u.Conn, "La bóveda premium está llena.", 1); return; }

        var bo = vault.Object[bankSlot];
        if (bo.ObjIndex == src.ObjIndex) vault.Object[bankSlot].Amount += amount;
        else
        {
            vault.Object[bankSlot].ObjIndex = src.ObjIndex;
            vault.Object[bankSlot].Amount = amount;
            vault.NroItems++;
        }

        Inventory.QuitarUserInvItem(u, invSlot, amount);

        SendBankSlotPremium(u, vaultId, bankSlot);
        SendInvSlot(u, invSlot);
    }

    /// <summary>HandleBankExtractItemPremium: mueve 'amount' de la bóveda premium 1|2 al inventario.</summary>
    public static void ExtractPremium(int userIndex, byte vaultId, byte bankSlot, int amount)
    {
        var u = UserListManager.UserList[userIndex];
        if (!u.Comerciando || !u.BovedaPremiumDesbloqueada || amount <= 0) return;
        if (vaultId != 1 && vaultId != 2) return;
        if (bankSlot < 1 || bankSlot > Constants.MAX_BANCOINVENTORY_SLOTS) return;
        var vault = VaultOf(u, vaultId);
        ref var src = ref vault.Object[bankSlot];
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
            if (vault.NroItems > 0) vault.NroItems--;
        }

        SendBankSlotPremium(u, vaultId, bankSlot);
        SendInvSlot(u, invSlot);
    }

    /// <summary>HandleBuyBovedaPremium: desbloquea las 2 solapas premium gastando CreditoDonador.</summary>
    public static void ComprarBovedaPremium(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null || !u.flags.UserLogged) return;
        if (u.BovedaPremiumDesbloqueada) { ServerPackets.ConsoleMsg(u.Conn, "Ya tenés las bóvedas premium.", 3); return; }

        int precio = PrecioBovedaPremium();
        if (u.CreditoDonador < precio)
        {
            ServerPackets.ConsoleMsg(u.Conn,
                $"No tenés créditos suficientes. Precio: {precio}, tenés: {u.CreditoDonador}.", 3);
            return;
        }

        u.CreditoDonador -= precio;
        u.BovedaPremiumDesbloqueada = true;

        ServerPackets.UpdateCreditos(u.Conn, u.CreditoDonador);
        ServerPackets.ConsoleMsg(u.Conn, "¡Desbloqueaste las bóvedas premium!", 3);
        SendBankInitPremium(u);
    }

    private static int PrecioBovedaPremium()
    {
        try
        {
            string iniPath = (string.IsNullOrEmpty(DataPaths.Root) ? AppContext.BaseDirectory : DataPaths.Root) + "Server.ini";
            if (File.Exists(iniPath))
            {
                var ini = new IniFile(iniPath);
                int precio = ini.GetInt("BovedaPremium", "PrecioCreditos");
                if (precio > 0) return precio;
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Bank] PrecioBovedaPremium: {ex.Message}"); }
        return 300; // default si falta la clave en Server.ini
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
        ServerPackets.ChangeInventorySlot(u.Conn, u, (byte)slot);
    }

    /// <summary>
    /// Mete 'cantidad' de 'objIndex' en la bóveda SIN exigir estar en el banco ni sacarlo de
    /// ningún inventario: lo usa la mascota al 'regresar al hogar' (PetInventory), que es el
    /// único camino en el que algo entra a la bóveda sin que el jugador esté parado ahí.
    /// false = la bóveda está llena (no se depositó nada).
    /// </summary>
    public static bool DepositarDirecto(User u, short objIndex, int cantidad)
    {
        if (u == null || objIndex <= 0 || cantidad <= 0) return false;
        int slot = FindBankSlot(u, objIndex);
        if (slot == 0) return false;
        if (u.BancoInvent.Object[slot].ObjIndex == objIndex) u.BancoInvent.Object[slot].Amount += cantidad;
        else
        {
            u.BancoInvent.Object[slot].ObjIndex = objIndex;
            u.BancoInvent.Object[slot].Amount = cantidad;
            u.BancoInvent.NroItems++;
        }
        // Si tiene la bóveda abierta en pantalla, que lo vea llegar.
        if (u.Comerciando) SendBankSlot(u, slot);
        return true;
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

    private static BancoInventario VaultOf(User u, byte vaultId) => vaultId == 2 ? u.BancoPremium2 : u.BancoPremium1;

    /// <summary>
    /// Manda BankInitPremium (desbloqueada sí/no) y, si está desbloqueada, todos los slots
    /// de las 2 bóvedas premium. Gateado por Connection.SoportaBovedaPremium adentro de
    /// ServerPackets.BankInitPremium/ChangeBankSlotPremium, así que es seguro llamarla
    /// siempre (a un cliente viejo simplemente no le llega nada).
    /// </summary>
    private static void SendBankInitPremium(User u)
    {
        ServerPackets.BankInitPremium(u.Conn, u.BovedaPremiumDesbloqueada);
        if (!u.BovedaPremiumDesbloqueada) return;
        for (byte vaultId = 1; vaultId <= 2; vaultId++)
            for (int slot = 1; slot <= Constants.MAX_BANCOINVENTORY_SLOTS; slot++)
                SendBankSlotPremium(u, vaultId, slot);
    }

    private static void SendBankSlotPremium(User u, byte vaultId, int slot)
    {
        var o = VaultOf(u, vaultId).Object[slot];
        int valor = o.ObjIndex > 0 ? ObjData.Get(o.ObjIndex).Valor : 0;
        ServerPackets.ChangeBankSlotPremium(u.Conn, vaultId, (byte)slot, o.ObjIndex, o.Amount, valor);
    }

    private static int FindBankSlotPremium(BancoInventario vault, short objIndex)
    {
        for (int s = 1; s <= Constants.MAX_BANCOINVENTORY_SLOTS; s++)
            if (vault.Object[s].ObjIndex == objIndex) return s;
        for (int s = 1; s <= Constants.MAX_BANCOINVENTORY_SLOTS; s++)
            if (vault.Object[s].ObjIndex == 0) return s;
        return 0;
    }
}
