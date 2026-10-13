# NlueMix · Bluetooth streaming mixer

> One PC collects several audio sources and plays them all through a **single Bluetooth speaker**.
> When a friend plays something on their phone, the PC steps aside. When they stop, it comes back.

[简体中文](README.md) | **English**

---

## What it is

Almost every Bluetooth speaker can be connected to **one device at a time** — that's a limit of the
speaker's own chip, and no software can change it. NlueMix's answer is to **put the PC in the middle**.

```
   Phone 1 ──Wi-Fi──┐
   Phone 2 ──Wi-Fi──┼──> PC (mix + auto-yield) ──Bluetooth──> Speaker
   Phone 3 ──Wi-Fi──┘
   The PC's own sound ──┘
```

The speaker still sees **exactly one Bluetooth connection**, but you hear up to 3 phones plus the PC.

## Features

| Feature | What it does |
|---|---|
| **Auto-yield** | A phone starts playing and the PC fades to silence (0.1 s fade — no click); 2 s after the phone stops, the PC fades back in |
| **Four modes** | Auto-yield / duck to 20% / play together / manual only |
| **Up to 3 phones** | Per-source volume and mute |
| **Sound quality first** | 48 kHz stereo PCM over the LAN (**lossless**); the only loss left is the single Bluetooth encode |
| **No drivers** | No virtual sound card, no system tweaks, nothing left behind after uninstall |
| **Eight languages** | 简体中文 / English / 日本語 / 한국어 / Deutsch / Français / Español / Русский — switches instantly, no restart |
| **Rescues apps that refuse to be captured** | Some apps (Bilibili and others) don't allow capture. The `🔌 Capture phone audio` shell path gets the sound anyway |

## Download and install

From the [Releases](../../releases) page. File names are ASCII on purpose — Chinese file names break
command-line downloads, CI references and some download managers.

| File | For | Notes |
|---|---|---|
| `NlueMix-0.2.0-Windows-Setup.exe` | **Most people — recommended** | Double-click. Creates desktop and Start Menu shortcuts and adds the firewall rule |
| `NlueMix-0.2.0-Windows-User.zip` | Anyone who prefers no installer | Unzip, run `NlueMix.exe` |
| `NlueMix-0.2.0-Windows-Dev.zip` | Troubleshooting | Adds diagnostic panels; audio behaviour is identical to the user build |
| `NlueMix-0.2.0-Android-User.apk` | Android phones | User and developer builds work the same way |
| `NlueMix-0.2.0-Source.zip` | Reading or building the code | Same content as this repository |

**Requirements**

- PC: Windows 10 version 2004 (build 19041) or later, or Windows 11; .NET 8 Desktop Runtime.
  The installer is a **self-contained single file**, so you don't need the runtime separately.
- Phone: Android 10 or later.

**What the installer does**

1. Extracts the program to `%LOCALAPPDATA%\Programs\NlueMix`
2. Creates desktop and Start Menu shortcuts
3. Adds a Windows Firewall rule (no prompts, no other rules touched)

To uninstall, use Settings → Apps, or run `卸载.bat` in the install folder.

> **The user and developer builds differ only in the diagnostic panels on screen. Capture, protocol
> and transport are identical.** That is deliberate: if the two builds behaved differently,
> "works in the dev build, broken in the user build" would make troubleshooting impossible.
> On Windows you can also switch at runtime with `NlueMix.exe --dev` (or `NLUEMIX_DEV=1`).

## Quick start

### PC

1. Pair Windows with your Bluetooth speaker once (Settings → Bluetooth & devices)
2. Run `NlueMix.exe`
3. It finds the speaker and takes over playback. The **pair code** (4 digits) is in the top-right corner

```
NlueMix.exe --selftest     check the environment (no window, prints diagnostics)
```

### Phone

Install `NlueMix-0.2.0-Android-User.apk` → open it → enter the 4-digit pair code shown on the PC → start.

> Android asks for audio-recording permission the first time. **You must allow it** — that's the
> platform's privacy guard, and without it no sound can be captured.
> A few apps (Netflix and other DRM-protected players) can't be captured at all. That's an Android limit.

## How auto-yield works

The incoming phone stream is measured continuously (RMS plus peak, with two thresholds):

- **320 ms** above −42 dB → "the phone is playing", the PC starts fading out
- **1800 ms** below −50 dB → "it stopped", the PC fades back in

The thresholds are **hysteretic** — the level that starts the fade is not the level that ends it —
so it doesn't flap around the boundary.

## System requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11
- Bluetooth 4.0 or newer adapter (the speaker needs A2DP)
- PC and phone on the **same Wi-Fi network**

## Architecture

```
src/NlueMix/
├── Audio/
│   ├── AudioEngine.cs        Engine control (start/stop, device hot-plug)
│   ├── SystemSource.cs       WASAPI loopback capture of local sound
│   ├── Mixer.cs              Mixing + auto-yield + soft limiter
│   ├── AudioDevices.cs       Playback device enumeration and matching
│   ├── RingSampleProvider.cs Ring buffer → NAudio adapter
│   └── NativeThread.cs       MMCSS audio thread priority
├── Bluetooth/
│   ├── BluetoothScanner.cs   Identify the speaker from the pairing database
│   └── BluetoothConnector.cs Connect an already-paired device via the OS
├── Core/
│   ├── AudioRing.cs          Lock-free ring buffer
│   ├── SilenceDetector.cs    Sound/silence detection (hysteresis)
│   ├── GainSmoother.cs       Gain ramping (prevents clicks)
│   ├── Dsp.cs                Resampling / channel mapping / PCM conversion
│   ├── Models.cs             Data models
│   └── SettingsStore.cs      Settings persistence
├── Network/
│   ├── Protocol.cs           Streaming protocol definition
│   ├── NetworkReceiver.cs    TCP handshake + UDP receive
│   ├── PhoneSession.cs       One session per phone
│   └── OpusCodec.cs          Opus codec (fallback)
└── UI/
    ├── MainWindow.xaml       Interface
    ├── MainWindow.xaml.cs    Interface logic
    └── Theme.xaml            Colours and control styles
```

