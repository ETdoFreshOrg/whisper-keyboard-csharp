using System.Text;
using WhisperKeyboard.Core.Transcription;
using Xunit;

namespace WhisperKeyboard.Core.Tests;

public class RecordingTests
{
    [Fact]
    public void Wav_is_16k_mono_16bit()
    {
        var wav = TestAudio.Clip(1.0).Wav;

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));       // channels
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));   // sample rate
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));      // bits per sample
        Assert.Equal(44 + 32000, wav.Length);
    }

    [Fact]
    public void Wav_downsamples_48k_stereo_to_16k_mono()
    {
        var recording = new Recording(TestAudio.Tone(1.0, 48000, 2), 48000, 2);
        var wav = recording.Wav;

        Assert.Equal(1, BitConverter.ToInt16(wav, 22));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        // One second of 16 kHz mono 16-bit, give or take the resampler's edge samples.
        Assert.InRange(wav.Length - 44, 31000, 33000);
        Assert.Equal(1.0, recording.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void OggOpus_is_a_valid_ogg_opus_stream_and_much_smaller()
    {
        var recording = TestAudio.Clip(3.0);
        var ogg = recording.OggOpus;

        Assert.NotNull(ogg);
        Assert.Equal("OggS", Encoding.ASCII.GetString(ogg!, 0, 4));
        Assert.Contains("OpusHead", Encoding.ASCII.GetString(ogg));
        // 24 kbit/s is ~3 KB/s, versus 32 KB/s of WAV.
        Assert.True(ogg.Length < recording.Wav.Length / 4, $"ogg {ogg.Length} vs wav {recording.Wav.Length}");
    }
}
