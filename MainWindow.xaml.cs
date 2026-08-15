using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using V6VoiceToon.Audio;
using V6VoiceToon.Network;

namespace V6VoiceToon;

public partial class MainWindow : Window
{
    // ─── Voice pipeline components ───
    private VoiceTransport? _transport;
    private AudioCapture? _capture;
    private AudioPlayback? _playback;
    
    // ─── Signaling ───
    private SignalingClient? _signaling;

    // ─── State ───
    private bool _isConnected = false;
    private bool _isMuted = false;
    private IPEndPoint? _publicEndpoint;
    private string _logText = "";
    
    // Remote peer that is currently calling us
    private string? _incomingCallerId;
    private IPEndPoint? _incomingEndpoint;
    
    // The ID of the person we are currently calling/connected to
    private string? _connectedPeerId;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    // ═══════════════════════════════════════════
    //  Lifecycle
    // ═══════════════════════════════════════════

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PopulateDevices();
        await DiscoverStunEndpointAsync();
        await ConnectSignalingAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        Disconnect(true);
        _signaling?.Dispose();
        base.OnClosed(e);
    }
    
    private async Task ConnectSignalingAsync()
    {
        try
        {
            _signaling = new SignalingClient();
            
            _signaling.OnIncomingCall += (callerId, endpoint) =>
            {
                Dispatcher.Invoke(() => HandleIncomingCall(callerId, endpoint));
            };
            
            _signaling.OnCallAccepted += (acceptorId, endpoint) =>
            {
                Dispatcher.Invoke(() => CompleteConnection(acceptorId, endpoint));
            };
            
            _signaling.OnLog += (msg) => 
            {
                Log($"[MQTT] {msg}");
            };
            
            _signaling.OnCallRejected += (rejectorId) =>
            {
                Dispatcher.Invoke(() => 
                {
                    Log($"Call was rejected by {rejectorId}.");
                    Disconnect(false);
                });
            };
            
            _signaling.OnRemoteDisconnect += (peerId) =>
            {
                Dispatcher.Invoke(() => 
                {
                    Log($"Remote peer {peerId} disconnected.");
                    Disconnect(false);
                });
            };
            
            await _signaling.ConnectAsync();
            
            SetStunStatus("Online - Your ID:", FindBrush("AccentGreenBrush"));
            TxtEndpoint.Text = _signaling.MyId;
            StunDot.Fill = FindBrush("AccentGreenBrush");
            Log($"Connected to signaling server. Your ID is {_signaling.MyId}");
        }
        catch (Exception ex)
        {
            SetStunStatus("Signaling error", FindBrush("AccentRedBrush"));
            StunDot.Fill = FindBrush("AccentRedBrush");
            Log($"Failed to connect to signaling server: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════
    //  STUN Discovery
    // ═══════════════════════════════════════════

    private async Task DiscoverStunEndpointAsync()
    {
        Log("Querying Google STUN server (stun.l.google.com:19302)...");
        try
        {
            _publicEndpoint = await StunClient.DiscoverPublicEndpointAsync();

            if (_publicEndpoint != null)
            {
                Log($"Public endpoint discovered: {FormatEndpoint(_publicEndpoint)}");
            }
            else
            {
                Log("STUN discovery failed. You may not have IPv6 connectivity.");
            }
        }
        catch (Exception ex)
        {
            Log($"STUN error: {ex.Message}");
        }
    }

    private void SetStunStatus(string text, Brush color)
    {
        TxtStunStatus.Text = text;
        TxtStunStatus.Foreground = color;
    }

    // ═══════════════════════════════════════════
    //  Device Enumeration
    // ═══════════════════════════════════════════

    private void PopulateDevices()
    {
        var inputs = AudioCapture.ListInputDevices();
        CmbInputDevice.Items.Clear();
        foreach (var (idx, name) in inputs)
            CmbInputDevice.Items.Add($"[{idx}] {name}");
        if (CmbInputDevice.Items.Count > 0)
            CmbInputDevice.SelectedIndex = 0;

        var outputs = AudioPlayback.ListOutputDevices();
        CmbOutputDevice.Items.Clear();
        foreach (var (idx, name) in outputs)
            CmbOutputDevice.Items.Add($"[{idx}] {name}");
        if (CmbOutputDevice.Items.Count > 0)
            CmbOutputDevice.SelectedIndex = 0;
    }

    // ═══════════════════════════════════════════
    //  Connect / Disconnect / Call Handling
    // ═══════════════════════════════════════════

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnected || _signaling == null)
            return;
            
        if (_publicEndpoint == null)
        {
            Log("Cannot call: STUN endpoint not yet discovered. Wait a moment.");
            return;
        }

        string targetId = TxtPeerAddress.Text.Trim();
        if (string.IsNullOrEmpty(targetId) || targetId.Length < 4)
        {
            Log("Please enter a valid Peer ID.");
            return;
        }
        
        if (targetId == _signaling.MyId)
        {
            Log("You cannot call yourself.");
            return;
        }

        Log($"Calling ID {targetId}...");
        BtnConnect.Content = "Calling...";
        BtnConnect.IsEnabled = false;
        
        _connectedPeerId = targetId;

        try
        {
            await _signaling.SendCallRequestAsync(targetId, _publicEndpoint);
            Log("Ringing...");
            // Wait for OnCallAccepted
        }
        catch (Exception ex)
        {
            Log($"Signaling error: {ex.Message}");
            BtnConnect.Content = "Connect";
            BtnConnect.IsEnabled = true;
        }
    }
    
    private async void HandleIncomingCall(string callerId, IPEndPoint endpoint)
    {
        if (_isConnected)
        {
            // Already in a call, reject
            if (_signaling != null)
                await _signaling.SendCallRejectAsync(callerId);
            Log($"Auto-rejected call from {callerId} (already in call).");
            return;
        }
        
        Log($"Incoming call from ID {callerId}...");
        _incomingCallerId = callerId;
        _incomingEndpoint = endpoint;
        
        // Show incoming call UI
        TxtIncomingCaller.Text = $"ID: {callerId}";
        IncomingCallPanel.Visibility = Visibility.Visible;
    }
    
    private async void BtnAcceptCall_Click(object sender, RoutedEventArgs e)
    {
        IncomingCallPanel.Visibility = Visibility.Collapsed;
        
        if (_incomingCallerId != null && _incomingEndpoint != null && _signaling != null && _publicEndpoint != null)
        {
            Log($"Accepting call from {_incomingCallerId}...");
            _connectedPeerId = _incomingCallerId;
            await _signaling.SendCallAcceptAsync(_incomingCallerId, _publicEndpoint);
            
            // We are accepted, complete the pipeline with their endpoint
            CompleteConnection(_incomingCallerId, _incomingEndpoint);
        }
        else
        {
            Log("Failed to accept call: Missing public endpoint (STUN failed).");
            Disconnect(false);
        }
    }
    
    private async void BtnRejectCall_Click(object sender, RoutedEventArgs e)
    {
        IncomingCallPanel.Visibility = Visibility.Collapsed;
        
        if (_incomingCallerId != null && _signaling != null)
        {
            Log("Call rejected.");
            await _signaling.SendCallRejectAsync(_incomingCallerId);
        }
        
        _incomingCallerId = null;
        _incomingEndpoint = null;
    }
    
    private async void CompleteConnection(string peerId, IPEndPoint peerEndpoint)
    {
        try
        {
            _connectedPeerId = peerId;
            
            // Map IPv4 to IPv6 if needed
            if (peerEndpoint.Address.AddressFamily == AddressFamily.InterNetwork)
                peerEndpoint = new IPEndPoint(peerEndpoint.Address.MapToIPv6(), peerEndpoint.Port);
                
            Log($"Connecting P2P stream to {FormatEndpoint(peerEndpoint)}...");

            // Start Transport
            if (_transport != null)
            {
                _transport.Dispose();
            }
            _transport = new VoiceTransport();
            
            // Wire up voice data event
            _transport.OnFrameReceived += (data) =>
            {
                _playback?.PlayOpusFrame(data);
            };
            
            // Start audio capture
            if (_capture == null)
            {
                _capture = new AudioCapture();
                _capture.OnFrameEncoded += async (data, length) =>
                {
                    if (!_isMuted && _transport != null)
                        await _transport.SendFrameAsync(data, length);
                };
                
                if (CmbInputDevice.SelectedIndex >= 0)
                    _capture.SetDevice(CmbInputDevice.SelectedIndex);
            }
            
            // Start audio playback
            if (_playback == null)
            {
                _playback = new AudioPlayback();
                if (CmbOutputDevice.SelectedIndex >= 0)
                    _playback.SetDevice(CmbOutputDevice.SelectedIndex);
            }

            // Punch hole and connect!
            await _transport.ConnectAndPunchHoleAsync(peerEndpoint);

            _playback.Start();
            _capture.Start();

            _isConnected = true;
            _isMuted = false;

            // Update UI
            SetConnectionState(true, peerId);
            Log($"Connected! P2P Hole Punch successful.");
        }
        catch (Exception ex)
        {
            Log($"Failed to start audio pipeline: {ex.Message}");
            Disconnect(true);
        }
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        Disconnect(true);
    }

    private void Disconnect(bool sendDisconnectSignal)
    {
        if (sendDisconnectSignal && _signaling != null && _connectedPeerId != null)
        {
            // Fire and forget the disconnect signal
            _ = _signaling.SendDisconnectAsync(_connectedPeerId);
        }

        if (_capture != null)
        {
            try { _capture.Stop(); } catch { }
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }

        if (_playback != null)
        {
            try { _playback.Stop(); } catch { }
            try { _playback.Dispose(); } catch { }
            _playback = null;
        }
        
        if (_transport != null)
        {
            try { _transport.Disconnect(); } catch { }
            try { _transport.Dispose(); } catch { }
            _transport = null;
        }

        _isConnected = false;
        _isMuted = false;
        _incomingCallerId = null;
        _incomingEndpoint = null;
        _connectedPeerId = null;

        Dispatcher.Invoke(() => 
        {
            IncomingCallPanel.Visibility = Visibility.Collapsed;
            SetConnectionState(false, null);
        });
        
        Log("Disconnected.");
    }

    private void SetConnectionState(bool connected, string? peerId)
    {
        if (connected)
        {
            // Show connected state
            ConnectionDot.Fill = FindBrush("AccentGreenBrush");
            TxtConnectionStatus.Text = "Connected";
            TxtConnectionStatus.Foreground = FindBrush("AccentGreenBrush");
            TxtConnectionDetail.Text = peerId != null ? $"In call with ID {peerId}" : "";

            BtnConnect.Content = "Connected";
            BtnConnect.IsEnabled = false;
            BtnConnect.Background = FindBrush("AccentGreenBrush");
            TxtPeerAddress.IsEnabled = false;

            BtnDisconnect.Visibility = Visibility.Visible;
            BtnMic.IsEnabled = true;

            // Show visualizer and start animations
            LevelVisualizer.Visibility = Visibility.Visible;
            TxtMicStatus.Text = "Tap microphone to mute";

            var pulseStoryboard = (Storyboard)FindResource("PulseAnimation");
            pulseStoryboard.Begin();

            var levelStoryboard = (Storyboard)FindResource("LevelAnimation");
            levelStoryboard.Begin();

            // Update mic button appearance
            UpdateMicButton();
        }
        else
        {
            // Show disconnected state
            ConnectionDot.Fill = FindBrush("TextMutedBrush");
            TxtConnectionStatus.Text = "Disconnected";
            TxtConnectionStatus.Foreground = FindBrush("TextSecondaryBrush");
            TxtConnectionDetail.Text = "Enter a peer ID to connect";

            BtnConnect.Content = "Connect";
            BtnConnect.IsEnabled = true;
            BtnConnect.Background = (Brush)FindResource("ConnectGradient");
            TxtPeerAddress.IsEnabled = true;

            BtnDisconnect.Visibility = Visibility.Collapsed;
            BtnMic.IsEnabled = false;

            LevelVisualizer.Visibility = Visibility.Collapsed;
            TxtMicStatus.Text = "";

            // Stop animations
            try
            {
                var pulseStoryboard = (Storyboard)FindResource("PulseAnimation");
                pulseStoryboard.Stop();
                MicPulseRing.Opacity = 0;

                var levelStoryboard = (Storyboard)FindResource("LevelAnimation");
                levelStoryboard.Stop();
            }
            catch { }
        }
    }

    // ═══════════════════════════════════════════
    //  Mute Toggle
    // ═══════════════════════════════════════════

    private void BtnMic_Click(object sender, RoutedEventArgs e)
    {
        if (!_isConnected) return;

        _isMuted = !_isMuted;
        UpdateMicButton();

        if (_isMuted)
        {
            Log("Microphone muted.");

            // Stop pulse animation
            var pulseStoryboard = (Storyboard)FindResource("PulseAnimation");
            pulseStoryboard.Stop();
            MicPulseRing.Opacity = 0;
        }
        else
        {
            Log("Microphone unmuted.");

            // Restart pulse animation
            var pulseStoryboard = (Storyboard)FindResource("PulseAnimation");
            pulseStoryboard.Begin();
        }
    }

    private void UpdateMicButton()
    {
        if (_isMuted)
        {
            TxtMicStatus.Text = "🔇 Muted — tap to unmute";
            TxtMicStatus.Foreground = FindBrush("AccentRedBrush");
            MicPulseRing.Stroke = FindBrush("AccentRedBrush");
        }
        else
        {
            TxtMicStatus.Text = "🎤 Live — tap to mute";
            TxtMicStatus.Foreground = FindBrush("AccentGreenBrush");
            MicPulseRing.Stroke = FindBrush("AccentCyanBrush");
        }
    }

    // ═══════════════════════════════════════════
    //  Device Selection
    // ═══════════════════════════════════════════

    private void CmbInputDevice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CmbInputDevice.SelectedIndex >= 0 && _capture != null)
        {
            _capture.SetDevice(CmbInputDevice.SelectedIndex);
            Log($"Input device: {CmbInputDevice.SelectedItem}");
        }
    }

    private void CmbOutputDevice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CmbOutputDevice.SelectedIndex >= 0 && _playback != null)
        {
            _playback.SetDevice(CmbOutputDevice.SelectedIndex);
            Log($"Output device: {CmbOutputDevice.SelectedItem}");
        }
    }

    // ═══════════════════════════════════════════
    //  Window Chrome
    // ═══════════════════════════════════════════

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            DragMove();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Disconnect(true);
        Close();
    }

    private void BtnCopyEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtEndpoint.Text) && TxtEndpoint.Text != "---")
        {
            try
            {
                Clipboard.SetText(TxtEndpoint.Text);
                Log("ID copied to clipboard.");

                // Visual feedback
                BtnCopyEndpoint.Content = "✓";
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1.5)
                };
                timer.Tick += (s, args) =>
                {
                    BtnCopyEndpoint.Content = "📋";
                    timer.Stop();
                };
                timer.Start();
            }
            catch { }
        }
    }

    private void BtnCloseLog_Click(object sender, RoutedEventArgs e)
    {
        LogPanel.Visibility = Visibility.Collapsed;
    }

    // ═══════════════════════════════════════════
    //  Logging
    // ═══════════════════════════════════════════

    private void Log(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string line = $"[{timestamp}] {message}\n";
        _logText += line;

        Dispatcher.Invoke(() =>
        {
            TxtLog.Text = _logText;
        });
    }

    // ═══════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════

    private SolidColorBrush FindBrush(string key)
    {
        return (SolidColorBrush)FindResource(key);
    }

    private static string FormatEndpoint(IPEndPoint ep)
    {
        if (ep.Address.AddressFamily == AddressFamily.InterNetworkV6)
            return $"[{ep.Address}]:{ep.Port}";
        return $"{ep.Address}:{ep.Port}";
    }
}
