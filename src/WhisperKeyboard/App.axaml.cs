using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace WhisperKeyboard;

public partial class App : Application
{
    private WhisperKeyboardApp? _app;
    private Window? _hiddenWindow;
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Create a hidden window to keep the app running and provide clipboard access
            _hiddenWindow = new Window
            {
                Width = 1,
                Height = 1,
                ShowInTaskbar = false,
                SystemDecorations = SystemDecorations.None,
                Opacity = 0,
                Focusable = false,
                CanResize = false,
            };
            // Position off-screen and hide from Mission Control
            _hiddenWindow.Opened += (s, e) =>
            {
                _hiddenWindow.Position = new PixelPoint(-10000, -10000);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    MacOSHelper.HideWindowFromMissionControl(_hiddenWindow);
                }
            };
            desktop.MainWindow = _hiddenWindow;

            // Don't shutdown when the hidden window closes
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // On macOS, check permissions AND the audio capture backend before
            // initializing when running from a .app bundle. The OpenAL term ensures
            // the setup dialog (and its "Audio Capture" row) appears when openal-soft
            // is missing even if both OS permissions are already granted.
            bool openAlMissing = OperatingSystem.IsMacOS() && !(OpenALNative.IsAvailable || OpenALNative.TryReload());
            if (OperatingSystem.IsMacOS() && MacOSPermissions.IsRunningFromAppBundle()
                && (MacOSPermissions.AnyPermissionMissing() || openAlMissing))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Missing macOS permissions or audio backend - showing setup dialog");
                var permissionWindow = new PermissionSetupWindow();
                permissionWindow.SetupComplete += (s, e) =>
                {
                    InitializeApp(desktop);
                };
                permissionWindow.Closed += (s, e) =>
                {
                    // Handle X button close — still initialize the app
                    if (_app == null)
                        InitializeApp(desktop);
                };
                permissionWindow.Show();
            }
            else
            {
                InitializeApp(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeApp(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Initialize the main app controller
        _app = new WhisperKeyboardApp(desktop);

        // Create tray icon programmatically
        var trayMenu = new NativeMenu();
        _app.SetupTrayMenu(trayMenu);

        // Load tray icon from embedded resource
        WindowIcon? icon = null;
        try
        {
            var iconUri = new Uri("avares://WhisperKeyboard/Assets/tray-icon.png");
            using var stream = AssetLoader.Open(iconUri);
            var bitmap = new Bitmap(stream);
            icon = new WindowIcon(bitmap);
            Console.WriteLine($"Loaded tray icon: {bitmap.Size.Width}x{bitmap.Size.Height}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load tray icon: {ex.Message}");
        }

        _trayIcon = new TrayIcon
        {
            ToolTipText = "Whisper Keyboard",
            Icon = icon,
            Menu = trayMenu,
            IsVisible = true
        };

        // Add to application's tray icons
        var icons = new TrayIcons { _trayIcon };
        SetValue(TrayIcon.IconsProperty, icons);

        // Hide from Dock on macOS (must be done after everything is set up)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Use a timer to call this after the event loop starts
            DispatcherTimer.RunOnce(() => MacOSHelper.HideFromDock(), TimeSpan.FromMilliseconds(100));
        }
    }
}

/// <summary>
/// macOS native interop for hiding app from Dock
/// </summary>
internal static partial class MacOSHelper
{
    // NSApplicationActivationPolicy values
    private const long NSApplicationActivationPolicyAccessory = 1;

    [LibraryImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string className);

    [LibraryImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string selectorName);

    [LibraryImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [LibraryImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_long(IntPtr receiver, IntPtr selector, long arg);

    [LibraryImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_void_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    // NSWindowCollectionBehavior flags
    private const long NSWindowCollectionBehaviorCanJoinAllSpaces = 1 << 0; // on every desktop
    private const long NSWindowCollectionBehaviorTransient = 1 << 3;     // not managed by Mission Control
    private const long NSWindowCollectionBehaviorIgnoresCycle = 1 << 6;  // not in Cmd+Tab cycling

    private const long NSWindowStyleMaskNonactivatingPanel = 1 << 7; // clicks don't activate the app
    private const long NSFloatingWindowLevel = 5;

    /// <summary>
    /// Make an overlay float above other windows without taking keyboard focus from the
    /// app being dictated into when clicked (macOS twin of WS_EX_NOACTIVATE).
    /// </summary>
    public static void MakeNonActivatingPanel(Window window)
    {
        try
        {
            var nsWindow = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (nsWindow == IntPtr.Zero) return;

            var styleMask = (long)objc_msgSend(nsWindow, sel_registerName("styleMask"));
            objc_msgSend_long(nsWindow, sel_registerName("setStyleMask:"), styleMask | NSWindowStyleMaskNonactivatingPanel);
            objc_msgSend_long(nsWindow, sel_registerName("setLevel:"), NSFloatingWindowLevel);
            objc_msgSend_long(nsWindow, sel_registerName("setCollectionBehavior:"),
                NSWindowCollectionBehaviorCanJoinAllSpaces | NSWindowCollectionBehaviorTransient | NSWindowCollectionBehaviorIgnoresCycle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to make window non-activating: {ex.Message}");
        }
    }

    public static void HideWindowFromMissionControl(Window window)
    {
        try
        {
            var nsWindow = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (nsWindow == IntPtr.Zero)
            {
                Console.WriteLine("Could not get NSWindow handle for hidden window");
                return;
            }

            var collectionBehavior = (IntPtr)(NSWindowCollectionBehaviorTransient | NSWindowCollectionBehaviorIgnoresCycle);
            objc_msgSend_void_IntPtr(nsWindow, sel_registerName("setCollectionBehavior:"), collectionBehavior);

            Console.WriteLine("Hidden window excluded from Mission Control");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to hide window from Mission Control: {ex.Message}");
        }
    }

    public static void HideFromDock()
    {
        try
        {
            // Get [NSApplication sharedApplication]
            var nsAppClass = objc_getClass("NSApplication");
            var sharedAppSel = sel_registerName("sharedApplication");
            var nsApp = objc_msgSend(nsAppClass, sharedAppSel);

            // [nsApp setActivationPolicy:NSApplicationActivationPolicyAccessory]
            var setActivationPolicySel = sel_registerName("setActivationPolicy:");
            objc_msgSend_long(nsApp, setActivationPolicySel, NSApplicationActivationPolicyAccessory);

            Console.WriteLine("Set activation policy to Accessory (hidden from Dock)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to hide from Dock: {ex.Message}");
        }
    }
}
