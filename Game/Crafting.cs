using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Crafteo (herrería, carpintería, sastrería, alquimia). Migrado 1:1 desde Trabajo.bas:
/// valida la skill correcta del item (SkHerreria/SkCarpinteria/SkSastreria/SkPociones > 0),
/// que el jugador tenga los materiales reales (lingotes/leña/raíces/pieles según los campos
/// del item en obj.dat) y los consume, entregando el item fabricado y subiendo la skill.
/// </summary>
public static class Crafting
{
    // Objindex de materiales (Declares.bas).
    private const short Lena = 58, LingoteHierro = 386, LingotePlata = 387, LingoteOro = 388,
        Raiz = 888, PielLobo = 414, PielOso = 415, PielOsoPolar = 1145;

    private const byte FONT_INFO = 3;

    public enum CraftType { Blacksmith, Carpenter, Sastre, Alquimia }

    // Herramientas (Declares.bas:411-415).
    public const short MARTILLO_HERRERO = 389, SERRUCHO_CARPINTERO = 198, OLLA = 887, COSTURERO = 886;

    // Listas de items fabricables por categoría, cacheadas (se construyen una vez desde obj.dat).
    private static List<short> _herreroArmas, _herreroArmaduras, _herreroCascos, _herreroEscudos,
        _carpinteria, _sastreria, _alquimia;

    private static void EnsureListas()
    {
        if (_herreroArmas != null) return;
        _herreroArmas = new(); _herreroArmaduras = new(); _herreroCascos = new(); _herreroEscudos = new();
        _carpinteria = new(); _sastreria = new(); _alquimia = new();
        for (short i = 1; i <= ObjData.Count; i++)
        {
            var od = ObjData.Get(i);
            if (od.SkHerreria > 0)
            {
                if (od.Type == ObjType.Weapon) _herreroArmas.Add(i);
                else if (od.Type == ObjType.Armadura)
                {
                    if (od.SubTipo == 1) _herreroCascos.Add(i);
                    else if (od.SubTipo == 2) _herreroEscudos.Add(i);
                    else _herreroArmaduras.Add(i);
                }
            }
            if (od.SkCarpinteria > 0) _carpinteria.Add(i);
            if (od.SkSastreria > 0) _sastreria.Add(i);
            if (od.SkPociones > 0) _alquimia.Add(i);
        }
    }

