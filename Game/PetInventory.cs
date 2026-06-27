namespace ServidorCS.Game;

using ServidorCS.Network;

/// <summary>
/// Mochila de la mascota compañera (NUEVO, no VB6): la mascota funciona como MULA — carga ítems
/// que no te entran en el inventario y puede llevárselos al banco de la ciudad ("regresar al
/// hogar"), que es la parte que le da sentido: no es un cofre portátil, es un viaje de ida.
///
/// Decisiones de diseño, para no re-discutirlas:
///
///  1. **12 slots** (`Constants.MAX_PETINVENTORY_SLOTS`), contra los 25 del inventario y los 80 de
///     la bóveda. Es acarreo, no una segunda bóveda.
///  2. **La mascota tiene que estar invocada y viva** para cargar o descargar. Cargarle cosas a
///     una mascota que no está en el mapa sería un baúl mágico; así hay que llamarla primero.
///  3. **La mochila es del PERSONAJE, no de la instancia** (vive en el `User`, sección
///     `[MASCOTA_INV]` del `.chr`). Si la mascota muere o se desinvoca, lo que llevaba **no se
///     pierde**: sigue ahí cuando la vuelvas a invocar. Perder ítems por un despawn accidental
///     (zona segura, cambio de mapa, desconexión) sería un castigo que nadie entendería.
///  4. **"Regresar al hogar" es un VIAJE A PIE, no un teletransporte**: la mascota se va
///     caminando (`NpcManager.IniciarViajeAlHogar`) y el depósito en la bóveda ocurre recién
///     cuando llega (`LlegoAlHogar`). Si se resolviera al apretar el botón, la caminata sería
///     una animación decorativa sobre algo ya hecho. La ganancia sigue siendo vaciar la mochila
///     sin volver caminando vos; el costo es quedarte sin mascota hasta reinvocarla.
///  5. **No entra oro** — el oro ya tiene su propio camino al banco (`Bank.DepositGold`).
/// </summary>
public static class PetInventory
{
    /// <summary>La mascota tiene que estar invocada, viva y en el mapa del dueño para poder
    /// cargarla o descargarla. Devuelve la instancia, o null (con aviso) si no se puede.</summary>
    private static NpcManager.NpcInstance MascotaUsable(User u, out string motivo)
    {
        motivo = null;
        if (u.PetTipo == 0) { motivo = "No tenés ninguna mascota compañera."; return null; }
        if (u.PetDead) { motivo = "Tu mascota está muerta: llevala a la Veterinaria."; return null; }
        if (u.PetCharIndex <= 0) { motivo = "Invocá a tu mascota para darle o sacarle cosas."; return null; }
        var pet = NpcManager.NpcByCharIndex(u.Pos.Map, u.PetCharIndex);
        if (pet == null || pet.Dead) { motivo = "Tu mascota no está acá."; return null; }
        return pet;
    }

    /// <summary>Primer slot de la mochila donde apilar/poner 'objIndex' (0 = mochila llena).
    /// Mismo criterio que Bank.FindBankSlot: primero apilar, después el primer hueco.</summary>
    private static int SlotParaObjeto(User u, short objIndex)
    {
        for (int s = 1; s <= Constants.MAX_PETINVENTORY_SLOTS; s++)
            if (u.PetInvent.Object[s].ObjIndex == objIndex) return s;
        for (int s = 1; s <= Constants.MAX_PETINVENTORY_SLOTS; s++)
            if (u.PetInvent.Object[s].ObjIndex == 0) return s;
        return 0;
    }

