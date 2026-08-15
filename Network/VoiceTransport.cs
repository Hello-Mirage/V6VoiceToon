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
    private IPEndPoint? _localRemoteEndpoint;
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
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetworkV6);
            try { _udp.Client.DualMode = true; } catch { }
            _udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        }
        catch
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
        LocalPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Connects to the remote peer, punches a hole, and starts listening for voice frames.
    /// This should be called by BOTH sides simultaneously after MQTT negotiation.
    /// </summary>
    public async Task ConnectAndPunchHoleAsync(IPEndPoint remoteEndpoint, IPEndPoint? localRemoteEndpoint)
    {
        _remoteEndpoint = MapEndpoint(remoteEndpoint);
        _localRemoteEndpoint = localRemoteEndpoint != null ? MapEndpoint(localRemoteEndpoint) : null;
        
        _isConnected = true;

        if (_receiveTask == null)
            _receiveTask = Task.Run(ReceiveLoopAsync);

        // Send hole punch packets
        await PunchHoleAsync();
    }

    private IPEndPoint MapEndpoint(IPEndPoint ep)
    {
        if (_udp.Client.AddressFamily == AddressFamily.InterNetwork && ep.Address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ep.Address.IsIPv4MappedToIPv6)
                return new IPEndPoint(ep.Address.MapToIPv4(), ep.Port);
        }
        else if (_udp.Client.AddressFamily == AddressFamily.InterNetworkV6 && ep.Address.AddressFamily == AddressFamily.InterNetwork)
        {
            return new IPEndPoint(ep.Address.MapToIPv6(), ep.Port);
        }
        return ep;
    }

    public void Disconnect()
    {
        _isConnected = false;
        _remoteEndpoint = null;
        _localRemoteEndpoint = null;
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

                if (data.Length <= 1)
                {
                    // If it's a hole-punch packet, lock onto this endpoint!
                    if (result.RemoteEndPoint != null)
                    {
                        _remoteEndpoint = result.RemoteEndPoint;
                        _localRemoteEndpoint = null; // Drop the other one
                    }
                    continue; 
                }

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
        byte[] punch = new byte[] { 0x00 };
        for (int i = 0; i < 5; i++)
        {
            if (_remoteEndpoint != null)
            {
                try { await _udp.SendAsync(punch, punch.Length, _remoteEndpoint); } catch { }
            }
            if (_localRemoteEndpoint != null)
            {
                try { await _udp.SendAsync(punch, punch.Length, _localRemoteEndpoint); } catch { }
            }
            await Task.Delay(100);
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
