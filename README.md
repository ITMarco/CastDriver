# CastDriver


**If you're an oldskool guy like me, you sometimes just prefer listening to mp3/flac files using Mediamonkey, Winamp, VLC or God forbid, Windows Media Player. The problem with those is that they don't support casting to your favourite audio system at your house. Well, this might be for you. Because sometimes you just want to hear that music on the home stereo and not through your $50 Target speakers**

**So cast all your Windows PC audio to Chromecast, Google speaker groups, Sonos, and DLNA devices (smart TVs, AV receivers, networked speakers) — with a per-device mixer, a graphic equalizer, and no virtual audio driver required.**

CastDriver captures whatever's playing on your PC (or a single app, or a line-in) and streams it live to one or more devices on your network, from a tidy system-tray app.

---

## ⬇ Download

**[Get the latest release](https://github.com/ITMarco/CastDriver/releases/latest).** Two builds are offered:

| File | Size | Needs .NET? |
|---|---|---|
| **`CastDriver.exe`** | ~4 MB | Yes — the free [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (under *Run desktop apps* → **.NET Desktop Runtime → Windows x64**) |
| **`CastDriver-standalone.exe`** | ~73 MB | No — runs anywhere, nothing to install |

If you're not sure, grab **`CastDriver-standalone.exe`** and just run it.

## Installing / first run

1. **Run the exe.** CastDriver lives in the **system tray** (a small cast icon). Left-click the icon to open the window; right-click for a quick menu (*Cast to*, *Stop all casting*, *Exit*).
2. **SmartScreen:** the app isn't code-signed, so Windows may show *"Windows protected your PC."* Click **More info → Run anyway**.
3. **Firewall:** the first time you cast, Windows may block the connection. If a device shows as casting but you hear nothing, CastDriver shows a **"Allow through firewall"** button — click it and approve the prompt (it adds the rule for you, no admin knowledge needed).

## How to use it

1. **Pick a Source** — your speakers/output (the whole PC), a specific **app** (🎵), or an **input** (🎤 line-in / mic). The list groups Devices on top and Applications below.
2. **Hit Cast ▶** next to a discovered device. Cast to several at once. Devices appear automatically as they're found; hit the **↻ refresh** button (top-right of *Cast to*) to scan again on demand.
3. **Per device:** adjust each device's **volume** and **🔇 mute** independently (mute feeds silence so it resumes instantly; no quality change).
4. **Equalizer:** open **🎚 Equalizer** for a 10-band graphic EQ with presets, savable custom presets, a pre-amp, and a one-click bypass to A/B it. EQ affects the cast only — your PC audio stays flat.

### Handy options
- **Cast Only** — silences your PC speakers while the cast keeps playing.
- **Cast everything except this app** — when an app is the source, cast the whole PC *minus* that app (e.g. mute notifications or a meeting from the cast).
- **Format** — WAV (lossless) or MP3 (smaller, wide compatibility incl. picky renderers like Sonos); pick the MP3 bitrate.
- **Latency** slider — trade lag for stability to suit your network.
- **Master volume** — the main volume slider stays in sync with Windows: change it from the tray volume flyout or media keys and the app follows (and vice-versa).
- **Sonos** — Sonos players are detected automatically (shown with a **Sonos** tag) and use an optimised streaming path that starts quickly. If one particular Sonos is slow or unreliable, flip its **Streaming: Fast / Compatibility** switch on that device's card — the choice is remembered per device. (Tip: Sonos likes MP3 — the app will suggest it.)
- **Auto-reconnect** — if a device drops off (Wi-Fi blip, speaker power-cycle), CastDriver retries automatically and shows **"Reconnecting… (n of 5)"** on the card; it gives up gracefully after five tries.
- **Notifications** — when minimized to the tray, CastDriver pops a notification when a **new device appears** or when a **cast drops** ("device disappeared", with retry progress). Silence them all with *Suppress notifications* in Preferences.
- **📌 Pin** — the window is pinned open by default (stays put and shows in the taskbar). Unpin it to have it auto-hide when it loses focus, tray-popup style.
- **Preferences** — Theme (dark / light), Start with Windows, Start minimized to tray, automatic update checks, suppress notifications, and a Debug screen.
- The app **checks for updates** and offers a one-click download when a newer version is out — the banner highlights the headline new feature (toggle the check off in Preferences).

---

## Which devices work?

CastDriver streams to anything that speaks **Google Cast** or **DLNA/UPnP** — between them that's the large majority of network speakers, AV receivers, and smart TVs. Most brands ship one or both:

| Brand / ecosystem | Google Cast | DLNA/UPnP | Works today? |
|---|:--:|:--:|---|
| Google / Nest, Android TV | ✅ | — | ✅ |
| Chromecast-built-in speakers & TVs (Vizio, JBL, Polk, B&O, newer Bose / Harman Kardon) | ✅ | — | ✅ |
| **Sonos** | — | ✅ *(optimised path)* | ✅ — auto-detected, with a per-device **Fast / Compatibility** switch |
| Samsung · LG · Sony · Panasonic smart TVs | some | ✅ | ✅ via DLNA |
| Denon · Marantz (HEOS), Yamaha (MusicCast), Onkyo · Pioneer, Bluesound · NAD (BluOS) | some | ✅ | ✅ via DLNA |
| NAS / PC media renderers (Kodi, etc.) | — | ✅ | ✅ |
| **Apple HomePod / Apple TV** | — | — | ❌ AirPlay only — *not yet supported* |
| **Amazon Echo / Fire** | — | — | ❌ closed ecosystem |
| **Spotify Connect endpoints** | — | — | ❌ closed — see note |

> **AirPlay** isn't supported yet — it's planned as a future milestone. Many AirPlay speakers also do Chromecast or DLNA, so try those in the meantime.
>
> **Spotify Connect and Amazon Echo are closed boxes.** A Spotify Connect receiver only plays what Spotify's own servers send it — there's no way to push your PC's audio into it. The upside: nearly all of these speakers *also* support Chromecast or DLNA, so you can reach them that way instead.

Not sure what a device supports? Just run CastDriver — anything castable shows up automatically, tagged **Cast**, **DLNA**, or **Sonos**.

---

# For developers

## Tech overview

- **Capture:** WASAPI loopback of a render device (`WasapiLoopbackCapture`), direct capture of an input device (`WasapiCapture`), or **per-app** capture via the Windows process-loopback API (`ActivateAudioInterfaceAsync` with `PROCESS_LOOPBACK`, include/exclude tree).
- **Processing:** captured float audio runs through the optional graphic **equalizer** (per-channel peaking biquads + pre-amp), then is converted to 16-bit PCM.
- **Serve:** a tiny `TcpListener` HTTP server streams the audio live as **chunked WAV** (`0xFFFFFFFF` sizes = "unknown/streaming") or **MP3** (LAME via NAudio.Lame). A silence keep-alive keeps the stream from starving when the PC is idle, and a per-client silence substitution implements per-device mute.
- **Cast:** the **Cast V2** protocol over TLS (port 8009) — `CONNECT → LAUNCH (Default Media Receiver) → LOAD → SET_VOLUME`, with PING/PONG heartbeat and auto-reconnect (5 attempts, surfaced in the UI as "Reconnecting… (n of 5)"). **DLNA/UPnP** via SSDP discovery + SOAP (`SetAVTransportURI` / `Play` / `Stop`, `RenderingControl` for volume).
- **Sonos** (UDN `RINCON_…`) gets a tuned path: the stream is presented as a live broadcast (`object.item.audioItem.audioBroadcast` + the Rincon descriptor), MP3 streams use the `x-rincon-mp3radio://` scheme for instant start, and the SOAP grace period is shorter. Each Sonos has a per-device opt-out (`SonosCompatibilityMode`) that falls back to the generic DLNA path.
- **Presence / pruning:** discovery stamps a last-seen time per device on every announce; a manual refresh re-queries and prunes anything that didn't answer (never a device you're actively casting to).
- Devices are abstracted behind `ICastDevice` / `ICastSession` so Chromecast, DLNA, and Sonos share the device list, media server, and UI.

## Projects

| Project | Purpose |
|---|---|
| `CastDriver.Audio` | Capture (`ICaptureSource`, loopback / input / process-loopback), `PcmConverter`, `Equalizer` |
| `CastDriver.Cast` | Discovery (mDNS + SSDP), Cast V2 + DLNA sessions, `LocalMediaServer`, `CastManager` |
| `CastDriver.Driver` | Optional Scream virtual-driver installer (not required) |
| `CastDriver.UI` | WPF tray application (MVVM, CommunityToolkit.Mvvm) |

## Build & run

Requires the **.NET 10 SDK**. Target framework is `net10.0-windows`.

```powershell
dotnet build
dotnet run --project CastDriver.UI
```

## Releasing

`./release.ps1` is the one-command release: it bumps the version (minor by default; `-Major` or an explicit `1.9` to override), commits and pushes, builds **both** single-file exes, creates the GitHub release, and uploads both assets (it reads the GitHub token from the git credential store).

Pass `-Feature "…"` to set the headline feature shown in the in-app update banner — it writes a `**Feature:** …` line into the release notes that `UpdateChecker` parses out. Example:

```powershell
./release.ps1 -Feature "Faster Sonos streaming + smarter reconnect"
```

To build the exes manually:

```powershell
# Self-contained (~73 MB, no .NET needed)
dotnet publish CastDriver.UI -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none

# Framework-dependent (~4 MB, needs the .NET 10 Desktop Runtime)
dotnet publish CastDriver.UI -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:DebugType=none -o publish-framework-dependent
```

## Design notes / gotchas (hard-won)

- **Keep the stream URL plain** (`http://ip:port/audio.wav`). The Chromecast Default Media Receiver accepts a `LOAD` with a non-standard URL (query/extra path) but then silently refuses to fetch it. Per-device routing is keyed by the **IP the device connects from**, not a URL token.
- **Don't use a versioned TFM** (e.g. `net10.0-windows10.0.19041.0`) casually — it relocates the output exe and breaks any per-path Windows Firewall rule, which silently kills inbound connections.
- **DLNA:** no `<upnp:albumArtURI>` in the DIDL — LG webOS returns HTTP 500. webOS also holds the SOAP response until playback starts, so the SOAP calls cap the wait and assume success on timeout.
- **Sonos** pre-buffers anything advertised as a normal `musicTrack`, so a plain DLNA `SetAVTransportURI` is slow to start. Presenting the stream as an `audioBroadcast` (and `x-rincon-mp3radio://` for MP3) flips it into low-latency "radio" mode. Sonos also prefers MP3 over chunked WAV. The per-device compatibility toggle exists because this idiom is unverified across every Sonos model.
- **Per-app *local* mute is impossible:** muting an app's volume-mixer session also silences its process-loopback capture (the cast goes quiet too).
- The media server must inject silence while the PC is idle (`WasapiLoopbackCapture` delivers nothing when silent), and `StopAudio` must drop the cached `MMDevice` (a stale handle fails the next cast with `0x9000FFFF`).

## License

**GNU GPL v3.0** — see [LICENSE](LICENSE).
