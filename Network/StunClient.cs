using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace V6VoiceToon.Network;

/// <summary>
/// Minimal STUN Binding Request client (RFC 5389).
/// Discovers the public IPv6 address and port by querying a STUN server.
/// </summary>
public static class StunClient
{
    // STUN message constants
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingResponse = 0x0101;
    private const uint MagicCookie = 0x2112A442;
    private const ushort AttrXorMappedAddress = 0x0020;
    private const ushort AttrMappedAddress = 0x0001;

    // Address family
    private const byte FamilyIPv6 = 0x02;

    /// <summary>
    /// Queries the given STUN server over IPv6 and returns the reflexive (public) endpoint.
    /// </summary>
    public static async Task<IPEndPoint?> DiscoverPublicEndpointAsync(
        string stunHost = "stun.l.google.com",
        int stunPort = 19302,
        int timeoutMs = 5000)
    {
        // Resolve the STUN server to an IPv6 address
        var addresses = await Dns.GetHostAddressesAsync(stunHost);
        var stunAddress = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);

        if (stunAddress == null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [!] No IPv6 address found for STUN server. Trying IPv4-mapped IPv6...");
            Console.ResetColor();

            // Fall back: try to use an IPv4 address mapped into IPv6 space
            var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (v4 == null)
                return null;

            stunAddress = v4.MapToIPv6();
        }

        var stunEndpoint = new IPEndPoint(stunAddress, stunPort);

        // Build the 20-byte STUN Binding Request
        byte[] transactionId = new byte[12];
        RandomNumberGenerator.Fill(transactionId);

        byte[] request = new byte[20];
        // Message Type: Binding Request (0x0001)
        request[0] = (byte)(BindingRequest >> 8);
        request[1] = (byte)(BindingRequest & 0xFF);
        // Message Length: 0 (no attributes)
        request[2] = 0;
        request[3] = 0;
        // Magic Cookie: 0x2112A442
        request[4] = unchecked((byte)(MagicCookie >> 24));
        request[5] = unchecked((byte)(MagicCookie >> 16));
        request[6] = unchecked((byte)(MagicCookie >> 8));
        request[7] = unchecked((byte)(MagicCookie & 0xFF));
        // Transaction ID: 12 random bytes
        Array.Copy(transactionId, 0, request, 8, 12);

        // Send via UDP over IPv6
        UdpClient udp = null!;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetworkV6);
            try { udp.Client.DualMode = true; } catch { }
            udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        }
        catch
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        }

        try
        {
            udp.Client.ReceiveTimeout = timeoutMs;
            await udp.SendAsync(request, request.Length, stunEndpoint);
    
            // Wait for response
            using var cts = new CancellationTokenSource(timeoutMs);
            var result = await udp.ReceiveAsync(cts.Token);
            return ParseBindingResponse(result.Buffer, transactionId);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        finally
        {
            udp?.Dispose();
        }
    }

    private static IPEndPoint? ParseBindingResponse(byte[] data, byte[] expectedTxId)
    {
        if (data.Length < 20)
            return null;

        // Verify it's a Binding Response
        ushort msgType = (ushort)((data[0] << 8) | data[1]);
        if (msgType != BindingResponse)
            return null;

        // Verify magic cookie
        uint cookie = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);
        if (cookie != MagicCookie)
            return null;

        // Verify transaction ID
        for (int i = 0; i < 12; i++)
        {
            if (data[8 + i] != expectedTxId[i])
                return null;
        }

        ushort msgLen = (ushort)((data[2] << 8) | data[3]);
        int offset = 20; // Start of attributes

        // Parse attributes looking for XOR-MAPPED-ADDRESS or MAPPED-ADDRESS
        while (offset + 4 <= 20 + msgLen)
        {
            ushort attrType = (ushort)((data[offset] << 8) | data[offset + 1]);
            ushort attrLen = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
            int attrStart = offset + 4;

            if (attrType == AttrXorMappedAddress)
            {
                return ParseXorMappedAddress(data, attrStart, attrLen);
            }
            else if (attrType == AttrMappedAddress)
            {
                return ParseMappedAddress(data, attrStart, attrLen);
            }

            // Attributes are padded to 4-byte boundaries
            offset = attrStart + ((attrLen + 3) & ~3);
        }

        return null;
    }

    private static IPEndPoint? ParseXorMappedAddress(byte[] data, int offset, int length)
    {
        if (length < 4)
            return null;

        byte family = data[offset + 1];
        ushort xorPort = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
        ushort port = (ushort)(xorPort ^ (MagicCookie >> 16));

        if (family == FamilyIPv6 && length >= 20)
        {
            // XOR with magic cookie (4 bytes) + transaction ID (12 bytes) = 16 bytes
            byte[] xorKey = new byte[16];
            xorKey[0] = unchecked((byte)(MagicCookie >> 24));
            xorKey[1] = unchecked((byte)(MagicCookie >> 16));
            xorKey[2] = unchecked((byte)(MagicCookie >> 8));
            xorKey[3] = unchecked((byte)(MagicCookie & 0xFF));
            // Transaction ID from the original response header
            // (bytes 8..19 of the STUN message — but we need the full response for this)
            // We'll read from the response data itself
            // Actually, the XOR key for IPv6 is: Magic Cookie || Transaction ID
            // We need to get the transaction ID from the response header
            // The response data starts at data[0], so txid is at data[8..19] — but data here
            // is the full response buffer, and offset is relative to data start.
            // Let me reconsider: data is the full packet, offset points into attribute value.

            // We need the full packet to get the transaction ID
            // Since this is called from ParseBindingResponse which has the full data array,
            // the txid is at data[8..19]
            Array.Copy(data, 4, xorKey, 0, 4);  // Magic cookie from header
            Array.Copy(data, 8, xorKey, 4, 12); // Transaction ID from header

            byte[] addrBytes = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                addrBytes[i] = (byte)(data[offset + 4 + i] ^ xorKey[i]);
            }

            return new IPEndPoint(new IPAddress(addrBytes), port);
        }
        else if (family == 0x01 && length >= 8) // IPv4
        {
            byte[] addrBytes = new byte[4];
            uint xorAddr = (uint)(
                (data[offset + 4] << 24) |
                (data[offset + 5] << 16) |
                (data[offset + 6] << 8) |
                data[offset + 7]);
            uint addr = xorAddr ^ MagicCookie;
            addrBytes[0] = (byte)(addr >> 24);
            addrBytes[1] = (byte)(addr >> 16);
            addrBytes[2] = (byte)(addr >> 8);
            addrBytes[3] = (byte)(addr & 0xFF);

            var ipv4 = new IPAddress(addrBytes);
            // Map to IPv6 to stay in our IPv6-only world
            return new IPEndPoint(ipv4.MapToIPv6(), port);
        }

        return null;
    }

    private static IPEndPoint? ParseMappedAddress(byte[] data, int offset, int length)
    {
        if (length < 4)
            return null;

        byte family = data[offset + 1];
        ushort port = (ushort)((data[offset + 2] << 8) | data[offset + 3]);

        if (family == FamilyIPv6 && length >= 20)
        {
            byte[] addrBytes = new byte[16];
            Array.Copy(data, offset + 4, addrBytes, 0, 16);
            return new IPEndPoint(new IPAddress(addrBytes), port);
        }
        else if (family == 0x01 && length >= 8)
        {
            byte[] addrBytes = new byte[4];
            Array.Copy(data, offset + 4, addrBytes, 0, 4);
            var ipv4 = new IPAddress(addrBytes);
            return new IPEndPoint(ipv4.MapToIPv6(), port);
        }

        return null;
    }
}