    /// <summary>
    /// Usar una herramienta de crafteo equipada (otAnillo, InvUsuario.bas:1669): envía la lista de items
    /// fabricables y abre el formulario correspondiente en el cliente. La herramienta debe estar equipada.
    /// </summary>
    public static void AbrirCrafteo(int userIndex, short toolObjIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null || u.flags.Muerto == 1) return;
        if (u.Invent.AnilloEqpObjIndex != toolObjIndex) return; // VB6: sólo si está equipada
        EnsureListas();
        switch (toolObjIndex)
        {
            case SERRUCHO_CARPINTERO:
                ServerPackets.CarpenterList(u.Conn, _carpinteria);
                ServerPackets.AbrirFormularios(u.Conn, 2); // frmCarp
                break;
            case COSTURERO:
                ServerPackets.SastreList(u.Conn, _sastreria);
                ServerPackets.AbrirFormularios(u.Conn, 3); // frmSastre
                break;
            case OLLA:
                ServerPackets.AlquimiaList(u.Conn, _alquimia);
                ServerPackets.AbrirFormularios(u.Conn, 4); // frmDruida (alquimia)
                break;
            case MARTILLO_HERRERO:
                ServerPackets.BlacksmithList(u.Conn, Network.ServerPacketID.BlacksmithWeapons, _herreroArmas);
                ServerPackets.BlacksmithList(u.Conn, Network.ServerPacketID.BlacksmithArmors, _herreroArmaduras);
                ServerPackets.BlacksmithList(u.Conn, Network.ServerPacketID.BlacksmithHelmet, _herreroCascos);
                ServerPackets.BlacksmithList(u.Conn, Network.ServerPacketID.BlacksmithShield, _herreroEscudos);
                ServerPackets.AbrirFormularios(u.Conn, 11); // frmHerrero
                break;
        }
    }

    public static void Craft(int userIndex, CraftType tipo, short itemIndex, int cantidad)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.flags.Muerto == 1) return;
        if (itemIndex <= 0 || itemIndex > ObjData.Count) return;
        if (cantidad < 1) cantidad = 1;

        var od = ObjData.Get(itemIndex);
        if (string.IsNullOrEmpty(od.Name)) return;

        // 1) Validar que el item se fabrique con esta skill (Trabajo.bas: ObjData(Item).SkXXX > 0).
        // 2) Validar y juntar los materiales requeridos (cantidad × material por unidad).
        var reqs = new List<(short obj, int total)>();
        switch (tipo)
        {
            case CraftType.Blacksmith:
                if (od.SkHerreria <= 0) { NoFabricable(u); return; }
                if (od.LingH > 0) reqs.Add((LingoteHierro, od.LingH * cantidad));
                if (od.LingP > 0) reqs.Add((LingotePlata, od.LingP * cantidad));
                if (od.LingO > 0) reqs.Add((LingoteOro, od.LingO * cantidad));
                break;
            case CraftType.Carpenter:
                if (od.SkCarpinteria <= 0) { NoFabricable(u); return; }
                if (od.Madera > 0) reqs.Add((Lena, od.Madera * cantidad));
                break;
            case CraftType.Alquimia:
                if (od.SkPociones <= 0) { NoFabricable(u); return; }
                if (od.Raices > 0) reqs.Add((Raiz, od.Raices * cantidad));
                break;
            case CraftType.Sastre:
                if (od.SkSastreria <= 0) { NoFabricable(u); return; }
                if (od.PielLobo > 0) reqs.Add((PielLobo, od.PielLobo * cantidad));
                if (od.PielOso > 0) reqs.Add((PielOso, od.PielOso * cantidad));
                if (od.PielOsoPolar > 0) reqs.Add((PielOsoPolar, od.PielOsoPolar * cantidad));
                break;
            default: return;
        }

        // ¿Tiene todos los materiales?
        foreach (var (obj, total) in reqs)
            if (ContarItem(u, obj) < total)
            {
                ServerPackets.ConsoleMsg(u.Conn, "No tienes suficientes materiales.", FONT_INFO);
                return;
            }

        // Consumir materiales.
        foreach (var (obj, total) in reqs) QuitarItem(u, obj, total);

        // Entregar el item fabricado.
        int dest = FindFreeOrSame(u, itemIndex);
        if (dest == 0) { ServerPackets.ConsoleMsg(u.Conn, "No tienes espacio en el inventario.", FONT_INFO); return; }
        if (u.Invent.Object[dest].ObjIndex == itemIndex) u.Invent.Object[dest].Amount += cantidad;
        else { u.Invent.Object[dest].ObjIndex = itemIndex; u.Invent.Object[dest].Amount = cantidad; u.Invent.NroItems++; }

        // Refrescar inventario y subir la skill.
        SendAllInv(u);
        SubirSkill(u, tipo);
        // Sonido de fabricación según la herramienta: herrero (martillo=41), carpintero (serrucho=169/170).
        short snd = tipo switch
        {
            CraftType.Blacksmith => Sounds.MARTILLOHERRERO,            // 41
            CraftType.Carpenter  => (System.Random.Shared.Next(2) == 0 ? Sounds.SERRUCHO1 : Sounds.SERRUCHO2), // 169/170
            _ => 0,
        };
        if (snd > 0) WaveArea(u, snd);
        ServerPackets.ConsoleMsg(u.Conn, $"Has fabricado {cantidad}x {od.Name}.", FONT_INFO);
    }

    private static void NoFabricable(User u)
        => ServerPackets.ConsoleMsg(u.Conn, "Ese objeto no se puede fabricar con esta habilidad.", FONT_INFO);

    /// <summary>Difunde un sonido a los usuarios del mapa del jugador (PlayWave por área).</summary>
    private static void WaveArea(User u, short wave)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.PlayWave(o.Conn, wave, (byte)u.Pos.X, (byte)u.Pos.Y);
        }
    }

    private static void SubirSkill(User u, CraftType tipo)
    {
        // eSkill: Herreria=22, Carpinteria=23, Sastreria=25, alquimia=24.
        int sk = tipo switch { CraftType.Blacksmith => 22, CraftType.Carpenter => 23,
            CraftType.Sastre => 25, CraftType.Alquimia => 24, _ => 0 };
        // SubirSkill 1:1 (Trabajo.bas:514/796/878/2889): probabilidad, hambre/sed, tope nivel, +10 exp.
        if (sk > 0 && sk < u.Stats.UserSkills.Length)
            Skills.SubirSkill(u.id, sk);
    }

    private static int ContarItem(User u, short obj)
    {
        int n = 0;
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == obj) n += u.Invent.Object[s].Amount;
        return n;
    }

    private static void QuitarItem(User u, short obj, int cant)
    {
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS && cant > 0; s++)
        {
            if (u.Invent.Object[s].ObjIndex != obj) continue;
            int quita = Math.Min(cant, u.Invent.Object[s].Amount);
            u.Invent.Object[s].Amount -= quita; cant -= quita;
            if (u.Invent.Object[s].Amount <= 0)
            {
                u.Invent.Object[s].ObjIndex = 0; u.Invent.Object[s].Equipped = false;
                if (u.Invent.NroItems > 0) u.Invent.NroItems--;
            }
        }
    }

    private static int FindFreeOrSame(User u, short obj)
    {
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == obj) return s;
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == 0) return s;
        return 0;
    }

    private static void SendAllInv(User u)
    {
        for (int slot = 1; slot <= Constants.MAX_INVENTORY_SLOTS; slot++)
        {
            var o = u.Invent.Object[slot];
            ServerPackets.ChangeInventorySlot(u.Conn, (byte)slot, o.ObjIndex, o.Amount, o.Equipped);
        }
    }
}
