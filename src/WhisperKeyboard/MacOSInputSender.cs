using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WhisperKeyboard;

/// <summary>
/// macOS key simulation using CGEvent APIs.
/// Requires Accessibility permission to post events.
/// When running from a .app bundle (Dock launch), the app itself must be added
/// to System Settings > Privacy & Security > Accessibility.
/// </summary>
public static class MacOSInputSender
{
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort keyCode, bool keyDown);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern void CGEventSetFlags(IntPtr eventRef, ulong flags);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern void CGEventPost(int tap, IntPtr eventRef);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern void CGEventKeyboardSetUnicodeString(IntPtr eventRef, int length, [MarshalAs(UnmanagedType.LPWStr)] string str);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern IntPtr CGEventSourceCreate(int stateID);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CGPreflightPostEventAccess();

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CGRequestPostEventAccess();

    // CGEventTapLocation
    private const int kCGSessionEventTap = 1;

    // CGEventSourceStateID
    private const int kCGEventSourceStateHIDSystemState = 1;

    // CGEvent modifier flags
    private const ulong kCGEventFlagMaskCommand = 0x00100000;

    // macOS virtual key codes
    private const ushort kVK_Return = 0x24;     // 36
    private const ushort kVK_Delete = 0x33;     // 51 (backspace)
    private const ushort kVK_V = 0x09;          // 9

    private static bool _permissionChecked;

    private static void EnsurePermission()
    {
        if (_permissionChecked) return;
        _permissionChecked = true;

        if (!CGPreflightPostEventAccess())
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] CGEvent post permission not granted. Requesting...");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Go to System Settings > Privacy & Security > Accessibility and add this app.");
            CGRequestPostEventAccess();
        }
        else
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] CGEvent post permission granted (Accessibility)");
        }
    }

    /// <summary>
    /// Verify that CGEvent posting actually works.
    /// When running from a .app bundle, CGPreflightPostEventAccess() can return true
    /// (inheriting Terminal's permission) even though CGEventPost silently fails.
    /// This method re-checks permission and requests it if running from a bundle.
    /// </summary>
    public static void VerifyPostingWorks()
    {
        EnsurePermission();

        // Detect .app bundle — the real permission check matters here
        var bundlePath = GetAppBundlePath();
        if (bundlePath != null)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MacOSInputSender: Running from .app bundle ({bundlePath})");

            // When running from a .app bundle, CGPreflightPostEventAccess may return a
            // cached/inherited result from Terminal. Force a fresh check by resetting and
            // requesting again. The system will show the permission dialog if needed.
            if (!CGPreflightPostEventAccess())
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MacOSInputSender: Accessibility permission NOT granted for this app bundle.");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MacOSInputSender: Typing will not work until you add this app to:");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]   System Settings > Privacy & Security > Accessibility");
                CGRequestPostEventAccess();
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MacOSInputSender: Accessibility permission appears granted for bundle");
            }
        }
        else
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MacOSInputSender: Not running from .app bundle (CGEvent should work via Terminal permission)");
        }
    }

    /// <summary>
    /// Simulate Cmd+V paste.
    /// </summary>
    public static void SendPaste()
    {
        EnsurePermission();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MacOSInputSender: Sending Cmd+V paste");
        PostKeyStroke(kVK_V, kCGEventFlagMaskCommand);
    }

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern ulong CGEventSourceFlagsState(int stateID);

    // Shift, Control, Option, Command (Caps Lock is left out on purpose)
    private const ulong ModifierMask = 0x00020000 | 0x00040000 | 0x00080000 | 0x00100000;

    /// <summary>
    /// Waits until no modifier key is held (or the timeout passes). Push-to-talk stops
    /// when either of its keys is released, so the other may still be down, and typed
    /// characters would carry its flags.
    /// </summary>
    public static async Task WaitForModifiersReleasedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        bool logged = false;
        while ((CGEventSourceFlagsState(kCGEventSourceStateHIDSystemState) & ModifierMask) != 0)
        {
            if (!logged)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MacOSInputSender: waiting for modifier keys to be released");
                logged = true;
            }
            if (DateTime.UtcNow >= deadline)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MacOSInputSender: modifier keys still held after {timeout.TotalMilliseconds:F0} ms - sending anyway");
                return;
            }
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Simulate Return key press.
    /// </summary>
    public static void SendEnter()
    {
        EnsurePermission();
        PostKeyStroke(kVK_Return, 0);
    }

    /// <summary>
    /// Simulate Backspace key press.
    /// </summary>
    public static void SendBackspace()
    {
        EnsurePermission();
        PostKeyStroke(kVK_Delete, 0);
    }

    /// <summary>
    /// Type text by posting CGEvents with unicode strings.
    /// </summary>
    public static void SendText(string text)
    {
        EnsurePermission();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MacOSInputSender: Sending text ({text.Length} chars)");

        var source = CGEventSourceCreate(kCGEventSourceStateHIDSystemState);

        // Send in chunks — CGEventKeyboardSetUnicodeString supports up to ~20 chars reliably
        const int chunkSize = 16;
        for (int i = 0; i < text.Length; i += chunkSize)
        {
            var chunk = text.Substring(i, Math.Min(chunkSize, text.Length - i));

            var keyDown = CGEventCreateKeyboardEvent(source, 0, true);
            var keyUp = CGEventCreateKeyboardEvent(source, 0, false);

            if (keyDown == IntPtr.Zero || keyUp == IntPtr.Zero)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MacOSInputSender: CGEventCreateKeyboardEvent returned null - check Accessibility permission");
                if (keyDown != IntPtr.Zero) CFRelease(keyDown);
                if (keyUp != IntPtr.Zero) CFRelease(keyUp);
                break;
            }

            CGEventKeyboardSetUnicodeString(keyDown, chunk.Length, chunk);
            CGEventKeyboardSetUnicodeString(keyUp, chunk.Length, chunk);

            CGEventPost(kCGSessionEventTap, keyDown);
            CGEventPost(kCGSessionEventTap, keyUp);

            CFRelease(keyDown);
            CFRelease(keyUp);

            // Small delay between chunks to let the system process them
            if (i + chunkSize < text.Length)
                Thread.Sleep(5);
        }

        if (source != IntPtr.Zero)
            CFRelease(source);
    }

    private static void PostKeyStroke(ushort keyCode, ulong modifierFlags)
    {
        var source = CGEventSourceCreate(kCGEventSourceStateHIDSystemState);

        var keyDown = CGEventCreateKeyboardEvent(source, keyCode, true);
        var keyUp = CGEventCreateKeyboardEvent(source, keyCode, false);

        if (keyDown == IntPtr.Zero || keyUp == IntPtr.Zero)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MacOSInputSender: CGEventCreateKeyboardEvent returned null - check Accessibility permission");
            if (keyDown != IntPtr.Zero) CFRelease(keyDown);
            if (keyUp != IntPtr.Zero) CFRelease(keyUp);
            if (source != IntPtr.Zero) CFRelease(source);
            return;
        }

        if (modifierFlags != 0)
        {
            CGEventSetFlags(keyDown, modifierFlags);
            CGEventSetFlags(keyUp, modifierFlags);
        }

        CGEventPost(kCGSessionEventTap, keyDown);
        Thread.Sleep(50);
        CGEventPost(kCGSessionEventTap, keyUp);

        CFRelease(keyDown);
        CFRelease(keyUp);
        if (source != IntPtr.Zero)
            CFRelease(source);
    }

    // ObjC runtime for bundle detection
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string className);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string selectorName);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_retPtr(IntPtr receiver, IntPtr selector);

    private static string? GetAppBundlePath()
    {
        try
        {
            var cls = objc_getClass("NSBundle");
            if (cls == IntPtr.Zero) return null;
            var bundle = objc_msgSend_retPtr(cls, sel_registerName("mainBundle"));
            if (bundle == IntPtr.Zero) return null;
            var pathObj = objc_msgSend_retPtr(bundle, sel_registerName("bundlePath"));
            if (pathObj == IntPtr.Zero) return null;
            var path = Marshal.PtrToStringUTF8(objc_msgSend_retPtr(pathObj, sel_registerName("UTF8String")));
            return path != null && path.EndsWith(".app") ? path : null;
        }
        catch
        {
            return null;
        }
    }
}
