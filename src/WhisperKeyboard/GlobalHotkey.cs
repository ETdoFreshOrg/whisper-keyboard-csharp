using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WhisperKeyboard.Core;

namespace WhisperKeyboard;

/// <summary>
/// Cross-platform global hotkey implementation.
/// Uses Win32 RegisterHotKey on Windows, CGEventTap on macOS.
/// </summary>
public class GlobalHotkey : IDisposable
{
    private readonly IGlobalHotkeyImpl _impl;

    public GlobalHotkey()
    {
        if (OperatingSystem.IsWindows())
        {
            _impl = new WindowsGlobalHotkey();
        }
        else if (OperatingSystem.IsMacOS())
        {
            _impl = new MacOSGlobalHotkey();
        }
        else
        {
            _impl = new NullGlobalHotkey();
        }
    }

    public uint Register(string hotkeyString, Action callback)
    {
        return _impl.Register(hotkeyString, callback);
    }

    public void UnregisterAll()
    {
        _impl.UnregisterAll();
    }

    public void Dispose()
    {
        _impl.Dispose();
    }
}

internal interface IGlobalHotkeyImpl : IDisposable
{
    uint Register(string hotkeyString, Action callback);
    void UnregisterAll();
}

/// <summary>
/// Null implementation for unsupported platforms.
/// </summary>
internal class NullGlobalHotkey : IGlobalHotkeyImpl
{
    public uint Register(string hotkeyString, Action callback)
    {
        Console.WriteLine($"Global hotkeys not supported on this platform");
        return 0;
    }

    public void UnregisterAll() { }
    public void Dispose() { }
}

