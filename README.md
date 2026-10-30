# Whisper Keyboard

A Windows system tray application for voice-to-text dictation using OpenAI's Whisper API. Speak into your microphone and have your words typed directly into any application.

## Features

- **Voice-to-Text Dictation** - Real-time speech recognition powered by OpenAI Whisper
- **System Tray Integration** - Runs quietly in background with no main window
- **Global Hotkeys** - Control recording from anywhere with customizable shortcuts
- **Flexible Output Modes** - Paste via clipboard or simulate keyboard typing
- **Voice Activity Detection** - Automatic speech start/stop detection with calibration wizard
- **Exit Words** - Say "over", "enter", or "submit" to automatically press Enter
- **Recording Indicator** - Floating overlay with waveform visualization and controls
- **Transcription History** - Access your last 10 transcriptions with one click
- **Text Processing** - Auto-punctuation and sentence capitalization

## Requirements

- Windows 10/11
- .NET 8.0 Runtime
- OpenAI API key with access to Whisper API
- Microphone

## Installation

### From Release

1. Download the latest release
2. Extract to your preferred location
3. Run `WhisperKeyboard.exe`

### Build from Source

```bash
git clone <repository-url>
cd whisper-keyboard-csharp
dotnet build
dotnet run
```

## Quick Start

1. **Set your API key**: On first run, you'll be prompted to enter your OpenAI API key
   - Alternatively, set the `OPENAI_API_KEY` environment variable
2. **Calibrate**: Run the calibration wizard to set optimal voice detection threshold
3. **Start listening**: Press `Ctrl+Shift+R` or click "Start Listening" in the tray menu
4. **Speak**: Talk naturally - the app detects when you start and stop speaking
5. **Text appears**: Your transcribed text is typed into the active window

## Default Hotkeys

| Action | Shortcut |
|--------|----------|
| Toggle Recording | `Ctrl+Shift+R` |
| Pause/Resume | `Ctrl+Shift+P` |
| Quit Application | `Ctrl+Shift+Q` |

## Tray Icon States

| Color | State |
|-------|-------|
| Gray | Idle/Stopped |
| Green | Listening |
| Red | Recording speech |
| Orange | Paused |

## Settings

Access settings by right-clicking the tray icon → Settings, or double-click the tray icon.

### Audio

- **Device**: Select input microphone
- **VAD Threshold**: Voice detection sensitivity (use Calibrate for best results)
- **Min Audio Duration**: Minimum speech length to process (default: 1.5s)
- **Max Silence Duration**: Silence before ending recording (default: 1.0s)

### OpenAI

- **API Key**: Your OpenAI API key
- **Language**: Target language (default: English, or auto-detect)

### Typing

- **Paste Mode**: Use clipboard paste (faster) vs keyboard simulation
- **Typing Speed**: Character delay for keyboard mode
- **Auto Punctuation**: Add period at end of sentences
- **Auto Capitalize**: Capitalize first letter and after punctuation
- **Exit Words**: Words that trigger Enter key (default: "over", "enter", "submit")

### Hotkeys

- Customize all keyboard shortcuts
- Supports Ctrl, Alt, Shift, Win modifiers
- Changes require app restart

### General

- **Show Notifications**: Enable/disable balloon notifications
- **Start Minimized**: Launch to system tray
- **Start with Windows**: Add to Windows startup

## Speech-to-Text Backends

Each clip is sent to the backends below in order (Settings → Speech) until one returns text:

1. **ChatGPT**: uses your Codex app's ChatGPT sign-in (`~/.codex/auth.json`, or `$CODEX_HOME/auth.json`). Sign in to Codex on this PC to use it. Audio goes up as Ogg/Opus.
2. **Your server**: the OpenAI-compatible endpoint under "Your server" (default `https://stt.etdofresh.com`, with your API key).
3. **Windows speech**: Windows' built-in offline recognizer. Least accurate; Windows only.

A backend that isn't usable (not signed in, server unreachable, not Windows) is skipped. Any error or timeout falls through to the next one, and a backend that just failed is tried last for the next two minutes. The bar shows which backend typed the text. If every backend fails, the clip is kept: hover the message to see why each one failed, then **Retry**, **Save audio** (writes a WAV next to `config.json` under `Recordings`), or **Discard**.

Push-to-talk taps shorter than 0.3 s are discarded (`PushToTalkMinHoldSeconds`).

For tests, set `WHISPER_KEYBOARD_TRANSCRIBE_FIXTURE=some text` and every clip "transcribes" to that text without calling any backend. Unit tests: `dotnet test`.

## Exit Words

Exit words let you end input and press Enter by voice. When enabled:

1. Say your text followed by an exit word: *"My email is john@example.com over"*
2. The exit word ("over") is removed from the text
3. Enter key is automatically pressed

Default exit words: `over`, `enter`, `submit` (configurable in Settings → Typing)

## Configuration File

Settings are stored in: `%AppData%\WhisperKeyboard\config.json`

## Troubleshooting

### No transcription happening

- Check your API key is valid
- Ensure microphone is working and selected in Audio settings
- Try running the Calibration Wizard
- Check Windows microphone permissions

### Text not appearing in target app

- Some apps may block simulated input - try Paste Mode
- Run WhisperKeyboard as Administrator
- Check that the target window is focused

### Hotkeys not working

- Another app may have registered the same hotkey
- Try different key combinations in Settings → Hotkeys
- Restart the application after changing hotkeys

### Picking up background noise

- Run Calibration Wizard in your typical environment
- Increase VAD Threshold in Audio settings
- Use a directional microphone or headset

## Architecture

```
Microphone → AudioProcessor (NAudio) → VAD Detection
    → SpeechTranscriber (Whisper API)
    → TextTyper (Clipboard/SendInput)
    → Active Window
```

**Core Components:**

- **TrayApplicationContext** - Main application coordinator
- **AudioProcessor** - Audio capture and voice activity detection
- **SpeechTranscriber** - OpenAI Whisper API integration
- **TextTyper** - Keyboard simulation and clipboard paste
- **GlobalHotkey** - System-wide hotkey registration
- **RecordingIndicator** - Visual feedback overlay

## Dependencies

- [NAudio](https://github.com/naudio/NAudio) 2.2.1 - Audio capture
- [Newtonsoft.Json](https://www.newtonsoft.com/json) 13.0.3 - Configuration serialization

## License

[Add license information]
