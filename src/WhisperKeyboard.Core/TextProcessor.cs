using System.Text.RegularExpressions;

namespace WhisperKeyboard.Core;

/// <summary>
/// Portable text processing logic extracted from TextTyper.
/// Handles punctuation, capitalization, exit words, etc.
/// </summary>
public class TextProcessor
{
    private readonly Config _config;

    public TextProcessor(Config config)
    {
        _config = config;
    }

    /// <summary>
    /// Process transcribed text according to configuration.
    /// </summary>
    /// <param name="text">Raw transcribed text</param>
    /// <param name="detectExitWords">False to leave a trailing exit word in the text
    /// (push-to-talk, where the key decides whether Enter is pressed)</param>
    /// <returns>Tuple of (processed text, whether Enter should be pressed)</returns>
    public (string text, bool shouldEnter) ProcessText(string text, bool detectExitWords = true)
    {
        // Clean whitespace
        text = text.Trim();
        text = Regex.Replace(text, @"\s+", " ");

        // Check for exit word at end
        bool shouldEnter = false;
        if (detectExitWords && _config.ExitWordsEnabled && _config.ExitWords.Count > 0)
        {
            // Get the last word (handle potential trailing punctuation)
            var words = text.Split(' ');
            if (words.Length > 0)
            {
                var lastWord = words[^1];
                // Strip trailing punctuation for matching
                var lastWordClean = lastWord.TrimEnd('.', '!', '?', ',', ';', ':');

                foreach (var exitWord in _config.ExitWords)
                {
                    if (lastWordClean.Equals(exitWord, StringComparison.OrdinalIgnoreCase))
                    {
                        // Remove the last word (including any punctuation attached to it)
                        if (words.Length == 1)
                        {
                            text = "";
                        }
                        else
                        {
                            text = string.Join(" ", words[..^1]).Trim();
                        }
                        shouldEnter = true;
                        break;
                    }
                }
            }
        }

        // Add punctuation if enabled and text doesn't end with punctuation
        if (_config.AddPunctuation && !string.IsNullOrEmpty(text))
        {
            char lastChar = text[^1];
            if (!".!?,:;".Contains(lastChar))
            {
                text += ".";
            }
        }

        // Capitalize first letter if enabled
        if (_config.CapitalizeSentences && !string.IsNullOrEmpty(text))
        {
            text = char.ToUpper(text[0]) + text[1..];

            // Also capitalize after sentence-ending punctuation
            text = Regex.Replace(text, @"([.!?]\s+)([a-z])", m =>
                m.Groups[1].Value + char.ToUpper(m.Groups[2].Value[0]));
        }

        // Add space after punctuation if missing
        text = Regex.Replace(text, @"([.!?,;:])([A-Za-z])", "$1 $2");

        return (text, shouldEnter);
    }

    /// <summary>
    /// Check if the transcription consists ONLY of an exit word.
    /// </summary>
    /// <param name="text">Raw transcribed text</param>
    /// <returns>True if the text is just an exit word</returns>
    public bool IsOnlyExitWord(string text)
    {
        if (!_config.ExitWordsEnabled || _config.ExitWords.Count == 0)
            return false;

        text = text.Trim();
        // Strip common trailing punctuation that Whisper might add
        text = text.TrimEnd('.', '!', '?', ',', ';', ':');

        foreach (var exitWord in _config.ExitWords)
        {
            if (text.Equals(exitWord, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Check if the transcription starts with a wake word.
    /// Wake words must appear at the START of the transcription.
    /// </summary>
    /// <param name="text">Raw transcribed text</param>
    /// <returns>Tuple of (isWakeWord, remainingText after wake word stripped)</returns>
    public (bool isWakeWord, string remainingText) CheckWakeWord(string text)
    {
        if (!_config.WakeWordsEnabled || _config.WakeWords.Count == 0)
            return (false, text);

        text = text.Trim();

        foreach (var wakeWord in _config.WakeWords)
        {
            if (text.StartsWith(wakeWord, StringComparison.OrdinalIgnoreCase))
            {
                // Strip the wake word from the beginning
                var remaining = text.Substring(wakeWord.Length).TrimStart();
                // Also strip common punctuation that might follow the wake word
                remaining = remaining.TrimStart(',', '.', '!', '?', ';', ':');
                remaining = remaining.TrimStart();
                return (true, remaining);
            }
        }

        return (false, text);
    }

    /// <summary>
    /// Check if the transcription consists ONLY of a pause word.
    /// Pause words must be the ENTIRE transcription content (after trimming).
    /// </summary>
    /// <param name="text">Raw transcribed text</param>
    /// <returns>True if the text matches a pause word exactly</returns>
    public bool CheckPauseWord(string text)
    {
        if (!_config.WakeWordsEnabled || _config.PauseWords.Count == 0)
            return false;

        text = text.Trim();

        // Strip common trailing punctuation that Whisper might add
        text = text.TrimEnd('.', '!', '?', ',', ';', ':');

        foreach (var pauseWord in _config.PauseWords)
        {
            if (text.Equals(pauseWord, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
