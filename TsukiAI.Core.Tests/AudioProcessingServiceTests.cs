using System.Buffers.Binary;
using System.Text;
using TsukiAI.VoiceChat.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class AudioProcessingServiceTests
{
    [Fact]
    public void ConvertWavToDiscordPcm_skips_metadata_chunks_before_audio_data()
    {
        var samples = new short[] { 1000, -2000, 3000 };
        var wav = WavWithListChunk(samples);

        var pcm = new AudioProcessingService().ConvertWavToDiscordPcm(wav);

        var upsampled = new short[] { 1000, -500, -2000, 500, 3000, 3000 };
        var expected = new byte[upsampled.Length * 4];
        for (var i = 0; i < upsampled.Length; i++)
        {
            var offset = i * 4;
            BinaryPrimitives.WriteInt16LittleEndian(expected.AsSpan(offset), upsampled[i]);
            BinaryPrimitives.WriteInt16LittleEndian(expected.AsSpan(offset + 2), upsampled[i]);
        }

        Assert.Equal(expected, pcm);
    }

    private static byte[] WavWithListChunk(IReadOnlyList<short> samples)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(0);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(24000);
            writer.Write(48000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("LIST"));
            writer.Write(4);
            writer.Write(Encoding.ASCII.GetBytes("INFO"));
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(samples.Count * 2);
            foreach (var sample in samples)
                writer.Write(sample);
        }

        var wav = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        return wav;
    }
}
