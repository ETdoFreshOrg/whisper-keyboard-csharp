using System.IO;
using System.Runtime.InteropServices;

namespace WhisperKeyboard;

/// <summary>
/// Pure P/Invoke wrapper for OpenAL, bypassing OpenTK to directly use openal-soft on macOS.
/// This is needed because OpenTK uses macOS's built-in OpenAL framework which doesn't support capture.
/// </summary>
public static class OpenALNative
{
    // OpenAL-soft library path - try multiple locations
    private static readonly string[] LibraryPaths = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "libopenal.dylib"),   // bundled in the .app (Contents/MacOS)
        Path.Combine(AppContext.BaseDirectory, "libopenal.1.dylib"),
        "/opt/homebrew/opt/openal-soft/lib/libopenal.dylib",  // Apple Silicon Mac
        "/usr/local/opt/openal-soft/lib/libopenal.dylib",     // Intel Mac
        "/usr/lib/libopenal.so.1",                             // Linux
        "/usr/lib/x86_64-linux-gnu/libopenal.so.1",           // Ubuntu/Debian
        "openal32.dll",                                        // Windows
        "libopenal.so",                                        // Generic Linux
    };

    private static IntPtr _libraryHandle;

    /// <summary>Whether the OpenAL capture backend (openal-soft) loaded successfully.</summary>
    public static bool IsAvailable { get; private set; }
    /// <summary>Path/source the library loaded from, or null if unavailable.</summary>
    public static string? LoadedPath { get; private set; }
    /// <summary>Human-readable reason the library failed to load, or null when available.</summary>
    public static string? LoadError { get; private set; }

    // Function delegates
    // openal-soft device names are UTF-8 on every platform (e.g. "Name’s AirPods").
    private delegate IntPtr AlcOpenDeviceDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string? devicename);
    private delegate bool AlcCloseDeviceDelegate(IntPtr device);
    private delegate IntPtr AlcCaptureOpenDeviceDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string? devicename, int frequency, int format, int buffersize);
    private delegate bool AlcCaptureCloseDeviceDelegate(IntPtr device);
    private delegate void AlcCaptureStartDelegate(IntPtr device);
    private delegate void AlcCaptureStopDelegate(IntPtr device);
    private delegate void AlcCaptureSamplesDelegate(IntPtr device, IntPtr buffer, int samples);
    private delegate void AlcGetIntegervDelegate(IntPtr device, int param, int size, out int values);
    private delegate IntPtr AlcGetStringDelegate(IntPtr device, int param);

    // Cached function pointers
    private static AlcOpenDeviceDelegate? _alcOpenDevice;
    private static AlcCloseDeviceDelegate? _alcCloseDevice;
    private static AlcCaptureOpenDeviceDelegate? _alcCaptureOpenDevice;
    private static AlcCaptureCloseDeviceDelegate? _alcCaptureCloseDevice;
    private static AlcCaptureStartDelegate? _alcCaptureStart;
    private static AlcCaptureStopDelegate? _alcCaptureStop;
    private static AlcCaptureSamplesDelegate? _alcCaptureSamples;
    private static AlcGetIntegervDelegate? _alcGetIntegerv;
    private static AlcGetStringDelegate? _alcGetString;

    // alcGetString(NULL, ...) returns pointers into strings that the next
    // enumeration overwrites, so enumerate-and-copy must not interleave across threads.
    private static readonly object _enumerationLock = new();

    // OpenAL constants
    public const int ALC_CAPTURE_DEVICE_SPECIFIER = 0x310;
    public const int ALC_CAPTURE_DEFAULT_DEVICE_SPECIFIER = 0x311;
    public const int ALC_CAPTURE_SAMPLES = 0x312;
    public const int AL_FORMAT_MONO16 = 0x1101;

    static OpenALNative()
    {
        TryLoadLibrary();
    }

    /// <summary>
    /// Re-attempt loading the library if it is not already available. Lets the app
    /// pick up an openal-soft install that happened after startup (e.g. the user runs
    /// `brew install openal-soft` and clicks "Check Again"). Returns IsAvailable.
    /// </summary>
    public static bool TryReload()
    {
        if (IsAvailable) return true;
        TryLoadLibrary();
        return IsAvailable;
    }

    private static bool TryLoadFrom(IntPtr handle, string source)
    {
        try
        {
            _libraryHandle = handle;
            LoadFunctions();
            LoadedPath = source;
            LoadError = null;
            IsAvailable = true;
            Console.WriteLine($"Loaded OpenAL from: {source}");
            return true;
        }
        catch (Exception ex)
        {
            // Library loaded but is missing expected entry points (e.g. macOS's
            // built-in OpenAL.framework lacks capture support) — treat as unavailable
            // and free the handle so repeated probes (TryReload) don't leak it.
            LoadError = $"OpenAL loaded from {source} but is missing required functions: {ex.Message}";
            IsAvailable = false;
            _libraryHandle = IntPtr.Zero;
            try { NativeLibrary.Free(handle); } catch { /* best effort */ }
            return false;
        }
    }

    private static void TryLoadLibrary()
    {
        foreach (var path in LibraryPaths)
        {
            if (!string.IsNullOrEmpty(path) && NativeLibrary.TryLoad(path, out IntPtr handle))
            {
                if (TryLoadFrom(handle, path)) return;
            }
        }

        // Try loading by name as fallback
        if (NativeLibrary.TryLoad("openal", out IntPtr sysHandle) ||
            NativeLibrary.TryLoad("OpenAL", out sysHandle))
        {
            if (TryLoadFrom(sysHandle, "system path")) return;
        }

        IsAvailable = false;
        LoadedPath = null;
        LoadError = "OpenAL capture backend not found. On macOS install with: brew install openal-soft";
        Console.WriteLine($"[OpenAL] {LoadError}");
    }

    private static void LoadFunctions()
    {
        _alcOpenDevice = GetFunction<AlcOpenDeviceDelegate>("alcOpenDevice");
        _alcCloseDevice = GetFunction<AlcCloseDeviceDelegate>("alcCloseDevice");
        _alcCaptureOpenDevice = GetFunction<AlcCaptureOpenDeviceDelegate>("alcCaptureOpenDevice");
        _alcCaptureCloseDevice = GetFunction<AlcCaptureCloseDeviceDelegate>("alcCaptureCloseDevice");
        _alcCaptureStart = GetFunction<AlcCaptureStartDelegate>("alcCaptureStart");
        _alcCaptureStop = GetFunction<AlcCaptureStopDelegate>("alcCaptureStop");
        _alcCaptureSamples = GetFunction<AlcCaptureSamplesDelegate>("alcCaptureSamples");
        _alcGetIntegerv = GetFunction<AlcGetIntegervDelegate>("alcGetIntegerv");
        _alcGetString = GetFunction<AlcGetStringDelegate>("alcGetString");
    }

    private static T GetFunction<T>(string name) where T : Delegate
    {
        var ptr = NativeLibrary.GetExport(_libraryHandle, name);
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    // Public API

    public static IntPtr CaptureOpenDevice(string? deviceName, int sampleRate, int format, int bufferSize)
    {
        if (!IsAvailable || _alcCaptureOpenDevice == null)
            throw new DllNotFoundException(LoadError ?? "OpenAL capture backend not available");
        return _alcCaptureOpenDevice(deviceName, sampleRate, format, bufferSize);
    }

    public static bool CaptureCloseDevice(IntPtr device)
    {
        return _alcCaptureCloseDevice!(device);
    }

    public static void CaptureStart(IntPtr device)
    {
        _alcCaptureStart!(device);
    }

    public static void CaptureStop(IntPtr device)
    {
        _alcCaptureStop!(device);
    }

    public static void CaptureSamples(IntPtr device, short[] buffer, int samples)
    {
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            _alcCaptureSamples!(device, handle.AddrOfPinnedObject(), samples);
        }
        finally
        {
            handle.Free();
        }
    }

    public static int GetCapturedSamples(IntPtr device)
    {
        _alcGetIntegerv!(device, ALC_CAPTURE_SAMPLES, 1, out int samples);
        return samples;
    }

    public static List<string> GetCaptureDeviceNames()
    {
        var devices = new List<string>();
        if (!IsAvailable || _alcGetString == null)
            return devices;

        lock (_enumerationLock)
        {
            var ptr = _alcGetString(IntPtr.Zero, ALC_CAPTURE_DEVICE_SPECIFIER);

            if (ptr == IntPtr.Zero)
                return devices;

            // The string is a null-separated list terminated by double-null. Step by
            // UTF-8 byte length, not character count, or names with non-ASCII
            // characters throw every following entry out of alignment.
            while (true)
            {
                int length = Utf8Length(ptr);
                if (length == 0)
                    break;

                devices.Add(Marshal.PtrToStringUTF8(ptr, length));
                ptr = IntPtr.Add(ptr, length + 1);
            }
        }

        return devices;
    }

    /// <summary>
    /// Name of the current system default capture device, or null if there is none.
    /// openal-soft caches the default until the capture list is re-enumerated, so this
    /// re-probes first (~1ms on macOS) to see changes made since the last call.
    /// </summary>
    public static string? GetDefaultCaptureDeviceName()
    {
        if (!IsAvailable || _alcGetString == null)
            return null;

        lock (_enumerationLock)
        {
            _alcGetString(IntPtr.Zero, ALC_CAPTURE_DEVICE_SPECIFIER);
            var ptr = _alcGetString(IntPtr.Zero, ALC_CAPTURE_DEFAULT_DEVICE_SPECIFIER);
            var name = ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
            return string.IsNullOrEmpty(name) ? null : name;
        }
    }

    /// <summary>Byte length of the null-terminated string at ptr.</summary>
    private static int Utf8Length(IntPtr ptr)
    {
        int length = 0;
        while (Marshal.ReadByte(ptr, length) != 0)
            length++;
        return length;
    }
}
