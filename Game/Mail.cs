using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sistema de correos entre jugadores (mod_Correos.bas). Enviar correo con item adjunto,
/// listar la bandeja, leer y borrar. Entrega tanto a jugadores online (en memoria) como
/// offline (escribiendo en su charfile [CORREO]).
/// </summary>
public static class Mail
{
    /// <summary>Packets_Correo acciones (Byte action del cliente).</summary>
    private const byte ACTION_LISTAR = 1, ACTION_BORRAR = 2, ACTION_RETIRAR = 3;

    /// <summary>HandlePackets_Correo: acciones sobre la bandeja (listar/borrar/retirar item).</summary>
    public static void Packets(int userIndex, byte action, byte slot)
    {
        var u = UserListManager.UserList[userIndex];
        switch (action)
        {
            case ACTION_LISTAR:
                ServerPackets.CorreoList(u.Conn, u.Correos);
                break;
            case ACTION_BORRAR:
                if (slot >= 1 && slot <= u.Correos.Count)
                {
                    u.Correos.RemoveAt(slot - 1);
                    ServerPackets.CorreoList(u.Conn, u.Correos);
                }
                break;
            case ACTION_RETIRAR:
                RetirarItem(u, slot);
                break;
        }
    }

    /// <summary>HandleEnviarCorreo: envía un correo (con item opcional) a otro personaje.</summary>
    public static void Enviar(int userIndex, string destinatario, string mensaje, short objIndex, int cantidad)
    {
        var u = UserListManager.UserList[userIndex];
        destinatario = destinatario.Trim();
        if (string.IsNullOrEmpty(destinatario)) return;

        // Si adjunta item, quitarlo del inventario del emisor.
        if (objIndex > 0 && cantidad > 0)
        {
            int slot = FindItemSlot(u, objIndex, cantidad);
            if (slot == 0) { ServerPackets.ConsoleMsg(u.Conn, "No tenés ese item/cantidad para adjuntar.", 1); return; }
            u.Invent.Object[slot].Amount -= cantidad;
            if (u.Invent.Object[slot].Amount <= 0)
            {
                u.Invent.Object[slot].ObjIndex = 0; u.Invent.Object[slot].Amount = 0; u.Invent.Object[slot].Equipped = false;
                if (u.Invent.NroItems > 0) u.Invent.NroItems--;
            }
            var o = u.Invent.Object[slot];
            ServerPackets.ChangeInventorySlot(u.Conn, (byte)slot, o.ObjIndex, o.Amount, o.Equipped);
        }

        var correo = new Correo { Emisor = u.Name, Mensaje = mensaje, Leida = false, ObjIndex = objIndex, Cantidad = cantidad };

        // Entrega: online (memoria) u offline (charfile).
        int destIndex = FindOnline(destinatario);
        if (destIndex > 0)
        {
            var dest = UserListManager.UserList[destIndex];
            dest.Correos.Add(correo);
            if (dest.Conn != null) ServerPackets.ConsoleMsg(dest.Conn, $"Has recibido un correo de {u.Name}.", 1);
        }
        else if (!EntregarOffline(destinatario, correo))
        {
            ServerPackets.ConsoleMsg(u.Conn, "El destinatario no existe.", 1);
            return;
        }
        ServerPackets.ConsoleMsg(u.Conn, $"Correo enviado a {destinatario}.", 1);
    }

    /// <summary>
    /// Entrega de correo "del sistema" (sin emisor real ni descuento de inventario). La usan
    /// sistemas como las Subastas para pagar items/oro al ganador/vendedor (online u offline).
    /// </summary>
    public static void DeliverSystem(string destinatario, string emisor, string mensaje, short objIndex, int cantidad)
    {
        if (string.IsNullOrWhiteSpace(destinatario)) return;
        var correo = new Correo { Emisor = emisor, Mensaje = mensaje, Leida = false, ObjIndex = objIndex, Cantidad = cantidad };
        int destIndex = FindOnline(destinatario);
        if (destIndex > 0)
        {
            var dest = UserListManager.UserList[destIndex];
            dest.Correos.Add(correo);
            if (dest.Conn != null) ServerPackets.ConsoleMsg(dest.Conn, $"Has recibido un correo de {emisor}.", 1);
        }
        else EntregarOffline(destinatario, correo);
    }

    private static void RetirarItem(User u, byte slot)
    {
        if (slot < 1 || slot > u.Correos.Count) return;
        var c = u.Correos[slot - 1];
        if (c.ObjIndex <= 0 || c.Cantidad <= 0) return;

        int dest = FindItemSlot(u, c.ObjIndex, 0);
        if (dest == 0) { ServerPackets.ConsoleMsg(u.Conn, "No tenés espacio en el inventario.", 1); return; }
        if (u.Invent.Object[dest].ObjIndex == c.ObjIndex) u.Invent.Object[dest].Amount += c.Cantidad;
        else { u.Invent.Object[dest].ObjIndex = c.ObjIndex; u.Invent.Object[dest].Amount = c.Cantidad; u.Invent.NroItems++; }

        // El item ya fue retirado: limpiarlo del correo.
        c.ObjIndex = 0; c.Cantidad = 0; u.Correos[slot - 1] = c;

        var o = u.Invent.Object[dest];
        ServerPackets.ChangeInventorySlot(u.Conn, (byte)dest, o.ObjIndex, o.Amount, o.Equipped);
        ServerPackets.CorreoList(u.Conn, u.Correos);
    }

    /// <summary>Entrega un correo a un personaje offline escribiéndolo en su charfile.</summary>
    private static bool EntregarOffline(string nombre, Correo c)
    {
        string file = System.IO.Path.Combine(CharLoader.CharPath, nombre.ToUpperInvariant() + ".chr");
        if (!System.IO.File.Exists(file)) return false;
        var doc = new IniDocument(file);
        if (!doc.Loaded) return false;

        // Buscar el primer slot de correo libre (Emisor == 0/vacío).
        var existente = new IniFile(file);
        for (int i = 1; i <= Constants.MAX_CORREOS_SLOTS; i++)
        {
            string emisor = existente.Get("CORREO", "Emisor" + i);
            if (string.IsNullOrEmpty(emisor) || emisor == "0")
            {
                doc.Set("CORREO", "Carta" + i, c.Mensaje ?? "");
                doc.Set("CORREO", "Emisor" + i, c.Emisor ?? "");
                doc.Set("CORREO", "Leida" + i, "0");
                doc.Set("CORREO", "Objeto" + i, $"{c.ObjIndex}-{c.Cantidad}");
                doc.Save(file);
                return true;
            }
        }
        return false; // bandeja llena
    }

    private static int FindOnline(string nombre)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && string.Equals(o.Name, nombre, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return 0;
    }

    private static int FindItemSlot(User u, short objIndex, int minCantidad)
    {
        // Para enviar: slot con el item y cantidad suficiente. Para retirar: slot apilable o vacío.
        if (minCantidad > 0)
        {
            for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
                if (u.Invent.Object[s].ObjIndex == objIndex && u.Invent.Object[s].Amount >= minCantidad) return s;
            return 0;
        }
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == objIndex) return s;
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == 0) return s;
        return 0;
    }
}
