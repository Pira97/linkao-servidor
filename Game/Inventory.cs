using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Interacción con objetos: agarrar del suelo (GetObj/HandlePickUp), tirar (HandleDrop)
/// y equipar (HandleEquipItem). Versión funcional sin obj.dat completo: maneja el
/// movimiento de items inventario↔suelo y el flag Equipped, que es lo que el cliente refleja.
///
/// Lo que falta al portar obj.dat (ObjData): validar tipo de objeto para equipar
/// (sólo armas/armaduras/etc.), aplicar bonus de stats, oro (FLAGORO), apilado por MaxHIT.
/// </summary>
public static class Inventory
{
    // FLAGORO = MAX_INVENTORY_SLOTS + 1 (Declares.bas:617). El cliente Godot manda este mismo valor
    // (Constants.FLAG_ORO) al tirar oro. Antes estaba en 200 → el drop de oro nunca matcheaba.
    /// <summary>
    /// ¿Puede el usuario USAR/EQUIPAR este objeto? Valida clase, raza, nivel y sexo (obj.dat).
    /// Devuelve true si puede; si no, motivo trae el texto a mostrar. Los GM pueden todo.
    /// </summary>
    public static bool PuedeUsarObjeto(User u, in ObjData.Obj od, out string motivo, bool validarNivel = true)
    {
        motivo = "";
        if (u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO) return true; // GM sin restricciones
        if (od.ClasesProhibidas != null && Array.IndexOf(od.ClasesProhibidas, (int)u.Clase) >= 0)
        { motivo = "Tu clase no puede usar este objeto."; return false; }
        if (od.RazasProhibidas != null && Array.IndexOf(od.RazasProhibidas, (int)u.raza) >= 0)
        { motivo = "Tu raza no puede usar este objeto."; return false; }
        // Las monturas/barcos NO se gatean por nivel (VB6: por skill Equitacion/Navegacion en DoEquita/DoNavega).
        if (validarNivel && od.MinELV > 0 && u.Stats.ELV < od.MinELV)
        { motivo = $"Necesitas ser nivel {od.MinELV} para usar este objeto."; return false; }
        // Sexo (SexoPuedeUsarItem, InvUsuario.bas:864): solo-mujeres = Mujer<>0 y Hombre=0;
        // solo-hombres = Hombre<>0 y Mujer=0. Con ambos o ninguno: cualquiera.
        if (od.Mujer != 0 && od.Hombre == 0 && u.Genero != 2) { motivo = "Solo las mujeres pueden usar este objeto."; return false; }
        if (od.Hombre != 0 && od.Mujer == 0 && u.Genero != 1) { motivo = "Solo los hombres pueden usar este objeto."; return false; }
        // Facción (FaccionPuedeUsarItem, InvUsuario.bas:907): items Real/Caos/Milicia exigen pertenecer.
        if (od.Real == 1 && !Facciones.EsArmada(u)) { motivo = "Solo la Armada Real puede usar este objeto."; return false; }
        if (od.Caos == 1 && !Facciones.EsCaos(u)) { motivo = "Solo la Legión del Caos puede usar este objeto."; return false; }
        if (od.Milicia == 1 && !Facciones.EsMili(u)) { motivo = "Solo la Milicia puede usar este objeto."; return false; }
        return true;
    }

    /// <summary>
    /// Código de motivo por el que el usuario NO puede usar el objeto (para el comercio con NPC).
    /// 0 = puede usar; 1 = clase; 2 = raza; 3 = sexo; 5 = nivel. Coincide con los textos del cliente.
    /// </summary>
    public static byte MotivoNoUsable(User u, in ObjData.Obj od)
    {
        if (u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO) return 0; // GM: todo
        if (od.ClasesProhibidas != null && Array.IndexOf(od.ClasesProhibidas, (int)u.Clase) >= 0) return 1;
        if (od.RazasProhibidas != null && Array.IndexOf(od.RazasProhibidas, (int)u.raza) >= 0) return 2;
        if (od.Mujer != 0 && od.Hombre == 0 && u.Genero != 2) return 3;
        if (od.Hombre != 0 && od.Mujer == 0 && u.Genero != 1) return 3;
        if ((od.Real == 1 && !Facciones.EsArmada(u)) || (od.Caos == 1 && !Facciones.EsCaos(u))
            || (od.Milicia == 1 && !Facciones.EsMili(u))) return 4; // facción
        if (od.MinELV > 0 && u.Stats.ELV < od.MinELV) return 5; // nivel (el cliente muestra el motivo genérico)
        return 0;
    }

    private const int FLAGORO = Constants.MAX_INVENTORY_SLOTS + 1;
    private const int MAX_INVENTORY_OBJS = 10000; // tope de unidades por tile/pila (Declares.bas:608)
    private const short ORO_INDEX = 12;           // iORO (Declares.bas:491)
    private const short SND_ORO2 = 172;           // SND_ORO2 (sonido al tirar oro, Declares.bas:599)

