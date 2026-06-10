# CastDriver

Cast all your Windows PC audio to Chromecast devices. CastDriver captures whatever is
playing on your default audio output (WASAPI loopback) and streams it live to one or more
Chromecasts on your network — with per-device volume control, a system-tray UI, and no
virtual audio driver required.

## Download

**[⬇ Download the latest release](https://github.com/ITMarco/CastDriver/releases/latest)** — grab `CastDriver.exe` (~4 MB).

This build needs the **.NET 10 Desktop Runtime** (a free, one-time Microsoft install):

👉 **[Download .NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)** — under *Run desktop apps*, pick **.NET Desktop Runtime → Windows x64**.

Then run `CastDriver.exe` (see [*Installing / first run*](#installing--first-run-end-users) below). A larger all-in-one build that needs no .NET install may be offered separately.

## Features

- **Multi-device casting** — stream to several Chromecasts at once, discovered automatically via mDNS.
- **Per-device volume** — control each Chromecast's own volume instantly via the Cast protocol.
- **Cast Only mode** — mute the local speakers while the cast keeps playing.
- **Low-latency live stream** — chunked streaming tuned for a small, stable buffer.
- **Selectable format** — WAV (lossless) or MP3 (compressed, wide compatibility incl. picky renderers).
- **Choosable source** — any playback device (loopback) or input (line-in / mic).
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

## Installing / first run (end users)

CastDriver ships as a single self-contained `.exe` — **no .NET install required**.

1. **Download** `CastDriver.exe` and double-click it. It lives in the system tray (a small cast icon); click the tray icon to open the window.
2. **SmartScreen:** because the app isn't code-signed, Windows may show *"Windows protected your PC."* Click **More info → Run anyway**.
3. **Firewall:** on first launch Windows asks whether to allow network access. Click **Allow** — this is required so your Chromecast / TV can reach the audio stream. (If you dismissed it, casting may not produce sound until you allow CastDriver through the firewall for private networks.)
4. Pick a **source** (your speakers/output, or an input like a line-in), then hit **Cast ▶** next to a discovered device. Adjust each device's volume independently; **Cast Only** mutes your local speakers while the cast keeps playing.

Supports **Chromecast** (and Google speaker groups) plus **DLNA/UPnP** renderers such as smart TVs and AV receivers.

## Building a release

**Self-contained** single-file (~73 MB, runs anywhere — no .NET needed). Best for shipping to people who may not have .NET installed:

```powershell
dotnet publish CastDriver.UI -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none
```

**Framework-dependent** single-file (~4 MB, requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download)). Best when you know the target has .NET:

```powershell
dotnet publish CastDriver.UI -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -o publish-framework-dependent
```

The single `CastDriver.UI.exe` is the whole app (rename to `CastDriver.exe` freely before distributing).

## Notes

- A Windows Firewall rule allowing inbound connections to the app may be needed so devices can reach the local stream.
- No virtual audio driver is required; CastDriver captures the default render device directly.
- Licensed under **GNU GPL v3.0** — see [LICENSE](LICENSE).
