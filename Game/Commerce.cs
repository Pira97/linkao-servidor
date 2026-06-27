using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Comercio con NPCs mercaderes. Porta HandleLeftClick/LookatTile (selección de NPC),
/// HandleCommerceStart (abrir ventana + inventario del NPC), HandleCommerceBuy/Sell.
///
/// Precios (versión núcleo): compra = Valor del obj.dat; venta = Valor / 3 (el server
/// VB6 usa porcentajes por tipo; se afina al portar la tabla de precios completa).
/// </summary>
public static class Commerce
{
    private const int RANGO_VISION_X = 8, RANGO_VISION_Y = 6;

    /// <summary>LeftClick (id+X+Y): selecciona el NPC en ese tile como objetivo.</summary>
    /// <summary>
    /// LookatTile (GameLogic.bas:779): clic izquierdo sobre un tile. Detecta personaje/NPC
    /// (primero en el tile de abajo y+1, donde el sprite "se para", luego en x,y) y manda su
    /// info por consola (CharMsgStatus/NPC). Guarda el target para comercio/trade.
    /// </summary>
    public static void LeftClick(int userIndex, byte x, byte y)
    {
        var u = UserListManager.UserList[userIndex];
        short map = (short)u.Pos.Map;

        // VB6 LookatTile (GameLogic.bas:779): rango de visión y posición válida.
        if (Math.Abs(u.Pos.Y - y) > RANGO_VISION_Y || Math.Abs(u.Pos.X - x) > RANGO_VISION_X)
            return;
        var mapData = MapLoader.Get(map);
        if (mapData == null || x < 1 || x > 100 || y < 1 || y > 100)
        {
            LimpiarTarget(u);
            return;
        }

        u.TargetMap = map;
        u.TargetX = x;
        u.TargetY = y;

        bool foundSomething = false;

        // ¿Es un objeto? (1:1 VB6: tile exacto, o puerta en x+1/y / x+1,y+1 / x,y+1)
        if (mapData.FloorObj[x, y] > 0)
        {
            u.TargetObjMap = map; u.TargetObjX = x; u.TargetObjY = y; foundSomething = true;
        }
        else if (x + 1 <= 100 && mapData.FloorObj[x + 1, y] > 0 && ObjData.Get(mapData.FloorObj[x + 1, y]).Type == ObjType.Puertas)
        {
            u.TargetObjMap = map; u.TargetObjX = (byte)(x + 1); u.TargetObjY = y; foundSomething = true;
        }
        else if (x + 1 <= 100 && y + 1 <= 100 && mapData.FloorObj[x + 1, y + 1] > 0 && ObjData.Get(mapData.FloorObj[x + 1, y + 1]).Type == ObjType.Puertas)
        {
            u.TargetObjMap = map; u.TargetObjX = (byte)(x + 1); u.TargetObjY = (byte)(y + 1); foundSomething = true;
        }
        else if (y + 1 <= 100 && mapData.FloorObj[x, y + 1] > 0 && ObjData.Get(mapData.FloorObj[x, y + 1]).Type == ObjType.Puertas)
        {
            u.TargetObjMap = map; u.TargetObjX = x; u.TargetObjY = (byte)(y + 1); foundSomething = true;
        }
        if (foundSomething)
            u.TargetObj = mapData.FloorObj[u.TargetObjX, u.TargetObjY];

        // ¿Es un personaje? (VB6: primero en y+1 —donde "se para" el sprite—, luego en y).
        // NPC tiene prioridad sobre usuario en el mismo tile.
        foreach (int dy in new[] { 1, 0 })
        {
            int ty = y + dy;
            if (ty < 1 || ty > 100) continue;
            var npc = NpcManager.NpcAt(map, x, ty);
            if (npc != null)
            {
                u.TargetNpcCharIndex = npc.CharIndex;
                u.TargetUserCharIndex = 0;
                EnviarCharMsgStatusNpc(u, npc);
                return; // VB6: NPC limpia TargetObj pero deja foundSomething=1; no se limpia más abajo.
            }
            var other = UserAtPos(map, x, ty);
            if (other != null)
            {
                u.TargetUserCharIndex = other.Char.CharIndex;
                u.TargetNpcCharIndex = 0;
                EnviarCharMsgStatusUser(u, other);
                return;
            }
        }

        // VB6: FoundChar=0 → limpia targets de char. Si además FoundSomething=0, limpia todo el objeto.
        u.TargetNpcCharIndex = 0;
        u.TargetUserCharIndex = 0;
        if (!foundSomething) LimpiarTarget(u);
    }

