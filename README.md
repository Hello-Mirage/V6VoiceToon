# V6 Voice Toon

A lightweight, IPv6-only peer-to-peer voice chat application built with .NET 8.

## Features

- **IPv6 Only** — All communication uses IPv6 exclusively
- **Google STUN** — NAT traversal via `stun.l.google.com:19302`
- **Opus Codec** — High-quality voice compression at 32kbps VBR
- **Zero Config** — No servers, no accounts, just connect and talk
- **Lightweight** — Only 2 dependencies (NAudio + Concentus), pure C#

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- IPv6 network connectivity
- Microphone and speakers/headphones

## Build & Run

```bash
# Clone and navigate to the project
cd V6

# Restore packages and run
dotnet run
```

## Usage

1. **Start the app** on both machines:
   ```
   dotnet run
   ```

2. **Share your endpoint** — The app will display your public IPv6 address and port discovered via STUN:
   ```
   [✓] Your public endpoint: [2001:db8::1]:54321
   ```

3. **Enter the remote peer's address** when prompted:
   ```
   Enter remote peer address ([IPv6]:port or IP:port): [2001:db8::2]:12345
   ```

4. **Start talking!** Audio flows bidirectionally in real-time.

5. **Press Q** to disconnect and quit.

## Architecture

```
Microphone → NAudio Capture → Opus Encode → UDP/IPv6 Send
                                                    ↓
                                              IPv6 Network
                                                    ↓
Speaker ← NAudio Playback ← Opus Decode ← UDP/IPv6 Receive
```

### Components

| Component | File | Purpose |
|-----------|------|---------|
| STUN Client | `Network/StunClient.cs` | Discovers public IPv6 endpoint via Google STUN |
| Voice Transport | `Network/VoiceTransport.cs` | UDP send/receive with NAT hole-punching |
| Audio Capture | `Audio/AudioCapture.cs` | Mic input → Opus encoding |
| Audio Playback | `Audio/AudioPlayback.cs` | Opus decoding → speaker output |
| Entry Point | `Program.cs` | CLI flow and pipeline orchestration |

## Audio Settings

| Setting | Value |
|---------|-------|
| Sample Rate | 48,000 Hz |
| Channels | Mono |
| Bit Depth | 16-bit |
| Frame Duration | 20ms |
| Opus Bitrate | 32 kbps (VBR) |
| Jitter Buffer | 500ms |
| Playback Latency | 100ms |

## Troubleshooting

### STUN discovery fails
- Ensure your machine has IPv6 connectivity (`ping -6 google.com`)
- Check your firewall allows outbound UDP to port 19302

### No audio
- Check that your microphone is connected and set as default
- The app lists detected input/output devices at startup

### Connection issues
- Both peers need either public IPv6 or compatible NAT types
- The app sends hole-punch packets automatically, but symmetric NATs may block P2P

## License

MIT