    /// <summary>Difunde una acción a todos los usuarios del mapa (incluido o no el emisor).</summary>
    private static void ToMap(int map, Action<Connection> send)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.Pos.Map == map)
                send(o.Conn);
        }
    }

    /// <summary>HandlePickUp → GetObj: levanta el objeto del tile donde está parado el PJ.</summary>
    public static void PickUp(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.flags.Muerto == 1) return;

        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return;

        int x = u.Pos.X, y = u.Pos.Y;
        short objIndex = map.FloorObj[x, y];
        if (objIndex <= 0) return; // no hay nada en el piso

        int amount = map.FloorAmount[x, y];

        // Buscar slot: uno con el mismo obj (apilar) o uno vacío.
        int slot = FindSlotForObject(u, objIndex);
        if (slot == 0) return; // inventario lleno

        if (u.Invent.Object[slot].ObjIndex == objIndex)
            u.Invent.Object[slot].Amount += amount;
        else
        {
            u.Invent.Object[slot].ObjIndex = objIndex;
            u.Invent.Object[slot].Amount = amount;
            u.Invent.Object[slot].Equipped = false;
            u.Invent.NroItems++;
        }

        // Quitar del suelo y avisar (por área) a quienes lo veían.
        map.FloorObj[x, y] = 0;
        map.FloorAmount[x, y] = 0;
        AreaVisibility.ObjectRemoved(u.Pos.Map, x, y);

        // Actualizar el slot del inventario en el cliente dueño.
        SendSlot(u, slot);
    }

    /// <summary>
    /// HandleDrop (Protocol.bas:2574) 1:1: tira 'amount' del slot (o oro si slot==FLAGORO) al piso.
    /// Bloqueos: muerto, comerciando, navegando+barco, items faccionarios/NoSeCae/newbie, Destruir/Permanente
    /// (piden confirmación con ShowMessageBox). Si está montado y tira la montura → desmonta.
    /// </summary>
    public static void Drop(int userIndex, byte slot, int amount)
    {
        var u = UserListManager.UserList[userIndex];

        // Muerto no puede tirar. (Los GM bajos tampoco; aquí no modelamos Consejero/RoleMaster.)
        if (u.flags.Muerto == 1) return;
        // Si está comerciando, no puede tirar (VB6: es cheating → Exit Sub).
        if (u.Comerciando) return;

        // ¿Oro u objeto?
        if (slot == FLAGORO)
        {
            if (amount > 100000) return;               // VB6: no tirar demasiado oro de una
            TirarOro(u, amount);
            BroadcastWaveArea(u, SND_ORO2);
            ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
            return;
        }

        if (slot < 1 || slot > Constants.MAX_INVENTORY_SLOTS) return;
        ref var item = ref u.Invent.Object[slot];
        if (item.ObjIndex == 0) return;

        var od = ObjData.Get(item.ObjIndex);

        // Montado y tira la montura → desmonta primero (VB6 DoEquita toggle), luego cae al piso.
        if (u.flags.Montando == 1 && od.Type == ObjType.Monturas && u.Invent.MonturaSlot > 0)
        {
            byte ms = u.Invent.MonturaSlot;
            DoEquita(u, ref u.Invent.Object[ms], ms, ObjData.Get(u.Invent.MonturaObjIndex));
        }

        // Navegando: no se puede tirar el barco (VB6 msg 20).
        if (u.flags.Navegando && od.Type == ObjType.Barcos)
        { ServerPackets.ConsoleMsg(u.Conn, "No puedes hacer eso mientras navegas.", 1); return; }

        // Items faccionarios (Real/Caos/Milicia): no se pueden tirar al piso.
        if (od.Real == 1 || od.Caos == 1 || od.Milicia == 1)
        { ServerPackets.ConsoleMsg(u.Conn, "No puedes desprenderte de un objeto faccionario.", 1); return; }

        // Destruir==1 → confirmación de destrucción (ShowMessageBox accion 1 → cliente reenvía DropDestroy).
        if (od.Destruir == 1)
        { ServerPackets.ShowMessageBox(u.Conn, "", true, 1); return; }

        // Item newbie permanente (Permanente==2) → confirmación de eliminación (accion 10).
        if (od.Permanente == 2)
        { ServerPackets.ShowMessageBox(u.Conn, "¿Deseas eliminar este objeto?", true, 10); return; }

        DropObj(u, slot, amount, u.Pos.Map, u.Pos.X, u.Pos.Y);
    }

    /// <summary>DropObj (InvUsuario.bas:401) 1:1: baja 'num' del slot al piso, con bloqueos de items.</summary>
    private static void DropObj(User u, byte slot, int num, int map, int x, int y)
    {
        ref var item = ref u.Invent.Object[slot];
        short objIndex = item.ObjIndex;
        if (objIndex <= 0 || num <= 0) return;

        var od = ObjData.Get(objIndex);
        if (num > item.Amount) num = item.Amount;

        // Bloqueo total para items faccionarios o con NoSeCae (DropObj:430).
        if (od.Real > 0 || od.Caos > 0 || od.Milicia > 0 || od.NoSeCae > 0)
        { ServerPackets.ConsoleMsg(u.Conn, "No puedes desprenderte de ese objeto.", 1); return; } // msg 260

        // Item newbie: los jugadores comunes no pueden tirarlos.
        if (ItemNewbie(objIndex) && u.FaccionStatus < 7)
        { ServerPackets.ConsoleMsg(u.Conn, "No puedes desprenderte de ese objeto.", 1); return; } // msg 260

        var mp = MapLoader.Get(map);
        if (mp == null) return;

        // Tope de pila en el piso / no se puede tirar encima de otro objeto distinto.
        long enPiso = (mp.FloorObj[x, y] == objIndex) ? mp.FloorAmount[x, y] : 0;
        if (mp.FloorObj[x, y] != 0 && mp.FloorObj[x, y] != objIndex)
        { ServerPackets.ConsoleMsg(u.Conn, "No puedes tirar el objeto ahí.", 1); return; } // msg 262
        if (num + enPiso > MAX_INVENTORY_OBJS) num = (int)(MAX_INVENTORY_OBJS - enPiso);
        if (num <= 0) return;

        // Poner en el piso (MakeObj) y quitar del inventario (QuitarUserInvItem, que desequipa si hace falta).
        mp.FloorObj[x, y] = objIndex;
        mp.FloorAmount[x, y] += num;
        AreaVisibility.ObjectAppeared(map, x, y, objIndex, mp.FloorAmount[x, y]);

        QuitarUserInvItem(u, slot, num);
        SendSlot(u, slot);
    }

    /// <summary>
    /// QuitarUserInvItem (InvUsuario.bas:270) 1:1: baja 'cant' del slot; si se llevaba todo y estaba
    /// equipado, lo DESEQUIPA primero (clave del bug: tirar un objeto equipado dejaba el char con él puesto).
    /// </summary>
    public static void QuitarUserInvItem(User u, byte slot, int cant)
    {
        if (slot < 1 || slot > Constants.MAX_INVENTORY_SLOTS) return;
        ref var it = ref u.Invent.Object[slot];

        if (it.Amount <= cant && it.Equipped)
            Desequipar(u, slot);

        it.Amount -= cant;
        if (it.Amount <= 0)
        {
            it.ObjIndex = 0;
            it.Amount = 0;
            it.Equipped = false;
            if (u.Invent.NroItems > 0) u.Invent.NroItems--;
        }
    }

    /// <summary>
    /// TirarOro (InvUsuario.bas:150) — deja el oro en el piso como pilas de iORO (tope MAX_INVENTORY_OBJS
    /// por tile). Versión núcleo: apila en el tile del jugador (sin dispersión por tiles vecinos).
    /// </summary>
    private static void TirarOro(User u, int cantidad)
    {
        if (cantidad <= 0 || cantidad > u.Stats.GLD) return;
        var mp = MapLoader.Get(u.Pos.Map);
        if (mp == null) return;
        int x = u.Pos.X, y = u.Pos.Y;

        // No tirar oro encima de un objeto que no sea oro.
        if (mp.FloorObj[x, y] != 0 && mp.FloorObj[x, y] != ORO_INDEX) return;

        long enPiso = (mp.FloorObj[x, y] == ORO_INDEX) ? mp.FloorAmount[x, y] : 0;
        int poner = (int)Math.Min(cantidad, MAX_INVENTORY_OBJS - enPiso);
        if (poner <= 0) return;

        mp.FloorObj[x, y] = ORO_INDEX;
        mp.FloorAmount[x, y] += poner;
        u.Stats.GLD -= poner;
        AreaVisibility.ObjectAppeared(u.Pos.Map, x, y, ORO_INDEX, mp.FloorAmount[x, y]);
    }

    /// <summary>
    /// Desequipar (InvUsuario.bas:666) 1:1: quita el equipo del slot, limpia el índice equipado,
    /// el anim correspondiente del Char y su aura, y difunde el cambio de apariencia al área.
    /// </summary>
    public static void Desequipar(User u, byte slot)
    {
        if (slot < 1 || slot > Constants.MAX_INVENTORY_SLOTS) return;
        ref var it = ref u.Invent.Object[slot];
        if (it.ObjIndex == 0) return;
        var od = ObjData.Get(it.ObjIndex);

        switch (od.Type)
        {
            case ObjType.Monturas:
                DoEquita(u, ref it, slot, od);      // toggle: desmonta
                return;

            case ObjType.Barcos:
                DoNavega(u, ref it, slot, od);       // toggle: desembarca
                return;

            case ObjType.Weapon:
            case ObjType.Nudillos:
                it.Equipped = false;
                u.Invent.WeaponEqpObjIndex = 0; u.Invent.WeaponEqpSlot = 0;
                u.Invent.NudiEqpObjIndex = 0; u.Invent.NudiEqpSlot = 0;
                u.Char.WeaponAnim = 0;               // NingunArma
                SetAura(u, ref u.Char.Arma_Aura, 1, 0);
                break;

            case ObjType.Flechas:
                it.Equipped = false;
                u.Invent.MunicionEqpObjIndex = 0; u.Invent.MunicionEqpSlot = 0;
                break;

            case ObjType.Anillo:
            case ObjType.Anillos:
                it.Equipped = false;
                u.Invent.AnilloEqpObjIndex = 0; u.Invent.AnilloEqpSlot = 0;
                SetAura(u, ref u.Char.Anillo_Aura, 6, 0);
                break;

            case ObjType.ItemsMagicos:
                // VB6 InvUsuario.bas:726-765 (otItemsMagicos): limpia MagicIndex/MagicSlot, el aura
                // de anillo y REVIERTE el EfectoMagico (atributo/skill/oculto del Anillo de las Sombras).
                it.Equipped = false;
                u.Invent.MagicIndex = 0; u.Invent.MagicSlot = 0;
                // .chr viejos (fallback del CharLoader) podían apuntar el ítem mágico al slot de anillo.
                if (u.Invent.AnilloEqpSlot == slot) { u.Invent.AnilloEqpObjIndex = 0; u.Invent.AnilloEqpSlot = 0; }
                SetAura(u, ref u.Char.Anillo_Aura, 6, 0);
                AplicarEfectoMagico(u, od, equip: false);
                break;

            case ObjType.Armadura:
                it.Equipped = false;
                switch (od.SubTipo)
                {
                    case 1: // casco
                        u.Invent.CascoEqpObjIndex = 0; u.Invent.CascoEqpSlot = 0;
                        u.Char.CascoAnim = 0;            // NingunCasco
                        SetAura(u, ref u.Char.Head_Aura, 4, 0);
                        break;
                    case 2: // escudo
                        u.Invent.EscudoEqpObjIndex = 0; u.Invent.EscudoEqpSlot = 0;
                        u.Char.ShieldAnim = 0;          // NingunEscudo
                        SetAura(u, ref u.Char.Escudo_Aura, 3, 0);
                        break;
                    default: // armadura (body)
                        u.Invent.ArmourEqpObjIndex = 0; u.Invent.ArmourEqpSlot = 0;
                        SetAura(u, ref u.Char.Body_Aura, 2, 0);
                        DarCuerpoDesnudo(u);
                        break;
                }
                break;

            default:
                it.Equipped = false;
                return;
        }

        SendSlot(u, slot);
        BroadcastCharChange(u);
    }

    /// <summary>ItemNewbie (InvUsuario.bas:2621): el item es de tipo newbie (no se puede tirar/vender).</summary>
    private static bool ItemNewbie(short objIndex) => objIndex > 0 && ObjData.Get(objIndex).Newbie > 0;

    /// <summary>
    /// HandleEquipItem: equipa/desequipa según el tipo del objeto (obj.dat).
    /// Arma/armadura/escudo/casco: cambia Char.* y difunde CharacterChange al mapa.
    /// </summary>
    public static void EquipItem(int userIndex, byte slot)
    {
        var u = UserListManager.UserList[userIndex];
        if (slot < 1 || slot > Constants.MAX_INVENTORY_SLOTS) return;
        ref var item = ref u.Invent.Object[slot];
        if (item.ObjIndex == 0) return;

        var od = ObjData.Get(item.ObjIndex);
        // Muerto: solo barcos (subir/bajar de la barca estando muerto). El resto del equipo no.
        if (u.flags.Muerto == 1 && od.Type != ObjType.Barcos) return;

        // Requisitos de uso (clase/raza/nivel/sexo). Barcos/Monturas NO se gatean por nivel:
        // el VB6 los valida por skill (Navegacion/Equitacion) dentro de DoNavega/DoEquita.
        // Si está desequipando (ya equipado) se permite siempre; sólo se valida al equipar.
        if (!item.Equipped && od.Type != ObjType.Barcos && od.Type != ObjType.Monturas)
        {
            if (!PuedeUsarObjeto(u, od, out string motivoEq))
            { ServerPackets.ConsoleMsg(u.Conn, motivoEq, 1); return; }
        }

        switch (od.Type)
        {
            case ObjType.Weapon:
                // Arma y nudillos comparten la mano: al equipar un arma, sacar los nudillos previos
                // (InvUsuario.bas otWeapon). Antes de ToggleEquip porque Desequipar limpia el slot de arma.
                if (!item.Equipped && u.Invent.NudiEqpObjIndex > 0)
                    Desequipar(u, u.Invent.NudiEqpSlot);
                ToggleEquip(u, ref item, slot, ref u.Invent.WeaponEqpObjIndex, ref u.Invent.WeaponEqpSlot,
                    equip => { // Navegando/Montando: NO tocar el body/anim visible (InvUsuario.bas:1145), así
                               // no se ve el arma sobre el caballo ni se pierde el body del barco. El slot
                               // equipado igual se guarda y RestaurarAparienciaAPie lo reconstruye al bajar.
                               if (AparienciaAPie(u)) u.Char.WeaponAnim = (short)(equip ? od.WeaponAnim : 0);
                               SetAura(u, ref u.Char.Arma_Aura, 1, equip ? od.Aura : 0);
                               // SND_SACARARMA al equipar (salvo anim 2 = desarmado). El cliente VB6 lo
                               // tocaba en CharacterChangeSlot Case 4, pero este server usa CharacterChange
                               // (full), así que el sonido se difunde acá. (Protocol.bas:2380)
                               if (equip && od.WeaponAnim != 2) BroadcastWaveArea(u, Sounds.SACARARMA);
                               SndAura(u, od, equip); });
                break;
            // otArmadura agrupa ARMADURA/CASCO/ESCUDO, distinguidos por SubTipo (InvUsuario.bas:1228-1230,
            // 786-846). En obj.dat NO hay tipos separados Casco/Escudo: todos son ObjType=3 (Armadura).
            case ObjType.Armadura:
                if (od.SubTipo == 1) goto case ObjType.Casco;     // SubTipo 1 = casco
                if (od.SubTipo == 2) goto case ObjType.Escudo;    // SubTipo 2 = escudo
                // SubTipo 0 = armadura (body via NumRopaje)
                ToggleEquip(u, ref item, slot, ref u.Invent.ArmourEqpObjIndex, ref u.Invent.ArmourEqpSlot,
                    equip => { // Navegando/Montando: NO cambiar el body visible (InvUsuario.bas:1268), si no
                               // se pierde el body del barco/montura. DarCuerpoDesnudo ya respeta esos flags.
                               if (equip) { if (AparienciaAPie(u)) u.Char.body = (short)od.Ropaje; u.flags.Desnudo = 0; } else DarCuerpoDesnudo(u);
                               SetAura(u, ref u.Char.Body_Aura, 2, equip ? od.Aura : 0);
                               SndAura(u, od, equip); });
                break;
            case ObjType.Escudo:
                ToggleEquip(u, ref item, slot, ref u.Invent.EscudoEqpObjIndex, ref u.Invent.EscudoEqpSlot,
                    equip => { if (AparienciaAPie(u)) u.Char.ShieldAnim = (short)(equip ? od.ShieldAnim : 0);
                               SetAura(u, ref u.Char.Escudo_Aura, 3, equip ? od.Aura : 0);
                               SndAura(u, od, equip); });
                break;
            case ObjType.Casco:
                ToggleEquip(u, ref item, slot, ref u.Invent.CascoEqpObjIndex, ref u.Invent.CascoEqpSlot,
                    equip => { if (AparienciaAPie(u)) u.Char.CascoAnim = (short)(equip ? od.CascoAnim : 0);
                               SetAura(u, ref u.Char.Head_Aura, 4, equip ? od.Aura : 0); });
                break;

            // Nudillos (otNudillos, InvUsuario.bas:1047): van en la MANO como un arma de lucha libre,
            // NO en el slot de anillo. Comparten la mano con el arma: equipar nudillos saca el arma.
            case ObjType.Nudillos:
            {
                if (item.Equipped) { Desequipar(u, slot); return; } // toggle off
                // Sacar arma y nudillos previos (comparten la mano).
                if (u.Invent.WeaponEqpObjIndex > 0) Desequipar(u, u.Invent.WeaponEqpSlot);
                if (u.Invent.NudiEqpObjIndex > 0) Desequipar(u, u.Invent.NudiEqpSlot);
                item.Equipped = true;
                u.Invent.NudiEqpObjIndex = item.ObjIndex;
                u.Invent.NudiEqpSlot = slot;
                if (AparienciaAPie(u)) u.Char.WeaponAnim = (short)od.WeaponAnim;
                SetAura(u, ref u.Char.Arma_Aura, 1, od.Aura); // aura va en la ranura de arma (1), no anillo
                SndAura(u, od, true);
                SendSlot(u, slot);
                BroadcastCharChange(u);
                return;
            }

            // Ítems mágicos (otItemsMagicos=21: orbes, anillos mágicos, collares, pendientes).
            // VB6 InvUsuario.bas:988-1044: van al slot MagicIndex/MagicSlot (NO al AnilloEqp de
            // herramientas) y aplican su EfectoMagico al equipar. Antes caían en "default" y no se
            // podían equipar — por eso ninguna orbe/collar funcionaba.
            case ObjType.ItemsMagicos:
            {
                if (item.Equipped) { Desequipar(u, slot); return; } // toggle off
                // Sacar el ítem mágico anterior (solo puede haber uno equipado).
                if (u.Invent.MagicSlot > 0) Desequipar(u, (byte)u.Invent.MagicSlot);
                item.Equipped = true;
                u.Invent.MagicIndex = item.ObjIndex;
                u.Invent.MagicSlot = slot;
                SetAura(u, ref u.Char.Anillo_Aura, 6, od.Aura); // aura en la ranura de anillo (6)
                SndAura(u, od, true);
                AplicarEfectoMagico(u, od, equip: true);
                SendSlot(u, slot);
                return; // no cambia la apariencia → sin BroadcastCharChange
            }

            case ObjType.Anillo:
            case ObjType.Anillos:
            {
                // VB6 InvUsuario.bas:1173-1192: equipa/desequipa anillo en AnilloEqpSlot
                if (item.Equipped)
                {
                    // desequipar
                    item.Equipped = false;
                    u.Invent.AnilloEqpObjIndex = 0;
                    u.Invent.AnilloEqpSlot = 0;
                }
                else
                {
                    // desequipar anterior si había
                    if (u.Invent.AnilloEqpSlot > 0)
                    {
                        ref var prev = ref u.Invent.Object[u.Invent.AnilloEqpSlot];
                        prev.Equipped = false;
                        SendSlot(u, u.Invent.AnilloEqpSlot);
                    }
                    item.Equipped = true;
                    u.Invent.AnilloEqpObjIndex = item.ObjIndex;
                    u.Invent.AnilloEqpSlot = slot;
                    SetAura(u, ref u.Char.Anillo_Aura, 6, od.Aura); // aura del anillo/item mágico
                    SndAura(u, od, true);                           // sonido de aura (nudillos/items mágicos)

                    // VB6: si es herramienta (caña, piquete, hacha, tijeras) → WorkRequestTarget
                    byte toolSkill = GetToolSkill(item.ObjIndex);
                    if (toolSkill > 0)
                        ServerPackets.WorkRequestTarget(u.Conn, toolSkill);
                }
                if (!item.Equipped) SetAura(u, ref u.Char.Anillo_Aura, 6, 0); // al desequipar, limpiar aura
                SendSlot(u, slot);
                return; // no BroadcastCharChange para anillos
            }

            // Munición (flechas): equipa/desequipa en MunicionEqpSlot (InvUsuario.bas:1201).
            case ObjType.Flechas:
                ToggleEquip(u, ref item, slot, ref u.Invent.MunicionEqpObjIndex, ref u.Invent.MunicionEqpSlot, _ => { });
                SendSlot(u, slot);
                return; // sin BroadcastCharChange (la munición no cambia la apariencia)

            case ObjType.Monturas:
                DoEquita(u, ref item, slot, od);
                SendSlot(u, slot);
                return;

            case ObjType.Barcos:
                DoNavega(u, ref item, slot, od);
                SendSlot(u, slot);
                return;

            default:
                return; // tipo no equipable
        }

        SendSlot(u, slot);
        BroadcastCharChange(u);
    }

    /// <summary>Setea un aura del personaje (Char.<X>_Aura) y la difunde al área (AuraToChar). VB6 PrepareMessageAuraToChar.</summary>
    private static void SetAura(User u, ref byte auraField, byte slotAura, int auraValue)
    {
        auraField = (byte)auraValue;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.AuraToChar(o.Conn, u.Char.CharIndex, auraField, slotAura);
        }
    }

    /// <summary>
    /// DoEquita (Trabajo.bas:2682) — montar/desmontar. Cambia el body al de la montura (Ropaje) y
    /// alterna flags.Montando; al desmontar restaura body (armadura o desnudo). Sonido 133 + MontateToggle.
    /// Versión núcleo (sin skin/skill/dungeon avanzado; valida no navegar).
    /// </summary>
    private static void DoEquita(User u, ref UserObj item, byte slot, ObjData.Obj od)
    {
        if (u.flags.Navegando) { ServerPackets.ConsoleMsg(u.Conn, "No puedes hacer eso mientras navegas.", 1); return; }

        // Al MONTAR (no al desmontar): skill Equitación (PuedeUsarSkill) + clase/raza/sexo/facción
        // (VB6 DoEquita, Trabajo.bas:2682). El nivel NO se valida (se usa la skill, no MinELV).
        if (u.flags.Montando == 0)
        {
            const int SK_EQUITACION = 27; // eSkill.Equitacion
            bool esGm = u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO;
            if (!esGm && od.MinSkill > 0 && u.Stats.UserSkills[SK_EQUITACION] < od.MinSkill)
            { ServerPackets.ConsoleMsg(u.Conn, $"Necesitas {od.MinSkill} puntos en Equitación para usar esta montura.", 1); return; }
            if (!PuedeUsarObjeto(u, od, out string motivoMont, validarNivel: false))
            { ServerPackets.ConsoleMsg(u.Conn, motivoMont, 1); return; }
        }

        if (u.flags.Montando == 0)
        {
            u.Char.body = (short)od.Ropaje;
            u.Char.Head = u.OrigChar.Head != 0 ? u.OrigChar.Head : u.Char.Head;
            u.Char.WeaponAnim = 0; // montado: sin arma a la vista
            u.flags.Montando = 1;
            item.Equipped = true;
            u.Invent.MonturaObjIndex = item.ObjIndex; u.Invent.MonturaSlot = slot;
            BroadcastWaveArea(u, 133);
        }
        else
        {
            u.flags.Montando = 0;
            RestaurarAparienciaAPie(u);
            item.Equipped = false;
            u.Invent.MonturaObjIndex = 0; u.Invent.MonturaSlot = 0;
        }
        BroadcastCharChange(u);
        ServerPackets.MontateToggle(u.Conn);
    }

    /// <summary>
    /// DoNavega (Trabajo.bas:147) — embarcar/desembarcar. Cambia el body al del barco (Ropaje) y
    /// alterna flags.Navegando; al bajar restaura body (armadura o desnudo). Sonido 133 + NavigateToggle.
    /// Versión núcleo (sin validación de costa/skill avanzada).
    /// </summary>
    private static void DoNavega(User u, ref UserObj item, byte slot, ObjData.Obj od)
    {
        var map = MapLoader.Get(u.Pos.Map);
        if (u.flags.Navegando == false)
        {
            // Skill Navegación (PuedeUsarSkill, Trabajo.bas): la barca exige MinSkill en Navegación.
            const int SK_NAVEGACION = 26; // eSkill.Navegacion
            bool esGm = u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO;
            if (!esGm && od.MinSkill > 0 && u.Stats.UserSkills[SK_NAVEGACION] < od.MinSkill)
            { ServerPackets.ConsoleMsg(u.Conn, $"Necesitas {od.MinSkill} puntos en Navegación para usar esta embarcación.", 1); return; }
            // Clase/raza/sexo/facción (sin nivel: se usa la skill).
            if (!PuedeUsarObjeto(u, od, out string motivoNav, validarNivel: false))
            { ServerPackets.ConsoleMsg(u.Conn, motivoNav, 1); return; }

            // DoNavega (Trabajo.bas:156): para embarcar hay que estar junto a una costa
            // (algún tile adyacente N/S/E/O es agua navegable). Si no, msg 394 y no embarca.
            bool costa = map != null && (map.HasWater(u.Pos.X - 1, u.Pos.Y) || map.HasWater(u.Pos.X + 1, u.Pos.Y)
                                      || map.HasWater(u.Pos.X, u.Pos.Y - 1) || map.HasWater(u.Pos.X, u.Pos.Y + 1));
            if (!costa) { ServerPackets.ConsoleMsg(u.Conn, "¡Estás demasiado lejos del agua!", 1); return; }

            if (u.flags.Montando == 1) { u.flags.Montando = 0; ServerPackets.MontateToggle(u.Conn); }
            // Muerto → barca fantasma (iFragataFantasmal=87); vivo → body del barco (Trabajo.bas:192).
            u.Char.body = u.flags.Muerto == 1 ? (short)87 : (short)(od.Ropaje > 0 ? od.Ropaje : 87);
            u.Char.Head = 0;
            u.Char.WeaponAnim = 0; u.Char.ShieldAnim = 0; u.Char.CascoAnim = 0;
            u.flags.Navegando = true;
            item.Equipped = true;
            u.Invent.BarcoObjIndex = item.ObjIndex; u.Invent.BarcoSlot = slot;
            BroadcastWaveArea(u, 133);
        }
        else
        {
            // DoNavega (Trabajo.bas:211): para desembarcar NO se puede estar en agua profunda;
            // hay que estar cerca de una costa (algún tile del entorno NO es agua). Si todo es
            // agua (actual + 4 adyacentes) → msg 430 y no baja.
            bool todoAgua = map != null && map.HasWater(u.Pos.X, u.Pos.Y)
                            && map.HasWater(u.Pos.X - 1, u.Pos.Y) && map.HasWater(u.Pos.X + 1, u.Pos.Y)
                            && map.HasWater(u.Pos.X, u.Pos.Y - 1) && map.HasWater(u.Pos.X, u.Pos.Y + 1);
            if (todoAgua) { ServerPackets.ConsoleMsg(u.Conn, "¡Debes estar cerca de una costa para bajar de tu barca!", 1); return; }

            u.flags.Navegando = false;
            // Muerto → fantasma a pie (body 8, cabeza de muerto); vivo → apariencia normal (Trabajo.bas:218).
            if (u.flags.Muerto == 1) { u.Char.body = 8; u.Char.Head = 500; u.Char.WeaponAnim = 0; u.Char.ShieldAnim = 0; u.Char.CascoAnim = 0; }
            else RestaurarAparienciaAPie(u);
            item.Equipped = false;
            u.Invent.BarcoObjIndex = 0; u.Invent.BarcoSlot = 0;
        }
        BroadcastCharChange(u);
        ServerPackets.NavigateToggle(u.Conn);
    }

    /// <summary>
    /// Restaura la apariencia "a pie" desde el equipo equipado (body de armadura o desnudo + anims de
    /// arma/escudo/casco + cabeza original). La usan el desmontar/desembarcar y el logout montado/navegando
    /// (para NO persistir el body del caballo/barca).
    /// </summary>
    /// <summary>
    /// True si la apariencia "a pie" está visible (no navegando ni montando). Mientras navega/monta,
    /// el body visible es el del barco/montura, así que equipar armadura/arma/escudo/casco NO debe
    /// pisar Char.body/anim (InvUsuario.bas: el envío de CharacterChangeSlot se omite con esos flags).
    /// </summary>
    private static bool AparienciaAPie(User u) => !u.flags.Navegando && u.flags.Montando != 1;

    public static void RestaurarAparienciaAPie(User u)
    {
        u.Char.Head = u.OrigChar.Head != 0 ? u.OrigChar.Head : u.Char.Head;
        if (u.Invent.ArmourEqpObjIndex > 0) u.Char.body = (short)ObjData.Get(u.Invent.ArmourEqpObjIndex).Ropaje;
        else DarCuerpoDesnudo(u);
        u.Char.WeaponAnim = u.Invent.WeaponEqpObjIndex > 0 ? (short)ObjData.Get(u.Invent.WeaponEqpObjIndex).WeaponAnim : (short)0;
        u.Char.ShieldAnim = u.Invent.EscudoEqpObjIndex > 0 ? (short)ObjData.Get(u.Invent.EscudoEqpObjIndex).ShieldAnim : (short)0;
        u.Char.CascoAnim  = u.Invent.CascoEqpObjIndex  > 0 ? (short)ObjData.Get(u.Invent.CascoEqpObjIndex).CascoAnim  : (short)0;
    }

    /// <summary>Difunde un sonido al área del usuario (PlayWave a los del mapa).</summary>
    /// <summary>
    /// Sonido de aura al equipar (VB6 InvUsuario.bas:1081/1160/1283/1399): solo si el item tiene
    /// Aura>0 y SndEspecial>0, y solo al EQUIPAR (no al desequipar). Se difunde al área.
    /// </summary>
    private static void SndAura(User u, ObjData.Obj od, bool equip)
    {
        if (equip && od.Aura != 0 && od.SndEspecial > 0)
            BroadcastWaveArea(u, (short)od.SndEspecial);
    }

    /// <summary>
    /// Aplica/revierte el EfectoMagico de un ítem mágico al equiparlo/desequiparlo
    /// (VB6 InvUsuario.bas:1013/737): ModificaAtributo(2), ModificaSkill(3) y CaminaOculto(13,
    /// Anillo de las Sombras: oculto permanente). El resto de los efectos (orbes de combate,
    /// inhibición, regen, defensa mágica, drop, Rykan/Sacrificio) se evalúan en runtime por MagicIndex.
    /// </summary>
    private static void AplicarEfectoMagico(User u, ObjData.Obj od, bool equip)
    {
        int signo = equip ? 1 : -1;
        switch (od.EfectoMagico)
        {
            case 2: // eMagicType.ModificaAtributo
                if (od.QueAtributo >= 1 && od.QueAtributo <= Constants.NUMATRIBUTOS)
                {
                    int v = u.Stats.UserAtributos[od.QueAtributo] + signo * od.CuantoAumento;
                    u.Stats.UserAtributos[od.QueAtributo] = (byte)Math.Clamp(v, 1, 255);
                    if (u.Conn != null)
                    {
                        if (od.QueAtributo == 1) ServerPackets.UpdateStrenght(u.Conn, u.Stats.UserAtributos[1]);
                        else if (od.QueAtributo == 2) ServerPackets.UpdateDexterity(u.Conn, u.Stats.UserAtributos[2]);
                    }
                }
                break;

            case 3: // eMagicType.ModificaSkill
                if (od.QueSkill >= 1 && od.QueSkill <= Constants.NUMSKILLS)
                {
                    int v = u.Stats.UserSkills[od.QueSkill] + signo * od.CuantoAumento;
                    u.Stats.UserSkills[od.QueSkill] = (byte)Math.Clamp(v, 0, 255);
                }
                break;

            case 13: // eMagicType.CaminaOculto — Anillo de las Sombras (1006)
                if (equip)
                {
                    if (u.flags.Oculto == 0)
                    {
                        u.flags.Oculto = 1;
                        BroadcastInvisibleArea(u, true);
                        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "¡Te has ocultado entre las sombras!", 1);
                    }
                }
                else if (u.flags.Oculto == 1)
                {
                    u.flags.Oculto = 0;
                    if (u.flags.Invisible == 0)
                    {
                        BroadcastInvisibleArea(u, false);
                        if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "¡Has vuelto a ser visible!", 1);
                    }
                }
                break;
        }
    }

    /// <summary>Difunde SetInvisible del personaje a todos los del mapa.</summary>
    private static void BroadcastInvisibleArea(User u, bool invisible)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.SetInvisible(o.Conn, u.Char.CharIndex, invisible);
        }
    }

    /// <summary>True si el ítem mágico equipado (MagicIndex; orbes/collares/pendientes) — o el arma,
    /// si incluirArma (espadas con efecto: Tierra/Abismal/Wivern/GM) — tiene ese EfectoMagico (eMagicType).</summary>
    public static bool TieneEfectoMagico(User u, int efecto, bool incluirArma = false)
    {
        if (u.Invent.MagicIndex > 0 && ObjData.Get(u.Invent.MagicIndex).EfectoMagico == efecto) return true;
        return incluirArma && u.Invent.WeaponEqpObjIndex > 0
            && ObjData.Get(u.Invent.WeaponEqpObjIndex).EfectoMagico == efecto;
    }

    /// <summary>CuantoAumento del ítem mágico equipado si su EfectoMagico coincide; 0 si no tiene.</summary>
    public static int CuantoEfectoMagico(User u, int efecto)
    {
        if (u.Invent.MagicIndex > 0)
        {
            var od = ObjData.Get(u.Invent.MagicIndex);
            if (od.EfectoMagico == efecto) return od.CuantoAumento;
        }
        return 0;
    }

    private static void BroadcastWaveArea(User u, short wave)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.PlayWave(o.Conn, wave, (byte)u.Pos.X, (byte)u.Pos.Y);
        }
    }

    /// <summary>Difunde un CreateFX (efecto visual) sobre un CharIndex a los del mapa del usuario.</summary>
    private static void BroadcastFXArea(User u, short charIndex, short fx, short loops)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.CreateFX(o.Conn, charIndex, fx, loops);
        }
    }

    private static readonly Random _rng = new();

    /// <summary>
    /// HandleUseItem / UseInvItem (InvUsuario.bas:1455). Select Case por OBJType:
    /// comida, bebida, pociones (todos los subtipos), guita, equipo. Migrado 1:1.
    /// </summary>
    public static void UseItem(int userIndex, byte slot, bool esAutoPot = false)
    {
        var u = UserListManager.UserList[userIndex];
        if (slot < 1 || slot > Constants.MAX_INVENTORY_SLOTS) return;
        ref var item = ref u.Invent.Object[slot];
        if (item.ObjIndex == 0) return;

        var od = ObjData.Get(item.ObjIndex);
        // Muerto: solo se permiten barcos (subir/bajar de la barca estando muerto) y la RUNA de
        // teletransporte (un muerto debe poder volver a su hogar/cementerio). El resto: DeadCheck.
        if (u.flags.Muerto == 1 && od.Type != ObjType.Barcos && od.Type != ObjType.Runa) return;
        // Restricción de uso por clase/raza/nivel/sexo: avisar el motivo y no usar.
        if (!PuedeUsarObjeto(u, od, out string motivoUso))
        { ServerPackets.ConsoleMsg(u.Conn, motivoUso, 1); return; }
        bool consumir = false;

        switch (od.Type)
        {
            case ObjType.UseOnce: // comida
                u.Stats.MinHam = (short)Math.Min(u.Stats.MaxHam, u.Stats.MinHam + od.MinHam);
                u.flags.Hambre = 0;
                ServerPackets.UpdateHungerAndThirst(u.Conn, u);
                consumir = true;
                break;

            case ObjType.Bebidas:
                u.Stats.MinAGU = (short)Math.Min(u.Stats.MaxAGU, u.Stats.MinAGU + od.MinSed);
                u.flags.Sed = 0;
                ServerPackets.UpdateHungerAndThirst(u.Conn, u);
                BroadcastWaveArea(u, Sounds.BEBER); // SND_BEBER=135
                consumir = true;
                break;

            case ObjType.Guita: // montón de oro
                u.Stats.GLD += item.Amount;
                item.ObjIndex = 0; item.Amount = 0; item.Equipped = false;
                if (u.Invent.NroItems > 0) u.Invent.NroItems--;
                ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
                SendSlot(u, slot);
                return;

            // Fuegos artificiales (cañitas/cohetes/petardos): son ObjType=11 sin SubTipo de poción,
            // con un campo "Particula=" en obj.dat. Al usarlos lanzan esa partícula sobre el personaje
            // (temporal) y reproducen su sonido (Snd1) en el área. Se consume 1.
            case ObjType.Pociones when od.Particula > 0:
                LanzarFuegoArtificial(u, od);
                consumir = true;
                break;

            case ObjType.Pociones:
                // Cooldown de uso de pociones (IntervaloGolpeUsar = 400ms), salvo autopot:
                // el autopot ya viene limitado por el cliente (250ms) y por el rate-limit
                // anti-cheat (10/seg) del handler.
                if (!esAutoPot && !Intervals.PuedeGolpeUsar(u)) return;
                consumir = UsarPocion(userIndex, u, od);
                break;

            case ObjType.Runa:
                UsarRuna(u, slot);
                return;

            case ObjType.Pergaminos:
                AprenderHechizo(u, slot, od);
                return;

            // El cliente Godot manda USE_ITEM (no EquipItem) para barcos y monturas
            // (input_handler.gd:784/822 → write_use_item). VB6: UsarObjeto → EquiparInvItem → DoNavega/DoEquita.
            case ObjType.Barcos:
                DoNavega(u, ref item, slot, od);
                SendSlot(u, slot);
                return;

            case ObjType.Monturas:
                DoEquita(u, ref item, slot, od);
                SendSlot(u, slot);
                return;

            // otBolsas (InvUsuario.bas:2022): bolsa de oro → suma Valor al GLD y se consume.
            case ObjType.Bolsas:
                u.Stats.GLD += od.Valor;
                ServerPackets.ConsoleMsg(u.Conn, $"Has obtenido {od.Valor} monedas de oro de {od.Name}.", 1);
                ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);
                consumir = true;
                break;

            // otAnilloEspec=49 (InvUsuario.bas:2075): manual que aumenta un skill (QueSkill += CuantoAumento).
            case ObjType.AnilloEspec:
            {
                if (od.QueSkill <= 0 || od.CuantoAumento <= 0)
                { ServerPackets.ConsoleMsg(u.Conn, "Este manual no está configurado correctamente.", 1); return; }
                // QueSkill=99 → MANUAL MAESTRO: sube TODOS los skills en CuantoAumento (usar 100 = al máximo).
                if (od.QueSkill == 99)
                {
                    bool subioAlgo = false;
                    for (int s = 1; s <= Constants.NUMSKILLS; s++)
                    {
                        int viejo = u.Stats.UserSkills[s];
                        if (viejo >= Skills.MAXSKILLPOINTS) continue;
                        u.Stats.UserSkills[s] = (byte)Math.Min(Skills.MAXSKILLPOINTS, viejo + od.CuantoAumento);
                        subioAlgo = true;
                    }
                    if (!subioAlgo)
                    { ServerPackets.ConsoleMsg(u.Conn, "Ya tienes todas tus habilidades al máximo.", 1); return; }
                    ServerPackets.UpdateUserStats(u.Conn, u);
                    ServerPackets.ConsoleMsg(u.Conn, $"Has aumentado TODAS tus habilidades en {od.CuantoAumento} puntos.", 28);
                    consumir = true;
                    break;
                }
                if (od.QueSkill > Constants.NUMSKILLS)
                { ServerPackets.ConsoleMsg(u.Conn, "Skill inválido en este manual.", 1); return; }
                int ant = u.Stats.UserSkills[od.QueSkill];
                if (ant >= Skills.MAXSKILLPOINTS)
                { ServerPackets.ConsoleMsg(u.Conn, "Ya tienes el maximo nivel en este skill.", 1); return; }
                int nuevo = Math.Min(Skills.MAXSKILLPOINTS, ant + od.CuantoAumento);
                u.Stats.UserSkills[od.QueSkill] = (byte)nuevo;
                ServerPackets.UpdateUserStats(u.Conn, u);
                ServerPackets.ConsoleMsg(u.Conn, $"Has aumentado tu skill de {ant} a {nuevo}.", 1);
                consumir = true;
                break;
            }

            // otBotellaLlena=34 (InvUsuario.bas:1965): beber → restaura sed y deja la botella vacía (IndexCerrada).
            case ObjType.BotellaLlena:
                u.Stats.MinAGU = (short)Math.Min(u.Stats.MaxAGU, u.Stats.MinAGU + od.MinSed);
                u.flags.Sed = 0;
                ServerPackets.UpdateHungerAndThirst(u.Conn, u);
                BroadcastWaveArea(u, 135); // SND_BEBER
                item.Amount--;
                if (item.Amount <= 0)
                { item.ObjIndex = 0; item.Amount = 0; item.Equipped = false; if (u.Invent.NroItems > 0) u.Invent.NroItems--; }
                SendSlot(u, slot);
                if (od.IndexCerrada > 0) AddItemToInventory(u, (short)od.IndexCerrada, 1); // botella vacía
                return;

            // otInstrumentos=26 (InvUsuario.bas:2033): toca el instrumento (Snd1) y duerme NPCs en radio 5.
            case ObjType.Instrumentos:
            {
                if (od.Snd1 > 0) BroadcastWaveArea(u, (short)od.Snd1);
                int dur = od.DuracionEfecto / 1000;
                if (dur < 30) dur = 60;
                double hasta = Environment.TickCount64 / 1000.0 + dur;
                foreach (var n in NpcManager.GetMapNpcs(u.Pos.Map))
                {
                    if (n.Dead) continue;
                    if (Math.Max(Math.Abs(n.X - u.Pos.X), Math.Abs(n.Y - u.Pos.Y)) > 5) continue;
                    n.ParalizadoHasta = hasta; // sin flag Dormido modelado: se reusa la parálisis (mismo efecto: NPC inmóvil)
                    BroadcastFXArea(u, n.CharIndex, 64, 0);
                }
                return;
            }

            // otWeapon (InvUsuario.bas:1621): arma a distancia equipada → pide objetivo (arco/arrojadiza).
            case ObjType.Weapon:
                if (u.Stats.MinSta <= 0) { ServerPackets.ConsoleMsg(u.Conn, "Estás muy cansado.", 1); return; }
                if (!item.Equipped) { ServerPackets.ConsoleMsg(u.Conn, "Antes de usarlo debes equipártelo.", 1); return; }
                if (od.Proyectil == 1)      ServerPackets.WorkRequestTarget(u.Conn, 6); // eSkill.Proyectiles
                else if (od.Proyectil == 2) ServerPackets.WorkRequestTarget(u.Conn, 5); // eSkill.ArmasArrojadizas
                return;

            // otAnillo (InvUsuario.bas:1669): herramienta de crafteo equipada (costurero/olla/serrucho)
            // → envía la lista de items fabricables y abre el formulario. El MARTILLO (herrería) NO abre
            // desde el inventario: requiere doble-click sobre el yunque (ver Accion). Si no está equipada, nada.
            case ObjType.Anillo:
            case ObjType.Anillos:
                if (item.Equipped && item.ObjIndex != Crafting.MARTILLO_HERRERO)
                    Crafting.AbrirCrafteo(userIndex, item.ObjIndex);
                return;

            // otMinerales=23 (InvUsuario.bas:2014): fundir mineral → pide objetivo (fragua) y marca el slot.
            case ObjType.Minerales:
                ServerPackets.WorkRequestTarget(u.Conn, 88); // FundirMetal
                u.flags.Lingoteando = slot;
                return;

            // otLlaves=9 (InvUsuario.bas:1895): abre/cierra una puerta apuntada (TargetObj) según la clave.
            // Requiere un doble-click/clic previo sobre la puerta (LookatTile/Accion setea TargetObj*).
            case ObjType.Llaves:
            {
                if (u.flags.Muerto == 1) return;          // DeadCheck
                if (u.TargetObj == 0) return;
                var targ = ObjData.Get(u.TargetObj);
                if (targ.Type != ObjType.Puertas) return;

                if (targ.Cerrada != 1)                    // VB6: sólo se opera sobre puertas cerradas
                { ServerPackets.ConsoleMsg(u.Conn, "La puerta no está cerrada.", 1); return; } // msg 476

                var mp = MapLoader.Get(u.TargetObjMap);
                if (mp == null) return;
                short actual = mp.FloorObj[u.TargetObjX, u.TargetObjY];
                var actualOd = ObjData.Get(actual);

                if (targ.Llave > 0)                        // cerrada CON llave → abrir si la clave coincide
                {
                    if (targ.Clave == od.Clave)
                    {
                        short ni = (short)actualOd.IndexCerrada;
                        mp.FloorObj[u.TargetObjX, u.TargetObjY] = ni;
                        u.TargetObj = ni;
                        ServerPackets.ConsoleMsg(u.Conn, "Has abierto la puerta.", 1); // msg 472
                    }
                    else ServerPackets.ConsoleMsg(u.Conn, "La llave no es la correcta.", 1); // msg 473
                }
                else                                       // cerrada SIN llave → cerrar con llave
                {
                    if (targ.Clave == od.Clave)
                    {
                        short ni = (short)actualOd.IndexCerradaLlave;
                        mp.FloorObj[u.TargetObjX, u.TargetObjY] = ni;
                        u.TargetObj = ni;
                        ServerPackets.ConsoleMsg(u.Conn, "Has cerrado la puerta con llave.", 1); // msg 474
                    }
                    else ServerPackets.ConsoleMsg(u.Conn, "La llave no es la correcta.", 1); // msg 475
                }
                return;
            }

            // otBotellaVacia=33 (InvUsuario.bas:1945): llena la botella si hay agua en el tile apuntado
            // (TargetX/TargetY, seteado por el clic izquierdo/LookatTile previo). Deja la botella llena (IndexAbierta).
            case ObjType.BotellaVacia:
            {
                if (u.flags.Muerto == 1) return;          // DeadCheck
                var mp = MapLoader.Get(u.Pos.Map);
                if (mp == null || !mp.HasWater(u.TargetX, u.TargetY))
                { ServerPackets.ConsoleMsg(u.Conn, "No hay agua allí para llenar la botella.", 1); return; } // msg 392

                short llena = (short)od.IndexAbierta;
                item.Amount--;                            // QuitarUserInvItem(slot,1)
                if (item.Amount <= 0)
                { item.ObjIndex = 0; item.Amount = 0; item.Equipped = false; if (u.Invent.NroItems > 0) u.Invent.NroItems--; }
                SendSlot(u, slot);
                if (llena > 0 && !AddItemToInventory(u, llena, 1))
                    DropItemToFloor(u.Pos, llena, 1);     // TirarItemAlPiso si no entra
                return;
            }

            // otRegalos=53: caja/regalo que entrega los ítems de su campo "Items=" y se consume.
            // Si algún ítem no entra en el inventario, se tira al piso (TirarItemAlPiso).
            case ObjType.Regalos:
            {
                if (u.flags.Muerto == 1) return; // DeadCheck
                if (od.RegaloItems == null || od.RegaloItems.Length == 0)
                { ServerPackets.ConsoleMsg(u.Conn, "Este regalo está vacío.", 1); return; }
                foreach (var (oi, amt) in od.RegaloItems)
                {
                    if (oi <= 0 || amt <= 0) continue;
                    if (!AddItemToInventory(u, oi, amt))
                        DropItemToFloor(u.Pos, oi, amt);
                }
                ServerPackets.ConsoleMsg(u.Conn, $"Has abierto {od.Name} y recibido su contenido.", 1);
                consumir = true;
                break;
            }

            // otInvi=50: runas de teletransporte custom (faccionaria/cercana/donador). Instantáneas.
            case ObjType.RunaTransporte:
                UsarRunaTransporte(userIndex, u, slot, od);
                return;

            // otMochila=52: ampliar slots de inventario. Requiere un cambio estructural (arrays
            // por jugador, persistencia .chr, protocolo y UI del cliente Godot que hoy son de
            // MAX_INVENTORY_SLOTS fijo). Pendiente de diseño: por ahora se avisa y no se consume.
            case ObjType.Mochila:
                ServerPackets.ConsoleMsg(u.Conn, "Las mochilas aún no están habilitadas en este servidor.", 1);
                return;

            // otLlaveCofre=51: abrir cofres por nivel. Requiere definir cofres-con-nivel en el
            // mundo (no existen aún). Pendiente de contenido: por ahora se avisa y no se consume.
            case ObjType.LlaveCofre:
                ServerPackets.ConsoleMsg(u.Conn, "No hay ningún cofre que abrir aquí.", 1);
                return;

            default:
                return; // pasajes, herramientas: requieren crafting (TODO)
        }

        if (consumir)
        {
            item.Amount--;
            if (item.Amount <= 0)
            {
                item.ObjIndex = 0; item.Amount = 0; item.Equipped = false;
                if (u.Invent.NroItems > 0) u.Invent.NroItems--;
            }
            SendSlot(u, slot);
        }
    }

    /// <summary>
    /// Runa de teletransporte (InvUsuario.bas:1486): inicia un casteo de 6s. Al completarse
    /// (GameTimer) teletransporta al hogar (vivo) o al cementerio del hogar (muerto). Vivo no
    /// puede usarla en zona insegura (PK). La runa NO se consume.
    /// </summary>
    private static void UsarRuna(User u, byte slot)
    {
        if (u.CasteandoRuna > 0)
        { ServerPackets.ConsoleMsg(u.Conn, "Ya estás teletransportándote, espera a que termine.", 1); return; }

        // Preso: no puede escaparse de la cárcel con la runa.
        if (Jail.EstaPreso(u))
        { ServerPackets.ConsoleMsg(u.Conn, "¡No puedes teletransportarte mientras cumples tu condena!", 1); return; }

        if (u.flags.Meditando) { u.flags.Meditando = false; ServerPackets.MeditateToggle(u.Conn); Facciones.QuitarParticulaMeditacion(u); }

        // Vivo: no se puede usar en mapa PK (zona insegura).
        if (u.flags.Muerto == 0)
        {
            var mi = MapLoader.Get(u.Pos.Map)?.Info;
            if (mi != null && mi.Pk)
            { ServerPackets.ConsoleMsg(u.Conn, "No puedes usar la runa en una zona insegura.", 1); return; }
        }

        u.CasteandoRuna = 6;
        u.RunaSlot = slot;
        ServerPackets.RunaCastProgress(u.Conn, u.Char.CharIndex, 6, 6);
        ServerPackets.ConsoleMsg(u.Conn, "Tu cuerpo comienza a tomar forma... serás teletransportado en 6 segundos.", 1);
    }

    /// <summary>
    /// Fuego artificial (cañita/cohete/petardo): lanza la partícula del objeto (obj.dat "Particula")
    /// en el TILE donde está el personaje (no atada al char: si se mueve, la partícula queda en esa
    /// posición), de forma temporal, a todos los del mapa, y reproduce su sonido (Snd1). La partícula
    /// se borra sola tras DURACION segundos (EfectoTerrenoParticula: Time>1 = vida finita en segundos).
    /// </summary>
    private static void LanzarFuegoArtificial(User u, ObjData.Obj od)
    {
        const int DURACION = 3; // segundos que vive la partícula en el tile (luego se borra sola)
        byte px = (byte)u.Pos.X, py = (byte)u.Pos.Y;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.EfectoTerrenoParticula(o.Conn, (short)od.Particula, px, py, DURACION);
        }
        if (od.Snd1 > 0) BroadcastWaveArea(u, (short)od.Snd1);
    }

    // Ciudades neutrales candidatas para la "Runa Cercana" (eCiudad): se elige la del mismo
    // mapa-continente más próxima; si ninguna está en el mapa actual, se usa el Hogar/facción.
    private static readonly int[] _ciudadesNeutrales = { 3 /*Ullathorpe*/, 4 /*Banderbill*/, 7 /*Lindos*/, 2 /*Illiandor*/ };

    /// <summary>
    /// Runas de teletransporte custom (ObjType 50): destino según el nombre del objeto.
    ///  - "Faccionaria" → ciudad principal de la facción del jugador (Facciones.CiudadDeFaccion).
    ///  - "Cercana"     → ciudad neutral más cercana (mismo mapa) o, si no, el hogar.
    ///  - resto (Donador) → hogar del jugador, con cooldown de 1 hora.
    /// No preso ni muerto. La faccionaria y la cercana NO funcionan en zona insegura (PK);
    /// la Donador SÍ se puede usar en insegura (su único límite es el cooldown de 60 min).
    /// El teletransporte es instantáneo y la runa NO se consume.
    /// </summary>
    private static void UsarRunaTransporte(int userIndex, User u, byte slot, ObjData.Obj od)
    {
        if (u.flags.Muerto == 1) return; // DeadCheck
        if (Jail.EstaPreso(u))
        { ServerPackets.ConsoleMsg(u.Conn, "¡No puedes teletransportarte mientras cumples tu condena!", 1); return; }

        string nombre = (od.Name ?? "").ToLowerInvariant();
        bool esDonador = !(nombre.Contains("faccionaria") || nombre.Contains("faccionario") || nombre.Contains("cercana"));

        // Zona insegura (PK): bloquea faccionaria/cercana; la Donador SÍ puede usarse en insegura.
        if (!esDonador)
        {
            var mi = MapLoader.Get(u.Pos.Map)?.Info;
            if (mi != null && mi.Pk)
            { ServerPackets.ConsoleMsg(u.Conn, "No puedes usar la runa en una zona insegura.", 1); return; }
        }

        if (u.flags.Meditando)
        { u.flags.Meditando = false; ServerPackets.MeditateToggle(u.Conn); Facciones.QuitarParticulaMeditacion(u); }

        int ciudad;

        if (nombre.Contains("faccionaria") || nombre.Contains("faccionario"))
        {
            ciudad = Facciones.CiudadDeFaccion(u);
        }
        else if (nombre.Contains("cercana"))
        {
            ciudad = CiudadNeutralMasCercana(u);
        }
        else // Runa de Transporte (Donador) u otras: hogar, con cooldown de 1 hora.
        {
            long now = Environment.TickCount64;
            if (u.RunaDonadorNextAt > now)
            {
                long restan = (u.RunaDonadorNextAt - now) / 60000;
                ServerPackets.ConsoleMsg(u.Conn, $"Debes esperar {restan + 1} minuto(s) para volver a usar esta runa.", 1);
                return;
            }
            u.RunaDonadorNextAt = now + 3600000; // 1 hora
            ciudad = u.Hogar > 0 ? u.Hogar : Facciones.CiudadDeFaccion(u);
        }

        var c = CityData.Get(ciudad);
        if (c.Map <= 0)
        { ServerPackets.ConsoleMsg(u.Conn, "No se pudo determinar el destino de la runa.", 1); return; }

        Movement.WarpUser(userIndex, c.Map, c.X, c.Y);
        ServerPackets.ConsoleMsg(u.Conn, "Has sido teletransportado.", 1);
    }

    // Ciudad neutral más cercana en el mismo mapa que el jugador; si ninguna coincide, el hogar/facción.
    private static int CiudadNeutralMasCercana(User u)
    {
        int mejor = -1; double mejorDist = double.MaxValue;
        foreach (int cid in _ciudadesNeutrales)
        {
            var c = CityData.Get(cid);
            if (c.Map != u.Pos.Map) continue;
            double dx = c.X - u.Pos.X, dy = c.Y - u.Pos.Y;
            double d = dx * dx + dy * dy;
            if (d < mejorDist) { mejorDist = d; mejor = cid; }
        }
        if (mejor > 0) return mejor;
        return u.Hogar > 0 ? u.Hogar : Facciones.CiudadDeFaccion(u);
    }

    /// <summary>
    /// Aprender un hechizo desde un pergamino (otPergaminos). Port 1:1 de la validación de
    /// UsarObjeto (InvUsuario.bas:1994) + AgregarHechizo (modHechizos.bas:247):
    /// requiere maná (MaxMAN>0), ser pergamino especial de guerrero (hechizos 115-120 y clase
    /// Guerrero/Gladiador/Mercenario) o ser GM. Si no lo tiene ya y hay slot libre, lo agrega,
    /// reenvía el slot (ChangeSpellSlot) y consume 1 pergamino.
    /// </summary>
    private static void AprenderHechizo(User u, byte slot, ObjData.Obj od)
    {
        int hIndex = od.HechizoIndex;
        if (hIndex <= 0) return;

        // ¿Pergamino especial para guerreros? (hechizos 115-120, clases Guerrero/Gladiador/Mercenario)
        bool esPergaminoEspecial = hIndex >= 115 && hIndex <= 120
            && (u.Clase == 3 || u.Clase == 8 || u.Clase == 17);  // Guerrero=3, Gladiador=8, Mercenario=17
        bool esGm = u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO;

        if (!(u.Stats.MaxMAN > 0 || esPergaminoEspecial || esGm))
        {
            ServerPackets.ConsoleMsg(u.Conn, "No tienes el conocimiento mágico para aprender hechizos.", 1);
            return;
        }

        // AgregarHechizo: ¿ya lo tiene? (TieneHechizo)
        for (int k = 1; k <= Constants.MAXUSERHECHIZOS; k++)
            if (u.Stats.UserHechizos[k] == hIndex)
            { ServerPackets.ConsoleMsg(u.Conn, "Ya tienes ese hechizo.", 1); return; }

        // Buscar un slot vacío.
        int j;
        for (j = 1; j <= Constants.MAXUSERHECHIZOS; j++)
            if (u.Stats.UserHechizos[j] == 0) break;

        if (j > Constants.MAXUSERHECHIZOS)
        { ServerPackets.ConsoleMsg(u.Conn, "No tienes más espacio para hechizos.", 1); return; }

        u.Stats.UserHechizos[j] = (short)hIndex;
        ServerPackets.ChangeSpellSlot(u.Conn, (byte)j, (short)hIndex, SpellData.GetName(hIndex));

        // Quitar 1 pergamino del inventario (QuitarUserInvItem).
        ref var item = ref u.Invent.Object[slot];
        item.Amount--;
        if (item.Amount <= 0)
        {
            item.ObjIndex = 0; item.Amount = 0; item.Equipped = false;
            if (u.Invent.NroItems > 0) u.Invent.NroItems--;
        }
        SendSlot(u, slot);
    }

    /// <summary>
    /// HandleDropDestroy (Protocol.bas:2681) — tirar y DESTRUIR 'amount' del slot (no cae al piso).
    /// No si navegando/muerto/montando/comerciando. Items faccionarios (Real/Caos/Milicia) nunca.
    /// NoSeCae bloquea salvo Permanente==2 (newbies/mapas/runas confirmados). 1:1 con VB6.
    /// </summary>
    public static void DropDestroy(int userIndex, byte slot, int amount)
    {
        var u = UserListManager.UserList[userIndex];
        if (!u.flags.UserLogged) return;
        if (u.flags.Navegando || u.flags.Muerto == 1 || u.flags.Montando != 0 || u.Comerciando) return;
        if (slot < 1 || slot > Constants.MAX_INVENTORY_SLOTS) return;

        ref var it = ref u.Invent.Object[slot];
        if (it.ObjIndex == 0) return;

        var od = ObjData.Get(it.ObjIndex);
        if (od.Real > 0 || od.Caos > 0 || od.Milicia > 0)
        { ServerPackets.ConsoleMsg(u.Conn, "No puedes destruir ese objeto.", 1); return; }
        if (od.NoSeCae > 0 && od.Permanente != 2)
        { ServerPackets.ConsoleMsg(u.Conn, "No puedes destruir ese objeto.", 1); return; }

        int quita = Math.Min(Math.Max(1, amount), it.Amount);
        QuitarUserInvItem(u, slot, quita); // desequipa si se destruye el stack equipado
        SendSlot(u, slot);
    }

    /// <summary>TieneObjetos: cuenta cuántas unidades del ObjIndex tiene el usuario en el inventario.</summary>
    public static int ContarObjeto(User u, int objIndex)
    {
        int n = 0;
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == objIndex) n += u.Invent.Object[s].Amount;
        return n;
    }

    /// <summary>QuitarObjetos: descuenta 'cant' unidades del ObjIndex (varios slots) y refresca cada slot tocado.</summary>
    public static void QuitarObjetos(User u, int objIndex, int cant)
    {
        for (byte s = 1; s <= Constants.MAX_INVENTORY_SLOTS && cant > 0; s++)
        {
            ref var it = ref u.Invent.Object[s];
            if (it.ObjIndex != objIndex) continue;
            int quita = Math.Min(cant, it.Amount);
            it.Amount -= quita; cant -= quita;
            if (it.Amount <= 0)
            {
                it.ObjIndex = 0; it.Amount = 0; it.Equipped = false;
                if (u.Invent.NroItems > 0) u.Invent.NroItems--;
            }
            SendSlot(u, s);
        }
    }

    // VB6: herramientas de anillo y su skill (InvUsuario.bas, Acciones.bas)
    private static byte GetToolSkill(short objIndex) => objIndex switch
    {
        881 or 138 => 18, // CAÑA_PESCA, RED_PESCA → Pesca
        187 => 19,        // PIQUETE_MINERO → Mineria
        127 => 20,        // HACHA_LEÑADOR → Talar
        885 => 20,        // TIJERAS → Talar/Botanica
        _ => 0
    };

    /// <summary>Aplica el efecto de una poción según SubTipo (InvUsuario.bas:1709-1869). True = consumir.</summary>
    /// <summary>Marca el buff temporal de fuerza/agilidad por poción: arma el temporizador que la
    /// rutina de expiración (Combat.RestaurarAtributos) usa para devolver los atributos al base
    /// (UserAtributosBackUP) cuando vence DuracionEfecto (General.bas DuracionPociones). El BackUP
    /// se mantiene desde el login, por eso no se re-respalda al beber pociones encadenadas.</summary>
    private static void AplicarEfectoTemporalAtributo(User u, ObjData.Obj od)
    {
        u.flags.TomoPocion = true;
        double dur = od.DuracionEfecto > 0 ? od.DuracionEfecto / 1000.0 : 50.0; // DuracionEfecto en ms (obj.dat=50000 → 50s)
        u.flags.AtributoEfectoExpira = Environment.TickCount64 / 1000.0 + dur;
    }

    private static bool UsarPocion(int userIndex, User u, ObjData.Obj od)
    {
        int mod() => od.MaxModificador >= od.MinModificador && od.MaxModificador > 0
            ? _rng.Next(od.MinModificador, od.MaxModificador + 1) : od.MinModificador;

        BroadcastWaveArea(u, Sounds.BEBER); // SND_BEBER=135 al tomar la poción (InvUsuario.bas)

        switch (od.SubTipo)
        {
            case 1: // Agilidad (poción amarilla): sube temporalmente; DuracionEfecto expira y restaura (General.bas DuracionPociones)
                u.Stats.UserAtributos[2] = (byte)Math.Min(40, u.Stats.UserAtributos[2] + mod());
                AplicarEfectoTemporalAtributo(u, od);
                ServerPackets.UpdateDexterity(u.Conn, u.Stats.UserAtributos[2]);
                break;
            case 2: // Fuerza (poción verde): ídem
                u.Stats.UserAtributos[1] = (byte)Math.Min(40, u.Stats.UserAtributos[1] + mod());
                AplicarEfectoTemporalAtributo(u, od);
                ServerPackets.UpdateStrenght(u.Conn, u.Stats.UserAtributos[1]);
                break;
            case 3: // Roja → vida
                u.Stats.MinHP = (short)Math.Min(u.Stats.MaxHP, u.Stats.MinHP + mod());
                ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP);
                break;
            case 4: // Azul → maná (fórmula exacta VB6: MinMAN + Porcentaje(MaxMAN,4) + ELV\2 + 40/ELV)
                int elv = Math.Max(1, (int)u.Stats.ELV);
                int rec = u.Stats.MaxMAN * 4 / 100 + elv / 2 + 40 / elv;
                u.Stats.MinMAN = (short)Math.Min(u.Stats.MaxMAN, u.Stats.MinMAN + rec);
                ServerPackets.UpdateMana(u.Conn, u.Stats.MinMAN);
                break;
            case 5: // Lanza hechizo sobre uno mismo (InvUsuario.bas:1798 → HechizoEstadoUsuario):
                    // remover parálisis, curar veneno, invisibilidad, desencantar. Si el efecto
                    // no aplica (p.ej. no estás paralizado), la poción NO se consume.
                if (od.LanzaHechizo <= 0) { u.flags.Envenenado = 0; ServerPackets.ConsoleMsg(u.Conn, "Te sientes mejor.", 3); break; }
                return Combat.AplicarHechizoPocion(userIndex, od.LanzaHechizo);
            case 6: // Scroll Intermundia: teletransporta a Intermundia (InvUsuario.bas:1815).
            {
                var inter = CityData.Get(15); // cIntermundia
                Movement.WarpUser(userIndex, inter.Map, inter.X, inter.Y);
                break;
            }
            case 9: // Nareth: partícula 23 permanente sobre el personaje (InvUsuario.bas:1849).
                if (u.Char.ParticulaFx != 0) return false; // ya tiene una partícula activa → no consumir
                u.Char.ParticulaFx = 23;
                for (int i = 1; i <= UserListManager.LastUser; i++)
                {
                    var o = UserListManager.UserList[i];
                    if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                        ServerPackets.EfectoCharParticula(o.Conn, u.Char.CharIndex, 23, -1, false);
                }
                break;
            case 13: // Adquirir créditos de donación (InvUsuario.bas:1854).
                if (od.CuantoAumento <= 0) return false;
                u.CreditoDonador += od.CuantoAumento;
                GuardarCreditosCuenta(u);
                ServerPackets.UpdateCreditos(u.Conn, u.CreditoDonador);
                ServerPackets.ConsoleMsg(u.Conn, $"¡Has obtenido {od.CuantoAumento} créditos! Total: {u.CreditoDonador}.", 3);
                break;
            case 7: // Cambio de cara (InvUsuario.bas:1822 → ChangeHead)
                if (u.flags.Navegando) { ServerPackets.LocaleMsg(u.Conn, 20); return false; }
                if (u.flags.Montando == 1) { ServerPackets.LocaleMsg(u.Conn, 21); return false; }
                ChangeHead(u);
                break;
            case 8: // Cambio de sexo (InvUsuario.bas:1836 → DarCuerpoNuevo)
                if (u.flags.Navegando) { ServerPackets.LocaleMsg(u.Conn, 20); return false; }
                if (u.flags.Montando == 1) { ServerPackets.LocaleMsg(u.Conn, 21); return false; }
                DarCuerpoNuevo(u);
                break;
            default:
                return false; // subtipos no usados
        }
        return true;
    }

    /// <summary>Devuelve una cabeza aleatoria válida para el género+raza (rangos hardcodeados en
    /// InvUsuario.bas ChangeHead/DarCuerpoNuevo). genero: 1=Hombre, 2=Mujer; raza: 1=Humano..6=Orco.</summary>
    private static int RandomHead(byte genero, byte raza)
    {
        (int lo, int hi) = genero == 1 // Hombre
            ? raza switch
            {
                1 => (1, 30),     // Humano
                2 => (101, 120),  // Elfo
                3 => (201, 213),  // Drow
                4 => (401, 410),  // gnomo
                5 => (301, 313),  // enano
                6 => (501, 514),  // Orco
                _ => (1, 30),
            }
            : raza switch // Mujer
            {
                1 => (70, 80),    // Humano
                2 => (170, 189),  // Elfo
                3 => (270, 278),  // Drow
                4 => (470, 481),  // gnomo
                5 => (370, 373),  // enano
                6 => (570, 573),  // Orco
                _ => (70, 80),
            };
        return _rng.Next(lo, hi + 1);
    }

    /// <summary>ChangeHead (InvUsuario.bas:2143) 1:1: cambia la cabeza al azar según género+raza,
    /// actualiza OrigChar y difunde CharacterChange + FX 30.</summary>
    private static void ChangeHead(User u)
    {
        int newHead = RandomHead(u.Genero, u.raza);
        u.Char.Head = (short)newHead;
        u.OrigChar.Head = (short)newHead;
        BroadcastCharChange(u);
        BroadcastFXArea(u, u.Char.CharIndex, 30, 0);
    }

    /// <summary>DarCuerpoNuevo (InvUsuario.bas:2214) 1:1: invierte el género, desequipa todo,
    /// asigna cuerpo/cabeza nuevos y deja al personaje desnudo. Difunde CharacterChange + FX 30.</summary>
    private static void DarCuerpoNuevo(User u)
    {
        var inv = u.Invent;

        // Desequipar todo el equipo (mismo orden que el VB6) y limpiar auras.
        if (inv.ArmourEqpObjIndex > 0) { Desequipar(u, inv.ArmourEqpSlot); u.Char.Body_Aura = 0; }
        if (inv.NudiEqpObjIndex > 0) { Desequipar(u, inv.NudiEqpSlot); u.Char.Arma_Aura = 0; }
        if (inv.WeaponEqpObjIndex > 0)
        {
            Desequipar(u, inv.WeaponEqpSlot);
            BroadcastAuraArea(u, u.Char.CharIndex, 0, 1);
            u.Char.Arma_Aura = 0;
        }
        if (inv.CascoEqpObjIndex > 0) { Desequipar(u, inv.CascoEqpSlot); u.Char.Head_Aura = 0; }
        if (inv.AnilloEqpSlot > 0) Desequipar(u, inv.AnilloEqpSlot);
        if (inv.MunicionEqpObjIndex > 0) Desequipar(u, inv.MunicionEqpSlot);
        if (inv.MagicIndex > 0) { Desequipar(u, (byte)inv.MagicSlot); u.Char.Anillo_Aura = 0; }
        if (inv.EscudoEqpObjIndex > 0) { Desequipar(u, inv.EscudoEqpSlot); u.Char.Escudo_Aura = 0; }

        int newHead, newBody;
        if (u.Genero == 1) // Hombre → Mujer
        {
            u.Genero = 2;
            (newHead, newBody) = u.raza switch
            {
                1 => (RandomHead(2, 1), 1),     // Humano
                2 => (RandomHead(2, 2), 2),     // Elfo
                3 => (RandomHead(2, 3), 3),     // Drow
                4 => (RandomHead(2, 4), 138),   // gnomo
                5 => (RandomHead(2, 5), 138),   // enano
                6 => (RandomHead(2, 6), 253),   // Orco
                _ => (RandomHead(2, 1), 1),
            };
        }
        else // Mujer → Hombre
        {
            u.Genero = 1;
            (newHead, newBody) = u.raza switch
            {
                1 => (RandomHead(1, 1), 1),     // Humano
                2 => (RandomHead(1, 2), 2),     // Elfo
                3 => (RandomHead(1, 3), 3),     // Drow
                4 => (RandomHead(1, 4), 52),    // gnomo
                5 => (RandomHead(1, 5), 52),    // enano
                6 => (RandomHead(1, 6), 252),   // Orco
                _ => (RandomHead(1, 1), 1),
            };
        }

        u.OrigChar.body = (short)newBody;
        u.Char.ShieldAnim = 0;  // NingunEscudo
        u.Char.WeaponAnim = 0;  // NingunArma
        u.Char.CascoAnim = 0;   // NingunCasco
        u.Char.Head = (short)newHead;
        u.OrigChar.Head = (short)newHead;

        DarCuerpoDesnudo(u);
        BroadcastCharChange(u);
        BroadcastFXArea(u, u.Char.CharIndex, 30, 0);
    }

    /// <summary>Difunde AuraToChar al mapa (equiv. SendData ToPCArea + PrepareMessageAuraToChar).</summary>
    private static void BroadcastAuraArea(User u, short charIndex, byte aura, byte slot)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.AuraToChar(o.Conn, charIndex, aura, slot);
        }
    }

    /// <summary>Persiste los créditos de donación en la cuenta (.cnt [cuenta] Creditos). Compartidos por cuenta.</summary>
    private static void GuardarCreditosCuenta(User u)
    {
        if (string.IsNullOrEmpty(u.Account)) return;
        try
        {
            string cnt = System.IO.Path.Combine(AccountManager.AccountPath, u.Account.ToUpperInvariant() + ".cnt");
            var doc = new IniDocument(cnt);
            if (!doc.Loaded) return;
            doc.Set(u.Account.ToUpperInvariant(), "Creditos", u.CreditoDonador.ToString());
            doc.Save(cnt);
        }
        catch { }
    }

    // --- helpers de equipo ---

    private static void ToggleEquip(User u, ref UserObj item, byte slot,
        ref short eqpObjIndex, ref byte eqpSlot, Action<bool> applyAnim)
    {
        if (item.Equipped)
        {
            item.Equipped = false;
            eqpObjIndex = 0; eqpSlot = 0;
            applyAnim(false);
        }
        else
        {
            // Desequipar el que estuviera en esa ranura.
            if (eqpSlot >= 1 && eqpSlot <= Constants.MAX_INVENTORY_SLOTS)
            {
                u.Invent.Object[eqpSlot].Equipped = false;
                SendSlot(u, eqpSlot);
            }
            item.Equipped = true;
            eqpObjIndex = item.ObjIndex; eqpSlot = slot;
            applyAnim(true);
        }
    }

    /// <summary>
    /// DarCuerpoDesnudo (General.bas) — body desnudo por género/raza. 1:1 con la tabla VB6.
    /// No cambia el body si navega/monta/metamorfoseado. eGenero: Hombre=1, Mujer=2.
    /// eRaza: Humano=1, Elfo=2, Drow=3, gnomo=4, enano=5, Orco=6.
    /// </summary>
    internal static void DarCuerpoDesnudo(User u)
    {
        if (u.flags.Navegando || u.flags.Montando == 1) return;
        if (u.flags.Metamorfoseado == 1) return;

        short cuerpo = (short)(u.Genero == 1 // Hombre
            ? u.raza switch
            {
                1 => 21,   // Humano
                2 => 21,   // Elfo
                3 => 32,   // Drow
                4 => 53,   // gnomo
                5 => 53,   // enano
                6 => 248,  // Orco
                _ => 21,
            }
            : u.raza switch // Mujer
            {
                1 => 39,   // Humano
                2 => 39,   // Elfo
                3 => 40,   // Drow
                4 => 60,   // gnomo
                5 => 60,   // enano
                6 => 249,  // Orco
                _ => 39,
            });

        u.Char.body = cuerpo;
        u.flags.Desnudo = 1;
    }

    private static void BroadcastCharChange(User u)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.CharacterChange(o.Conn, u.Char.CharIndex, u.Char.body, u.Char.Head,
                    u.Char.heading, u.Char.WeaponAnim, u.Char.ShieldAnim, u.Char.CascoAnim, 0, 0, 0);
        }
    }

    /// <summary>Agrega items al inventario (usada por comandos GM como /ci).</summary>
    public static bool AddItemToInventory(User u, short objIndex, int amount)
    {
        int slot = FindSlotForObject(u, objIndex);
        if (slot == 0) return false; // inventario lleno

        if (u.Invent.Object[slot].ObjIndex == objIndex)
            u.Invent.Object[slot].Amount += amount;
        else
        {
            u.Invent.Object[slot].ObjIndex = objIndex;
            u.Invent.Object[slot].Amount = amount;
            u.Invent.Object[slot].Equipped = false;
            if (u.Invent.NroItems >= 0) u.Invent.NroItems++;
        }

        SendSlot(u, slot);
        return true;
    }

    /// <summary>TirarItemAlPiso (InvUsuario.bas): deja el objeto en el suelo del tile y lo difunde al mapa.</summary>
    private static void DropItemToFloor(WorldPos pos, short objIndex, int amount)
    {
        var map = MapLoader.Get(pos.Map);
        if (map == null) return;
        int x = pos.X, y = pos.Y;
        if (map.FloorObj[x, y] != 0 && map.FloorObj[x, y] != objIndex) return; // ya hay otro objeto
        map.FloorObj[x, y] = objIndex;
        map.FloorAmount[x, y] += amount;
        AreaVisibility.ObjectAppeared(pos.Map, x, y, objIndex, map.FloorAmount[x, y]);
    }

    // --- helpers ---

    private static int FindSlotForObject(User u, short objIndex)
    {
        // 1) slot existente con el mismo objeto (apilar)
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == objIndex) return s;
        // 2) primer slot vacío
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == 0) return s;
        return 0;
    }

    private static void SendSlot(User u, int slot)
    {
        var o = u.Invent.Object[slot];
        // PuedeUsar: el cliente pinta el item en rojo si es 0 (no usable por clase/raza/nivel/sexo).
        byte puedeUsar = 1;
        if (o.ObjIndex > 0) puedeUsar = PuedeUsarObjeto(u, ObjData.Get(o.ObjIndex), out _) ? (byte)1 : (byte)0;
        ServerPackets.ChangeInventorySlot(u.Conn, (byte)slot, o.ObjIndex, o.Amount, o.Equipped, 0f, puedeUsar);
    }

    /// <summary>
    /// Editor de objetos GM: re-envía los slots de inventario que contienen objIndex a TODOS
    /// los usuarios online. Refresca en tiempo real el flag "puede usar" (rojo) si el GM
    /// cambió requisitos (MinELV, clases/razas prohibidas, género, facción...).
    /// </summary>
    public static void RefreshObjEverywhere(short objIndex)
    {
        if (objIndex <= 0) return;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u?.flags.UserLogged != true || u.Conn == null) continue;
            for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
                if (u.Invent.Object[s].ObjIndex == objIndex) SendSlot(u, s);
        }
    }

    /// <summary>
    /// Re-envía TODOS los slots de inventario de TODOS los usuarios online. Lo usa la recarga
    /// global del editor de objetos: tras releer obj.dat de disco, refresca el flag "puede usar",
    /// nombres, gráficos y stats de cada item en mano sin necesidad de reloguear.
    /// </summary>
    public static void RefreshAllInventories()
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u?.flags.UserLogged != true || u.Conn == null) continue;
            for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
                if (u.Invent.Object[s].ObjIndex > 0) SendSlot(u, s);
        }
    }

    /// <summary>
    /// HandleSwapObjects (Protocol.bas:18019) 1:1. Intercambia dos slots del inventario y
    /// reasigna los punteros de equipo (*EqpSlot) que apuntaban a alguno de los dos slots,
    /// para que el item equipado siga señalando el slot correcto tras el intercambio.
    /// </summary>
    public static void SwapObjects(int userIndex, byte slot1, byte slot2)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged) return;
        if (slot1 < 1 || slot1 > Constants.MAX_INVENTORY_SLOTS) return;
        if (slot2 < 1 || slot2 > Constants.MAX_INVENTORY_SLOTS) return;

        var inv = u.Invent;

        // Reasignar cada puntero de equipo si apuntaba a uno de los dos slots (1:1 VB6).
        SwapEqpSlot(ref inv.AnilloEqpSlot, slot1, slot2);
        SwapEqpSlot(ref inv.ArmourEqpSlot, slot1, slot2);
        SwapEqpSlot(ref inv.BarcoSlot, slot1, slot2);
        SwapEqpSlot(ref inv.CascoEqpSlot, slot1, slot2);
        SwapEqpSlot(ref inv.EscudoEqpSlot, slot1, slot2);
        SwapEqpSlot(ref inv.MunicionEqpSlot, slot1, slot2);
        SwapEqpSlot(ref inv.NudiEqpSlot, slot1, slot2);
        SwapEqpSlot(ref inv.WeaponEqpSlot, slot1, slot2);
        SwapEqpSlot(ref inv.MonturaSlot, slot1, slot2);
        // MagicSlot es short en VB6/C#; usar variante propia.
        if (inv.MagicSlot == slot1) inv.MagicSlot = slot2;
        else if (inv.MagicSlot == slot2) inv.MagicSlot = slot1;

        // Intercambio propiamente dicho.
        var tmp = inv.Object[slot1];
        inv.Object[slot1] = inv.Object[slot2];
        inv.Object[slot2] = tmp;

        // Actualizar solo los 2 slots cambiados.
        SendSlot(u, slot1);
        SendSlot(u, slot2);
    }

    private static void SwapEqpSlot(ref byte eqp, byte slot1, byte slot2)
    {
        if (eqp == slot1) eqp = slot2;
        else if (eqp == slot2) eqp = slot1;
    }
}
