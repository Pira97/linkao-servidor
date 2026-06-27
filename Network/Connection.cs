using System.Net;
using System.Net.Sockets;

namespace ServidorCS.Network;

/// <summary>
/// Representa la conexión de un cliente. Reemplaza al Winsock del VB6
/// (wsksock.bas / wskapiAO.bas / TCP.bas) con System.Net.Sockets.
///
/// Mantiene dos colas igual que el VB6:
///   - IncomingData: bytes recibidos pendientes de parsear (HandleIncomingData).
///   - OutgoingData: bytes a enviar (flush periódico, como el FlushBuffer del server).
/// </summary>
public sealed class Connection
{
    private readonly Socket _socket;
    private readonly byte[] _recvBuffer = new byte[8192];
    /// <summary>Fix C3: evita relogear/recerrar repetidas veces mientras el socket todavía no
    /// terminó de cerrarse tras superar el backlog de salida (EnqueueOutgoing puede llamarse
    /// muchas veces más antes de que ReceiveLoopAsync note el cierre).</summary>
    private volatile bool _cerrandoPorBacklogSaliente;

    public int UserIndex { get; set; }
    public ByteQueue IncomingData { get; } = new();
    public ByteQueue OutgoingData { get; } = new();
    public bool Connected => _socket.Connected;
    public string RemoteEndPoint { get; }
    /// <summary>Solo la IP (sin puerto) del endpoint remoto, para el control AntiDos por IP.</summary>
    public string RemoteIp { get; }

    /// <summary>
    /// Clave XOR para los datos ENTRANTES (cliente→server). El cliente Godot arranca
    /// con 13 y, al recibir el packet Logged, cambia a su 'redundance'. El server S→C
    /// va en texto plano. Ver network.gd / protocol_incoming.gd del cliente.
    /// </summary>
    public byte IncomingXorKey = 13;

    /// <summary>
    /// Capacidades declaradas por el cliente con ClientPacketID.ClientCaps (bit0 = entiende
    /// los paquetes del modo espía). El cliente Godot viejo NO manda ese paquete, así que
    /// queda en 0 y el server no le envía nada que no sepa leer: su dispatcher no puede
    /// saltear un id desconocido (consume 1 byte y se desincroniza el stream entero).
    /// Es el candado que permite agregar paquetes S→C sin romper a los clientes viejos.
    /// </summary>
    public byte Caps;

    /// <summary>¿Este cliente entiende los paquetes nuevos del modo espía? (bit0 de Caps)</summary>
    public bool SoportaEspia => (Caps & 1) != 0;

    /// <summary>
    /// ¿Este cliente entiende los paquetes de efectos visuales nuevos? (bit1 de Caps).
    /// Hoy: LevelUpFx (117). Va en su propio bit y no colgado del de espía porque los
    /// clientes web YA desplegados declaran solo el bit0 y su JS cacheado no sabría leer
    /// el paquete nuevo (mismo problema de desincronización que el Godot viejo).
    /// </summary>
    public bool SoportaEfectosNuevos => (Caps & 2) != 0;

    /// <summary>
    /// ¿Este cliente entiende los paquetes de bóvedas premium? (bit2 de Caps). Mismo motivo
    /// que SoportaEfectosNuevos: los clientes ya desplegados con el JS viejo cacheado no
    /// declaran este bit, así que nunca reciben BankInitPremium/ChangeBankSlotPremium.
    /// </summary>
    public bool SoportaBovedaPremium => (Caps & 4) != 0;

    /// <summary>
    /// ¿Este cliente entiende ObjInfoUpdate (210)? (bit3 de Caps). Es el paquete que refresca
    /// el catálogo de objetos del cliente cuando un GM edita uno con /editobj. Bit propio por
    /// el motivo de siempre: un cliente ya desplegado con el JS viejo cacheado no sabría
    /// saltear el id y se le cerraría la sesión — y este paquete es un BROADCAST a todos los
    /// online, así que sin el candado una sola edición echaría a medio servidor.
    /// </summary>
    public bool SoportaObjInfoUpdate => (Caps & 8) != 0;

    /// <summary>
    /// Modo espía: true mientras el server le pidió a ESTE cliente que reporte lo que no
    /// viaja en el protocolo normal —su mouse y qué tiene abierto en la interfaz— porque un
    /// Dios lo está espiando. Sirve para no volver a pedírselo y para descartar reportes de
    /// quien no debería estar mandándolos.
    /// </summary>
    public bool ReportaAlEspia;

    /// <summary>
    /// Conexión en modo ESPECTADOR: se autenticó con una cuenta de Dios pero nunca entró al
    /// mundo con un personaje. Solo mira (ver Espia.EmpezarEspectador). Sin esta marca, los
    /// paquetes de espectador de una conexión cualquiera se descartan.
    /// </summary>
    public bool EsEspectador;

