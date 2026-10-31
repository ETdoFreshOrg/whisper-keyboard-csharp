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
3. **Start listening**: Hold the push-to-talk keys, or click "Start Listening" in the tray menu
4. **Speak**: Talk naturally - the app detects when you start and stop speaking
5. **Text appears**: Your transcribed text is typed into the active window

## Default Hotkeys

| Action | Shortcut |
|--------|----------|
| Push-to-talk (types, then presses Enter) | Right Shift + Right Option (Right Alt on Windows) |
| Push-to-talk without Enter | Right Shift + Right Option + Right Command (Right Win on Windows) |
| Cancel push-to-talk | Escape, while holding the keys |
| Toggle Recording (hands-free) | None. Set one in Settings → Hotkeys if you want it |
| Pause/Resume | None |
| Open Settings | None |
| Toggle-to-Talk | None |

The hands-free toggle has no default shortcut on purpose: a common choice like
Ctrl/Cmd+Shift+R is also the browser's hard reload, so reloading a page would start
typing whatever the mic hears.

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

- **Device**: Select input microphone. "Default" follows the system's input setting, switching within a second when it changes. A chosen mic is saved by name; while it's unplugged the default is used, and it switches back when the mic returns.
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
3. **Windows speech / macOS speech**: the OS's own recognizer. Least accurate.
   - Windows: the built-in offline recognizer (System.Speech).
   - macOS: Apple's speech recognizer (`SFSpeechRecognizer`), run by the small `whisper-keyboard-speech` helper next to the app. It stays on the Mac when the language's on-device model is installed (English is), otherwise it uses Apple's servers. The first use asks for Speech Recognition permission, and it needs Dictation turned on (System Settings → Keyboard → Dictation).

A backend that isn't usable (not signed in, server unreachable, permission denied) is skipped. Any error or timeout falls through to the next one, and a backend that just failed is tried last for the next two minutes. The bar shows which backend typed the text. If every backend fails, the clip is kept: hover the message to see why each one failed, then **Retry**, **Save audio** (writes a WAV next to `config.json` under `Recordings`), or **Discard**.

## Push-to-Talk

Hold the keys to record, release to transcribe (Settings → Hotkeys). There are two key sets:

- **Push-to-Talk** (default Right Shift + Right Option, Right Alt on Windows): types the text and presses Enter to submit it.
- **Push-to-Talk without Enter** (default Right Shift + Right Option + Right Command, Right Alt + Right Win on Windows): only types the text.

The second set can be the first plus one more key. Recording starts as soon as the first two keys are down; if the extra key is held at any point during the press, Enter isn't pressed. Exit words don't apply to push-to-talk: "over" is typed as spoken, and only the keys decide whether Enter is pressed.

Push-to-talk taps shorter than 0.3 s are discarded (`PushToTalkMinHoldSeconds`).

Press **Escape** while holding the keys to cancel: the recording is thrown away, nothing is typed and Enter isn't pressed. The bar shows "Cancelled". Escape also cancels during the short post-roll after release and during toggle-to-talk. That Escape is swallowed, so it doesn't also close a dialog or fire a shortcut in the app you're in (on macOS this needs the Accessibility permission; without it Escape still cancels but the app sees it too). Release the keys and press them again to record anew. When nothing is recording, Escape works as usual.

For tests, set `WHISPER_KEYBOARD_TRANSCRIBE_FIXTURE=some text` and every clip "transcribes" to that text without calling any backend. Unit tests: `dotnet test`.

## macOS

Builds and runs on Apple Silicon with the .NET 8 SDK. Needs:

- `brew install openal-soft` for microphone capture (`scripts/publish.sh` copies it into the app).
- Xcode or the Command Line Tools (`xcode-select --install`): the build compiles the macOS speech helper with `swiftc`. Without them the build only warns and the macOS speech backend is skipped.

Build the app with `scripts/publish.sh`, which writes a self-contained, signed `publish/WhisperKeyboard.app`. Run it from there or copy it to `/Applications`. `dotnet run` also works for development, but macOS then asks permission for your terminal, not for Whisper Keyboard.

On first launch a setup window lists the permissions. Grant them to **Whisper Keyboard** in System Settings → Privacy & Security:

| Permission | Needed for |
|------------|------------|
| Accessibility | Typing or pasting text into other apps |
| Input Monitoring | Global hotkeys and push-to-talk |
| Microphone | Recording (asked on first use) |
| Speech Recognition | The macOS speech fallback only (asked on first use) |

`scripts/publish.sh` signs with `CODESIGN_IDENTITY` if set, otherwise the first "Apple Development" identity in your keychain (a free Apple ID in Xcode → Settings → Accounts creates one), otherwise ad-hoc. With a real identity the grants carry over to rebuilds. Ad-hoc signing makes macOS treat each rebuild as a new app: after rebuilding, remove the old Whisper Keyboard entries in Accessibility and Input Monitoring and grant them again.

## Exit Words

Exit words let you end input and press Enter by voice (in hands-free listening; push-to-talk uses its keys instead). When enabled:

1. Say your text followed by an exit word: *"My email is john@example.com over"*
2. The exit word ("over") is removed from the text
3. Enter key is automatically pressed

Default exit words: `over`, `enter`, `submit` (configurable in Settings → Typing)

## Configuration File

Settings are stored in: `%AppData%\WhisperKeyboard\config.json` (Windows) or `~/Library/Application Support/WhisperKeyboard/config.json` (macOS)

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
