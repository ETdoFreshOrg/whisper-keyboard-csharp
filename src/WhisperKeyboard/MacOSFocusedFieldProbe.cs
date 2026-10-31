using System.Runtime.InteropServices;
using WhisperKeyboard.Core.TextInsertion;

namespace WhisperKeyboard;

/// <summary>
/// Reads the focused text field through the macOS Accessibility API (the same
/// permission the app already needs to post keystrokes). Asks Chromium-based apps
/// to expose their web content (see <see cref="WebContentAccessibility"/>), and never
/// reads secure (password) fields.
/// </summary>
public class MacOSFocusedFieldProbe : IFocusedFieldProbe
{
    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string LibObjC = "/usr/lib/libobjc.dylib";
    private const uint kCFStringEncodingUTF8 = 0x08000100;
    private const int MaxTextLength = 200_000;
    private const int ProcPidPathMax = 4096;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1000);

    [DllImport(AppServices)] private static extern IntPtr AXUIElementCreateSystemWide();
    [DllImport(AppServices)] private static extern IntPtr AXUIElementCreateApplication(int pid);
    [DllImport(AppServices)] private static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
    [DllImport(AppServices)] private static extern int AXUIElementSetAttributeValue(IntPtr element, IntPtr attribute, IntPtr value);
    [DllImport(AppServices)] private static extern int AXUIElementGetPid(IntPtr element, out int pid);
    [DllImport(AppServices)] private static extern int AXUIElementSetMessagingTimeout(IntPtr element, float timeoutInSeconds);

    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetLength(IntPtr str);
    // CharSet.Unicode: UniChar is UTF-16. The default (Ansi) passes a 1-byte-per-char
    // buffer, which CFStringGetCharacters overruns and the result comes back garbled.
    [DllImport(CoreFoundation, CharSet = CharSet.Unicode)] private static extern void CFStringGetCharacters(IntPtr str, CFRange range, [Out] char[] buffer);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(IntPtr cf);
    [DllImport(CoreFoundation)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
    [DllImport(CoreFoundation)] private static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr alloc, byte[] buffer, nint length, bool isDirectory);
    [DllImport(CoreFoundation)] private static extern IntPtr CFBundleCreate(IntPtr alloc, IntPtr bundleUrl);
    [DllImport(CoreFoundation)] private static extern IntPtr CFBundleGetIdentifier(IntPtr bundle);

    [DllImport(LibSystem)] private static extern int proc_pidpath(int pid, byte[] buffer, uint bufferSize);

    [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string className);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string selectorName);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend_retPtr(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern int objc_msgSend_retInt(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC)] private static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(LibObjC)] private static extern void objc_autoreleasePoolPop(IntPtr pool);

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
    private readonly WebContentAccessibility _webContent = new(log: m => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}"));
    private readonly Dictionary<int, (string? BundleId, bool BundlesElectron)> _apps = new();

    public void Prepare()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var (app, pid) = FocusedApplication();
                if (app == IntPtr.Zero) return;
                try { EnsureWebContent(app, pid); }
                finally { CFRelease(app); }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Insert check: preparing the focused app failed: {ex.Message}");
            }
        });
    }

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
        var (app, pid) = FocusedApplication();
        if (app == IntPtr.Zero) return null;
        try
        {
            EnsureWebContent(app, pid);

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
    /// The app that receives key events, as an AX element the caller releases. Asks
    /// NSWorkspace first: the system-wide AXFocusedApplication fails (kAXErrorCannotComplete)
    /// while Chrome or one of its web apps is in front and its accessibility is off,
    /// which is exactly when it needs to be asked to turn it on.
    /// </summary>
    private static (IntPtr App, int Pid) FocusedApplication()
    {
        int pid = FrontmostPid();
        if (pid > 0)
        {
            var app = AXUIElementCreateApplication(pid);
            if (app != IntPtr.Zero)
            {
                AXUIElementSetMessagingTimeout(app, 0.5f);
                return (app, pid);
            }
        }
        if (AXUIElementCopyAttributeValue(SystemWide.Value, Name("AXFocusedApplication"), out var focused) != 0 || focused == IntPtr.Zero)
            return (IntPtr.Zero, 0);
        AXUIElementGetPid(focused, out pid);
        return (focused, pid);
    }

    private static int FrontmostPid()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var workspace = objc_msgSend_retPtr(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
            if (workspace == IntPtr.Zero) return 0;
            var app = objc_msgSend_retPtr(workspace, sel_registerName("frontmostApplication"));
            return app == IntPtr.Zero ? 0 : objc_msgSend_retInt(app, sel_registerName("processIdentifier"));
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private void EnsureWebContent(IntPtr app, int pid)
    {
        var cfTrue = CFBooleanTrue.Value;
        if (cfTrue == IntPtr.Zero || pid <= 0) return;
        var (bundleId, bundlesElectron) = AppInfo(pid);
        _webContent.Ensure(pid, bundleId, bundlesElectron,
            attribute => AXUIElementSetAttributeValue(app, Name(attribute), cfTrue) == 0);
    }

    /// <summary>The process's bundle identifier and whether its bundle contains Electron,
    /// looked up once per process.</summary>
    private (string? BundleId, bool BundlesElectron) AppInfo(int pid)
    {
        lock (_apps)
        {
            if (_apps.TryGetValue(pid, out var known)) return known;
        }

        (string?, bool) info = (null, false);
        var buffer = new byte[ProcPidPathMax];
        int length = proc_pidpath(pid, buffer, (uint)buffer.Length);
        if (length > 0)
        {
            var executable = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
            int end = executable.LastIndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
            if (end >= 0)
            {
                var bundlePath = executable[..(end + ".app".Length)];
                info = (BundleIdentifier(bundlePath),
                        Directory.Exists(Path.Combine(bundlePath, "Contents", "Frameworks", "Electron Framework.framework")));
            }
        }

        lock (_apps) _apps[pid] = info;
        return info;
    }

    private static string? BundleIdentifier(string bundlePath)
    {
        var path = System.Text.Encoding.UTF8.GetBytes(bundlePath);
        var url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, path, path.Length, true);
        if (url == IntPtr.Zero) return null;
        try
        {
            var bundle = CFBundleCreate(IntPtr.Zero, url);
            if (bundle == IntPtr.Zero) return null;
            try { return ToManagedString(CFBundleGetIdentifier(bundle)); } // Get rule: not released
            finally { CFRelease(bundle); }
        }
        finally { CFRelease(url); }
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
        try { return ToManagedString(value); }
        finally { CFRelease(value); }
    }

    private static string? ToManagedString(IntPtr value)
    {
        if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID()) return null;
        var length = (int)Math.Min(CFStringGetLength(value), MaxTextLength);
        var buffer = new char[length];
        CFStringGetCharacters(value, new CFRange { Location = 0, Length = length }, buffer);
        return new string(buffer);
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
