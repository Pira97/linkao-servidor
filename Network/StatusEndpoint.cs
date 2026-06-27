using System.Net;
using System.Text;
using ServidorCS.Game;

namespace ServidorCS.Network;

/// <summary>
/// Mini servidor HTTP de solo lectura para que herramientas externas (p.ej. un bot de
/// Discord) consulten el estado del juego sin tocar el protocolo TCP del cliente.
///
/// Expone GET /status -> { "online": true, "players": N, "version": "1.4.5" }
///
/// Se levanta en un puerto aparte (StatusPort en Server.ini, default 7667). No requiere
/// permisos de admin si se escucha en http://+ con urlacl, pero por simplicidad escucha
/// en http://*:puerto/ (en Windows puede pedir urlacl; ver nota al final del archivo).
/// </summary>
public static class StatusEndpoint
{
    public static string Version = "1.4.5";

    public static void Start(int port, CancellationToken ct)
    {
        if (!HttpListener.IsSupported) return;

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{port}/");

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Status] No se pudo iniciar el endpoint HTTP en :{port} ({ex.Message}). " +
                              "El server sigue funcionando; el bot de estado no tendrá datos.");
            return;
        }

        Console.WriteLine($"[Status] Endpoint de estado escuchando en http://*:{port}/status");

        // Loop de atención en un thread aparte para no bloquear el game loop.
        _ = Task.Run(async () =>
        {
            ct.Register(() => { try { listener.Stop(); } catch { } });
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; } // listener detenido

                try
                {
                    string ruta = ctx.Request.Url?.AbsolutePath ?? "/";
                    string json;
                    if (ruta.Equals("/online", StringComparison.OrdinalIgnoreCase))
                    {
                        // Lista de conectados con su posición: son datos privados de los
                        // jugadores, así que va detrás del token del panel (ver Espia).
                        json = Espia.TokenValido(ctx.Request.QueryString["tok"])
                            ? JsonDeConectados()
                            : "{\"error\":\"token\"}";
                        if (!Espia.TokenValido(ctx.Request.QueryString["tok"]))
                            ctx.Response.StatusCode = 403;
                    }
                    else if (ruta.Equals("/security", StringComparison.OrdinalIgnoreCase))
                    {
                        // Monitor de seguridad del panel (auditoría DDoS 24-ago-2026): sólo
                        // contadores agregados y eventos ya resumidos (ver SecurityLog/GlobalStats),
                        // nada de credenciales/IPs de jugadores individuales salvo las que YA
                        // quedan en los eventos de seguridad (misma info que ya se ve en consola).
                        // Mismo candado de token que /online: es información operativa interna.
                        if (!Espia.TokenValido(ctx.Request.QueryString["tok"]))
                        {
                            json = "{\"error\":\"token\"}";
                            ctx.Response.StatusCode = 403;
                        }
                        else json = JsonDeSeguridad();
                    }
                    else
                    {
                        int players = UserListManager.OnlineCount();
                        json = $"{{\"online\":true,\"players\":{players},\"version\":\"{Version}\"}}";
                    }
                    byte[] buf = Encoding.UTF8.GetBytes(json);

                    ctx.Response.ContentType = "application/json";
                    ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                    ctx.Response.ContentLength64 = buf.Length;
                    await ctx.Response.OutputStream.WriteAsync(buf, 0, buf.Length, ct);
                }
                catch { /* cliente cortó: ignorar */ }
                finally { try { ctx.Response.Close(); } catch { } }
            }
        }, ct);
    }

    /// <summary>
    /// GET /online?tok=… → { "players":[{"name","map","x","y","lvl","clase","gm"}] }.
    /// Lo consume el panel de deploy para armar la lista de "Espectar mundo vivo".
    /// Se toma bajo GameLock: recorrer UserList mientras el tick lo muta daría datos rotos.
    /// </summary>
    private static string JsonDeConectados()
    {
        var sb = new StringBuilder("{\"players\":[");
        bool primero = true;
        lock (UserListManager.GameLock)
        {
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var u = UserListManager.UserList[i];
                if (u?.flags.UserLogged != true || u.Conn == null) continue;
                if (!primero) sb.Append(',');
                primero = false;
                sb.Append("{\"name\":\"").Append(Esc(u.Name)).Append("\",")
                  .Append("\"map\":").Append(u.Pos.Map).Append(',')
                  .Append("\"x\":").Append(u.Pos.X).Append(',')
                  .Append("\"y\":").Append(u.Pos.Y).Append(',')
                  .Append("\"lvl\":").Append(u.Stats.ELV).Append(',')
                  .Append("\"gm\":").Append(u.FaccionStatus).Append(',')
                  .Append("\"muerto\":").Append(u.flags.Muerto == 1 ? "true" : "false")
                  .Append('}');
            }

            // Bots de guerra y progresivos: van en la misma lista con "bot":true y su bando, así
            // el panel los puede mostrar aparte y se pueden espectar igual que un jugador (ver
            // Espia.EmpezarEspectadorNpc). Sin guerra activa ni población del mundo, no agrega nada.
            foreach (var b in NpcManager.BotsEspectables())
            {
                if (!primero) sb.Append(',');
                primero = false;
                sb.Append("{\"name\":\"").Append(Esc(b.Name)).Append("\",")
                  .Append("\"map\":").Append(b.Map).Append(',')
                  .Append("\"x\":").Append(b.X).Append(',')
                  .Append("\"y\":").Append(b.Y).Append(',')
                  .Append("\"lvl\":").Append(b.BotLeveling ? b.BotNivelActual : 0).Append(",\"gm\":0,\"muerto\":false,")
                  .Append("\"bot\":true,\"faccion\":").Append(b.BotFaccion)
                  .Append('}');
            }
        }
        return sb.Append("]}").ToString();
    }

    /// <summary>
    /// GET /security?tok=… → contadores de GlobalStats + latencia del GameLoop + últimos eventos
    /// de seguridad ya agregados (SecurityLog.Recientes). Lo consume el panel de deploy para el
    /// monitor "Security / DDoS" (sección 17 de la auditoría 24-ago-2026). De sólo lectura: no
    /// modifica nada, no expone credenciales — misma información que ya se ve en la consola del
    /// server, sólo que estructurada para el dashboard.
    /// </summary>
    private static string JsonDeSeguridad()
    {
        var s = GlobalStats.Snapshot();
        var eventos = SecurityLog.Recientes(30);

        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"online\":").Append(UserListManager.OnlineCount()).Append(',');
        sb.Append("\"uptimeSeg\":").Append((Environment.TickCount64 - GameServer.StartTick) / 1000).Append(',');
        sb.Append("\"paquetesProcesados\":").Append(s.PaquetesProcesados).Append(',');
        sb.Append("\"bytesEntrantes\":").Append(s.BytesEntrantes).Append(',');
        sb.Append("\"conexionesActivas\":").Append(s.ConexionesActivas).Append(',');
        sb.Append("\"conexionesNuevas\":").Append(s.ConexionesNuevas).Append(',');
        sb.Append("\"conexionesRechazadas\":").Append(s.ConexionesRechazadas).Append(',');
        sb.Append("\"paquetesLimitados\":").Append(s.PaquetesLimitados).Append(',');
        sb.Append("\"clientesDesconectadosPorAbuso\":").Append(s.ClientesDesconectadosPorAbuso).Append(',');
        sb.Append("\"ultimoTickMs\":").Append(s.UltimoTickMs.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"maxTickMsUltimoMinuto\":").Append(s.MaxTickMsUltimoMinuto.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"eventos\":[");
        for (int i = 0; i < eventos.Count; i++)
        {
            var e = eventos[i];
            if (i > 0) sb.Append(',');
            sb.Append('{')
              .Append("\"ts\":\"").Append(e.Utc.ToString("O")).Append("\",")
              .Append("\"sev\":\"").Append(e.Sev).Append("\",")
              .Append("\"categoria\":\"").Append(Esc(e.Categoria)).Append("\",")
              .Append("\"detalle\":\"").Append(Esc(e.Detalle)).Append("\",")
              .Append("\"ip\":").Append(e.Ip != null ? $"\"{Esc(e.Ip)}\"" : "null").Append(',')
              .Append("\"repeticiones\":").Append(e.Repeticiones)
              .Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>Escapa lo mínimo para meter un nombre en JSON (comillas y barras).</summary>
    private static string Esc(string s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}

// NOTA (Windows): si al arrancar ves "Acceso denegado" al iniciar el HttpListener en :7667,
// abrí una consola como administrador UNA sola vez y ejecutá:
//   netsh http add urlacl url=http://*:7667/ user=Everyone
// y abrí el puerto en el firewall:
//   netsh advfirewall firewall add rule name="LinkAO Status" dir=in action=allow protocol=TCP localport=7667