/// <summary>
/// Windows global hotkey implementation using Win32 RegisterHotKey.
/// </summary>
internal class WindowsGlobalHotkey : IGlobalHotkeyImpl
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    // Modifier key constants
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    // Message constants
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_USER = 0x0400;
    private const uint WM_REGISTER_HOTKEY = WM_USER + 1;
    private const uint PM_REMOVE = 0x0001;

    private readonly Dictionary<int, Action> _callbacks = new();
    private readonly ConcurrentQueue<(int id, uint modifiers, uint vk, Action callback, TaskCompletionSource<bool> tcs)> _pendingRegistrations = new();
    private int _nextId = 1;
    private IntPtr _hwnd;
    private uint _messageThreadId;
    private Thread? _messageThread;
    private volatile bool _disposed;
    private readonly ManualResetEventSlim _windowReady = new(false);

    // Virtual key codes
    private static readonly Dictionary<string, uint> VirtualKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // Letters
        { "A", 0x41 }, { "B", 0x42 }, { "C", 0x43 }, { "D", 0x44 }, { "E", 0x45 },
        { "F", 0x46 }, { "G", 0x47 }, { "H", 0x48 }, { "I", 0x49 }, { "J", 0x4A },
        { "K", 0x4B }, { "L", 0x4C }, { "M", 0x4D }, { "N", 0x4E }, { "O", 0x4F },
        { "P", 0x50 }, { "Q", 0x51 }, { "R", 0x52 }, { "S", 0x53 }, { "T", 0x54 },
        { "U", 0x55 }, { "V", 0x56 }, { "W", 0x57 }, { "X", 0x58 }, { "Y", 0x59 },
        { "Z", 0x5A },
        // Numbers
        { "0", 0x30 }, { "1", 0x31 }, { "2", 0x32 }, { "3", 0x33 }, { "4", 0x34 },
        { "5", 0x35 }, { "6", 0x36 }, { "7", 0x37 }, { "8", 0x38 }, { "9", 0x39 },
        // Function keys
        { "F1", 0x70 }, { "F2", 0x71 }, { "F3", 0x72 }, { "F4", 0x73 },
        { "F5", 0x74 }, { "F6", 0x75 }, { "F7", 0x76 }, { "F8", 0x77 },
        { "F9", 0x78 }, { "F10", 0x79 }, { "F11", 0x7A }, { "F12", 0x7B },
        { "F13", 0x7C }, { "F14", 0x7D }, { "F15", 0x7E }, { "F16", 0x7F },
        { "F17", 0x80 }, { "F18", 0x81 }, { "F19", 0x82 }, { "F20", 0x83 },
        { "F21", 0x84 }, { "F22", 0x85 }, { "F23", 0x86 }, { "F24", 0x87 },
        // Special keys
        { "Space", 0x20 }, { "Return", 0x0D }, { "Enter", 0x0D }, { "Tab", 0x09 },
        { "Escape", 0x1B }, { "Delete", 0x2E }, { "Backspace", 0x08 },
        { "Up", 0x26 }, { "Down", 0x28 }, { "Left", 0x25 }, { "Right", 0x27 },
        { "Home", 0x24 }, { "End", 0x23 }, { "PageUp", 0x21 }, { "PageDown", 0x22 },
    };

    public WindowsGlobalHotkey()
    {
        _messageThread = new Thread(MessageLoop)
        {
            Name = "GlobalHotkeyThread",
            IsBackground = true
        };
        _messageThread.Start();

        // Wait for window to be created
        if (!_windowReady.Wait(5000))
        {
            Console.WriteLine("Timeout waiting for hotkey window to be created");
        }
    }

    private void MessageLoop()
    {
        try
        {
            _messageThreadId = GetCurrentThreadId();

            // Create a message-only window to receive hotkey messages
            _hwnd = CreateWindowEx(
                0, "STATIC", "WhisperKeyboard Hotkey Window",
                0, 0, 0, 0, 0,
                new IntPtr(-3), // HWND_MESSAGE - message-only window
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Console.WriteLine($"Failed to create hotkey window. Error: {Marshal.GetLastWin32Error()}");
                _windowReady.Set();
                return;
            }

            Console.WriteLine("Global hotkey window created successfully");
            _windowReady.Set();

            // Message loop
            while (!_disposed && GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
            {
                if (msg.message == WM_HOTKEY)
                {
                    int id = (int)msg.wParam;
                    if (_callbacks.TryGetValue(id, out var callback))
                    {
                        Console.WriteLine($"Hotkey triggered: id={id}");
                        // Invoke on UI thread
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                callback();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Hotkey callback error: {ex.Message}");
                            }
                        });
                    }
                }
                else if (msg.message == WM_REGISTER_HOTKEY)
                {
                    // Process pending registrations from the message thread
                    ProcessPendingRegistrations();
                }
                else
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in hotkey message loop: {ex.Message}");
            _windowReady.Set();
        }
    }

    private void ProcessPendingRegistrations()
    {
        while (_pendingRegistrations.TryDequeue(out var reg))
        {
            bool success = RegisterHotKey(_hwnd, reg.id, reg.modifiers | MOD_NOREPEAT, reg.vk);
            if (success)
            {
                _callbacks[reg.id] = reg.callback;
                Console.WriteLine($"Registered hotkey: id={reg.id}, modifiers=0x{reg.modifiers:X}, vk=0x{reg.vk:X}");
            }
            else
            {
                Console.WriteLine($"Failed to register hotkey: id={reg.id}. Error: {Marshal.GetLastWin32Error()}");
            }
            reg.tcs.SetResult(success);
        }
    }

    public uint Register(string hotkeyString, Action callback)
    {
        if (string.IsNullOrWhiteSpace(hotkeyString))
            return 0;

        if (!_windowReady.Wait(2000))
        {
            Console.WriteLine("Hotkey window not ready - cannot register hotkey");
            return 0;
        }

        if (_hwnd == IntPtr.Zero)
        {
            Console.WriteLine("Hotkey window not available - cannot register hotkey");
            return 0;
        }

        if (!ParseHotkey(hotkeyString, out uint modifiers, out uint vk))
        {
            Console.WriteLine($"Failed to parse hotkey: {hotkeyString}");
            return 0;
        }

        int id = _nextId++;

        // Queue the registration and post a message to process it on the message thread
        var tcs = new TaskCompletionSource<bool>();
        _pendingRegistrations.Enqueue((id, modifiers, vk, callback, tcs));
        PostThreadMessage(_messageThreadId, WM_REGISTER_HOTKEY, IntPtr.Zero, IntPtr.Zero);

        // Wait for the registration to complete (with timeout)
        if (tcs.Task.Wait(2000) && tcs.Task.Result)
        {
            Console.WriteLine($"Registered hotkey: {hotkeyString} (id={id}, modifiers=0x{modifiers:X}, vk=0x{vk:X})");
            return (uint)id;
        }
        else
        {
            Console.WriteLine($"Failed to register hotkey: {hotkeyString}");
            return 0;
        }
    }

    public void UnregisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;

        foreach (var id in _callbacks.Keys)
        {
            UnregisterHotKey(_hwnd, id);
        }
        _callbacks.Clear();
    }

    private bool ParseHotkey(string hotkeyString, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        var parts = hotkeyString.Split('+').Select(p => p.Trim()).ToArray();
        if (parts.Length == 0)
            return false;

        foreach (var part in parts)
        {
            var lower = part.ToLowerInvariant();
            switch (lower)
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                case "option":
                case "opt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                case "cmd":
                case "command":
                case "meta":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    // This should be the key
                    if (VirtualKeys.TryGetValue(part, out var code))
                    {
                        vk = code;
                    }
                    else
                    {
                        Console.WriteLine($"Unknown key: {part}");
                        return false;
                    }
                    break;
            }
        }

        return vk != 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();

        if (_messageThreadId != 0)
        {
            // Post WM_QUIT to exit the message loop
            PostThreadMessage(_messageThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _messageThread?.Join(1000);
        _windowReady.Dispose();
    }
}

/// <summary>
/// macOS global hotkey implementation using CGEventTap.
/// Requires accessibility permissions to work properly.
/// </summary>
internal class MacOSGlobalHotkey : IGlobalHotkeyImpl
{
    // CGEvent types
    private const int kCGEventKeyDown = 10;
    private const int kCGEventFlagsChanged = 12;
    private const int kCGEventNull = 0;

