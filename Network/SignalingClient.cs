using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;

namespace V6VoiceToon.Network;

public class SignalingMessage
{
    public string Type { get; set; } = "";
    public string CallerId { get; set; } = "";
    public string Endpoint { get; set; } = ""; // e.g. "[2a02:1234::1]:55555"
}

public class SignalingClient : IDisposable
{
    private IMqttClient? _mqttClient;
    private readonly string _myId;
    private readonly string _baseTopic = "v6voicetoon/users/";

    public event Action<string, IPEndPoint>? OnIncomingCall;
    public event Action<string, IPEndPoint>? OnCallAccepted;
    public event Action<string>? OnCallRejected;
    public event Action<string>? OnRemoteDisconnect;

    public string MyId => _myId;

    public SignalingClient()
    {
        // Generate a random 6-digit ID
        _myId = new Random().Next(100000, 999999).ToString();
    }

    public async Task ConnectAsync()
    {
        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer("test.mosquitto.org", 1883)
            .WithClientId($"v6voice_{Guid.NewGuid()}")
            .WithCleanSession()
            .Build();

        _mqttClient.ApplicationMessageReceivedAsync += HandleIncomingMessage;

        await _mqttClient.ConnectAsync(options);
        await _mqttClient.SubscribeAsync($"{_baseTopic}{_myId}");
    }

    private Task HandleIncomingMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            string payload = e.ApplicationMessage.ConvertPayloadToString();
            var msg = JsonSerializer.Deserialize<SignalingMessage>(payload);

            if (msg == null) return Task.CompletedTask;

            IPEndPoint? endpoint = ParseEndpoint(msg.Endpoint);

            switch (msg.Type)
            {
                case "CallRequest":
                    if (endpoint != null)
                        OnIncomingCall?.Invoke(msg.CallerId, endpoint);
                    break;

                case "CallAccept":
                    if (endpoint != null)
                        OnCallAccepted?.Invoke(msg.CallerId, endpoint);
                    break;

                case "CallReject":
                    OnCallRejected?.Invoke(msg.CallerId);
                    break;

                case "Disconnect":
                    OnRemoteDisconnect?.Invoke(msg.CallerId);
                    break;
            }
        }
        catch { }

        return Task.CompletedTask;
    }

    public async Task SendCallRequestAsync(string targetId, IPEndPoint myEndpoint)
    {
        await PublishMessageAsync(targetId, new SignalingMessage
        {
            Type = "CallRequest",
            CallerId = _myId,
            Endpoint = FormatEndpoint(myEndpoint)
        });
    }

    public async Task SendCallAcceptAsync(string targetId, IPEndPoint myEndpoint)
    {
        await PublishMessageAsync(targetId, new SignalingMessage
        {
            Type = "CallAccept",
            CallerId = _myId,
            Endpoint = FormatEndpoint(myEndpoint)
        });
    }

    public async Task SendCallRejectAsync(string targetId)
    {
        await PublishMessageAsync(targetId, new SignalingMessage
        {
            Type = "CallReject",
            CallerId = _myId
        });
    }

    public async Task SendDisconnectAsync(string targetId)
    {
        await PublishMessageAsync(targetId, new SignalingMessage
        {
            Type = "Disconnect",
            CallerId = _myId
        });
    }

    private async Task PublishMessageAsync(string targetId, SignalingMessage msg)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected) return;

        string payload = JsonSerializer.Serialize(msg);
        var applicationMessage = new MqttApplicationMessageBuilder()
            .WithTopic($"{_baseTopic}{targetId}")
            .WithPayload(payload)
            .Build();

        await _mqttClient.PublishAsync(applicationMessage);
    }

    public void Dispose()
    {
        _mqttClient?.Dispose();
    }

    private static IPEndPoint? ParseEndpoint(string input)
    {
        try
        {
            if (IPEndPoint.TryParse(input, out var ep)) return ep;
            if (input.StartsWith('['))
            {
                int close = input.IndexOf(']');
                if (close < 0) return null;
                string addr = input[1..close];
                string port = input[(close + 1)..].TrimStart(':');
                if (IPAddress.TryParse(addr, out var a) && int.TryParse(port, out int p))
                    return new IPEndPoint(a, p);
            }
            else
            {
                int lastColon = input.LastIndexOf(':');
                if (lastColon > 0)
                {
                    string addr = input[..lastColon];
                    string port = input[(lastColon + 1)..];
                    if (IPAddress.TryParse(addr, out var a) && int.TryParse(port, out int p))
                        return new IPEndPoint(a, p);
                }
            }
        }
        catch { }
        return null;
    }

    private static string FormatEndpoint(IPEndPoint ep)
    {
        if (ep.Address.AddressFamily == AddressFamily.InterNetworkV6)
            return $"[{ep.Address}]:{ep.Port}";
        return $"{ep.Address}:{ep.Port}";
    }
}
