using System.Net;
using System.Net.Sockets;

namespace V6VoiceToon.Network;

/// <summary>
/// IPv6-only UDP transport for sending and receiving Opus voice frames.
/// P2P hole punching is performed manually when requested by the signaling layer.
/// </summary>
public sealed class VoiceTransport : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveTask;
    private IPEndPoint? _remoteEndpoint;
    private bool _isConnected = false;

    /// <summary>
    /// Fired when an Opus voice frame is received from the remote peer.
    /// </summary>
    public event Action<byte[]>? OnFrameReceived;

    /// <summary>
    /// The local port this transport is bound to.
    /// </summary>
    public int LocalPort { get; }

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
        catch { }
    }

    /// <summary>
    /// Connects to the remote peer, punches a hole, and starts listening for voice frames.
    /// This should be called by BOTH sides simultaneously after MQTT negotiation.
    /// </summary>
    public async Task ConnectAndPunchHoleAsync(IPEndPoint remoteEndpoint)
    {
        _remoteEndpoint = remoteEndpoint;
        _isConnected = true;

        if (_receiveTask == null)
            _receiveTask = Task.Run(ReceiveLoopAsync);

        // Send hole punch packets
        await PunchHoleAsync();
    }

    public void Disconnect()
    {
        _isConnected = false;
        _remoteEndpoint = null;
    }

    /// <summary>
    /// Sends an encoded Opus frame to the remote peer.
    /// </summary>
    public async Task SendFrameAsync(byte[] opusData, int length)
    {
        if (_remoteEndpoint == null || !_isConnected)
            return;

        try
        {
            await _udp.SendAsync(opusData.AsMemory(0, length), _remoteEndpoint, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                byte[] data = result.Buffer;

                if (data.Length <= 1) continue; // Ignore hole-punch packets (1 byte)

                // Deliver voice frame
                if (_isConnected && _remoteEndpoint != null)
                {
                    OnFrameReceived?.Invoke(data);
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

        byte[] punch = new byte[] { 0x00 };
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
