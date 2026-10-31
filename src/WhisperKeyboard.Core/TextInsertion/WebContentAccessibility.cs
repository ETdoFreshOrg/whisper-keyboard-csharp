namespace WhisperKeyboard.Core.TextInsertion;

/// <summary>How an app was asked to expose its web content to accessibility clients.</summary>
public enum WebContentAccess
{
    /// <summary>Not a Chromium-based app, so it wasn't asked.</summary>
    NotNeeded,
    /// <summary>AXManualAccessibility was accepted (Electron apps).</summary>
    Manual,
    /// <summary>AXManualAccessibility was rejected, so AXEnhancedUserInterface was set
    /// instead (Chrome, Edge, Brave, Arc and other Chromium browsers).</summary>
    Enhanced
}

/// <summary>
/// Chromium-based apps only expose web content (text boxes in pages) to accessibility
/// clients that ask for it, and then take about two seconds to build it. Electron
/// apps accept AXManualAccessibility. Chrome and the other Chromium browsers reject it
/// with kAXErrorAttributeUnsupported and only respond to AXEnhancedUserInterface,
/// which can make window-manager window moves animate, so that's the fallback. Chrome
/// acts on AXEnhancedUserInterface even though setting it returns an error, so that
/// result isn't checked. Each app process is asked once.
/// </summary>
public class WebContentAccessibility
{
    public const string ManualAttribute = "AXManualAccessibility";
    public const string EnhancedAttribute = "AXEnhancedUserInterface";

    /// <summary>Chromium browsers. Beta/Dev/Canary builds and installed web apps add a
    /// suffix (com.google.Chrome.canary, com.google.Chrome.app.&lt;id&gt;), so a match is
    /// the id itself or the id plus ".".</summary>
    private static readonly string[] ChromiumBrowserIds =
    {
        "com.google.Chrome",
        "org.chromium.Chromium",
        "com.microsoft.edgemac",
        "com.brave.Browser",
        "company.thebrowser.Browser", // Arc
        "com.vivaldi.Vivaldi",
        "com.operasoftware.Opera",
    };

    private readonly Action<string> _log;
    private readonly Dictionary<(int Pid, string? BundleId), WebContentAccess> _asked = new();

    public WebContentAccessibility(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    /// <summary>True for Chromium browsers and for any app that bundles Electron.</summary>
    public static bool IsChromiumBased(string? bundleId, bool bundlesElectron)
    {
        if (bundlesElectron) return true;
        if (string.IsNullOrEmpty(bundleId)) return false;
        foreach (var id in ChromiumBrowserIds)
        {
            if (bundleId.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                bundleId.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Asks the app to expose its web content, the first time it's seen. <paramref name="trySet"/>
    /// sets a boolean accessibility attribute to true on the app and returns whether
    /// the app accepted it.
    /// </summary>
    public WebContentAccess Ensure(int pid, string? bundleId, bool bundlesElectron, Func<string, bool> trySet)
    {
        lock (_asked)
        {
            if (_asked.TryGetValue((pid, bundleId), out var cached)) return cached;

            var access = WebContentAccess.NotNeeded;
            if (IsChromiumBased(bundleId, bundlesElectron))
            {
                if (trySet(ManualAttribute))
                {
                    access = WebContentAccess.Manual;
                }
                else
                {
                    trySet(EnhancedAttribute);
                    access = WebContentAccess.Enhanced;
                }
                _log($"Insert check: asked {bundleId ?? $"pid {pid}"} to expose web content " +
                     (access == WebContentAccess.Manual
                         ? $"({ManualAttribute})"
                         : $"({EnhancedAttribute}; {ManualAttribute} not supported)"));
            }
            _asked[(pid, bundleId)] = access;
            return access;
        }
    }
}
