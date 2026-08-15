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
    public string Endpoint { get; set; } = "";
    public string LocalEndpoint { get; set; } = "";
}

public class SignalingClient : IDisposable
{
    private IMqttClient? _mqttClient;
    private readonly string _myId;
    private readonly string _baseTopic = "v6voicetoon/users/";

    public event Action<string, IPEndPoint, IPEndPoint?>? OnIncomingCall;
    public event Action<string, IPEndPoint, IPEndPoint?>? OnCallAccepted;
    public event Action<string>? OnCallRejected;
    public event Action<string>? OnRemoteDisconnect;
    public event Action<string>? OnLog;

    public string MyId => _myId;

    public SignalingClient()
    {
        _myId = new Random().Next(100000, 999999).ToString();
    }

    public async Task ConnectAsync()
    {
        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer("broker.hivemq.com", 1883)
            .WithClientId($"v6voice_{Guid.NewGuid()}")
            .WithCleanSession()
            .Build();

        _mqttClient.ApplicationMessageReceivedAsync += HandleIncomingMessage;

        await _mqttClient.ConnectAsync(options);
        
        var subscribeOptions = factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic($"{_baseTopic}{_myId}").WithAtLeastOnceQoS())
            .Build();
            
        await _mqttClient.SubscribeAsync(subscribeOptions);
        OnLog?.Invoke("Subscribed to signaling topic.");
    }

    private Task HandleIncomingMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            string payload = e.ApplicationMessage.ConvertPayloadToString();
            OnLog?.Invoke($"Received MQTT: {payload}");
            
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var msg = JsonSerializer.Deserialize<SignalingMessage>(payload, options);

            if (msg == null)
            {
                OnLog?.Invoke("Failed to parse signaling message (null).");
                return Task.CompletedTask;
            }

            IPEndPoint? endpoint = ParseEndpoint(msg.Endpoint);
            IPEndPoint? localEndpoint = ParseEndpoint(msg.LocalEndpoint);
            
            if (endpoint == null && !string.IsNullOrEmpty(msg.Endpoint))
            {
                OnLog?.Invoke($"Warning: Could not parse endpoint '{msg.Endpoint}'");
            }

            switch (msg.Type)
            {
                case "CallRequest":
                    if (endpoint != null)
                        OnIncomingCall?.Invoke(msg.CallerId, endpoint, localEndpoint);
                    break;

                case "CallAccept":
                    if (endpoint != null)
                        OnCallAccepted?.Invoke(msg.CallerId, endpoint, localEndpoint);
                    else
                        OnLog?.Invoke("CallAccept ignored: Endpoint is null.");
                    break;

                case "CallReject":
                    OnCallRejected?.Invoke(msg.CallerId);
                    break;

                case "Disconnect":
                    OnRemoteDisconnect?.Invoke(msg.CallerId);
                    break;
                    
                default:
                    OnLog?.Invoke($"Unknown message type: {msg.Type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"MQTT Parse Exception: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public async Task SendCallRequestAsync(string targetId, IPEndPoint myEndpoint, IPEndPoint myLocalEndpoint)
    {
        await PublishMessageAsync(targetId, new SignalingMessage
        {
            Type = "CallRequest",
            CallerId = _myId,
            Endpoint = FormatEndpoint(myEndpoint),
            LocalEndpoint = FormatEndpoint(myLocalEndpoint)
        });
    }

    public async Task SendCallAcceptAsync(string targetId, IPEndPoint myEndpoint, IPEndPoint myLocalEndpoint)
    {
        await PublishMessageAsync(targetId, new SignalingMessage
        {
            Type = "CallAccept",
            CallerId = _myId,
            Endpoint = FormatEndpoint(myEndpoint),
            LocalEndpoint = FormatEndpoint(myLocalEndpoint)
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
        if (_mqttClient == null || !_mqttClient.IsConnected)
        {
            OnLog?.Invoke("Cannot publish: MQTT client disconnected.");
            return;
        }

        string payload = JsonSerializer.Serialize(msg);
        OnLog?.Invoke($"Sending MQTT to {targetId}: {payload}");
        
        var applicationMessage = new MqttApplicationMessageBuilder()
            .WithTopic($"{_baseTopic}{targetId}")
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
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
            if (string.IsNullOrEmpty(input)) return null;
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
        if (ep == null) return "";
        if (ep.Address.AddressFamily == AddressFamily.InterNetworkV6)
            return $"[{ep.Address}]:{ep.Port}";
        return $"{ep.Address}:{ep.Port}";
    }
}