    // CGEvent flags (modifier keys)
    private const ulong kCGEventFlagMaskCommand = 0x00100000;
    private const ulong kCGEventFlagMaskShift = 0x00020000;
    private const ulong kCGEventFlagMaskAlternate = 0x00080000;
    private const ulong kCGEventFlagMaskControl = 0x00040000;

    // CGEventTap location
    private const int kCGSessionEventTap = 1;
    private const int kCGHeadInsertEventTap = 0;

    // CFRunLoop constants
    private const string kCFRunLoopCommonModes = "kCFRunLoopCommonModes";
    private const string kCFRunLoopDefaultMode = "kCFRunLoopDefaultMode";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CGEventTapCallback(IntPtr proxy, int type, IntPtr eventRef, IntPtr userInfo);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern IntPtr CGEventTapCreate(
        int tap,
        int place,
        int options,
        ulong eventsOfInterest,
        CGEventTapCallback callback,
        IntPtr userInfo);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern void CGEventTapEnable(IntPtr tap, bool enable);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern ulong CGEventGetFlags(IntPtr eventRef);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern long CGEventGetIntegerValueField(IntPtr eventRef, int field);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, int order);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFRunLoopGetMain();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFRunLoopGetCurrent();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopAddSource(IntPtr rl, IntPtr source, IntPtr mode);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopRemoveSource(IntPtr rl, IntPtr source, IntPtr mode);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopRun();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, bool returnAfterSourceHandled);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopStop(IntPtr rl);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, int encoding);

    private readonly List<(uint keyCode, ulong modifiers, Action callback)> _registeredHotkeys = new();
    private IntPtr _eventTap;
    private IntPtr _runLoopSource;
    private IntPtr _runLoop;
    private CGEventTapCallback? _callbackDelegate;
    private Thread? _runLoopThread;
    private bool _disposed;
    private volatile bool _stopRequested;
    private readonly ManualResetEventSlim _eventTapReady = new(false);

    private static readonly Dictionary<string, uint> KeyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Letters
        { "A", 0x00 }, { "B", 0x0B }, { "C", 0x08 }, { "D", 0x02 }, { "E", 0x0E },
        { "F", 0x03 }, { "G", 0x05 }, { "H", 0x04 }, { "I", 0x22 }, { "J", 0x26 },
        { "K", 0x28 }, { "L", 0x25 }, { "M", 0x2E }, { "N", 0x2D }, { "O", 0x1F },
        { "P", 0x23 }, { "Q", 0x0C }, { "R", 0x0F }, { "S", 0x01 }, { "T", 0x11 },
        { "U", 0x20 }, { "V", 0x09 }, { "W", 0x0D }, { "X", 0x07 }, { "Y", 0x10 },
        { "Z", 0x06 },
        // Numbers
        { "0", 0x1D }, { "1", 0x12 }, { "2", 0x13 }, { "3", 0x14 }, { "4", 0x15 },
        { "5", 0x17 }, { "6", 0x16 }, { "7", 0x1A }, { "8", 0x1C }, { "9", 0x19 },
        // Function keys
        { "F1", 0x7A }, { "F2", 0x78 }, { "F3", 0x63 }, { "F4", 0x76 },
        { "F5", 0x60 }, { "F6", 0x61 }, { "F7", 0x62 }, { "F8", 0x64 },
        { "F9", 0x65 }, { "F10", 0x6D }, { "F11", 0x67 }, { "F12", 0x6F },
        // Special keys
        { "Space", 0x31 }, { "Return", 0x24 }, { "Enter", 0x24 }, { "Tab", 0x30 },
        { "Escape", 0x35 }, { "Delete", 0x33 }, { "Backspace", 0x33 },
        { "Up", 0x7E }, { "Down", 0x7D }, { "Left", 0x7B }, { "Right", 0x7C },
        { "Home", 0x73 }, { "End", 0x77 }, { "PageUp", 0x74 }, { "PageDown", 0x79 },
    };

    public MacOSGlobalHotkey()
    {
        // Start the event tap on a background thread with its own run loop
        _runLoopThread = new Thread(RunEventTapThread)
        {
            Name = "GlobalHotkeyThread",
            IsBackground = true
        };
        _runLoopThread.Start();
    }

    private void RunEventTapThread()
    {
        try
        {
            // Keep a reference to prevent GC
            _callbackDelegate = EventTapCallback;

            // We only care about key down events
            ulong eventsOfInterest = (1UL << kCGEventKeyDown);

            _eventTap = CGEventTapCreate(
                kCGSessionEventTap,
                kCGHeadInsertEventTap,
                0, // kCGEventTapOptionDefault - we don't want to block events
                eventsOfInterest,
                _callbackDelegate,
                IntPtr.Zero);

            if (_eventTap == IntPtr.Zero)
            {
                Console.WriteLine("Failed to create event tap. Make sure accessibility permissions are granted.");
                Console.WriteLine("Go to System Settings > Privacy & Security > Accessibility and add this app.");
                return;
            }

            _runLoopSource = CFMachPortCreateRunLoopSource(IntPtr.Zero, _eventTap, 0);
            if (_runLoopSource == IntPtr.Zero)
            {
                Console.WriteLine("Failed to create run loop source");
                return;
            }

            // Get the current thread's run loop
            _runLoop = CFRunLoopGetCurrent();

            // Get the default mode string (kept alive for the lifetime of the loop)
            var modeHandle = CFStringCreateWithCString(IntPtr.Zero, kCFRunLoopDefaultMode, 0x08000100); // kCFStringEncodingUTF8

            CFRunLoopAddSource(_runLoop, _runLoopSource, modeHandle);
            CGEventTapEnable(_eventTap, true);

            Console.WriteLine("Global hotkey event tap installed successfully");

            // Signal that the event tap is ready
            _eventTapReady.Set();

            // Run the loop with a bounded timeout rather than CFRunLoopRun(), which
            // returns instantly and busy-spins at 100% CPU if the tap's run loop
            // source is ever removed (e.g. when macOS disables the tap). Re-enable
            // the tap each wake-up so a disabled tap recovers even if the callback
            // never fires.
            while (!_stopRequested)
            {
                CFRunLoopRunInMode(modeHandle, 1.0, false);
                if (!_stopRequested && _eventTap != IntPtr.Zero)
                    CGEventTapEnable(_eventTap, true);
            }

            CFRelease(modeHandle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in hotkey thread: {ex.Message}");
        }
        finally
        {
            // Tear down CoreFoundation objects on the SAME thread that owns the run
            // loop, before this thread (and therefore its CFRunLoop) is destroyed.
            // Doing this from Dispose() on another thread dereferences a freed
            // CFRunLoop and traps forever in __CFCheckCFInfoPACSignature.
            TeardownEventTap();

            // Always signal readiness so Register()/callers never block on failure.
            _eventTapReady.Set();
        }
    }

    private void TeardownEventTap()
    {
        if (_eventTap != IntPtr.Zero)
            CGEventTapEnable(_eventTap, false);

        if (_runLoopSource != IntPtr.Zero)
        {
            var modeHandle = CFStringCreateWithCString(IntPtr.Zero, kCFRunLoopDefaultMode, 0x08000100);
            if (_runLoop != IntPtr.Zero)
                CFRunLoopRemoveSource(_runLoop, _runLoopSource, modeHandle);
            CFRelease(modeHandle);
            CFRelease(_runLoopSource);
            _runLoopSource = IntPtr.Zero;
        }

        if (_eventTap != IntPtr.Zero)
        {
            CFRelease(_eventTap);
            _eventTap = IntPtr.Zero;
        }

        _runLoop = IntPtr.Zero;
    }

    private IntPtr EventTapCallback(IntPtr proxy, int type, IntPtr eventRef, IntPtr userInfo)
    {
        if (_disposed) return eventRef;

        // Handle event tap disabled events (type 0 = kCGEventTapDisabledByTimeout or kCGEventTapDisabledByUserInput)
        if (type == 0)
        {
            Console.WriteLine("Event tap was disabled, re-enabling...");
            if (_eventTap != IntPtr.Zero)
            {
                CGEventTapEnable(_eventTap, true);
            }
            return eventRef;
        }

        if (type != kCGEventKeyDown)
        {
            return eventRef;
        }

        // Get the key code (field 9 = kCGKeyboardEventKeycode)
        var keyCode = (uint)CGEventGetIntegerValueField(eventRef, 9);
        var flags = CGEventGetFlags(eventRef);

        // Check against registered hotkeys
        foreach (var (registeredKeyCode, registeredModifiers, callback) in _registeredHotkeys)
        {
            if (keyCode == registeredKeyCode && ModifiersMatch(flags, registeredModifiers))
            {
                Console.WriteLine($"Hotkey triggered: keyCode=0x{keyCode:X2}");

                // Invoke callback on UI thread
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        callback();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Hotkey callback error: {ex.Message}");
                    }
                });

                // Don't consume the event - let it pass through
                break;
            }
        }

        return eventRef;
    }

    private bool ModifiersMatch(ulong eventFlags, ulong requiredModifiers)
    {
        // Mask out non-modifier flags
        const ulong modifierMask = kCGEventFlagMaskCommand | kCGEventFlagMaskShift |
                                   kCGEventFlagMaskAlternate | kCGEventFlagMaskControl;

        var eventModifiers = eventFlags & modifierMask;

        // Check if all required modifiers are present
        return (eventModifiers & requiredModifiers) == requiredModifiers &&
               // And no extra modifiers (optional - could be more lenient)
               (eventModifiers & ~requiredModifiers) == 0;
    }

    public uint Register(string hotkeyString, Action callback)
    {
        if (string.IsNullOrWhiteSpace(hotkeyString))
            return 0;

        // Wait for the event tap to be ready (with timeout)
        if (!_eventTapReady.Wait(2000)) // Wait up to 2 seconds
        {
            Console.WriteLine("Timeout waiting for event tap - cannot register hotkey");
            return 0;
        }

        if (_eventTap == IntPtr.Zero)
        {
            Console.WriteLine("Event tap not available - cannot register hotkey");
            return 0;
        }

        if (!ParseHotkey(hotkeyString, out uint keyCode, out ulong modifiers))
        {
            Console.WriteLine($"Failed to parse hotkey: {hotkeyString}");
            return 0;
        }

        _registeredHotkeys.Add((keyCode, modifiers, callback));
        Console.WriteLine($"Registered hotkey: {hotkeyString} (keyCode={keyCode:X}, modifiers={modifiers:X})");

        return (uint)_registeredHotkeys.Count;
    }

    public void UnregisterAll()
    {
        _registeredHotkeys.Clear();
    }

    private bool ParseHotkey(string hotkeyString, out uint keyCode, out ulong modifiers)
    {
        keyCode = 0;
        modifiers = 0;

        var parts = hotkeyString.Split('+').Select(p => p.Trim()).ToArray();
        if (parts.Length == 0)
            return false;

        foreach (var part in parts)
        {
            var lower = part.ToLowerInvariant();
            switch (lower)
            {
                case "ctrl":
                case "control":
                    modifiers |= kCGEventFlagMaskControl;
                    break;
                case "alt":
                case "option":
                case "opt":
                    modifiers |= kCGEventFlagMaskAlternate;
                    break;
                case "shift":
                    modifiers |= kCGEventFlagMaskShift;
                    break;
                case "cmd":
                case "command":
                case "win":
                case "meta":
                    modifiers |= kCGEventFlagMaskCommand;
                    break;
                default:
                    // This should be the key
                    if (KeyCodes.TryGetValue(part, out var code))
                    {
                        keyCode = code;
                    }
                    else
                    {
                        Console.WriteLine($"Unknown key: {part}");
                        return false;
                    }
                    break;
            }
        }

        return keyCode != 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();
        _stopRequested = true;

        // Stop the run loop. CFRunLoopStop is safe to call cross-thread while the
        // loop is still alive (it is here, before the Join). The run loop thread
        // then releases every CoreFoundation object itself in its finally block.
        // Tearing those down here instead would operate on a freed CFRunLoop once
        // the thread exits and hang forever in __CFCheckCFInfoPACSignature.
        if (_runLoop != IntPtr.Zero)
        {
            CFRunLoopStop(_runLoop);
        }

        // Wait for the thread to finish (and complete its own teardown).
        if (_runLoopThread != null && _runLoopThread.IsAlive)
        {
            _runLoopThread.Join(3000);
        }

        _eventTapReady.Dispose();
    }
}

