using Concentus.Structs;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace V6VoiceToon.Audio;

/// <summary>
/// Decodes incoming Opus frames and plays them through the default speaker.
/// </summary>
public sealed class AudioPlayback : IDisposable
{
    private readonly OpusDecoder _decoder;
    private readonly BufferedWaveProvider _bufferedProvider;
    private readonly WaveOutEvent _waveOut;
    private readonly short[] _decodeBuffer = new short[AudioCapture.FrameSize];

    public AudioPlayback()
    {
        _decoder = new OpusDecoder(AudioCapture.SampleRate, AudioCapture.Channels);

        _bufferedProvider = new BufferedWaveProvider(
            new WaveFormat(AudioCapture.SampleRate, AudioCapture.BitsPerSample, AudioCapture.Channels))
        {
            // Buffer up to 500ms of audio for jitter absorption
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
        };

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 100, // 100ms latency target
        };

        _waveOut.Init(_bufferedProvider);
    }

    /// <summary>
    /// Lists available audio output devices.
    /// </summary>
    public static List<(int index, string name)> ListOutputDevices()
    {
        var devices = new List<(int, string)>();
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int i = 0; i < endpoints.Count; i++)
            {
                devices.Add((i, endpoints[i].FriendlyName));
            }
        }
        catch
        {
            devices.Add((0, "Default Output Device"));
        }
        return devices;
    }

    /// <summary>
    /// Sets the output device index (default 0).
    /// </summary>
    public void SetDevice(int deviceIndex)
    {
        _waveOut.DeviceNumber = deviceIndex;
        // Re-init with the new device
        _waveOut.Init(_bufferedProvider);
    }

    public void Start()
    {
        _waveOut.Play();
    }

    public void Stop()
    {
        _waveOut.Stop();
    }

    /// <summary>
    /// Decodes an incoming Opus frame and queues it for playback.
    /// </summary>
    public void PlayOpusFrame(byte[] opusData)
    {
        try
        {
            int samplesDecoded = _decoder.Decode(
                opusData, 0, opusData.Length,
                _decodeBuffer, 0, AudioCapture.FrameSize);

            if (samplesDecoded > 0)
            {
                // Convert short[] to byte[] for BufferedWaveProvider
                int byteCount = samplesDecoded * sizeof(short);
                byte[] pcmBytes = new byte[byteCount];
                Buffer.BlockCopy(_decodeBuffer, 0, pcmBytes, 0, byteCount);
                _bufferedProvider.AddSamples(pcmBytes, 0, byteCount);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [!] Decode error: {ex.Message}");
            Console.ResetColor();
        }
    }

    public void Dispose()
    {
        _waveOut.Stop();
        _waveOut.Dispose();
    }
}
