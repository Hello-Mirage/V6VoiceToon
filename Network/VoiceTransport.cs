using System.Net;
using System.Net.Sockets;

namespace V6VoiceToon.Network;

/// <summary>
/// Packet type prefixes for the signaling protocol.
/// Every UDP packet starts with one of these bytes.
/// </summary>
public static class PacketType
{
    public const byte HolePunch = 0x00;
    public const byte ConnectRequest = 0x01;
    public const byte ConnectAccept = 0x02;
    public const byte ConnectReject = 0x03;
    public const byte Disconnect = 0x04;
    public const byte VoiceData = 0xFF;
}

/// <summary>
/// IPv6-only UDP transport for sending and receiving Opus voice frames.
/// Includes a signaling protocol for connection requests.
/// </summary>
public sealed class VoiceTransport : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveTask;
    private IPEndPoint? _remoteEndpoint;
    private bool _isAccepted = false;

    /// <summary>
    /// Fired when an Opus voice frame is received from the accepted peer.
    /// </summary>
    public event Action<byte[]>? OnFrameReceived;

    /// <summary>
    /// Fired when a connection request arrives from a remote peer.
    /// Parameters: (IPEndPoint senderEndpoint)
    /// </summary>
    public event Action<IPEndPoint>? OnConnectionRequest;

    /// <summary>
    /// Fired when the remote peer accepts our connection request.
    /// </summary>
    public event Action? OnConnectionAccepted;

    /// <summary>
    /// Fired when the remote peer rejects our connection request.
    /// </summary>
    public event Action? OnConnectionRejected;

    /// <summary>
    /// Fired when the remote peer disconnects.
    /// </summary>
    public event Action? OnRemoteDisconnect;

    /// <summary>
    /// The local port this transport is bound to.
    /// </summary>
    public int LocalPort { get; }

    /// <summary>
    /// The currently connected remote endpoint, if any.
    /// </summary>
    public IPEndPoint? RemoteEndpoint => _remoteEndpoint;

    public VoiceTransport()
    {
        // Bind to IPv6 any address, OS-assigned port
        _udp = new UdpClient(AddressFamily.InterNetworkV6);
        _udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        LocalPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        // Allow receiving from IPv4-mapped IPv6 addresses (dual-stack)
        try
        {
            _udp.Client.DualMode = true;
        }
        catch
        {
            // Dual-mode not supported, pure IPv6 only — that's fine
        }
    }

    /// <summary>
    /// Starts listening for ANY incoming packets (connection requests or voice).
    /// Call this on startup so the app can receive incoming calls.
    /// </summary>
    public void StartListening()
    {
        if (_receiveTask == null)
            _receiveTask = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>
    /// Sends a connection request to the remote peer.
    /// The peer will see an "incoming call" and can accept or reject.
    /// </summary>
    public async Task SendConnectRequestAsync(IPEndPoint remoteEndpoint)
    {
        _remoteEndpoint = remoteEndpoint;
        _isAccepted = false;

        // Start listening if not already
        StartListening();

        // Send hole-punch packets first
        await PunchHoleAsync();

        // Send the connect request (repeat a few times for reliability since UDP)
        byte[] request = new byte[] { PacketType.ConnectRequest };
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await _udp.SendAsync(request, request.Length, _remoteEndpoint);
                await Task.Delay(200);
            }
            catch { }
        }
    }

    /// <summary>
    /// Accepts an incoming connection request from the given peer.
    /// </summary>
    public async Task AcceptConnectionAsync(IPEndPoint remoteEndpoint)
    {
        _remoteEndpoint = remoteEndpoint;
        _isAccepted = true;

        byte[] accept = new byte[] { PacketType.ConnectAccept };
        for (int i = 0; i < 3; i++)
        {
            try
            {
                await _udp.SendAsync(accept, accept.Length, _remoteEndpoint);
                await Task.Delay(100);
            }
            catch { }
        }
    }

    /// <summary>
    /// Rejects an incoming connection request from the given peer.
    /// </summary>
    public async Task RejectConnectionAsync(IPEndPoint remoteEndpoint)
    {
        byte[] reject = new byte[] { PacketType.ConnectReject };
        try
        {
            await _udp.SendAsync(reject, reject.Length, remoteEndpoint);
        }
        catch { }
    }

    /// <summary>
    /// Sends a disconnect signal to the remote peer.
    /// </summary>
    public async Task SendDisconnectAsync()
    {
        if (_remoteEndpoint == null) return;

        byte[] disconnect = new byte[] { PacketType.Disconnect };
        for (int i = 0; i < 3; i++)
        {
            try
            {
                await _udp.SendAsync(disconnect, disconnect.Length, _remoteEndpoint);
                await Task.Delay(50);
            }
            catch { }
        }

        _remoteEndpoint = null;
        _isAccepted = false;
    }

    /// <summary>
    /// Sends an encoded Opus frame to the remote peer (prefixed with VoiceData type byte).
    /// </summary>
    public async Task SendFrameAsync(byte[] opusData, int length)
    {
        if (_remoteEndpoint == null || !_isAccepted)
            return;

        try
        {
            // Prefix with VoiceData type byte
            byte[] packet = new byte[1 + length];
            packet[0] = PacketType.VoiceData;
            Array.Copy(opusData, 0, packet, 1, length);

            await _udp.SendAsync(packet.AsMemory(0, packet.Length), _remoteEndpoint, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }

    /// <summary>
    /// Marks the connection as accepted (called when remote peer sends Accept).
    /// </summary>
    public void MarkAccepted()
    {
        _isAccepted = true;
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                byte[] data = result.Buffer;

                if (data.Length == 0) continue;

                byte packetType = data[0];

                switch (packetType)
                {
                    case PacketType.HolePunch:
                        // Ignore hole-punch packets silently
                        break;

                    case PacketType.ConnectRequest:
                        // Someone wants to connect to us
                        OnConnectionRequest?.Invoke(result.RemoteEndPoint);
                        break;

                    case PacketType.ConnectAccept:
                        // Our connection request was accepted
                        _isAccepted = true;
                        OnConnectionAccepted?.Invoke();
                        break;

                    case PacketType.ConnectReject:
                        // Our connection request was rejected
                        _remoteEndpoint = null;
                        _isAccepted = false;
                        OnConnectionRejected?.Invoke();
                        break;

                    case PacketType.Disconnect:
                        // Remote peer disconnected
                        _remoteEndpoint = null;
                        _isAccepted = false;
                        OnRemoteDisconnect?.Invoke();
                        break;

                    case PacketType.VoiceData:
                        // Strip the type byte and deliver the Opus frame
                        if (data.Length > 1 && _isAccepted)
                        {
                            byte[] opusFrame = new byte[data.Length - 1];
                            Array.Copy(data, 1, opusFrame, 0, opusFrame.Length);
                            OnFrameReceived?.Invoke(opusFrame);
                        }
                        break;

                    default:
                        // Unknown packet type — ignore
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                await Task.Delay(10);
            }
        }
    }

    /// <summary>
    /// Sends a few UDP packets to punch through NAT for the remote peer.
    /// </summary>
    public async Task PunchHoleAsync()
    {
        if (_remoteEndpoint == null) return;

        byte[] punch = new byte[] { PacketType.HolePunch };
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await _udp.SendAsync(punch, punch.Length, _remoteEndpoint);
                await Task.Delay(100);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _receiveTask?.Wait(2000);
        _udp.Dispose();
        _cts.Dispose();
    }
}
