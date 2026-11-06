namespace WhisperKeyboard.Core.TextInsertion;

/// <summary>
/// Which focused accessibility elements are text boxes, whose value is the text
/// typed into them. Anything else can have keyboard focus with an empty value
/// that never shows typed text: a web page's own area (AXWebArea) when no field
/// in it has focus, a button, a group. An app may still take a paste there (a
/// page that handles paste itself, as ETbot's canvas UI does), so an unchanged
/// value proves nothing. Reading it as a field turned every such insert into
/// "NotLanded" and pasted the text again.
/// </summary>
public static class TextEntryRole
{
    /// <summary>macOS AXRole values of editable text. Search and secure fields are
    /// AXTextField subroles; editable web content (contenteditable) is AXTextArea.</summary>
    private static readonly HashSet<string> MacRoles = new(StringComparer.Ordinal)
    {
        "AXTextField",
        "AXTextArea",
        "AXComboBox",
    };

    public static bool IsMacTextEntry(string? role) => role != null && MacRoles.Contains(role);
}