    private static int SlotInventarioPara(User u, short objIndex)
    {
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == objIndex) return s;
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
            if (u.Invent.Object[s].ObjIndex == 0) return s;
        return 0;
    }

    /// <summary>Inventario del jugador → mochila de la mascota.</summary>
    public static void Guardar(User u, byte invSlot, int cantidad)
    {
        if (u?.Conn == null || cantidad <= 0) return;
        if (invSlot < 1 || invSlot > Constants.MAX_INVENTORY_SLOTS) return;
        var pet = MascotaUsable(u, out string motivo);
        if (pet == null) { ServerPackets.ConsoleMsg(u.Conn, motivo, 1); return; }
        // Ya se está yendo: la mochila queda cerrada hasta que llegue. Si no, se podría
        // vaciar en pleno viaje y el "viaje" no significaría nada.
        if (pet.YendoAlHogar)
        { ServerPackets.ConsoleMsg(u.Conn, "Tu mascota ya está volviendo al hogar.", 1); return; }

        ref var src = ref u.Invent.Object[invSlot];
        if (src.ObjIndex == 0) return;
        if (cantidad > src.Amount) cantidad = src.Amount;

        int slot = SlotParaObjeto(u, src.ObjIndex);
        if (slot == 0)
        { ServerPackets.ConsoleMsg(u.Conn, "La mochila de tu mascota está llena.", 1); return; }

        short obj = src.ObjIndex;
        if (u.PetInvent.Object[slot].ObjIndex == obj) u.PetInvent.Object[slot].Amount += cantidad;
        else
        {
            u.PetInvent.Object[slot].ObjIndex = obj;
            u.PetInvent.Object[slot].Amount = cantidad;
            u.PetInvent.NroItems++;
        }
        // QuitarUserInvItem desequipa solo si hacía falta (el mismo camino que usa el banco).
        Inventory.QuitarUserInvItem(u, invSlot, cantidad);

        ServerPackets.ChangeInventorySlot(u.Conn, u, invSlot);
        EnviarMochila(u);
    }

    /// <summary>Mochila de la mascota → inventario del jugador.</summary>
    public static void Sacar(User u, byte petSlot, int cantidad)
    {
        if (u?.Conn == null || cantidad <= 0) return;
        if (petSlot < 1 || petSlot > Constants.MAX_PETINVENTORY_SLOTS) return;
        var pet = MascotaUsable(u, out string motivo);
        if (pet == null) { ServerPackets.ConsoleMsg(u.Conn, motivo, 1); return; }
        // Ya se está yendo: la mochila queda cerrada hasta que llegue. Si no, se podría
        // vaciar en pleno viaje y el "viaje" no significaría nada.
        if (pet.YendoAlHogar)
        { ServerPackets.ConsoleMsg(u.Conn, "Tu mascota ya está volviendo al hogar.", 1); return; }

        ref var src = ref u.PetInvent.Object[petSlot];
        if (src.ObjIndex == 0) return;
        if (cantidad > src.Amount) cantidad = src.Amount;

        int invSlot = SlotInventarioPara(u, src.ObjIndex);
        if (invSlot == 0)
        { ServerPackets.ConsoleMsg(u.Conn, "No tenés espacio en el inventario.", 1); return; }

        if (u.Invent.Object[invSlot].ObjIndex == src.ObjIndex) u.Invent.Object[invSlot].Amount += cantidad;
        else
        {
            u.Invent.Object[invSlot].ObjIndex = src.ObjIndex;
            u.Invent.Object[invSlot].Amount = cantidad;
            u.Invent.Object[invSlot].Equipped = false;
            u.Invent.NroItems++;
        }

        src.Amount -= cantidad;
        if (src.Amount <= 0)
        {
            src.ObjIndex = 0; src.Amount = 0;
            if (u.PetInvent.NroItems > 0) u.PetInvent.NroItems--;
        }

        ServerPackets.ChangeInventorySlot(u.Conn, u, (byte)invSlot);
        EnviarMochila(u);
    }

    /// <summary>
    /// "Regresar al hogar": la mascota se va, y lo que llevaba encima aparece en la bóveda del
    /// jugador. Devuelve la cantidad de slots depositados.
    ///
    /// Lo que NO entra en la bóveda (llena) se queda en la mochila y la mascota NO se va: es
    /// preferible que el viaje no salga a que los ítems desaparezcan. El aviso lo dice.
    /// </summary>
    public static void RegresarAlHogar(User u)
    {
        if (u?.Conn == null) return;
        var pet = MascotaUsable(u, out string motivo);
        if (pet == null) { ServerPackets.ConsoleMsg(u.Conn, motivo, 1); return; }
        if (pet.YendoAlHogar)
        { ServerPackets.ConsoleMsg(u.Conn, "Tu mascota ya está volviendo al hogar.", 1); return; }

        string nombre = !string.IsNullOrEmpty(u.PetNombre) ? u.PetNombre : pet.Name;

        // 🔴 NO se teletransporta: se VA CAMINANDO. El depósito en la bóveda ocurre cuando llega
        // (LlegoAlHogar), no ahora — si se resolviera acá, la caminata sería una animación
        // decorativa sobre algo que ya pasó, y encima el jugador podría seguir sacando cosas de la
        // mochila mientras "viaja".
        var ciudad = CityData.Get(u.Hogar);
        var (dx, dy) = DestinoDeSalida(u.Pos.Map, pet.X, pet.Y, ciudad);
        NpcManager.IniciarViajeAlHogar(pet, dx, dy, SEGUNDOS_TOPE_VIAJE);

        ServerPackets.ConsoleMsg(u.Conn,
            $"{nombre} emprende el regreso al hogar. Dejará lo que lleva en tu bóveda al llegar.", 1);
    }

    /// <summary>Cuánto puede durar el viaje antes de darlo por terminado igual (y hacer el efecto).
    /// Es una red de
    /// seguridad contra quedarse trabada detrás de una pared: el viaje TIENE que terminar, si no
    /// la mochila queda en el limbo y el jugador sin mascota.</summary>
    private const double SEGUNDOS_TOPE_VIAJE = 6.0;

    /// <summary>Largo del tramo que camina antes de desvanecerse. Corto a propósito: son ~6
    /// pasos (unos 2 s al ritmo de IA de 0,38 s) — se ve que se va caminando y el efecto llega
    /// enseguida. Con un tramo largo el jugador apretaba el botón y no pasaba nada visible.</summary>
    private const int TILES_VIAJE = 6;

    /// <summary>Cuánto descansa la mascota en el hogar antes de poder volver a invocarla. 5
    /// minutos: es el precio de haber mandado la carga a la bóveda sin caminar vos.</summary>
    public const double SEGUNDOS_DESCANSO_HOGAR = 300.0;

    /// <summary>FX 46 = el "vanish" de siempre (invisibilidad); en el motor nuevo cae en el preset
    /// `vanish`. Sonido 3 = el del teletransporte.</summary>
    private const short FX_DESAPARECER = 46, SND_DESAPARECER = 3;

    /// <summary>
    /// Hacia dónde camina para "irse". Es un tramo CORTO —
    ///  <see cref="TILES_VIAJE"/> baldosas— en la dirección de su ciudad, no el borde del mapa:
    /// lo que el jugador tiene que ver es a la mascota **dando unos pasos y desvaneciéndose**,
    /// no una caminata larga. Con el tramo largo el efecto tardaba y parecía que no pasaba nada.
    /// La ciudad casi siempre está en otro mapa; igual sólo importa la DIRECCIÓN.
    /// </summary>
    private static (int x, int y) DestinoDeSalida(int map, int petX, int petY, CityData.City ciudad)
    {
        // Los mapas de AO son de 100x100 con un borde no caminable; MapData expone los límites
        // reales (XMin/XMax/YMin/YMax), que es lo que hay que respetar para no mandarla a un tile
        // que no existe.
        var md = MapLoader.Get(map);
        int xMin = md?.XMin > 0 ? md.XMin : 1, xMax = md?.XMax > 0 ? md.XMax : 100;
        int yMin = md?.YMin > 0 ? md.YMin : 1, yMax = md?.YMax > 0 ? md.YMax : 100;

        int dx = ciudad.X - petX, dy = ciudad.Y - petY;
        if (ciudad.Map != map)
        {
            // Otra ciudad, otro mapa: sólo se usa el eje dominante para elegir para qué lado se va.
            if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0; else dx = 0;
        }
        if (dx == 0 && dy == 0) dy = 1; // sin dirección (parada justo encima): que camine al sur

        // Normalizar a un tramo corto en esa dirección.
        double largo = Math.Sqrt((double)dx * dx + (double)dy * dy);
        int destX = petX + (int)Math.Round(dx / largo * TILES_VIAJE);
        int destY = petY + (int)Math.Round(dy / largo * TILES_VIAJE);
        return (Math.Clamp(destX, xMin, xMax), Math.Clamp(destY, yMin, yMax));
    }
    /// <summary>
    /// La mascota llegó (o se venció el tope): recién ACÁ deja la mochila en la bóveda y se va.
    /// Si la bóveda está llena, lo que no entró vuelve con ella: la mascota se desinvoca igual
    /// (ya hizo el viaje) pero la mochila conserva lo que no pudo dejar, y el aviso lo dice.
    /// Nada se destruye nunca.
    /// </summary>
    public static void LlegoAlHogar(User u, NpcManager.NpcInstance pet)
    {
        string nombre = !string.IsNullOrEmpty(u.PetNombre) ? u.PetNombre : pet.Name;
        int depositados = 0, sinLugar = 0;
        for (int s = 1; s <= Constants.MAX_PETINVENTORY_SLOTS; s++)
        {
            ref var it = ref u.PetInvent.Object[s];
            if (it.ObjIndex == 0 || it.Amount <= 0) continue;
            if (!Bank.DepositarDirecto(u, it.ObjIndex, it.Amount)) { sinLugar++; continue; }
            it.ObjIndex = 0; it.Amount = 0;
            if (u.PetInvent.NroItems > 0) u.PetInvent.NroItems--;
            depositados++;
        }

        // Efecto de desaparición ANCLADO AL TILE, no al personaje: un CreateFX sobre el char se
        // borra junto con él (el cliente limpia los FX de un personaje al sacarlo del mapa, ver
        // removeCharFx), así que el destello no se vería. EfectoTerrenoFX queda en la baldosa.
        int fxMap = pet.Map, fxX = pet.X, fxY = pet.Y;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == fxMap)
            {
                ServerPackets.EfectoTerrenoFX(o.Conn, FX_DESAPARECER, fxX, fxY, 0);
                ServerPackets.PlayWave(o.Conn, SND_DESAPARECER, fxX, fxY);
            }
        }

        pet.YendoAlHogar = false;
        Combat.DespawnMascotaPersistente(u);
        // Descanso: mandarla al hogar tiene un costo real, si no la mula sería gratis (cargar,
        // mandarla, reinvocar al instante, repetir). Se guarda el MOMENTO en que vuelve a estar
        // disponible; CastSpell lo chequea antes de gastar maná.
        u.PetHogarHasta = Environment.TickCount64 / 1000.0 + SEGUNDOS_DESCANSO_HOGAR;
        Combat.EnviarPetInfo(u);
        EnviarMochila(u);

        if (u.Conn != null)
        {
            ServerPackets.ConsoleMsg(u.Conn, depositados > 0
                ? $"{nombre} llegó al hogar y dejó {depositados} cosa(s) en tu bóveda."
                : $"{nombre} llegó al hogar.", 1);
            ServerPackets.ConsoleMsg(u.Conn,
                $"Descansa {(int)(SEGUNDOS_DESCANSO_HOGAR / 60)} minutos: vas a poder invocarla de nuevo después de eso.", 1);
            if (sinLugar > 0)
                ServerPackets.ConsoleMsg(u.Conn,
                    $"La bóveda estaba llena: {sinLugar} cosa(s) siguen en la mochila y vuelven con ella cuando la invoques.", 1);
        }
        CharSaver.SaveUser(u); // el viaje movió ítems: que no dependa del logout
    }
    /// <summary>Manda la mochila completa al dueño (12 slots, incluidos los vacíos).</summary>
    public static void EnviarMochila(User u)
    {
        if (u?.Conn == null) return;
        ServerPackets.PetInv(u.Conn, u.PetInvent);
    }
}
