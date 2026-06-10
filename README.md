# CastDriver

Cast all your Windows PC audio to Chromecast devices. CastDriver captures whatever is
playing on your default audio output (WASAPI loopback) and streams it live to one or more
Chromecasts on your network — with per-device volume control, a system-tray UI, and no
virtual audio driver required.

## Features

- **Multi-device casting** — stream to several Chromecasts at once, discovered automatically via mDNS.
- **Per-device volume** — control each Chromecast's own volume instantly via the Cast protocol.
- **Cast Only mode** — mute the local speakers while the cast keeps playing.
- **Low-latency live stream** — chunked WAV streaming tuned for a small, stable buffer.
- **System tray app** — pinnable popup UI with a live audio-level meter.
- **Start with Windows** option.
- **Debug screen** — toggle logging, view and clear the diagnostics log.

## How it works

1. Captures the default render endpoint with `WasapiLoopbackCapture` (NAudio).
2. Converts to 16-bit PCM and serves it as a live, chunked `audio/wav` HTTP stream.
3. Speaks the Cast V2 protocol (TLS on port 8009) to launch the Default Media Receiver and `LOAD` the stream URL.
4. Sends `SET_VOLUME` control messages for instant per-device volume.

## Projects

| Project | Purpose |
|---|---|
| `CastDriver.Audio` | WASAPI loopback capture and PCM conversion |
| `CastDriver.Cast` | Discovery, Cast V2 protocol, HTTP media server |
| `CastDriver.Driver` | Optional Scream virtual-driver installer |
| `CastDriver.UI` | WPF tray application |

## Build & run

Requires the .NET SDK (net10.0-windows).

```powershell
dotnet build
dotnet run --project CastDriver.UI
```

## Notes

- A Windows Firewall rule allowing inbound connections to the app may be needed so Chromecasts can reach the local stream.
- No virtual audio driver is required; CastDriver captures the default render device directly.
