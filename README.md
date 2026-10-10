# DikTap

**Voice dictation for any Windows app.** Press a shortcut, speak, press it again and the transcribed text appears where
your cursor was: chat, editor, browser... It uses OpenAI's transcription API (`gpt-transcribe`) with your own API key.

[Versión en español](README.es.md)

## Features

- **One shortcut everywhere:** `Win+H` (replaces Windows dictation while DikTap is running) or `Ctrl+Alt+Space`. `Esc` cancels.
- **Minimal indicator** with an audio waveform that reacts to your voice, so you know the microphone is hearing you.
- **Safe pasting:** it only pastes if you are still in the same window; otherwise the text stays in the clipboard and you are notified. It never presses Enter.
- **Lightweight:** lives in the system tray, ~10 MB private memory and 0 % CPU when idle.
- **Portable, no installer:** a single `DikTap.exe` (.NET included). Spanish and English interface.
- **Private:** no telemetry; audio and transcripts are never stored. Your key is kept in Windows Credential Manager.
- **Local usage and estimated cost statistics** to see what you spend on OpenAI.

## Requirements

- Windows 11 (x64).
- An [OpenAI API key](https://platform.openai.com/api-keys) with access to transcription. Usage is billed to your OpenAI account (≈ USD 0.0045 per audio minute).

## Installation

<!-- Uncomment once the package is approved in winget:
### With winget

```powershell
winget install jorgeju.DikTap
```
-->

### Manual download (ZIP)

1. Download `DikTap-<version>-win-x64.zip` from [Releases](https://github.com/jorgeju/DikTap/releases/latest).
2. Extract it to a stable folder (for example `C:\Tools\DikTap`) and run `DikTap.exe`.
3. **Settings** opens: paste your API key and click *Save*. It starts with Windows by default (you can untick it).

> DikTap is not code-signed, so Windows SmartScreen may warn you the first time
> ("More info" → "Run anyway"). Only run it if you downloaded it from this repository; each release page lists its SHA-256.

## Usage

1. Put the cursor where you want to type and press `Win+H`.
2. Speak while the waveform moves. Press `Win+H` again to finish.
3. The text is transcribed and pasted. `Esc` cancels at any time.

Double-click the tray icon (or launch the EXE again) to open Settings.

## Settings

| Option | What it does |
|---|---|
| API key | Stored in Windows Credential Manager; never displayed. |
| Microphone and shortcut | A specific device or the default one; `Win+H` or `Ctrl+Alt+Space`. |
| Languages, vocabulary and context | Optional hints to improve recognition (proper names, jargon...). |
| Clipboard after pasting | Restore what you had (default), keep the dictation, or keep it in the `Win+V` history too. |
| Usage statistics | Dictations, minutes and estimated cost by period and by key. Data stays on your computer. |
| Language | Automatic (Windows language), Spanish or English. |

## Privacy

- Audio is sent **directly** to `api.openai.com` over HTTPS, only while you dictate, and DikTap does not store it anywhere. There is no intermediate server and no telemetry.
- Everyone uses their own API key and billing; DikTap does not use your ChatGPT session or subscription.
- Settings live in `%LOCALAPPDATA%\DikTap`. The technical log contains no keys, audio or dictated text.

## Limitations

- It cannot paste into apps running as administrator (Windows prevents it): the text stays in the clipboard.
- `Win+H` is not intercepted if the foreground app is elevated, nor on the lock screen.
- Password fields are not detected: you choose the destination.
- A recording lasts at most 10 minutes.
- If the microphone is unplugged mid-dictation, what was recorded up to that point is transcribed and pasted.

## Uninstall

*Settings → Uninstall…* removes the key, startup entry, data and the program itself (with an option to keep your settings).
<!-- Uncomment together with the winget install block: if you installed with winget, the same button also runs `winget uninstall jorgeju.DikTap`, which you can run yourself too. -->

## Build from source

You need the .NET 10 SDK (no Visual Studio required).

```powershell
./scripts/Build.ps1                # build and run tests
./scripts/Publish.ps1 -Package     # self-contained EXE and ZIP in artifacts/
```

## Contributing

Contributions are welcome! You can [open an issue](https://github.com/jorgeju/DikTap/issues/new/choose) with a bug or an idea,
or send a pull request. Please read [CONTRIBUTING.md](CONTRIBUTING.md) first. All changes go through maintainer review.

**Current focus:** DikTap aims to be ultra-light and fast, with few options. Every proposal is welcome, including ones that would add weight or complexity:
all of them get considered. But not everything will be included, however well it works, if it does not fit that philosophy (which may evolve in the future). Before coding something big, open an issue to discuss it.

## License

DikTap is free software under the [GNU GPL v3.0 only](LICENSE): you may use it for free, study it, modify it and share it. If you distribute a modified version,
it must stay under GPL-3.0 with its source code available. It comes with no warranty.

**Name and icon:** the license covers the code; it grants no rights to the name "DikTap" or its icon. If you publish a modified version, use a different name and icon so it is not confused with the original.
DikTap is not affiliated with OpenAI or Microsoft. Third-party notices in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