    /// <summary>
    /// El espectador ya recibió la ráfaga de entrada (LoggedSuccessful/Logged/UserIndex). Al
    /// CAMBIAR de objetivo se le reenvía el mundo pero NO esa ráfaga: el packet Logged cambia
    /// la clave XOR de la sesión, y si se renegocia con el cliente ya andando, cualquier
    /// paquete suyo en vuelo (el PING de cada 5s, sin ir más lejos) se descifra con la clave
    /// vieja y desincroniza el stream entero.
    /// </summary>
    public bool EspectadorEntro;

    public Connection(Socket socket, int userIndex)
    {
        _socket = socket;
        _socket.NoDelay = true; // deshabilita Nagle, como espera el cliente AO
        UserIndex = userIndex;
        RemoteEndPoint = socket.RemoteEndPoint?.ToString() ?? "?";
        RemoteIp = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? RemoteEndPoint;
    }

    /// <summary>Bucle de recepción. Cada bloque recibido se anexa a IncomingData
    /// y se invoca el dispatcher para drenar los packets completos.</summary>
    public async Task ReceiveLoopAsync(Action<Connection> onData, Action<Connection> onClose)
    {
        try
        {
            while (true)
            {
                int read = await _socket.ReceiveAsync(_recvBuffer, SocketFlags.None);
                if (read <= 0) break; // cliente cerró
                GlobalStats.BytesEntrantes(read);
                // Desencriptar XOR (cliente→server). Clave fija salvo que el login la cambie.
                for (int i = 0; i < read; i++)
                    _recvBuffer[i] ^= IncomingXorKey;
                int backlog;
                lock (IncomingData)
                {
                    IncomingData.AppendRaw(_recvBuffer, read);
                    backlog = IncomingData.Length;
                }
                // Tope de backlog sin parsear (SecurityConfig.MaxIncomingBacklogBytes): un packet
                // legítimo nunca acumula tanto sin completarse. Sin este corte, ByteQueue.EnsureSpace
                // duplicaría el buffer sin límite ante un cliente que manda basura que nunca arma un
                // packet válido (o que envía más rápido de lo que el dispatcher puede drenar).
                if (backlog > SecurityConfig.MaxIncomingBacklogBytes)
                {
                    SecurityLog.Log(SecuritySeverity.Blocked, "packet-size",
                        $"backlog {backlog} bytes supera el máximo, desconectando", RemoteIp);
                    break;
                }
                onData(this);
            }
        }
        catch (Exception)
        {
            // socket roto / cliente desconectado abruptamente
        }
        finally
        {
            onClose(this);
        }
    }

    /// <summary>Encola un packet ya serializado en la cola de salida.</summary>
    public void EnqueueOutgoing(ByteQueue packet)
    {
        byte[] bytes = packet.ToArray();
        int backlog;
        lock (OutgoingData)
        {
            OutgoingData.AppendRaw(bytes, bytes.Length);
            backlog = OutgoingData.Length;
        }

        // Fix C3 (auditoría DDoS 24-ago-2026): tope de OutgoingData, simétrico al de
        // IncomingData. Sin esto, un cliente lento/malicioso que nunca lee su socket hacía
        // crecer esta cola sin límite mientras el server le sigue mandando broadcasts (chat,
        // combate, movimiento de otros) — memoria del server agotada sin que el atacante mande
        // un solo byte de más. Cerrar el socket acá dispara el 'finally' de ReceiveLoopAsync,
        // que llama a onClose (GameServer.OnClose → CloseUser): mismo camino de limpieza que
        // cualquier otra desconexión, no uno nuevo.
        if (backlog > SecurityConfig.MaxOutgoingBacklogBytes && !_cerrandoPorBacklogSaliente)
        {
            _cerrandoPorBacklogSaliente = true;
            SecurityLog.Log(SecuritySeverity.Blocked, "outgoing-backlog",
                $"backlog saliente {backlog} bytes supera el máximo (cliente lento/no lee), desconectando", RemoteIp);
            Close();
        }
    }

    /// <summary>Envía y vacía la cola de salida (equivale a FlushBuffer del VB6).</summary>
    public async Task FlushAsync()
    {
        byte[] toSend;
        lock (OutgoingData)
        {
            if (OutgoingData.Length == 0) return;
            toSend = OutgoingData.ToArray();
            OutgoingData.Clear();
        }
        try
        {
            int sent = 0;
            while (sent < toSend.Length)
                sent += await _socket.SendAsync(
                    new ArraySegment<byte>(toSend, sent, toSend.Length - sent), SocketFlags.None);
        }
        catch (Exception)
        {
            Close();
        }
    }

    public void Close()
    {
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        try { _socket.Close(); } catch { }
    }

    /// <summary>
    /// FlushBuffer + CloseSocket del VB6: manda lo encolado (p.ej. el ShowMessageBox con el
    /// motivo del rechazo) y cierra la conexión. Usar en los rechazos de login: si el socket
    /// queda abierto, el cliente nunca dispara _on_disconnected y el botón Conectar queda
    /// deshabilitado.
    /// </summary>
    public void FlushAndClose()
    {
        try { FlushAsync().GetAwaiter().GetResult(); } catch { }
        Close();
    }
}
