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
                // Desencriptar XOR (cliente→server). Clave fija salvo que el login la cambie.
                for (int i = 0; i < read; i++)
                    _recvBuffer[i] ^= IncomingXorKey;
                lock (IncomingData)
                {
                    IncomingData.AppendRaw(_recvBuffer, read);
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
        lock (OutgoingData)
        {
            OutgoingData.AppendRaw(bytes, bytes.Length);
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
