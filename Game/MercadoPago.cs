using System.Net.Http;
using System.Text;
using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Donaciones MercadoPago (modMercadoPago.bas) 1:1. Catálogo + ranking + historial (display, siempre
/// disponibles) y el cobro real vía API de MercadoPago (crear preferencia + polling de pagos para
/// acreditar créditos). El cobro/polling está GATEADO por Server.ini [MercadoPago] Habilitado=1 +
/// AccessToken: sin token, ShopBuyItem avisa "no disponible" y el polling no corre (nada se acredita).
///
/// Para activarlo: en Server.ini, sección [MercadoPago]:
///   Habilitado=1, AccessToken=APP_USR-..., Sandbox=0/1, IntervaloPollSeg=15,
///   Item1=Nombre|PrecioARS|Creditos, Item2=...  (hasta Item32)
/// Requiere probar contra la API real antes de producción.
/// </summary>
public static class MercadoPago
{
    private const int MP_MAX_ITEMS = 32, MP_MAX_POLL_POR_TICK = 3, MP_VENTANA_REFUND_DIAS = 60;
    private const string MP_API_PREF = "https://api.mercadopago.com/checkout/preferences";
    private const string MP_API_SEARCH = "https://api.mercadopago.com/v1/payments/search";

    public static bool Habilitado { get; private set; }
    private static string _accessToken = "";
    private static bool _sandbox;
    private static int _intervaloPollSeg = 15;
    private static readonly List<ServerPackets.ShopItem> _catalogo = new();

    private sealed class Pendiente
    {
        public string ExtRef = "", Account = "", Char = "", Estado = "PENDIENTE";
        public int ItemId; public int Creditos;
    }
    private static readonly List<Pendiente> _pendientes = new();
    private static readonly Dictionary<string, int> _ranking = new(StringComparer.OrdinalIgnoreCase); // account → total
    private static readonly Dictionary<string, string> _rankingChar = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly object _lock = new();

    // ---------------------------------------------------------------- Init / config

    public static void Init()
    {
        try
        {
            string iniPath = (string.IsNullOrEmpty(DataPaths.Root) ? AppContext.BaseDirectory : DataPaths.Root) + "Server.ini";
            if (File.Exists(iniPath))
            {
                var ini = new IniFile(iniPath);
                Habilitado = ini.GetInt("MercadoPago", "Habilitado") == 1;
                _accessToken = ini.Get("MercadoPago", "AccessToken").Trim();
                _sandbox = ini.GetInt("MercadoPago", "Sandbox") == 1;
                _intervaloPollSeg = ini.GetInt("MercadoPago", "IntervaloPollSeg");
                if (_intervaloPollSeg < 5) _intervaloPollSeg = 15;

                _catalogo.Clear();
                for (int i = 1; i <= MP_MAX_ITEMS; i++)
                {
                    string ln = ini.Get("MercadoPago", "Item" + i).Trim();
                    if (string.IsNullOrEmpty(ln)) break;
                    var p = ln.Split('|');
                    if (p.Length >= 3 && double.TryParse(p[1].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var precio)
                        && int.TryParse(p[2].Trim(), out var cred))
                        _catalogo.Add(new ServerPackets.ShopItem { Id = i, Nombre = p[0].Trim(), PrecioARS = (int)precio, Creditos = cred });
                }
            }
            CargarPendientes();
            CargarRanking();

            if (Habilitado && string.IsNullOrEmpty(_accessToken))
            {
                Console.WriteLine("[MercadoPago] Habilitado pero sin AccessToken en Server.ini → DESHABILITADO.");
                Habilitado = false;
            }
            Console.WriteLine($"[MercadoPago] {(Habilitado ? "ACTIVO" : "inactivo")}; {_catalogo.Count} items en catálogo.");

            if (Habilitado) _ = Task.Run(PollLoopAsync);
        }
        catch (Exception ex) { Habilitado = false; Console.WriteLine($"[MercadoPago] Error en Init: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- Display (siempre disponible)

    /// <summary>HandleRequestShopData: envía catálogo + historial + ranking al cliente.</summary>
    public static void RequestShopData(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null || !u.flags.UserLogged) return;
        ServerPackets.ShopCatalog(u.Conn, _catalogo);
        ServerPackets.DonationHistory(u.Conn, HistorialDe(u.Account));
        ServerPackets.DonorRanking(u.Conn, RankingTop10());
    }

    private static List<ServerPackets.DonationEntry> HistorialDe(string account)
    {
        var list = new List<ServerPackets.DonationEntry>();
        lock (_lock)
        {
            for (int i = _pendientes.Count - 1; i >= 0 && list.Count < 10; i--)
            {
                var p = _pendientes[i];
                if (!string.Equals(p.Account, account, StringComparison.OrdinalIgnoreCase)) continue;
                byte estado = p.Estado switch { "ACREDITADO" => 1, "REEMBOLSADO" => 2, _ => 0 };
                list.Add(new ServerPackets.DonationEntry { Fecha = FechaDeExtRef(p.ExtRef), Creditos = p.Creditos, Estado = estado });
            }
        }
        return list;
    }

    private static List<ServerPackets.DonorEntry> RankingTop10()
    {
        lock (_lock)
        {
            return _ranking.OrderByDescending(kv => kv.Value).Take(10)
                .Select(kv => new ServerPackets.DonorEntry { Nombre = _rankingChar.TryGetValue(kv.Key, out var c) ? c : kv.Key, Total = kv.Value })
                .ToList();
        }
    }

    // ---------------------------------------------------------------- Compra (gateada por token)

    /// <summary>HandleShopBuyItem: crea la preferencia de pago y devuelve la URL (si está habilitado).</summary>
    public static void ShopBuyItem(int userIndex, int itemId)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null || !u.flags.UserLogged) return;
        if (!Habilitado) { ServerPackets.ConsoleMsg(u.Conn, "El sistema de donaciones no está disponible en este momento.", 3); return; }

        var item = _catalogo.FirstOrDefault(c => c.Id == itemId);
        if (item.Id == 0) { ServerPackets.ConsoleMsg(u.Conn, "Item de donación inválido.", 3); return; }

        string account = u.Account, charName = u.Name;
        var conn = u.Conn;
        // HTTP fuera del game loop: crear preferencia y devolver la URL al completar.
        _ = Task.Run(async () =>
        {
            var (ok, url, _, extRef) = await CrearPreferenciaAsync(account, charName, item);
            if (ok && !string.IsNullOrEmpty(url))
            {
                AgregarPendiente(extRef, account, charName, item.Id, item.Creditos);
                Log("CREADA", account, charName, item.Id, item.Creditos, extRef);
                ServerPackets.ShopPaymentURL(conn, url);
            }
            else ServerPackets.ConsoleMsg(conn, "No se pudo generar el link de pago. Intentá de nuevo en unos segundos.", 3);
        });
    }

    private static async Task<(bool ok, string url, string prefId, string extRef)> CrearPreferenciaAsync(string account, string charName, ServerPackets.ShopItem item)
    {
        try
        {
            string extRef = $"LINKAO|{account}|{item.Id}|{DateTime.Now:yyyyMMddHHmmss}{(Environment.TickCount & 0x3FF):D3}";
            string body = "{\"items\":[{\"title\":\"" + EscaparJson(item.Nombre) + "\",\"quantity\":1,\"unit_price\":" +
                          item.PrecioARS.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"currency_id\":\"ARS\"}]," +
                          "\"external_reference\":\"" + EscaparJson(extRef) + "\"," +
                          "\"metadata\":{\"account\":\"" + EscaparJson(account) + "\",\"char\":\"" + EscaparJson(charName) + "\",\"item_id\":" + item.Id + "}}";

            using var req = new HttpRequestMessage(HttpMethod.Post, MP_API_PREF);
            req.Headers.Add("Authorization", "Bearer " + _accessToken);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(req);
            string txt = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            { Console.WriteLine($"[MercadoPago] crear preferencia HTTP {(int)resp.StatusCode}: {Trunc(txt, 200)}"); return (false, "", "", extRef); }

            string prefId = ExtraerStr(txt, "id");
            string url = _sandbox ? ExtraerStr(txt, "sandbox_init_point") : ExtraerStr(txt, "init_point");
            if (string.IsNullOrEmpty(url)) url = ExtraerStr(txt, "init_point");
            if (string.IsNullOrEmpty(url)) url = ExtraerStr(txt, "sandbox_init_point");
            return (!string.IsNullOrEmpty(url), url, prefId, extRef);
        }
        catch (Exception ex) { Console.WriteLine($"[MercadoPago] CrearPreferencia: {ex.Message}"); return (false, "", "", ""); }
    }

    // ---------------------------------------------------------------- Polling (gateado por token)

    private static async Task PollLoopAsync()
    {
        while (Habilitado)
        {
            try { await PollPagosAsync(); }
            catch (Exception ex) { Console.WriteLine($"[MercadoPago] PollLoop: {ex.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(_intervaloPollSeg));
        }
    }

    private static int _pollCursor;

    private static async Task PollPagosAsync()
    {
        if (!Habilitado) return;
        List<Pendiente> snapshot;
        lock (_lock) { if (_pendientes.Count == 0) return; snapshot = new List<Pendiente>(_pendientes); }

        int revisados = 0, vueltas = 0;
        while (revisados < MP_MAX_POLL_POR_TICK && vueltas < snapshot.Count)
        {
            _pollCursor = (_pollCursor + 1) % snapshot.Count;
            vueltas++;
            var p = snapshot[_pollCursor];
            bool revisar = p.Estado == "PENDIENTE" || (p.Estado == "ACREDITADO" && EdadDias(p.ExtRef) <= MP_VENTANA_REFUND_DIAS);
            if (!revisar) continue;
            revisados++;

            var (ok, aprobado, revertido) = await ConsultarPagoAsync(p.ExtRef);
            if (!ok) continue;
            if (p.Estado == "PENDIENTE" && aprobado) AcreditarCompra(p);
            else if (p.Estado == "ACREDITADO" && revertido && !aprobado) RevertirCompra(p);
        }
    }

    private static async Task<(bool ok, bool aprobado, bool revertido)> ConsultarPagoAsync(string extRef)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, MP_API_SEARCH + "?external_reference=" + Uri.EscapeDataString(extRef));
            req.Headers.Add("Authorization", "Bearer " + _accessToken);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (false, false, false);
            string t = await resp.Content.ReadAsStringAsync();
            bool aprobado = t.Contains("\"status\":\"approved\"");
            bool revertido = t.Contains("\"status\":\"refunded\"") || t.Contains("\"status\":\"charged_back\"") || t.Contains("\"status\":\"cancelled\"");
            return (true, aprobado, revertido);
        }
        catch (Exception ex) { Console.WriteLine($"[MercadoPago] ConsultarPago: {ex.Message}"); return (false, false, false); }
    }

    private static void AcreditarCompra(Pendiente p)
    {
        lock (_lock)
        {
            var real = _pendientes.FirstOrDefault(x => x.ExtRef == p.ExtRef);
            if (real == null || real.Estado != "PENDIENTE") return;
            int actuales = LeerCreditosCnt(real.Account) + real.Creditos;
            EscribirCreditosCnt(real.Account, actuales);
            real.Estado = "ACREDITADO";
            RankingSumar(real.Account, real.Char, real.Creditos);
            GuardarPendientes();

            int tUser = UserListManager.NameIndex(real.Char);
            if (tUser > 0)
            {
                var u = UserListManager.UserList[tUser];
                u.CreditoDonador = actuales;
                if (u.Conn != null)
                {
                    ServerPackets.UpdateCreditos(u.Conn, actuales);
                    ServerPackets.ShopItemGranted(u.Conn, real.ItemId, NombreItem(real.ItemId));
                    ServerPackets.ConsoleMsg(u.Conn, $"¡Gracias por tu donación! Recibiste {real.Creditos} créditos. Total: {actuales}.", 3);
                }
            }
            Log("ACREDITADA", real.Account, real.Char, real.ItemId, real.Creditos, real.ExtRef);
        }
    }

    private static void RevertirCompra(Pendiente p)
    {
        lock (_lock)
        {
            var real = _pendientes.FirstOrDefault(x => x.ExtRef == p.ExtRef);
            if (real == null || real.Estado != "ACREDITADO") return;
            int actuales = Math.Max(0, LeerCreditosCnt(real.Account) - real.Creditos);
            EscribirCreditosCnt(real.Account, actuales);
            real.Estado = "REEMBOLSADO";
            RankingSumar(real.Account, real.Char, -real.Creditos);
            GuardarPendientes();

            int tUser = UserListManager.NameIndex(real.Char);
            if (tUser > 0)
            {
                var u = UserListManager.UserList[tUser];
                u.CreditoDonador = actuales;
                if (u.Conn != null)
                {
                    ServerPackets.UpdateCreditos(u.Conn, actuales);
                    ServerPackets.ConsoleMsg(u.Conn, $"Se revirtió una donación por reembolso/contracargo: -{real.Creditos} créditos. Total: {actuales}.", 3);
                }
            }
            Log("REVERTIDA", real.Account, real.Char, real.ItemId, real.Creditos, real.ExtRef);
        }
    }

    // ---------------------------------------------------------------- Persistencia / helpers

    private static string MpDir()
    {
        string d = (string.IsNullOrEmpty(DataPaths.Root) ? AppContext.BaseDirectory : DataPaths.Root) + "MercadoPago";
        Directory.CreateDirectory(d);
        return d;
    }

    private static void AgregarPendiente(string extRef, string account, string charName, int itemId, int creditos)
    {
        lock (_lock)
        {
            _pendientes.Add(new Pendiente { ExtRef = extRef, Account = account, Char = charName, ItemId = itemId, Creditos = creditos, Estado = "PENDIENTE" });
            GuardarPendientes();
        }
    }

    private static void GuardarPendientes()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var p in _pendientes)
                sb.Append(p.ExtRef).Append('\t').Append(p.Account).Append('\t').Append(p.Char).Append('\t')
                  .Append(p.ItemId).Append('\t').Append(p.Creditos).Append('\t').Append(p.Estado).Append('\n');
            File.WriteAllText(Path.Combine(MpDir(), "pendientes.txt"), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) { Console.WriteLine($"[MercadoPago] GuardarPendientes: {ex.Message}"); }
    }

    private static void CargarPendientes()
    {
        try
        {
            string f = Path.Combine(MpDir(), "pendientes.txt");
            if (!File.Exists(f)) return;
            _pendientes.Clear();
            foreach (var ln in File.ReadAllLines(f))
            {
                var c = ln.Split('\t');
                if (c.Length < 6) continue;
                _pendientes.Add(new Pendiente { ExtRef = c[0], Account = c[1], Char = c[2], ItemId = int.Parse(c[3]), Creditos = int.Parse(c[4]), Estado = c[5] });
            }
        }
        catch (Exception ex) { Console.WriteLine($"[MercadoPago] CargarPendientes: {ex.Message}"); }
    }

    private static void RankingSumar(string account, string charName, int delta)
    {
        _ranking.TryGetValue(account, out int t);
        t = Math.Max(0, t + delta);
        _ranking[account] = t;
        _rankingChar[account] = charName;
        GuardarRanking();
    }

    private static void GuardarRanking()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var kv in _ranking)
                sb.Append(kv.Key).Append('\t').Append(_rankingChar.TryGetValue(kv.Key, out var c) ? c : kv.Key).Append('\t').Append(kv.Value).Append('\n');
            File.WriteAllText(Path.Combine(MpDir(), "ranking.txt"), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) { Console.WriteLine($"[MercadoPago] GuardarRanking: {ex.Message}"); }
    }

    private static void CargarRanking()
    {
        try
        {
            string f = Path.Combine(MpDir(), "ranking.txt");
            if (!File.Exists(f)) return;
            _ranking.Clear(); _rankingChar.Clear();
            foreach (var ln in File.ReadAllLines(f))
            {
                var c = ln.Split('\t');
                if (c.Length < 3) continue;
                _ranking[c[0]] = int.Parse(c[2]);
                _rankingChar[c[0]] = c[1];
            }
        }
        catch (Exception ex) { Console.WriteLine($"[MercadoPago] CargarRanking: {ex.Message}"); }
    }

    private static int LeerCreditosCnt(string account)
    {
        string f = Path.Combine(AccountManager.AccountPath, account.ToUpperInvariant() + ".cnt");
        return new IniFile(f).GetInt(account.ToUpperInvariant(), "Creditos");
    }

    private static void EscribirCreditosCnt(string account, int valor)
    {
        string f = Path.Combine(AccountManager.AccountPath, account.ToUpperInvariant() + ".cnt");
        var doc = new IniDocument(f);
        doc.Set(account.ToUpperInvariant(), "Creditos", valor.ToString());
        doc.Save(f);
    }

    private static string NombreItem(int itemId) => _catalogo.FirstOrDefault(c => c.Id == itemId).Nombre ?? "";

    private static void Log(string evento, string account, string charName, int itemId, int creditos, string extRef)
    {
        try
        {
            string ln = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {evento} | cuenta={account} | pj={charName} | item={itemId} | creditos={creditos} | ref={extRef}\n";
            File.AppendAllText(Path.Combine(MpDir(), "donaciones.log"), ln, Encoding.UTF8);
        }
        catch { /* log no crítico */ }
    }

    private static string FechaDeExtRef(string extRef)
    {
        var p = extRef.Split('|');
        if (p.Length < 4 || p[3].Length < 14) return "";
        string ts = p[3];
        return $"{ts.Substring(6, 2)}/{ts.Substring(4, 2)}/{ts.Substring(0, 4)}";
    }

    private static long EdadDias(string extRef)
    {
        var p = extRef.Split('|');
        if (p.Length < 4 || p[3].Length < 14) return 0;
        string ts = p[3];
        if (!DateTime.TryParseExact(ts.Substring(0, 14), "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out var d)) return 0;
        return (long)(DateTime.Now - d).TotalDays;
    }

    private static string EscaparJson(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

    /// <summary>Extrae el valor string de "clave":"valor" de un JSON plano (1:1 MP_ExtraerStr).</summary>
    private static string ExtraerStr(string json, string clave)
    {
        string key = "\"" + clave + "\":\"";
        int i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return "";
        i += key.Length;
        int j = json.IndexOf('"', i);
        return j < 0 ? "" : json.Substring(i, j - i).Replace("\\/", "/");
    }
}