    private static void LimpiarTarget(User u)
    {
        u.TargetNpcCharIndex = 0;
        u.TargetUserCharIndex = 0;
        u.TargetObj = 0;
        u.TargetObjMap = 0;
        u.TargetObjX = 0;
        u.TargetObjY = 0;
    }

    private static User UserAtPos(int map, int x, int y)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Pos.Map == map && o.Pos.X == x && o.Pos.Y == y)
                return o;
        }
        return null;
    }

    /// <summary>Manda la info del usuario clickeado por consola (WriteCharMsgStatus).</summary>
    private static void EnviarCharMsgStatusUser(User u, User t)
    {
        int vidaPct = t.Stats.MaxHP > 0 ? t.Stats.MinHP * 100 / t.Stats.MaxHP : 0;
        ServerPackets.CharMsgStatus(u.Conn, t.Char.CharIndex, StatusByte(t), vidaPct,
            st1: 0, st2: 0, clase: t.Clase, nivel: t.Stats.ELV, raza: t.raza,
            donador: t.Char.Donador, rango: (byte)t.Faccion.Rango,
            pareja: t.CasamientoPareja, desc: t.desc, arenaPoints: t.Stats.ArenaPoints);
    }

    /// <summary>Manda la info del NPC clickeado por consola (WriteCharMsgStatusNPC).</summary>
    private static void EnviarCharMsgStatusNpc(User u, NpcManager.NpcInstance n)
    {
        int porcVida = n.MaxHP > 0 ? n.MinHP * 100 / n.MaxHP : 0;
        ServerPackets.CharMsgStatusNPC(u.Conn, (short)n.NpcIndex, n.Status, puedeVerVida: 0,
            porcVida: porcVida, st1: 0, nivel: 0, maestro: (byte)(n.MaestroUser > 0 ? 1 : 0), owner: 0);
    }

    // bt_status del tooltip (WriteCharMsgStatus, Protocol.bas:20191): GM o facción mapeada.
    private static byte StatusByte(User t)
    {
        if (t.FaccionStatus >= 7)
            return t.FaccionStatus switch { 7 => 10, 8 => 12, 9 => 13, 10 => 14, _ => 10 };
        return t.Faccion.Status switch { 1 => 1, 2 => 2, 3 => 3, 4 => 5, 5 => 6, 6 => 7, _ => 1 };
    }

    /// <summary>HandleCommerceStart: si el NPC seleccionado comercia y está cerca, abre la ventana.</summary>
    public static void CommerceStart(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.flags.Muerto == 1) return;
        if (u.TargetNpcCharIndex == 0)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Primero seleccioná un mercader (clic sobre él).", 1);
            return;
        }

        var npc = NpcManager.NpcByCharIndex(u.Pos.Map, u.TargetNpcCharIndex);
        if (npc == null || !npc.Comercia)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Ese personaje no comercia.", 1);
            return;
        }
        if (Math.Abs(npc.X - u.Pos.X) + Math.Abs(npc.Y - u.Pos.Y) > 3)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos del mercader.", 1);
            return;
        }

        AbrirComercioNpc(userIndex, npc);
    }

    /// <summary>Abre la ventana de comercio con un NPC ya validado (lo usa Accion/doble-click).</summary>
    public static void AbrirComercioNpc(int userIndex, NpcManager.NpcInstance npc)
    {
        var u = UserListManager.UserList[userIndex];
        u.Comerciando = true;
        u.ComercioNpcNoCompra = npc.NoCompra;   // recordar si este NPC compra (para validar la venta)

        // ¿Es transportador? El cliente abre el form de Viajar (sin inventario del usuario) en vez del
        // comercio normal. Lo decide el server porque los slots del NPC se envían DESPUÉS del CommerceInit
        // (el cliente todavía no los tiene al elegir qué ventana abrir). Transportador = NpcType 7, o un
        // NPC cuyo inventario es 100% Pasajes (otPasajes).
        bool esViajes = npc.NpcType == 7;
        if (!esViajes && npc.Inventario != null)
        {
            bool any = false, allPasajes = true;
            foreach (var (objIndex, _) in npc.Inventario)
            {
                if (objIndex <= 0) continue;
                any = true;
                if (ObjData.Get(objIndex).Type != ObjType.Pasajes) { allPasajes = false; break; }
            }
            esViajes = any && allPasajes;
        }

        ServerPackets.CommerceInit(u.Conn, !npc.NoCompra, esViajes);

        if (npc.Inventario != null)
        {
            for (byte slot = 0; slot < npc.Inventario.Length; slot++)
            {
                var (objIndex, amount) = npc.Inventario[slot];
                var od = ObjData.Get(objIndex);
                float precio = od.Valor;
                // Marcar si el usuario puede usar ese ropaje/arma/escudo por clase/raza/nivel/sexo.
                byte motivo = objIndex > 0 ? Inventory.MotivoNoUsable(u, od) : (byte)0;
                byte puedeUsar = (byte)(motivo == 0 ? 1 : 0);
                ServerPackets.ChangeNPCInventorySlot(u.Conn, (byte)(slot + 1), amount, precio, objIndex, puedeUsar, motivo);
            }
        }
    }

    /// <summary>HandleCommerceEnd: cierra la ventana.</summary>
    public static void CommerceEnd(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        u.Comerciando = false;
        ServerPackets.CommerceEnd(u.Conn);
    }

    /// <summary>HandleCommerceBuy: compra 'amount' del slot 'slot' del inventario del NPC.</summary>
    public static void CommerceBuy(int userIndex, byte slot, int amount)
    {
        var u = UserListManager.UserList[userIndex];
        if (!u.Comerciando || amount <= 0) return;
        var npc = NpcManager.NpcByCharIndex(u.Pos.Map, u.TargetNpcCharIndex);
        if (npc?.Inventario == null || slot < 1 || slot > npc.Inventario.Length) return;

        var (objIndex, _) = npc.Inventario[slot - 1];
        if (objIndex <= 0) return;

        // === Sistema de Viajes (Comercio.bas:92): si el objeto es un Pasaje, viajar en vez
        //     de agregarlo al inventario. El NPC transportador "vende" pasajes (otPasajes). ===
        var odBuy = ObjData.Get(objIndex);
        if (odBuy.Type == ObjType.Pasajes)
        {
            ComprarPasaje(u, userIndex, npc, odBuy);
            return;
        }

        int precioUnit = ObjData.Get(objIndex).Valor;
        long costo = (long)precioUnit * amount;

        if (u.Stats.GLD < costo)
        {
            ServerPackets.ConsoleMsg(u.Conn, "No tienes suficiente oro.", 1);
            return;
        }

        int invSlot = FindSlot(u, objIndex);
        if (invSlot == 0)
        {
            ServerPackets.ConsoleMsg(u.Conn, "No tienes espacio en el inventario.", 1);
            return;
        }

        u.Stats.GLD -= (int)costo;
        if (u.Invent.Object[invSlot].ObjIndex == objIndex)
            u.Invent.Object[invSlot].Amount += amount;
        else
        {
            u.Invent.Object[invSlot].ObjIndex = objIndex;
            u.Invent.Object[invSlot].Amount = amount;
            u.Invent.Object[invSlot].Equipped = false;
            u.Invent.NroItems++;
        }

        ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
        SendInvSlot(u, invSlot);
        Skills.SubirSkill(userIndex, 15); // eSkill.Comerciar 1:1 (Comercio.bas:253)
    }

    private const byte NPCTYPE_TRANSPORTADOR = 7; // eNPCType.transportadores

    /// <summary>
    /// Compra de un Pasaje (Comercio.bas:92): valida transportador, distancia, mapa de origen
    /// (DesdeMap), mapa destino válido y oro; descuenta el precio y teletransporta al destino.
    /// </summary>
    private static void ComprarPasaje(User u, int userIndex, NpcManager.NpcInstance npc, ObjData.Obj pasaje)
    {
        if (npc.NpcType != NPCTYPE_TRANSPORTADOR)
        { ServerPackets.ConsoleMsg(u.Conn, "Este NPC no ofrece viajes.", 1); return; }

        if (Math.Abs(npc.X - u.Pos.X) + Math.Abs(npc.Y - u.Pos.Y) > 3)
        { ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos.", 1); return; }

        // Restricción por facción: cada ciudad pertenece a un bando y su pirata no atiende
        // a personajes del bando enemigo. Las ciudades neutrales las usan todos. Los GMs pasan.
        if (!PuedeUsarTransporte(u, npc.Map))
        { ServerPackets.ConsoleMsg(u.Conn, "Los marineros de esta ciudad no le dan pasaje a los de tu bando.", 1); return; }

        // Tampoco se viaja a una ciudad de bando enemigo (las neutrales las visita cualquiera).
        if (!PuedeUsarTransporte(u, pasaje.HastaMap))
        { ServerPackets.ConsoleMsg(u.Conn, "No puedes viajar a una ciudad de un bando enemigo.", 1); return; }

        // El pasaje solo sirve desde su mapa de origen.
        if (pasaje.DesdeMap != 0 && u.Pos.Map != pasaje.DesdeMap)
        { ServerPackets.ConsoleMsg(u.Conn, "No puedes usar este pasaje desde aquí.", 1); return; }

        if (MapLoader.Get(pasaje.HastaMap) == null)
        { ServerPackets.ConsoleMsg(u.Conn, "El destino no es válido.", 1); return; }

        int precio = pasaje.Valor;
        if (u.Stats.GLD < precio)
        { ServerPackets.ConsoleMsg(u.Conn, "No tienes suficiente oro para el viaje.", 1); return; }

        u.Stats.GLD -= precio;
        ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
        Movement.WarpUser(userIndex, (short)pasaje.HastaMap, (short)pasaje.HastaX, (short)pasaje.HastaY);
        ServerPackets.ConsoleMsg(u.Conn, "Has llegado a tu destino.", 1);

        // Efecto del pasaje original: resetea hambre y sed (VB6 MinAGU/MinHam = 0).
        u.Stats.MinAGU = 0;
        u.Stats.MinHam = 0;
    }

    // Bando al que pertenece el puerto/ciudad de cada pirata transportador (por mapa).
    private enum CityBando { Neutral, Imperial, Republica, Caos }

    private static CityBando PortBando(int map) => map switch
    {
        61 or 34 or 150  => CityBando.Imperial,  // Banderbill, Nix, Arghâl
        179 or 64 or 183 => CityBando.Republica, // Illiandor, Lindos, Suramei
        181              => CityBando.Caos,      // Orac
        _                => CityBando.Neutral,   // Rinkel(99), Nueva Esperanza(111), Tiama(217) y resto
    };

    /// <summary>El transportador de una ciudad solo atiende a su propio bando (las neutrales a
    /// todos). Imperial=Ciudadano/Armada, República=Republicano/Milicia, Caos=Caos/Renegado.</summary>
    private static bool PuedeUsarTransporte(User u, int portMap)
    {
        var city = PortBando(portMap);
        if (city == CityBando.Neutral) return true;
        if (u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO) return true; // GMs/Dioses
        return city switch
        {
            CityBando.Imperial  => u.Faccion.Status is Facciones.CIUDADANO or Facciones.ARMADA,
            CityBando.Republica => u.Faccion.Status is Facciones.REPUBLICANO or Facciones.MILICIA,
            CityBando.Caos      => u.Faccion.Status is Facciones.CAOS or Facciones.RENEGADO,
            _                   => true,
        };
    }

    /// <summary>HandleCommerceSell: vende 'amount' del slot 'slot' del inventario del jugador.</summary>
    public static void CommerceSell(int userIndex, byte slot, int amount)
    {
        var u = UserListManager.UserList[userIndex];
        if (!u.Comerciando || amount <= 0) return;
        if (u.ComercioNpcNoCompra)   // NPC que solo vende: no compra ítems al usuario
        {
            ServerPackets.ConsoleMsg(u.Conn, "Este mercader no compra objetos.", 1);
            return;
        }
        if (slot < 1 || slot > Constants.MAX_INVENTORY_SLOTS) return;
        ref var item = ref u.Invent.Object[slot];
        if (item.ObjIndex == 0) return;

        if (amount > item.Amount) amount = item.Amount;
        int precioVenta = Math.Max(1, ObjData.Get(item.ObjIndex).Valor / 3);
        long ganancia = (long)precioVenta * amount;

        Inventory.QuitarUserInvItem(u, slot, amount); // desequipa si se vende el stack equipado

        u.Stats.GLD += (int)ganancia;
        ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
        SendInvSlot(u, slot);
        Skills.SubirSkill(userIndex, 15); // eSkill.Comerciar 1:1 (Comercio.bas:253)
    }

    private static int FindSlot(User u, short objIndex)
    {
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == objIndex) return s;
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == 0) return s;
        return 0;
    }

    private static void SendInvSlot(User u, int slot)
    {
        var o = u.Invent.Object[slot];
        ServerPackets.ChangeInventorySlot(u.Conn, (byte)slot, o.ObjIndex, o.Amount, o.Equipped);
    }
}
