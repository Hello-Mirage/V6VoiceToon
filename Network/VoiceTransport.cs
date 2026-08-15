using System.Net;
using System.Net.Sockets;

namespace V6VoiceToon.Network;

/// <summary>
/// IPv6-only UDP transport for sending and receiving Opus voice frames.
/// </summary>
public sealed class VoiceTransport : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveTask;
    private IPEndPoint? _remoteEndpoint;

    /// <summary>
    /// Fired when an Opus frame is received from the remote peer.
    /// </summary>
    public event Action<byte[]>? OnFrameReceived;

    /// <summary>
    /// The local port this transport is bound to.
    /// </summary>
    public int LocalPort { get; }

    public VoiceTransport()
    {
        // Bind to IPv6 any address, OS-assigned port
        _udp = new UdpClient(AddressFamily.InterNetworkV6);
        _udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        LocalPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        // Allow receiving from IPv4-mapped IPv6 addresses (dual-stack)
        // This may fail on some systems, which is fine — we're IPv6 primary
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
    /// Sets the remote peer endpoint and starts the receive loop.
    /// </summary>
    public void Connect(IPEndPoint remoteEndpoint)
    {
        _remoteEndpoint = remoteEndpoint;
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>
    /// Sends an encoded Opus frame to the remote peer.
    /// </summary>
    public async Task SendFrameAsync(byte[] opusData, int length)
    {
        if (_remoteEndpoint == null)
            return;

        try
        {
            await _udp.SendAsync(opusData.AsMemory(0, length), _remoteEndpoint, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
        catch (SocketException)
        {
            // Network error — drop silently (VoIP best practice)
        }
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                OnFrameReceived?.Invoke(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // Transient network error — continue
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

        byte[] punch = new byte[] { 0x00 }; // 1-byte keepalive/punch
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await _udp.SendAsync(punch, punch.Length, _remoteEndpoint);
                await Task.Delay(100);
            }
            catch { /* best effort */ }
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
