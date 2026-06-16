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
                    int players = UserListManager.OnlineCount();
                    string json =
                        $"{{\"online\":true,\"players\":{players},\"version\":\"{Version}\"}}";
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
}

// NOTA (Windows): si al arrancar ves "Acceso denegado" al iniciar el HttpListener en :7667,
// abrí una consola como administrador UNA sola vez y ejecutá:
//   netsh http add urlacl url=http://*:7667/ user=Everyone
// y abrí el puerto en el firewall:
//   netsh advfirewall firewall add rule name="LinkAO Status" dir=in action=allow protocol=TCP localport=7667
