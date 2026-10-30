using WhisperKeyboard.Core.Transcription;

namespace WhisperKeyboard.Core.Tests;

internal static class TestAudio
{
    /// <summary>16-bit PCM sine tone.</summary>
    public static byte[] Tone(double seconds, int sampleRate = 16000, int channels = 1, double hz = 440)
    {
        int frames = (int)(seconds * sampleRate);
        var pcm = new byte[frames * channels * 2];
        for (int i = 0; i < frames; i++)
        {
            var s = (short)(Math.Sin(2 * Math.PI * hz * i / sampleRate) * 8000);
            for (int c = 0; c < channels; c++)
            {
                int o = (i * channels + c) * 2;
                pcm[o] = (byte)(s & 0xFF);
                pcm[o + 1] = (byte)((s >> 8) & 0xFF);
            }
        }
        return pcm;
    }

    public static Recording Clip(double seconds = 1.0) => new(Tone(seconds), 16000, 1);
}
