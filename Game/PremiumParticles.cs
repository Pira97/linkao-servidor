using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Partículas premium de meditación (NUEVO, no VB6). Cosméticos que reemplazan el cálculo
/// normal de Facciones.ParticleToLevel mientras el jugador medita. Se compran con
/// CreditoDonador — el MISMO saldo de cuenta que ya carga dinero real vía MercadoPago.cs
/// (crear preferencia → poll → acreditar), así que esto NO abre una pasarela de pago nueva:
/// solo agrega un sumidero de gasto más, igual que BattlePass.ComprarPasePremium.
///
/// Catálogo en Server.ini [ParticulasPremium]:
///   Item1=Nombre|StreamId|PrecioCreditos, Item2=..., hasta MAX_ITEMS.
/// StreamId es el id del emisor en assets/particles.json (ver mini/particles.js) — los
/// streams premium nuevos viven en LinkAO-Godot/init/particles.dat, ids 412+.
///
/// Persistencia por CUENTA (no por personaje) en Cuentas/&lt;CUENTA&gt;.cnt:
///   [Particulas] Owned=412,413  Equipado=412
/// </summary>
public static class PremiumParticles
{
    private const int MAX_ITEMS = 32;
    private static readonly List<ServerPackets.PremiumParticleItem> _catalogo = new();

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
                    string ln = ini.Get("ParticulasPremium", "Item" + i).Trim();
                    if (string.IsNullOrEmpty(ln)) break;
                    var p = ln.Split('|');
                    if (p.Length >= 3 && int.TryParse(p[1].Trim(), out var streamId)
                        && int.TryParse(p[2].Trim(), out var precio))
                        _catalogo.Add(new ServerPackets.PremiumParticleItem
                        { Id = i, StreamId = streamId, PrecioCreditos = precio, Nombre = p[0].Trim() });
                }
            }
            Console.WriteLine($"[PremiumParticles] {_catalogo.Count} items en catálogo.");
        }
        catch (Exception ex) { Console.WriteLine($"[PremiumParticles] Error en Init: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- Login / display

    /// <summary>Carga lo poseído/equipado desde el .cnt de cuenta y lo manda al cliente (LoginFlow).</summary>
    public static void LoadForLogin(User u)
    {
        u.PremiumParticlesOwned = new HashSet<int>();
        u.EquippedPremiumParticle = 0;
        if (string.IsNullOrEmpty(u.Account) || u.Conn == null) return;

        var ini = new IniFile(CntPath(u.Account));
        string owned = ini.Get("Particulas", "Owned");
        if (!string.IsNullOrEmpty(owned))
            foreach (var tok in owned.Split(','))
                if (int.TryParse(tok.Trim(), out var streamId) && streamId > 0)
                    u.PremiumParticlesOwned.Add(streamId);
        u.EquippedPremiumParticle = ini.GetInt("Particulas", "Equipado");

        ServerPackets.PremiumParticlesOwned(u.Conn, u.PremiumParticlesOwned, u.EquippedPremiumParticle);
    }

    /// <summary>HandleRequestPremiumParticles: manda el catálogo (posesión ya la tiene el cliente desde el login).</summary>
    public static void RequestCatalog(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null || !u.flags.UserLogged) return;
        ServerPackets.PremiumParticlesCatalog(u.Conn, _catalogo);
    }

    // ---------------------------------------------------------------- Compra / equipar

    /// <summary>HandleBuyPremiumParticle: descuenta CreditoDonador y otorga el streamId.</summary>
    public static void Comprar(int userIndex, int itemId)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null || !u.flags.UserLogged) return;

        var item = _catalogo.FirstOrDefault(c => c.Id == itemId);
        if (item.Id == 0) { ServerPackets.ConsoleMsg(u.Conn, "Partícula premium inválida.", 3); return; }
        if (u.PremiumParticlesOwned.Contains(item.StreamId))
        { ServerPackets.ConsoleMsg(u.Conn, "Ya tenés esa partícula.", 3); return; }
        if (u.CreditoDonador < item.PrecioCreditos)
        {
            ServerPackets.ConsoleMsg(u.Conn,
                $"No tenés créditos suficientes. Precio: {item.PrecioCreditos}, tenés: {u.CreditoDonador}.", 3);
            return;
        }

        u.CreditoDonador -= item.PrecioCreditos;
        u.PremiumParticlesOwned.Add(item.StreamId);
        Persistir(u);

        ServerPackets.UpdateCreditos(u.Conn, u.CreditoDonador);
        ServerPackets.PremiumParticleGranted(u.Conn, item.StreamId, item.Nombre);
        ServerPackets.PremiumParticlesOwned(u.Conn, u.PremiumParticlesOwned, u.EquippedPremiumParticle);
        ServerPackets.ConsoleMsg(u.Conn, $"¡Compraste \"{item.Nombre}\"! Ya podés equiparla.", 3);
    }

    /// <summary>HandleEquipPremiumParticle: streamId=0 desequipa (vuelve al de nivel/facción).
    /// Si el jugador está meditando, hace el swap en caliente reusando el mismo mecanismo
    /// que Facciones.ConCambioDeFaccion (quitar la vieja, cambiar, mandar la nueva) — ahora
    /// que Facciones.ParticleToLevel mira EquippedPremiumParticle, alcanza con llamar a los
    /// broadcast ya existentes antes/después de cambiar el campo.</summary>
    public static void Equipar(int userIndex, int streamId)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null || !u.flags.UserLogged) return;
        if (streamId != 0 && !u.PremiumParticlesOwned.Contains(streamId))
        { ServerPackets.ConsoleMsg(u.Conn, "No tenés esa partícula premium.", 3); return; }

        bool meditando = u.flags.Meditando;
        if (meditando) Facciones.QuitarParticulaMeditacion(u); // con el equipado VIEJO
        u.EquippedPremiumParticle = streamId;
        Persistir(u);
        if (meditando) Facciones.EnviarParticulaMeditacion(u); // con el equipado NUEVO

        ServerPackets.PremiumParticlesOwned(u.Conn, u.PremiumParticlesOwned, u.EquippedPremiumParticle);
    }

    // ---------------------------------------------------------------- Persistencia

    private static string CntPath(string account)
        => Path.Combine(AccountManager.AccountPath, account.ToUpperInvariant() + ".cnt");

    private static void Persistir(User u)
    {
        try
        {
            string f = CntPath(u.Account);
            var doc = new IniDocument(f);
            string acc = u.Account.ToUpperInvariant();
            doc.Set(acc, "Creditos", u.CreditoDonador.ToString());
            doc.Set("Particulas", "Owned", string.Join(",", u.PremiumParticlesOwned));
            doc.Set("Particulas", "Equipado", u.EquippedPremiumParticle.ToString());
            doc.Save(f);
        }
        catch (Exception ex) { Console.WriteLine($"[PremiumParticles] Persistir: {ex.Message}"); }
    }
}
