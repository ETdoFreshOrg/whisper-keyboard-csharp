using System.Text;

namespace WhisperKeyboard.Core.TextInsertion;

/// <summary>
/// What the focused field held at one moment. <see cref="Owner"/> identifies the
/// field's window/app so a read after focus moved elsewhere isn't compared against
/// the original field. <see cref="Text"/> is null when the field can't be read
/// (password box, canvas editor, accessibility off).
/// </summary>
public record FieldSnapshot(string Owner, string? Text);

public enum LandingVerdict
{
    /// <summary>The inserted text shows up in the field.</summary>
    Landed,
    /// <summary>The field is readable and still exactly what it was before: nothing arrived.</summary>
    NotLanded,
    /// <summary>Can't tell (unreadable, focus moved, or the field changed some other way).
    /// Never retried, so a doubtful check can't paste the text twice.</summary>
    Unknown
}

/// <summary>
/// Decides whether dictated text reached the focused field by comparing what the
/// field held before and after the paste/typing.
/// </summary>
public static class LandingCheck
{
    public static LandingVerdict Evaluate(FieldSnapshot? before, FieldSnapshot? after, string inserted)
    {
        if (after?.Text == null) return LandingVerdict.Unknown;
        if (before != null && before.Owner != after.Owner) return LandingVerdict.Unknown;

        var needle = Normalize(inserted);
        if (needle.Length == 0) return LandingVerdict.Unknown;

        var afterText = Normalize(after.Text);
        var afterCount = CountOccurrences(afterText, needle);

        if (before?.Text == null)
            return afterCount > 0 ? LandingVerdict.Landed : LandingVerdict.Unknown;

        var beforeText = Normalize(before.Text);
        if (afterCount > CountOccurrences(beforeText, needle)) return LandingVerdict.Landed;
        if (afterText == beforeText) return LandingVerdict.NotLanded;
        return LandingVerdict.Unknown;
    }

    /// <summary>
    /// Folds the differences browsers introduce when they insert text: non-breaking and
    /// zero-width spaces, CRLF vs LF, collapsed whitespace, and edge spaces trimmed.
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (var c in text)
        {
            if (c is '​' or '‌' or '‍' or '﻿') continue;
            if (char.IsWhiteSpace(c) || c == ' ')
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