## Protocol (v1)

Two channels, each doing one job:

| Channel | Port | Purpose |
|---|---|---|
| TCP | 47820 | Handshake, heartbeat, disconnect detection (reliable, carries no audio) |
| UDP | 47821 | Audio data (unreliable, low latency; losing a few packets is inaudible) |

**Why UDP for audio instead of TCP?**
TCP retransmits lost packets, and a retransmission makes everything that already arrived **queue up
behind it** (head-of-line blocking). The result is a stutter plus growing latency. Audio would rather
lose one 20 ms fragment than be late as a whole.

### UDP header (16 bytes, little-endian)

```
Offset  Size  Meaning
 0       4    Magic 0x4D424C55
 4       1    Protocol version
 5       1    Codec (0=PCM16, 1=Opus)
 6       1    Flags
 7       1    Reserved
 8       4    Sequence number (used to compute packet loss)
12       2    Sample count in this packet
14       2    Reserved
```

Followed by 16-bit little-endian PCM. One packet per 20 ms = 1920 samples (48 kHz stereo).

## Security

NlueMix is a LAN tool and the pair code is only 4 digits (10,000 combinations). Three defences:

| Defence | Mechanism | Effect |
|---|---|---|
| **① Handshake rate limit** | 5 failures from one IP → locked for 60 s; **even the correct code is refused while locked** | Turns brute force from seconds into centuries |
| **② UDP source check** | Audio packets must come from the same IP that completed the TCP handshake | Closes the "send packets first to claim the slot" hole |
| **③ Strict packet validation** | Five checks: magic, version, codec, sample count, length | Malformed packets are dropped **before** they reach the audio thread |

Also: a cap on concurrent TCP connections (16), a control-line length cap (512 bytes), a UDP packet
size cap (8 KB), and log throttling (malformed packets are reported at most once every 30 seconds, so
the log can't be used as an amplifier against us).

**Not encrypted**: audio crosses the LAN as plain PCM. Anyone on the same Wi-Fi with a packet capture
tool can reconstruct it. Fine on your own network; **be careful on public networks** (though most
public Wi-Fi uses client isolation, in which case NlueMix can't work there anyway).

## Known limitations

- **The Bluetooth hop is limited by hardware**, and software can't change that. Windows lets you pick
  a codec (SBC/AAC/aptX) depending on your adapter and speaker
- **Latency is roughly 200–400 ms** (inherent to Bluetooth). Not suitable for gaming or as a karaoke mic
- **Windows has no API for pairing from inside an app**, so the first pairing must be done in Settings
- The phone side is **Android only** (iOS doesn't allow system audio capture)

## Configuration

`%LOCALAPPDATA%\NlueMix\settings.json` (falls back to the program folder if not writable)

Log: `nluemix.log` in the same folder

## Release notes

### 0.2.0

- **New shell capture path on the phone** (toolbar `🔌 Capture phone audio`). Apps like Bilibili don't
  declare `allowAudioPlaybackCapture`, so Android refuses to let them be recorded — a platform limit,
  not a bug. The shell path bypasses it. Measured: peak 32768 (full scale), 97–99% non-zero samples
- **Eight UI languages**, switching instantly without a restart
- **Feedback entry** (top-right `✉`): clickable email address and a one-click way to open the log
  folder, so you don't have to copy paths by hand when reporting a problem
- Light/dark theme toggle (top-right ☀/🌙) and a splash animation
- **Firewall repair** (toolbar `🛡`). Windows Firewall rules match on program path *and* network
  profile, so changing networks (new router, Windows re-classifying the connection) silently
  invalidates the rule. The symptom is **the phone saying it can't connect while the PC log shows not
  a single connection attempt** — impossible for a user to diagnose, hence the one-click fix
- **Security hardening**: handshake rate limiting (5 failures per IP → 60 s lock), UDP source
  validation, five-way header validation, connection and line-length caps, malformed-packet log throttling

### Known issues

- Bluetooth latency and audio quality are determined by hardware; software can't change them
- A few DRM-protected streaming apps can't be captured even via the shell path — platform limitation
- All UI text is available in eight languages. The one remaining Chinese-only string is the
  parenthetical "(device native sample rate …)" on the audio-format line, which only appears when the
  device's native rate isn't 48 kHz

## License

MIT License — use it, change it, ship it commercially, just keep the copyright notice.

### Third-party components

| Component | License | Used for |
|---|---|---|
| NAudio | MIT | WASAPI capture and output |
| Concentus | MIT | Pure managed Opus codec |
| .NET 8 | MIT | Runtime |

This project contains **no ads, no promotion, no telemetry, no background uploads**.
It makes no network requests other than talking to phones on the local network.
