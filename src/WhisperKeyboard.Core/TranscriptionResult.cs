namespace WhisperKeyboard.Core;

public class TranscriptionResult
{
    public string Text { get; set; } = "";
    public string Language { get; set; } = "";
    /// <summary>Name of the speech-to-text backend that produced the text.</summary>
    public string Backend { get; set; } = "";
    public double Confidence { get; set; } = 1.0;
    public double AvgLogProb { get; set; }
    public double NoSpeechProb { get; set; }
    public double CompressionRatio { get; set; } = 1.0;

    public int WordCount => string.IsNullOrWhiteSpace(Text)
        ? 0
        : Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
