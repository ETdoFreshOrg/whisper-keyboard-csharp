using System.Runtime.InteropServices;
using WhisperKeyboard.Core.TextInsertion;

namespace WhisperKeyboard;

/// <summary>
/// Reads the focused text field through the macOS Accessibility API (the same
/// permission the app already needs to post keystrokes). Asks Chromium-based apps
/// to expose their web content with AXManualAccessibility, and never reads secure
/// (password) fields.
/// </summary>
public class MacOSFocusedFieldProbe : IFocusedFieldProbe
{
    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint kCFStringEncodingUTF8 = 0x08000100;
    private const int MaxTextLength = 200_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1000);

    [DllImport(AppServices)] private static extern IntPtr AXUIElementCreateSystemWide();
    [DllImport(AppServices)] private static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
    [DllImport(AppServices)] private static extern int AXUIElementSetAttributeValue(IntPtr element, IntPtr attribute, IntPtr value);
    [DllImport(AppServices)] private static extern int AXUIElementGetPid(IntPtr element, out int pid);
    [DllImport(AppServices)] private static extern int AXUIElementSetMessagingTimeout(IntPtr element, float timeoutInSeconds);

    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetLength(IntPtr str);
    [DllImport(CoreFoundation)] private static extern void CFStringGetCharacters(IntPtr str, CFRange range, [Out] char[] buffer);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(IntPtr cf);
    [DllImport(CoreFoundation)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);

    [StructLayout(LayoutKind.Sequential)]
    private struct CFRange
    {
        public nint Location;
        public nint Length;
    }

    private static readonly Lazy<IntPtr> SystemWide = new(() =>
    {
        var e = AXUIElementCreateSystemWide();
        AXUIElementSetMessagingTimeout(e, 0.5f);
        return e;
    });
    private static readonly Dictionary<string, IntPtr> Names = new();
    private readonly HashSet<int> _manualAccessibilityPids = new();

    public async Task<FieldSnapshot?> CaptureAsync()
    {
        var read = Task.Run(Read);
        try { return await read.WaitAsync(Timeout); }
        catch (TimeoutException)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Insert check: Accessibility read timed out");
            return null;
        }
    }

    private FieldSnapshot? Read()
    {
        if (AXUIElementCopyAttributeValue(SystemWide.Value, Name("AXFocusedApplication"), out var app) != 0 || app == IntPtr.Zero)
            return null;
        try
        {
            AXUIElementGetPid(app, out var pid);
            EnableManualAccessibility(app, pid);

            if (AXUIElementCopyAttributeValue(app, Name("AXFocusedUIElement"), out var element) != 0 || element == IntPtr.Zero)
                return null;
            try
            {
                var owner = pid.ToString();
                if (CopyString(element, "AXSubrole") == "AXSecureTextField")
                    return new FieldSnapshot(owner, null);
                return new FieldSnapshot(owner, CopyString(element, "AXValue"));
            }
            finally { CFRelease(element); }
        }
        finally { CFRelease(app); }
    }

    /// <summary>
    /// Chrome, Edge and Electron apps only expose web content to accessibility clients
    /// that ask for it; AXManualAccessibility asks without the side effects of
    /// AXEnhancedUserInterface. Apps that don't know the attribute ignore it.
    /// </summary>
    private void EnableManualAccessibility(IntPtr app, int pid)
    {
        lock (_manualAccessibilityPids)
        {
            if (!_manualAccessibilityPids.Add(pid)) return;
        }
        var cfTrue = CFBooleanTrue.Value;
        if (cfTrue != IntPtr.Zero)
            AXUIElementSetAttributeValue(app, Name("AXManualAccessibility"), cfTrue);
    }

    private static readonly Lazy<IntPtr> CFBooleanTrue = new(() =>
    {
        try
        {
            var lib = NativeLibrary.Load(CoreFoundation);
            return Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, "kCFBooleanTrue"));
        }
        catch { return IntPtr.Zero; }
    });

    private static string? CopyString(IntPtr element, string attribute)
    {
        if (AXUIElementCopyAttributeValue(element, Name(attribute), out var value) != 0 || value == IntPtr.Zero)
            return null;
        try
        {
            if (CFGetTypeID(value) != CFStringGetTypeID()) return null;
            var length = (int)Math.Min(CFStringGetLength(value), MaxTextLength);
            var buffer = new char[length];
            CFStringGetCharacters(value, new CFRange { Location = 0, Length = length }, buffer);
            return new string(buffer);
        }
        finally { CFRelease(value); }
    }

    // Attribute names are created once and kept for the life of the app.
    private static IntPtr Name(string name)
    {
        lock (Names)
        {
            if (!Names.TryGetValue(name, out var cf))
                Names[name] = cf = CFStringCreateWithCString(IntPtr.Zero, name, kCFStringEncodingUTF8);
            return cf;
        }
    }
}
