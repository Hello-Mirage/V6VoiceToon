using Concentus.Enums;
using Concentus.Structs;
using NAudio.Wave;

namespace V6VoiceToon.Audio;

/// <summary>
/// Captures audio from the default microphone, encodes it to Opus,
/// and fires a callback with each encoded frame.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    // Opus-optimal settings: 48kHz, 16-bit, mono
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;

    // 20ms frame = 960 samples at 48kHz
    public const int FrameDurationMs = 20;
    public const int FrameSize = SampleRate * FrameDurationMs / 1000; // 960

    private readonly WaveInEvent _waveIn;
    private readonly OpusEncoder _encoder;
    private readonly byte[] _encodeBuffer = new byte[4000]; // Max Opus frame
    private readonly short[] _pcmFrameBuffer = new short[FrameSize];
    private int _pcmBufferOffset = 0;

    /// <summary>
    /// Fired when an Opus-encoded frame is ready to send.
    /// Parameters: (byte[] opusData, int length)
    /// </summary>
    public event Action<byte[], int>? OnFrameEncoded;

    public AudioCapture()
    {
        _encoder = new OpusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP)
        {
            Bitrate = 32000,         // 32 kbps — good quality for voice
            Complexity = 5,          // Balanced CPU vs quality
            UseVBR = true,           // Variable bitrate for efficiency
            SignalType = OpusSignal.OPUS_SIGNAL_VOICE,
        };

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
            BufferMilliseconds = FrameDurationMs,
        };

        _waveIn.DataAvailable += OnDataAvailable;
    }

    /// <summary>
    /// Lists available audio input devices.
    /// </summary>
    public static List<(int index, string name)> ListInputDevices()
    {
        var devices = new List<(int, string)>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            devices.Add((i, caps.ProductName));
        }
        return devices;
    }

    /// <summary>
    /// Sets the input device index (default 0).
    /// </summary>
    public void SetDevice(int deviceIndex)
    {
        _waveIn.DeviceNumber = deviceIndex;
    }

    public void Start()
    {
        _waveIn.StartRecording();
    }

    public void Stop()
    {
        _waveIn.StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int bytesPerSample = BitsPerSample / 8;
        int samplesReceived = e.BytesRecorded / bytesPerSample;

        // Convert byte[] to short[]
        int byteOffset = 0;
        for (int i = 0; i < samplesReceived; i++)
        {
            short sample = (short)(e.Buffer[byteOffset] | (e.Buffer[byteOffset + 1] << 8));
            _pcmFrameBuffer[_pcmBufferOffset++] = sample;

            if (_pcmBufferOffset >= FrameSize)
            {
                EncodeAndSend();
                _pcmBufferOffset = 0;
            }

            byteOffset += bytesPerSample;
        }
    }

    private void EncodeAndSend()
    {
        try
        {
            int encodedLength = _encoder.Encode(
                _pcmFrameBuffer, 0, FrameSize,
                _encodeBuffer, 0, _encodeBuffer.Length);

            if (encodedLength > 0)
            {
                OnFrameEncoded?.Invoke(_encodeBuffer, encodedLength);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [!] Encode error: {ex.Message}");
            Console.ResetColor();
        }
    }

    public void Dispose()
    {
        _waveIn.StopRecording();
        _waveIn.Dispose();
    }
}