/// <summary>
/// Interface for platform-specific push-to-talk hooks. A hook watches two key
/// chords (submit and type-only, either may be off); see PushToTalkChordTracker.
/// Events are raised on the UI thread.
/// </summary>
public interface IPushToTalkHook : IDisposable
{
    /// <summary>A chord went down; the argument is the mode so far.</summary>
    event Action<PushToTalkMode>? Started;
    /// <summary>The type-only chord became held mid-press.</summary>
    event Action<PushToTalkMode>? ModeChanged;
    /// <summary>Neither chord is held any more.</summary>
    event Action? Stopped;
}

internal static class PushToTalkHookEvents
{
    /// <summary>Posts a tracker change to the UI thread as the matching hook event.</summary>
    public static void Raise(PushToTalkChordTracker.Change change, PushToTalkMode mode,
        Action<PushToTalkMode>? started, Action<PushToTalkMode>? modeChanged, Action? stopped)
    {
        Action? raise = change switch
        {
            PushToTalkChordTracker.Change.Started => started == null ? null : () => started(mode),
            PushToTalkChordTracker.Change.ModeChanged => modeChanged == null ? null : () => modeChanged(mode),
            PushToTalkChordTracker.Change.Stopped => stopped,
            _ => null
        };
        if (raise == null) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { raise(); }
            catch (Exception ex) { Console.WriteLine($"PTT {change} callback error: {ex.Message}"); }
        });
    }

    public static string Label(string? submitKeys, string? typeOnlyKeys) =>
        $"submit: {Describe(submitKeys)}, no Enter: {Describe(typeOnlyKeys)}";

    private static string Describe(string? keys)
    {
        var normalized = PushToTalkKeySpec.Normalize(keys);
        return normalized == "" ? "off" : normalized;
    }
}

