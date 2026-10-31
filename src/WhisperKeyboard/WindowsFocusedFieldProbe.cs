using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WhisperKeyboard.Core.TextInsertion;

namespace WhisperKeyboard;

/// <summary>
/// Reads the focused text field through Windows UI Automation: the Value property
/// for inputs and textareas, the Text pattern for rich editors (contenteditable).
/// Password fields are never read. Runs on a thread-pool (MTA) thread with a timeout
/// because the first query can make a browser build its accessibility tree.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsFocusedFieldProbe : IFocusedFieldProbe
{
    private const int UIA_TextPatternId = 10014;
    private const int UIA_IsPasswordPropertyId = 30019;
    private const int UIA_ValueValuePropertyId = 30045;
    private const int MaxTextLength = 200_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1000);

    private IUIAutomation? _automation;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public async Task<FieldSnapshot?> CaptureAsync()
    {
        var read = Task.Run(Read);
        try { return await read.WaitAsync(Timeout); }
        catch (TimeoutException)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Insert check: UI Automation read timed out");
            return null;
        }
    }

    private FieldSnapshot? Read()
    {
        var owner = GetForegroundWindow().ToString();
        _automation ??= (IUIAutomation)new CUIAutomation();
        if (_automation.GetFocusedElement(out var element) != 0 || element == null)
            return null;

        try
        {
            if (element.GetCurrentPropertyValue(UIA_IsPasswordPropertyId, out var isPassword) == 0 && isPassword is true)
                return new FieldSnapshot(owner, null);

            if (element.GetCurrentPropertyValue(UIA_ValueValuePropertyId, out var value) == 0 && value is string s && s.Length > 0)
                return new FieldSnapshot(owner, s);

            if (element.GetCurrentPattern(UIA_TextPatternId, out var pattern) == 0 && pattern is IUIAutomationTextPattern textPattern)
            {
                try
                {
                    if (textPattern.get_DocumentRange(out var range) == 0 && range != null)
                    {
                        try
                        {
                            if (range.GetText(MaxTextLength, out var text) == 0)
                                return new FieldSnapshot(owner, text ?? "");
                        }
                        finally { Marshal.ReleaseComObject(range); }
                    }
                }
                finally { Marshal.ReleaseComObject(textPattern); }
            }

            // A Value that exists but is empty is still a reading (an empty field).
            return new FieldSnapshot(owner, value as string);
        }
        finally
        {
            Marshal.ReleaseComObject(element);
        }
    }

    // Minimal UI Automation COM declarations. Only the vtable slots we call carry real
    // signatures; the placeholders keep later methods at the right slot.

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void _CompareElements();
        void _CompareRuntimeIds();
        void _GetRootElement();
        void _ElementFromHandle();
        void _ElementFromPoint();
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement? element);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void _SetFocus();
        void _GetRuntimeId();
        void _FindFirst();
        void _FindAll();
        void _FindFirstBuildCache();
        void _FindAllBuildCache();
        void _BuildUpdatedCache();
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? value);
        void _GetCurrentPropertyValueEx();
        void _GetCachedPropertyValue();
        void _GetCachedPropertyValueEx();
        void _GetCurrentPatternAs();
        void _GetCachedPatternAs();
        [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object? pattern);
    }

    [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextPattern
    {
        void _RangeFromPoint();
        void _RangeFromChild();
        void _GetSelection();
        void _GetVisibleRanges();
        [PreserveSig] int get_DocumentRange(out IUIAutomationTextRange? range);
    }

    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRange
    {
        void _Clone();
        void _Compare();
        void _CompareEndpoints();
        void _ExpandToEnclosingUnit();
        void _FindAttribute();
        void _FindText();
        void _GetAttributeValue();
        void _GetBoundingRectangles();
        void _GetEnclosingElement();
        [PreserveSig] int GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string? text);
    }
}
