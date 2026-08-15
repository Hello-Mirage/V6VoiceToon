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

    // ─── State ───
    private bool _isConnected = false;
    private bool _isMuted = false;
    private IPEndPoint? _publicEndpoint;
    private string _logText = "";

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
    }

    protected override void OnClosed(EventArgs e)
    {
        Disconnect();
        base.OnClosed(e);
    }

    // ═══════════════════════════════════════════
    //  STUN Discovery
    // ═══════════════════════════════════════════

    private async Task DiscoverStunEndpointAsync()
    {
        Log("Querying Google STUN server (stun.l.google.com:19302)...");
        SetStunStatus("Discovering...", Brushes.Orange);

        try
        {
            _publicEndpoint = await StunClient.DiscoverPublicEndpointAsync();

            if (_publicEndpoint != null)
            {
                string epStr = FormatEndpoint(_publicEndpoint);
                SetStunStatus("Endpoint discovered", FindBrush("AccentGreenBrush"));
                TxtEndpoint.Text = epStr;
                BtnCopyEndpoint.Visibility = Visibility.Visible;
                StunDot.Fill = FindBrush("AccentGreenBrush");
                Log($"Public endpoint: {epStr}");
            }
            else
            {
                SetStunStatus("STUN failed — no IPv6?", FindBrush("AccentYellowBrush"));
                StunDot.Fill = FindBrush("AccentYellowBrush");
                Log("STUN discovery failed. You may not have IPv6 connectivity.");
                Log("You can still connect directly on your local network.");
            }
        }
        catch (Exception ex)
        {
            SetStunStatus("STUN error", FindBrush("AccentRedBrush"));
            StunDot.Fill = FindBrush("AccentRedBrush");
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
    //  Connect / Disconnect
    // ═══════════════════════════════════════════

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnected)
            return;

        string input = TxtPeerAddress.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            Log("Please enter a peer address.");
            return;
        }

        var remoteEp = ParseEndpoint(input);
        if (remoteEp == null)
        {
            Log($"Invalid address: {input}");
            return;
        }

        // Map IPv4 to IPv6 if needed
        if (remoteEp.Address.AddressFamily == AddressFamily.InterNetwork)
            remoteEp = new IPEndPoint(remoteEp.Address.MapToIPv6(), remoteEp.Port);

        Log($"Connecting to {FormatEndpoint(remoteEp)}...");

        try
        {
            // Create components
            _transport = new VoiceTransport();
            _capture = new AudioCapture();
            _playback = new AudioPlayback();

            // Wire pipeline
            _capture.OnFrameEncoded += async (data, length) =>
            {
                if (!_isMuted)
                    await _transport.SendFrameAsync(data, length);
            };

            _transport.OnFrameReceived += (data) =>
            {
                if (data.Length <= 1) return; // Skip hole-punch
                _playback.PlayOpusFrame(data);
            };

            // Connect
            _transport.Connect(remoteEp);

            Log("Punching NAT hole...");
            await _transport.PunchHoleAsync();

            _playback.Start();
            _capture.Start();

            _isConnected = true;
            _isMuted = false;

            // Update UI
            SetConnectionState(true, remoteEp);
            Log($"Connected! Local port: {_transport.LocalPort}");
        }
        catch (Exception ex)
        {
            Log($"Connection error: {ex.Message}");
            Disconnect();
        }
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        Disconnect();
    }

    private void Disconnect()
    {
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
            try { _transport.Dispose(); } catch { }
            _transport = null;
        }

        _isConnected = false;
        _isMuted = false;

        Dispatcher.Invoke(() => SetConnectionState(false, null));
        Log("Disconnected.");
    }

    private void SetConnectionState(bool connected, IPEndPoint? remote)
    {
        if (connected)
        {
            // Show connected state
            ConnectionDot.Fill = FindBrush("AccentGreenBrush");
            TxtConnectionStatus.Text = "Connected";
            TxtConnectionStatus.Foreground = FindBrush("AccentGreenBrush");
            TxtConnectionDetail.Text = remote != null ? FormatEndpoint(remote) : "";

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
            TxtConnectionDetail.Text = "Enter a peer address to connect";

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
        Disconnect();
        Close();
    }

    private void BtnCopyEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtEndpoint.Text))
        {
            try
            {
                Clipboard.SetText(TxtEndpoint.Text);
                Log("Endpoint copied to clipboard.");

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

    private static IPEndPoint? ParseEndpoint(string input)
    {
        try
        {
            if (IPEndPoint.TryParse(input, out var ep))
                return ep;

            // [IPv6]:port
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
                // IPv4:port
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
}