/// <summary>
/// Push-to-Talk hook using WH_KEYBOARD_LL to detect the push-to-talk chords' hold/release.
/// Windows-only.
/// </summary>
public class PushToTalkHook : IPushToTalkHook
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_QUIT = 0x0012;

    private const uint VK_LSHIFT = 0xA0;
    private const uint VK_RSHIFT = 0xA1;
    private const uint VK_LCONTROL = 0xA2;
    private const uint VK_RCONTROL = 0xA3;
    private const uint VK_LMENU = 0xA4; // Left Alt
    private const uint VK_RMENU = 0xA5; // Right Alt
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;

    /// <summary>
    /// Parse a "+"-joined push-to-talk key spec into the set of Windows virtual key
    /// codes that must all be held. Empty when the spec is empty (chord off).
    /// (Fn has no Windows virtual key code and is ignored here.)
    /// </summary>
    private static uint[] ParseVks(string? keys)
    {
        var vks = new List<uint>();
        foreach (var key in PushToTalkKeySpec.Parse(keys))
        {
            switch (key)
            {
                case "LeftControl": vks.Add(VK_LCONTROL); break;
                case "RightControl": vks.Add(VK_RCONTROL); break;
                case "LeftShift": vks.Add(VK_LSHIFT); break;
                case "RightShift": vks.Add(VK_RSHIFT); break;
                case "LeftOption": vks.Add(VK_LMENU); break;
                case "RightOption": vks.Add(VK_RMENU); break;
                case "LeftCommand": vks.Add(VK_LWIN); break;
                case "RightCommand": vks.Add(VK_RWIN); break;
            }
        }
        return vks.ToArray();
    }

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;
    private readonly uint[] _submitVks;
    private readonly uint[] _typeOnlyVks;
    private readonly HashSet<uint> _pressed = new();
    private readonly string _keysLabel;
    private readonly PushToTalkChordTracker _tracker = new();
    private bool _disposed;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private readonly ManualResetEventSlim _hookReady = new(false);

    public event Action<PushToTalkMode>? Started;
    public event Action<PushToTalkMode>? ModeChanged;
    public event Action? Stopped;

    public PushToTalkHook(string? submitKeys, string? typeOnlyKeys)
    {
        _submitVks = ParseVks(submitKeys);
        _typeOnlyVks = ParseVks(typeOnlyKeys);
        _keysLabel = PushToTalkHookEvents.Label(submitKeys, typeOnlyKeys);

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Push-to-Talk hook is only supported on Windows");
            _hookReady.Set();
            return;
        }

        _hookThread = new Thread(HookThreadProc)
        {
            Name = "PushToTalkHookThread",
            IsBackground = true
        };
        _hookThread.Start();

        if (!_hookReady.Wait(5000))
        {
            Console.WriteLine("Timeout waiting for PTT hook to be installed");
        }
    }

    private void HookThreadProc()
    {
        try
        {
            _hookThreadId = GetCurrentThreadId();
            _proc = HookCallback;

            var moduleHandle = GetModuleHandle(null);
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, moduleHandle, 0);

            if (_hookId == IntPtr.Zero)
            {
                Console.WriteLine($"Failed to install PTT keyboard hook. Error: {Marshal.GetLastWin32Error()}");
                _hookReady.Set();
                return;
            }

            Console.WriteLine($"Push-to-Talk keyboard hook installed (keys: {_keysLabel})");
            _hookReady.Set();

            // Message pump - required for WH_KEYBOARD_LL to work
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
            {
                if (msg.message == WM_QUIT) break;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in PTT hook thread: {ex.Message}");
            _hookReady.Set();
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (Array.IndexOf(_submitVks, hookStruct.vkCode) >= 0 || Array.IndexOf(_typeOnlyVks, hookStruct.vkCode) >= 0)
            {
                if (isDown) _pressed.Add(hookStruct.vkCode);
                else if (isUp) _pressed.Remove(hookStruct.vkCode);
            }

            var change = _tracker.Update(AllPressed(_submitVks), AllPressed(_typeOnlyVks));
            PushToTalkHookEvents.Raise(change, _tracker.Mode, Started, ModeChanged, Stopped);
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool AllPressed(uint[] vks) => vks.Length > 0 && Array.TrueForAll(vks, _pressed.Contains);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        if (_hookThreadId != 0)
        {
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        _hookThread?.Join(1000);
        _hookReady.Dispose();
    }
}

/// <summary>
/// Push-to-Talk hook for macOS using CGEventTap to detect the push-to-talk chords' hold/release.
/// Monitors kCGEventFlagsChanged events to track modifier key state.
/// Requires Input Monitoring permission (System Settings > Privacy & Security > Input Monitoring).
/// </summary>
public class PushToTalkHookMacOS : IPushToTalkHook
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CGEventTapCallbackDelegate(IntPtr proxy, int type, IntPtr eventRef, IntPtr userInfo);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern IntPtr CGEventTapCreate(
        int tap, int place, int options, ulong eventsOfInterest,
        CGEventTapCallbackDelegate callback, IntPtr userInfo);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern void CGEventTapEnable(IntPtr tap, bool enable);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern ulong CGEventGetFlags(IntPtr eventRef);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CGPreflightListenEventAccess();

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CGRequestListenEventAccess();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, int order);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFRunLoopGetCurrent();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopAddSource(IntPtr rl, IntPtr source, IntPtr mode);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopRemoveSource(IntPtr rl, IntPtr source, IntPtr mode);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopRun();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, bool returnAfterSourceHandled);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopStop(IntPtr rl);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, int encoding);

    // CGEventTap constants
    private const int kCGSessionEventTap = 1;
    private const int kCGHeadInsertEventTap = 0;
    private const int kCGEventTapOptionListenOnly = 1;

    // CGEvent types
    private const int kCGEventFlagsChanged = 12;

    // Device-dependent modifier flags (IOKit NX_DEVICE* masks)
    // These distinguish left vs right modifier keys directly from CGEventGetFlags.
    private const ulong NX_DEVICELCTLKEYMASK   = 0x00000001;
    private const ulong NX_DEVICELSHIFTKEYMASK = 0x00000002;
    private const ulong NX_DEVICERSHIFTKEYMASK = 0x00000004;
    private const ulong NX_DEVICELCMDKEYMASK   = 0x00000008;
    private const ulong NX_DEVICERCMDKEYMASK   = 0x00000010;
    private const ulong NX_DEVICELALTKEYMASK   = 0x00000020;
    private const ulong NX_DEVICERALTKEYMASK   = 0x00000040;
    private const ulong NX_DEVICERCTLKEYMASK   = 0x00002000;
    private const ulong NX_SECONDARYFNMASK     = 0x00800000; // Fn key (general CGEvent flag)

    /// <summary>
    /// Parse a "+"-joined push-to-talk key spec (e.g. "RightOption+RightShift", "Fn")
    /// into the set of device-dependent flag masks that must all be present.
    /// Empty when the spec is empty (chord off).
    /// </summary>
    private static ulong[] ParseMasks(string? keys)
    {
        var masks = new List<ulong>();
        foreach (var key in PushToTalkKeySpec.Parse(keys))
        {
            switch (key)
            {
                case "LeftControl": masks.Add(NX_DEVICELCTLKEYMASK); break;
                case "RightControl": masks.Add(NX_DEVICERCTLKEYMASK); break;
                case "LeftShift": masks.Add(NX_DEVICELSHIFTKEYMASK); break;
                case "RightShift": masks.Add(NX_DEVICERSHIFTKEYMASK); break;
                case "LeftOption": masks.Add(NX_DEVICELALTKEYMASK); break;
                case "RightOption": masks.Add(NX_DEVICERALTKEYMASK); break;
                case "LeftCommand": masks.Add(NX_DEVICELCMDKEYMASK); break;
                case "RightCommand": masks.Add(NX_DEVICERCMDKEYMASK); break;
                case "Fn": masks.Add(NX_SECONDARYFNMASK); break;
            }
        }
        return masks.ToArray();
    }

    // CFString encoding
    private const int kCFStringEncodingUTF8 = 0x08000100;
    private const string kCFRunLoopDefaultMode = "kCFRunLoopDefaultMode";

    // State
    private IntPtr _eventTap;
    private IntPtr _runLoopSource;
    private IntPtr _runLoop;
    private CGEventTapCallbackDelegate? _callbackDelegate;
    private Thread? _runLoopThread;
    private volatile bool _stopRequested;
    private bool _disposed;
    private readonly ManualResetEventSlim _hookReady = new(false);

    // Activation state
    private readonly PushToTalkChordTracker _tracker = new();

    // Device-dependent masks that must ALL be present in the event flags for each chord.
    private readonly ulong[] _submitMasks;
    private readonly ulong[] _typeOnlyMasks;
    private readonly string _keysLabel;

    public event Action<PushToTalkMode>? Started;
    public event Action<PushToTalkMode>? ModeChanged;
    public event Action? Stopped;

    public PushToTalkHookMacOS(string? submitKeys, string? typeOnlyKeys)
    {
        _submitMasks = ParseMasks(submitKeys);
        _typeOnlyMasks = ParseMasks(typeOnlyKeys);
        _keysLabel = PushToTalkHookEvents.Label(submitKeys, typeOnlyKeys);

        if (!OperatingSystem.IsMacOS())
        {
            Console.WriteLine("PushToTalkHookMacOS is only supported on macOS");
            _hookReady.Set();
            return;
        }

        // Check Input Monitoring permission
        if (!CGPreflightListenEventAccess())
        {
            Console.WriteLine("Input Monitoring permission not granted. Requesting...");
            Console.WriteLine("Go to System Settings > Privacy & Security > Input Monitoring and add this app.");
            CGRequestListenEventAccess();
        }

        _runLoopThread = new Thread(RunEventTapThread)
        {
            Name = "PushToTalkHookThread",
            IsBackground = true
        };
        _runLoopThread.Start();

        if (!_hookReady.Wait(5000))
        {
            Console.WriteLine("Timeout waiting for macOS PTT hook to be installed");
        }
    }

    private void RunEventTapThread()
    {
        try
        {
            _callbackDelegate = EventTapCallback;

            // Listen for kCGEventFlagsChanged (modifier key state changes)
            ulong eventsOfInterest = 1UL << kCGEventFlagsChanged;

            _eventTap = CGEventTapCreate(
                kCGSessionEventTap,
                kCGHeadInsertEventTap,
                kCGEventTapOptionListenOnly,
                eventsOfInterest,
                _callbackDelegate,
                IntPtr.Zero);

            if (_eventTap == IntPtr.Zero)
            {
                Console.WriteLine(
                    "Failed to create PTT event tap. " +
                    "Ensure Input Monitoring permission is granted in " +
                    "System Settings > Privacy & Security > Input Monitoring.");
                _hookReady.Set();
                return;
            }

            _runLoopSource = CFMachPortCreateRunLoopSource(IntPtr.Zero, _eventTap, 0);
            if (_runLoopSource == IntPtr.Zero)
            {
                Console.WriteLine("Failed to create PTT run loop source");
                CFRelease(_eventTap);
                _eventTap = IntPtr.Zero;
                _hookReady.Set();
                return;
            }

            _runLoop = CFRunLoopGetCurrent();

            var modeStr = CFStringCreateWithCString(IntPtr.Zero, kCFRunLoopDefaultMode, kCFStringEncodingUTF8);
            CFRunLoopAddSource(_runLoop, _runLoopSource, modeStr);
            CGEventTapEnable(_eventTap, true);
            CFRelease(modeStr);

            Console.WriteLine($"Push-to-Talk event tap installed (macOS, keys: {_keysLabel})");
            _hookReady.Set();

            var modeHandle = CFStringCreateWithCString(IntPtr.Zero, kCFRunLoopDefaultMode, kCFStringEncodingUTF8);
            while (!_stopRequested)
            {
                // Run with a timeout so we can periodically re-enable the tap
                // if macOS disabled it due to timeout
                CFRunLoopRunInMode(modeHandle, 2.0, false);

                if (!_stopRequested && _eventTap != IntPtr.Zero)
                    CGEventTapEnable(_eventTap, true);
            }
            CFRelease(modeHandle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in macOS PTT hook thread: {ex.Message}");
        }
        finally
        {
            // Release CoreFoundation objects on the SAME thread that owns the run
            // loop, before the thread (and its CFRunLoop) goes away. Doing this from
            // Dispose() on another thread dereferences a freed CFRunLoop and traps
            // forever in __CFCheckCFInfoPACSignature.
            TeardownEventTap();

            // Always signal readiness so the constructor never blocks on failure.
            _hookReady.Set();
        }
    }

    private void TeardownEventTap()
    {
        if (_eventTap != IntPtr.Zero)
            CGEventTapEnable(_eventTap, false);

        if (_runLoopSource != IntPtr.Zero)
        {
            var modeStr = CFStringCreateWithCString(IntPtr.Zero, kCFRunLoopDefaultMode, kCFStringEncodingUTF8);
            if (_runLoop != IntPtr.Zero)
                CFRunLoopRemoveSource(_runLoop, _runLoopSource, modeStr);
            CFRelease(modeStr);
            CFRelease(_runLoopSource);
            _runLoopSource = IntPtr.Zero;
        }

        if (_eventTap != IntPtr.Zero)
        {
            CFRelease(_eventTap);
            _eventTap = IntPtr.Zero;
        }

        _runLoop = IntPtr.Zero;
    }

    private IntPtr EventTapCallback(IntPtr proxy, int type, IntPtr eventRef, IntPtr userInfo)
    {
        if (_disposed) return eventRef;

        // Handle event tap disabled by timeout — re-enable it
        if (type != kCGEventFlagsChanged)
        {
            if (_eventTap != IntPtr.Zero)
            {
                Console.WriteLine("PTT event tap was disabled, re-enabling...");
                CGEventTapEnable(_eventTap, true);
            }
            return eventRef;
        }

        // Use device-dependent flag masks (NX_DEVICE*) to directly read
        // right-side modifier state from flags. This is more reliable than
        // tracking state via keyCode, which macOS doesn't always report
        // correctly for modifier key releases.
        ulong flags = CGEventGetFlags(eventRef);
        var change = _tracker.Update(AllPresent(flags, _submitMasks), AllPresent(flags, _typeOnlyMasks));
        PushToTalkHookEvents.Raise(change, _tracker.Mode, Started, ModeChanged, Stopped);

        return eventRef;
    }

    private static bool AllPresent(ulong flags, ulong[] masks) =>
        masks.Length > 0 && Array.TrueForAll(masks, mask => (flags & mask) != 0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopRequested = true;

        // Stop the run loop. CFRunLoopStop is safe cross-thread while the loop is
        // still alive (it is here, before the Join). The run loop thread releases
        // every CoreFoundation object itself in its finally block; doing it here
        // would operate on a freed CFRunLoop once the thread exits and hang forever
        // in __CFCheckCFInfoPACSignature.
        if (_runLoop != IntPtr.Zero)
        {
            CFRunLoopStop(_runLoop);
        }

        // Join timeout must exceed CFRunLoopRunInMode timeout (2s)
        _runLoopThread?.Join(3000);

        _hookReady.Dispose();
    }
}
