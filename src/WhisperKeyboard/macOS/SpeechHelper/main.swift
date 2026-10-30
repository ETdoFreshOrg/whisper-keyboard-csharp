// whisper-keyboard-speech: the "macOS speech" backend's helper.
//
// Runs Apple's speech recognizer (SFSpeechRecognizer) on a recorded WAV file and
// prints one line of JSON. The app runs it as a separate process because macOS
// kills any process that touches Speech without the right permission setup; a
// crash here only fails this backend instead of taking the app down.
//
//   whisper-keyboard-speech check [language]
//       {"ok":true,"locale":"en-US","onDevice":true,"authorization":"authorized"}
//       Never shows the permission prompt.
//   whisper-keyboard-speech transcribe <file.wav> [language]
//       {"text":"...","locale":"en-US","onDevice":true}
//       Asks for Speech Recognition permission the first time.
//
// Failures print {"error":"one line for the user"} and exit 1.
// language: "en", "en-US", "auto" or empty (= the Mac's current language).

import Foundation
import Speech

private let outputLock = NSLock()

/// Print the one JSON line and exit. Never unlocks: a second caller waits for the exit.
private func finish(_ payload: [String: Any], code: Int32) -> Never {
    outputLock.lock()
    if let data = try? JSONSerialization.data(withJSONObject: payload),
       let line = String(data: data, encoding: .utf8) {
        print(line)
    }
    fflush(stdout)
    exit(code)
}

private func fail(_ message: String) -> Never {
    finish(["error": message], code: 1)
}

private func languageCode(of identifier: String) -> String {
    String(identifier.lowercased().split(whereSeparator: { $0 == "-" || $0 == "_" }).first ?? "")
}

/// Pick a recognizer for "en", "en-US" or "auto": exact match, then the language in
/// this Mac's region (en + US), then its home region (fr-FR), then any region of it.
private func makeRecognizer(_ language: String) -> SFSpeechRecognizer? {
    let wanted = language.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "_", with: "-")
    if wanted.isEmpty || wanted.lowercased() == "auto" {
        return SFSpeechRecognizer() ?? SFSpeechRecognizer(locale: Locale(identifier: "en-US"))
    }

    let supported = SFSpeechRecognizer.supportedLocales()
        .sorted { $0.identifier < $1.identifier }
    func normalized(_ locale: Locale) -> String {
        locale.identifier.replacingOccurrences(of: "_", with: "-").lowercased()
    }

    if let exact = supported.first(where: { normalized($0) == wanted.lowercased() }) {
        return SFSpeechRecognizer(locale: exact)
    }

    let code = languageCode(of: wanted)
    let currentRegion = Locale.current.identifier
        .split(whereSeparator: { $0 == "-" || $0 == "_" })
        .dropFirst().last.map { String($0).lowercased() } ?? ""
    for region in [currentRegion, code] {
        if let regional = supported.first(where: { normalized($0) == "\(code)-\(region)" }) {
            return SFSpeechRecognizer(locale: regional)
        }
    }
    if let any = supported.first(where: { languageCode(of: $0.identifier) == code }) {
        return SFSpeechRecognizer(locale: any)
    }
    return nil
}

private func describe(_ status: SFSpeechRecognizerAuthorizationStatus) -> String {
    switch status {
    case .authorized: return "authorized"
    case .denied: return "denied"
    case .restricted: return "restricted"
    case .notDetermined: return "notDetermined"
    @unknown default: return "unknown"
    }
}

private func failIfBlocked(_ status: SFSpeechRecognizerAuthorizationStatus) {
    switch status {
    case .denied:
        fail("Speech Recognition permission is off (System Settings > Privacy & Security > Speech Recognition)")
    case .restricted:
        fail("Speech Recognition is restricted on this Mac")
    default:
        break
    }
}

private func usableRecognizer(_ language: String) -> SFSpeechRecognizer {
    guard let recognizer = makeRecognizer(language) else {
        fail("no Apple speech recognizer for language '\(language)'")
    }
    guard recognizer.isAvailable else {
        fail("Apple speech recognizer is unavailable (is Dictation turned on in System Settings > Keyboard?)")
    }
    // Deliver results off the main thread so the waits below can't deadlock.
    recognizer.queue = OperationQueue()
    return recognizer
}

private func check(_ language: String) -> Never {
    let status = SFSpeechRecognizer.authorizationStatus()
    failIfBlocked(status)
    let recognizer = usableRecognizer(language)
    finish([
        "ok": true,
        "locale": recognizer.locale.identifier,
        "onDevice": recognizer.supportsOnDeviceRecognition,
        "authorization": describe(status)
    ], code: 0)
}

private func authorize() {
    var status = SFSpeechRecognizer.authorizationStatus()
    if status == .notDetermined {
        let answered = DispatchSemaphore(value: 0)
        SFSpeechRecognizer.requestAuthorization { result in
            status = result
            answered.signal()
        }
        answered.wait()
    }
    failIfBlocked(status)
    if status != .authorized {
        fail("Speech Recognition permission was not granted")
    }
}

private func isNoSpeech(_ error: NSError) -> Bool {
    // "No speech detected": silence is a valid answer (empty text), not a failure.
    (error.domain == "kAFAssistantErrorDomain" && error.code == 1110)
        || error.localizedDescription.localizedCaseInsensitiveContains("no speech detected")
}

private func transcribe(_ path: String, _ language: String) -> Never {
    let url = URL(fileURLWithPath: path)
    guard FileManager.default.fileExists(atPath: url.path) else {
        fail("audio file not found: \(path)")
    }

    authorize()
    let recognizer = usableRecognizer(language)

    let request = SFSpeechURLRecognitionRequest(url: url)
    request.shouldReportPartialResults = false
    request.taskHint = .dictation
    // Stay on this Mac when it has the model for the language; otherwise Apple's servers.
    let onDevice = recognizer.supportsOnDeviceRecognition
    request.requiresOnDeviceRecognition = onDevice
    if #available(macOS 13, *) {
        request.addsPunctuation = true
    }

    let base: [String: Any] = ["locale": recognizer.locale.identifier, "onDevice": onDevice]
    let task = recognizer.recognitionTask(with: request) { result, error in
        if let result = result, result.isFinal {
            finish(base.merging(["text": result.bestTranscription.formattedString]) { $1 }, code: 0)
        }
        if let error = error as NSError? {
            if isNoSpeech(error) {
                finish(base.merging(["text": ""]) { $1 }, code: 0)
            }
            fail("Apple speech failed: \(error.localizedDescription)")
        }
    }
    _ = task
    // finish() exits the process; keep this thread parked until then.
    while true { sleep(60) }
}

// The app owns timeouts, but don't outlive a parent that died mid-request.
DispatchQueue.global().asyncAfter(deadline: .now() + 600) {
    fail("speech helper gave up after 10 minutes")
}

DispatchQueue.global(qos: .userInitiated).async {
    let args = Array(CommandLine.arguments.dropFirst())
    switch args.first {
    case "check":
        check(args.count > 1 ? args[1] : "")
    case "transcribe" where args.count >= 2:
        transcribe(args[1], args.count > 2 ? args[2] : "")
    default:
        fail("usage: whisper-keyboard-speech check [language] | transcribe <file.wav> [language]")
    }
}

// Run the main queue for any framework callbacks delivered there.
dispatchMain()
