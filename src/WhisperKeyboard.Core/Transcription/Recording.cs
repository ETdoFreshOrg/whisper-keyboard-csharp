using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WhisperKeyboard.Core.Transcription;

/// <summary>
/// One captured clip, kept until a transcription succeeds so audio is never lost.
/// Holds the raw 16-bit PCM plus lazily built encodings every backend can pick from:
/// 16 kHz mono 16-bit WAV, and Ogg/Opus (VoIP, 24 kbit/s, 20 ms frames).
/// </summary>
public sealed class Recording
{
    public const int TargetSampleRate = 16000;
    public const int OpusBitrate = 24000;

    private readonly Lazy<byte[]> _pcm16kMono;
    private readonly Lazy<byte[]> _wav;
    private readonly Lazy<byte[]?> _oggOpus;

    public byte[] Pcm { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public DateTime CapturedAt { get; } = DateTime.Now;

    public TimeSpan Duration => TimeSpan.FromSeconds(Pcm.Length / (double)(SampleRate * Channels * 2));

    public Recording(byte[] pcm, int sampleRate, int channels)
    {
        Pcm = pcm;
        SampleRate = sampleRate;
        Channels = channels;
        _pcm16kMono = new Lazy<byte[]>(() => ToMono16k(pcm, sampleRate, channels));
        _wav = new Lazy<byte[]>(() => BuildWav(_pcm16kMono.Value, TargetSampleRate, 1));
        _oggOpus = new Lazy<byte[]?>(() =>
        {
            try { return EncodeOggOpus(_pcm16kMono.Value); }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Opus encoding failed: {ex.Message}");
                return null;
            }
        });
    }

    /// <summary>16 kHz mono 16-bit PCM WAV.</summary>
    public byte[] Wav => _wav.Value;

    /// <summary>Ogg/Opus, or null if encoding failed.</summary>
    public byte[]? OggOpus => _oggOpus.Value;

    private static byte[] ToMono16k(byte[] pcm, int sampleRate, int channels)
    {
        if (sampleRate == TargetSampleRate && channels == 1)
            return pcm;

        var source = new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(sampleRate, 16, channels));
        ISampleProvider samples = source.ToSampleProvider();
        if (channels == 2)
            samples = new StereoToMonoSampleProvider(samples);
        else if (channels > 2)
            throw new NotSupportedException($"{channels}-channel audio is not supported");
        if (sampleRate != TargetSampleRate)
            samples = new WdlResamplingSampleProvider(samples, TargetSampleRate);

        using var output = new MemoryStream();
        var buffer = new float[TargetSampleRate];
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                var s = (short)Math.Clamp(buffer[i] * 32767f, short.MinValue, short.MaxValue);
                output.WriteByte((byte)(s & 0xFF));
                output.WriteByte((byte)((s >> 8) & 0xFF));
            }
        }
        return output.ToArray();
    }

    /// <summary>Plain 44-byte-header PCM WAV.</summary>
    public static byte[] BuildWav(byte[] pcmData, int sampleRate, int channels)
    {
        using var memoryStream = new MemoryStream();
        using var writer = new BinaryWriter(memoryStream);

        int bitsPerSample = 16;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        int blockAlign = channels * (bitsPerSample / 8);

        // RIFF header
        writer.Write(new char[] { 'R', 'I', 'F', 'F' });
        writer.Write(36 + pcmData.Length); // File size - 8
        writer.Write(new char[] { 'W', 'A', 'V', 'E' });

        // fmt chunk
        writer.Write(new char[] { 'f', 'm', 't', ' ' });
        writer.Write(16); // Chunk size
        writer.Write((short)1); // Audio format (PCM)
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        // data chunk
        writer.Write(new char[] { 'd', 'a', 't', 'a' });
        writer.Write(pcmData.Length);
        writer.Write(pcmData);

        return memoryStream.ToArray();
    }

    private static byte[] EncodeOggOpus(byte[] pcm16kMono)
    {
        var encoder = OpusCodecFactory.CreateEncoder(TargetSampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = OpusBitrate;
        encoder.ExpertFrameDuration = OpusFramesize.OPUS_FRAMESIZE_20_MS;

        var samples = new short[pcm16kMono.Length / 2];
        Buffer.BlockCopy(pcm16kMono, 0, samples, 0, samples.Length * 2);

        using var output = new MemoryStream();
        var ogg = new OpusOggWriteStream(encoder, output, new OpusTags(), TargetSampleRate, 5, leaveOpen: true);
        ogg.WriteSamples(samples, 0, samples.Length);
        ogg.Finish();
        return output.ToArray();
    }
}
