using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Cosméticos (cascos/escudos/monturas) comprados con CreditoDonador — el MISMO saldo de
/// cuenta que ya carga dinero real vía MercadoPago.cs, igual criterio que PremiumParticles.cs
/// pero acá se entrega un OBJETO real al inventario (Inventory.AddItemToInventory) en vez de
/// desbloquear un stream de partícula.
///
/// Catálogo en Server.ini [TiendaCreditosItems]:
///   Item1=Nombre|ObjIndex|PrecioCreditos, Item2=..., hasta MAX_ITEMS.
/// </summary>
public static class CreditItems
{
    private const int MAX_ITEMS = 32;
    private static readonly List<ServerPackets.CreditShopItem> _catalogo = new();

    public static void Init()
    {
        try
        {
            string iniPath = (string.IsNullOrEmpty(DataPaths.Root) ? AppContext.BaseDirectory : DataPaths.Root) + "Server.ini";
            _catalogo.Clear();
            if (File.Exists(iniPath))
            {
                var ini = new IniFile(iniPath);
                for (int i = 1; i <= MAX_ITEMS; i++)
                {
                    string ln = ini.Get("TiendaCreditosItems", "Item" + i).Trim();
                    if (string.IsNullOrEmpty(ln)) break;
                    var p = ln.Split('|');
                    // AuraId (5to campo) es opcional: los ítems sin partícula propia no lo traen
                    // (líneas viejas de 4 campos siguen andando, con AuraId=0).
                    int auraId = 0;
                    if (p.Length >= 5) int.TryParse(p[4].Trim(), out auraId);
                    if (p.Length >= 4 && int.TryParse(p[1].Trim(), out var objIndex)
                        && int.TryParse(p[2].Trim(), out var precio)
                        && int.TryParse(p[3].Trim(), out var visualId))
                        _catalogo.Add(new ServerPackets.CreditShopItem
                        { Id = i, ObjIndex = objIndex, PrecioCreditos = precio, VisualId = visualId, AuraId = auraId, Nombre = p[0].Trim() });
                }
            }
            Console.WriteLine($"[CreditItems] {_catalogo.Count} items en catálogo.");
        }
        catch (Exception ex) { Console.WriteLine($"[CreditItems] Error en Init: {ex.Message}"); }
    }

    /// <summary>HandleRequestCreditItems: manda el catálogo.</summary>
    public static void RequestCatalog(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null || !u.flags.UserLogged) return;
        ServerPackets.CreditItemsCatalog(u.Conn, _catalogo);
    }

    /// <summary>HandleBuyCreditItem: descuenta CreditoDonador y entrega el objeto al inventario
    /// (o lo tira al piso si el inventario está lleno, mismo criterio que CofresEvento).</summary>
    public static void Comprar(int userIndex, int itemId)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null || !u.flags.UserLogged) return;

        var item = _catalogo.FirstOrDefault(c => c.Id == itemId);
        if (item.Id == 0) { ServerPackets.ConsoleMsg(u.Conn, "Objeto inválido.", 3); return; }
        if (u.CreditoDonador < item.PrecioCreditos)
        {
            ServerPackets.ConsoleMsg(u.Conn,
                $"No tenés créditos suficientes. Precio: {item.PrecioCreditos}, tenés: {u.CreditoDonador}.", 3);
            return;
        }

        u.CreditoDonador -= item.PrecioCreditos;
        Persistir(u);
        ServerPackets.UpdateCreditos(u.Conn, u.CreditoDonador);

        if (Inventory.AddItemToInventory(u, (short)item.ObjIndex, 1))
        {
            ServerPackets.CreditItemGranted(u.Conn, item.ObjIndex, item.Nombre);
            ServerPackets.ConsoleMsg(u.Conn, $"¡Compraste \"{item.Nombre}\"!", 3);
        }
        else
        {
            DropItemAtUser(u, (short)item.ObjIndex, 1);
            ServerPackets.ConsoleMsg(u.Conn, "Tu inventario estaba lleno. ¡El objeto cayó al suelo!", 2);
        }
    }

    /// <summary>Mismo criterio que CofresEvento.DropItemAtUser: tira el objeto a los pies del
    /// jugador, o a la celda libre más cercana si esa está ocupada.</summary>
    private static void DropItemAtUser(User u, short objIndex, int amount)
    {
        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return;
        int x = u.Pos.X, y = u.Pos.Y;
        if (map.FloorObj[x, y] != 0)
        {
            bool libre = false;
            for (int dx = -1; dx <= 1 && !libre; dx++)
                for (int dy = -1; dy <= 1 && !libre; dy++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx is >= 1 and <= 100 && ny is >= 1 and <= 100 && !map.IsBlocked(nx, ny) && map.FloorObj[nx, ny] == 0)
                    { x = nx; y = ny; libre = true; }
                }
            if (!libre) return;
        }
        map.FloorObj[x, y] = objIndex;
        map.FloorAmount[x, y] = amount;
        AreaVisibility.ObjectAppeared(u.Pos.Map, x, y, objIndex, amount);
    }

    private static void Persistir(User u)
    {
        try
        {
            string f = Path.Combine(AccountManager.AccountPath, u.Account.ToUpperInvariant() + ".cnt");
            var doc = new IniDocument(f);
            doc.Set(u.Account.ToUpperInvariant(), "Creditos", u.CreditoDonador.ToString());
            doc.Save(f);
        }
        catch (Exception ex) { Console.WriteLine($"[CreditItems] Persistir: {ex.Message}"); }
    }
}
