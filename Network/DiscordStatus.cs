using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using ServidorCS.Game;

namespace ServidorCS.Network;

/// <summary>
/// Actualiza los canales-cartel de Discord (estado del server + jugadores online)
/// SIN proceso aparte ni Python: el propio server renombra los canales de voz vía
/// la API REST de Discord usando el token del bot.
///
/// Renombrar un canal no requiere conexión de gateway; basta un PATCH al canal con
/// el header "Authorization: Bot &lt;token&gt;". Así todo viaja dentro del exe y se
/// despliega con el flujo normal (SUBIR_A_VM), sin instalar nada en la VM.
///
/// Configuración en Server.ini (NO está en el repo público, el token no se filtra):
///   DiscordToken=...           ; token del bot (Bot -> Reset Token en el portal)
///   DiscordCanalEstado=...     ; ID del canal de voz "estado"
///   DiscordCanalJugadores=...  ; ID del canal de voz "jugadores"
///   DiscordIntervalo=360       ; opcional, segundos entre updates (default 360)
///
/// Si DiscordToken está vacío, el módulo no hace nada (queda desactivado).
///
/// NOTA de rate-limit: Discord limita los cambios de NOMBRE de canal a ~2 cada 10
/// minutos por canal. Por eso el intervalo por defecto es 360s (6 min) y solo se
/// hace PATCH cuando el nombre realmente cambió.
/// </summary>
public static class DiscordStatus
{
    public static string Version = "1.4.5";

    private static readonly HttpClient Http = new();

    private static string _lastEstado = "";
    private static string _lastJugadores = "";

    public static void Start(CancellationToken ct)
    {
        string token = ServerConfig.ReadString("DiscordToken");
        string canalEstado = ServerConfig.ReadString("DiscordCanalEstado");
        string canalJugadores = ServerConfig.ReadString("DiscordCanalJugadores");
        int intervalo = ServerConfig.ReadInt("DiscordIntervalo", 360);
        if (intervalo < 120) intervalo = 120; // proteger contra rate-limit de Discord

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("[Discord] Desactivado (falta DiscordToken en Server.ini).");
            return;
        }

        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("LinkAO-Server (status, v1)");

        Console.WriteLine($"[Discord] Actualizador de canales activo (cada {intervalo}s).");

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int players = UserListManager.OnlineCount();
                    await Renombrar(canalEstado, "\U0001F7E2 Servidor: ONLINE", _lastEstadoSetter: v => _lastEstado = v, last: _lastEstado, ct);
                    await Renombrar(canalJugadores, $"\U0001F465 Jugadores: {players}", _lastEstadoSetter: v => _lastJugadores = v, last: _lastJugadores, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Discord] Error al actualizar: {ex.Message}");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(intervalo), ct); }
                catch { break; }
            }
        }, ct);
    }

    private static async Task Renombrar(string canalId, string nombre, Action<string> _lastEstadoSetter, string last, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(canalId)) return;
        if (nombre == last) return; // evita rate-limit: solo PATCH si cambió

        var url = $"https://discord.com/api/v10/channels/{canalId}";
        // JSON simple: {"name":"<nombre>"}. Escapamos comillas/backslash por las dudas.
        string esc = nombre.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var body = new StringContent($"{{\"name\":\"{esc}\"}}", Encoding.UTF8, "application/json");

        using var req = new HttpRequestMessage(HttpMethod.Patch, url) { Content = body };
        using var resp = await Http.SendAsync(req, ct);

        if (resp.IsSuccessStatusCode)
        {
            _lastEstadoSetter(nombre);
        }
        else
        {
            string detalle = await resp.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"[Discord] No pude renombrar canal {canalId}: {(int)resp.StatusCode} {detalle}");
        }
    }
}
