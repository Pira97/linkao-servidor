using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Sistema de Subastas (origen: mdlSubastas.bas, reescrito idiomático en C#).
///
/// Un jugador subasta un ítem (queda en escrow), otros pujan oro (cobrado al pujar; al
/// superado se le devuelve por correo). Al expirar (o alcanzar el buyout) se entrega el
/// ítem al ganador y el oro al vendedor por correo. Todo se persiste a disco para no
/// perder ítems/oro en escrow ante un reinicio. CheckExpirations() corre 1/seg.
///
/// Protocolo con el cliente (sin cambios): AuctionCreate(objIndex,amount,buyout) /
/// AuctionBid(id,bid) / AuctionList. El "id" que ve el cliente es el slot (1..MAX).
///
/// Config opcional en Server.ini [SUBASTAS]:
///   DuracionHoras    (default 48)   — duración de cada subasta.
///   MaxPorJugador    (default 5)    — tope de subastas activas por vendedor.
///   IncrementoMinPct (default 5)    — la puja debe superar a la actual al menos este %.
///   ComisionPct      (default 0)    — % que retiene la casa sobre lo que cobra el vendedor.
///   AntiSnipingSegs  (default 0)    — si pujan faltando menos que esto, se extiende el fin.
/// </summary>
public static class Subastas
{
    private const int MAX_SUBASTAS = 100;
    private const short ORO = 12;        // iORO: ObjIndex usado para enviar oro por correo.
    private const byte FONT_INFO = 3;

    private sealed class Subasta
    {
        public bool Active;
        public short ObjIndex;
        public int Amount;
        public string SellerName = "";
        public string LastBidderName = NADIE;
        public long InitialPrice;
        public long CurrentBid;
        public long BuyoutPrice;
        public DateTime EndTime;

        public bool HasBidder => !string.Equals(LastBidderName.Trim(), NADIE, StringComparison.OrdinalIgnoreCase);
    }

    private const string NADIE = "Nadie";

    // El array es fijo porque el índice ES el id público (compatibilidad con el cliente y
    // con el archivo de persistencia). El lock serializa el acceso entre el loop de juego
    // (CheckExpirations) y los handlers de red (Crear/Pujar/SendList).
    private static readonly Subasta[] _subastas = CrearVacias();
    private static readonly object _gate = new();
    private static bool _cargado;

    // --- Configuración (cargada perezosamente junto con las subastas) ---
    private static double _duracionHoras = 48;
    private static int _maxPorJugador = 5;
    private static int _incrementoMinPct = 5;
    private static int _comisionPct = 0;
    private static int _antiSnipingSegs = 0;

    private static Subasta[] CrearVacias()
    {
        var a = new Subasta[MAX_SUBASTAS + 1];
        for (int i = 1; i <= MAX_SUBASTAS; i++) a[i] = new Subasta();
        return a;
    }

    // ============================================================
    //  API pública (firmas estables, las llaman PacketHandler/Accion/GameServer)
    // ============================================================

    /// <summary>Subasta_Crear. El cliente envía objIndex (no slot); se busca en el inventario.</summary>
    public static void Crear(int userIndex, short objIndex, int amount, long buyout)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged) return;
        if (amount <= 0) { Msg(u, "Cantidad inválida."); return; }
        if (buyout < 0) buyout = 0;

        lock (_gate)
        {
            EnsureLoaded();

            if (CuantasTiene(u.Name) >= _maxPorJugador)
            { Msg(u, $"Ya tenés {_maxPorJugador} subastas activas (el máximo)."); return; }

            int subId = PrimerSlotLibre();
            if (subId == 0) { Msg(u, "No hay lugar para más subastas en este momento."); return; }

            int slot = BuscarSlotInventario(u, objIndex, amount, out string motivo);
            if (slot == 0) { Msg(u, motivo); return; }

            // Sacar el ítem del inventario (escrow).
            ref var it = ref u.Invent.Object[slot];
            it.Amount -= amount;
            if (it.Amount <= 0)
            {
                it.ObjIndex = 0; it.Amount = 0; it.Equipped = false;
                if (u.Invent.NroItems > 0) u.Invent.NroItems--;
            }
            ServerPackets.ChangeInventorySlot(u.Conn, (byte)slot, it.ObjIndex, it.Amount, it.Equipped);

            var sub = _subastas[subId];
            sub.Active = true;
            sub.ObjIndex = objIndex;
            sub.Amount = amount;
            sub.SellerName = u.Name;
            sub.InitialPrice = 0;
            sub.CurrentBid = 0;
            sub.BuyoutPrice = buyout;
            sub.LastBidderName = NADIE;
            sub.EndTime = DateTime.Now.AddHours(_duracionHoras);

            Guardar();
            Msg(u, $"Pusiste {amount}x {NombreObj(objIndex)} en subasta.");
        }
        SendList(userIndex); // refrescar la ventana del cliente
    }

    /// <summary>Subasta_Pujar: valida y registra la puja; devuelve el oro al superado por correo.</summary>
    public static void Pujar(int userIndex, int subId, long bid)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged) return;

        lock (_gate)
        {
            EnsureLoaded();
            if (subId < 1 || subId > MAX_SUBASTAS) return;
            var sub = _subastas[subId];
            if (!sub.Active) { Msg(u, "Esa subasta ya no existe."); return; }

            if (string.Equals(u.Name, sub.SellerName, StringComparison.OrdinalIgnoreCase))
            { Msg(u, "No podés pujar en tu propia subasta."); return; }

            long minimo = PujaMinima(sub);
            if (bid < minimo) { Msg(u, $"La puja debe ser de al menos {minimo:N0} de oro."); return; }
            if (u.Stats.GLD < bid) { Msg(u, "No tenés suficiente oro."); return; }

            // Devolver el oro al pujador anterior (por correo).
            if (sub.HasBidder)
                Mail.DeliverSystem(sub.LastBidderName, "Finanzas",
                    "Se te devuelve el oro de una subasta en la que te superaron.", ORO, (int)sub.CurrentBid);

            // Cobrar al nuevo pujador.
            u.Stats.GLD -= (int)bid;
            ServerPackets.UpdateGold(u.Conn, u.Stats.GLD);

            sub.CurrentBid = bid;
            sub.LastBidderName = u.Name;

            // Anti-sniping: si pujan sobre el final, extender la subasta.
            if (_antiSnipingSegs > 0)
            {
                var faltan = (sub.EndTime - DateTime.Now).TotalSeconds;
                if (faltan < _antiSnipingSegs)
                {
                    sub.EndTime = DateTime.Now.AddSeconds(_antiSnipingSegs);
                    Msg(u, "¡Puja sobre el cierre! Se extendió el tiempo de la subasta.");
                }
            }

            // Compra inmediata.
            if (sub.BuyoutPrice > 0 && bid >= sub.BuyoutPrice)
            {
                Finalizar(subId);
            }
            else
            {
                AvisarVendedor(sub, $"Recibiste una puja de {bid:N0} de oro por tu {NombreObj(sub.ObjIndex)}.");
                Guardar();
                Msg(u, "Pujaste correctamente.");
            }
        }
        SendList(userIndex); // refrescar la ventana del cliente
    }

    /// <summary>Subasta_CheckExpirations: 1/seg. Finaliza las subastas vencidas.</summary>
    public static void CheckExpirations()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var now = DateTime.Now;
            bool alguna = false;
            for (int i = 1; i <= MAX_SUBASTAS; i++)
                if (_subastas[i].Active && now >= _subastas[i].EndTime) { Finalizar(i); alguna = true; }
            if (alguna) Guardar();
        }
    }

    /// <summary>Subastas_SendList: envía al usuario la lista de subastas activas (AuctionList).</summary>
    public static void SendList(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null) return;

        var activas = new List<ServerPackets.AuctionEntry>();
        lock (_gate)
        {
            EnsureLoaded();
            var now = DateTime.Now;
            for (int i = 1; i <= MAX_SUBASTAS; i++)
            {
                var s = _subastas[i];
                if (!s.Active) continue;
                long secs = (long)(s.EndTime - now).TotalSeconds;
                if (secs < 0) secs = 0;
                activas.Add(new ServerPackets.AuctionEntry
                {
                    Id = i, ObjIndex = s.ObjIndex, Amount = s.Amount, Seller = s.SellerName.Trim(),
                    CurrentBid = s.CurrentBid, LastBidder = s.LastBidderName.Trim(),
                    Buyout = s.BuyoutPrice, RemainingSecs = secs,
                });
            }
        }
        ServerPackets.AuctionList(u.Conn, activas);
    }

    // ============================================================
    //  Lógica interna (siempre invocada con el lock tomado)
    // ============================================================

    /// <summary>Entrega el ítem al ganador y el oro (menos comisión) al vendedor; o devuelve el ítem si nadie pujó.</summary>
    private static void Finalizar(int subId)
    {
        var sub = _subastas[subId];
        if (!sub.Active) return;

        string seller = sub.SellerName.Trim();
        string item = NombreObj(sub.ObjIndex);

        if (!sub.HasBidder)
        {
            Mail.DeliverSystem(seller, "Subastas", "Tu subasta finalizó sin pujas. Se te devuelve el ítem.", sub.ObjIndex, sub.Amount);
        }
        else
        {
            string winner = sub.LastBidderName.Trim();
            long neto = AplicarComision(sub.CurrentBid, out long comision);

            Mail.DeliverSystem(winner, "Subastas", $"¡Felicidades! Ganaste la subasta de {item}.", sub.ObjIndex, sub.Amount);
            string detalle = comision > 0
                ? $"Tu {item} se vendió por {sub.CurrentBid:N0} (comisión {comision:N0}). Recibís {neto:N0} de oro."
                : $"Tu {item} se vendió en subasta por {neto:N0} de oro.";
            Mail.DeliverSystem(seller, "Subastas", detalle, ORO, (int)neto);
        }

        sub.Active = false;
        sub.LastBidderName = NADIE;
    }

    private static long PujaMinima(Subasta sub)
    {
        if (!sub.HasBidder)
            return Math.Max(1, sub.InitialPrice);
        long incremento = Math.Max(1, sub.CurrentBid * _incrementoMinPct / 100);
        return sub.CurrentBid + incremento;
    }

    private static long AplicarComision(long monto, out long comision)
    {
        comision = _comisionPct > 0 ? monto * _comisionPct / 100 : 0;
        return monto - comision;
    }

    private static int CuantasTiene(string seller)
    {
        int n = 0;
        for (int i = 1; i <= MAX_SUBASTAS; i++)
            if (_subastas[i].Active && string.Equals(_subastas[i].SellerName, seller, StringComparison.OrdinalIgnoreCase))
                n++;
        return n;
    }

    private static int PrimerSlotLibre()
    {
        for (int i = 1; i <= MAX_SUBASTAS; i++)
            if (!_subastas[i].Active) return i;
        return 0;
    }

    /// <summary>Busca un slot del inventario con el ítem y cantidad pedidos; valida que sea subastable.</summary>
    private static int BuscarSlotInventario(User u, short objIndex, int amount, out string motivo)
    {
        motivo = "No tenés ese ítem/cantidad para subastar.";
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
        {
            ref var it = ref u.Invent.Object[s];
            if (it.ObjIndex != objIndex || it.Amount < amount) continue;
            if (it.Equipped) { motivo = "Desequipá el ítem antes de subastarlo."; return 0; }
            var od = ObjData.Get(objIndex);
            if (od.Newbie == 1) { motivo = "Los ítems newbie no se pueden subastar."; return 0; }
            return s;
        }
        return 0;
    }

    // ============================================================
    //  Persistencia y configuración
    // ============================================================

    private static string FilePath()
    {
        string dir = string.IsNullOrEmpty(DataPaths.Root) ? AppContext.BaseDirectory : DataPaths.Sub("Dat");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "Subastas.txt");
    }

    private static void CargarConfig()
    {
        try
        {
            string iniPath = (string.IsNullOrEmpty(DataPaths.Root) ? AppContext.BaseDirectory : DataPaths.Root) + "Server.ini";
            if (!File.Exists(iniPath)) return;
            var ini = new IniFile(iniPath);
            _duracionHoras   = PosOr(ini.GetInt("SUBASTAS", "DuracionHoras"), 48);
            _maxPorJugador   = PosOr(ini.GetInt("SUBASTAS", "MaxPorJugador"), 5);
            _incrementoMinPct = NonNegOr(ini.GetInt("SUBASTAS", "IncrementoMinPct"), 5);
            _comisionPct     = Math.Clamp(ini.GetInt("SUBASTAS", "ComisionPct"), 0, 90);
            _antiSnipingSegs = Math.Max(0, ini.GetInt("SUBASTAS", "AntiSnipingSegs"));
        }
        catch (Exception ex) { Console.WriteLine($"[Subastas] Error al leer config: {ex.Message}"); }
    }

    private static int PosOr(int v, int def) => v > 0 ? v : def;
    private static int NonNegOr(int v, int def) => v >= 0 ? v : def;

    private static void EnsureLoaded()
    {
        if (_cargado) return;
        _cargado = true;
        CargarConfig();
        try
        {
            string f = FilePath();
            if (!File.Exists(f)) return;
            var ini = new IniFile(f);
            for (int i = 1; i <= MAX_SUBASTAS; i++)
            {
                string sec = "SUB" + i;
                if (ini.GetInt(sec, "Active") != 1) continue;
                var s = _subastas[i];
                s.Active = true;
                s.ObjIndex = (short)ini.GetInt(sec, "ObjIndex");
                s.Amount = ini.GetInt(sec, "Amount");
                s.SellerName = ini.Get(sec, "Seller");
                s.LastBidderName = ini.Get(sec, "LastBidder");
                s.InitialPrice = ParseLong(ini.Get(sec, "InitialPrice"));
                s.CurrentBid = ParseLong(ini.Get(sec, "CurrentBid"));
                s.BuyoutPrice = ParseLong(ini.Get(sec, "Buyout"));
                s.EndTime = long.TryParse(ini.Get(sec, "EndTime"), out var et)
                    ? new DateTime(et) : DateTime.Now.AddHours(_duracionHoras);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Subastas] Error al cargar: {ex.Message}"); }
    }

    private static void Guardar()
    {
        try
        {
            var doc = new IniDocument(FilePath());
            for (int i = 1; i <= MAX_SUBASTAS; i++)
            {
                var s = _subastas[i];
                string sec = "SUB" + i;
                doc.Set(sec, "Active", s.Active ? "1" : "0");
                if (!s.Active) continue;
                doc.Set(sec, "ObjIndex", s.ObjIndex.ToString());
                doc.Set(sec, "Amount", s.Amount.ToString());
                doc.Set(sec, "Seller", s.SellerName);
                doc.Set(sec, "LastBidder", s.LastBidderName);
                doc.Set(sec, "InitialPrice", s.InitialPrice.ToString());
                doc.Set(sec, "CurrentBid", s.CurrentBid.ToString());
                doc.Set(sec, "Buyout", s.BuyoutPrice.ToString());
                doc.Set(sec, "EndTime", s.EndTime.Ticks.ToString());
            }
            doc.Save(FilePath());
        }
        catch (Exception ex) { Console.WriteLine($"[Subastas] Error al guardar: {ex.Message}"); }
    }

    private static long ParseLong(string v) => long.TryParse(v, out var n) ? n : 0;

    // ============================================================
    //  Utilidades de mensajería
    // ============================================================

    private static string NombreObj(short objIndex)
    {
        string n = ObjData.Get(objIndex).Name;
        return string.IsNullOrEmpty(n) ? $"objeto #{objIndex}" : n;
    }

    private static void AvisarVendedor(Subasta sub, string mensaje)
    {
        int si = UserListManager.NameIndex(sub.SellerName);
        if (si <= 0) return;
        var su = UserListManager.UserList[si];
        if (su?.Conn != null) Msg(su, "[Subastas] " + mensaje);
    }

    private static void Msg(User u, string m)
    {
        if (u?.Conn != null) ServerPackets.ConsoleMsg(u.Conn, m, FONT_INFO);
    }
}
