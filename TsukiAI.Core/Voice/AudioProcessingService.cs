using System.Buffers.Binary;
using System.Text;

namespace TsukiAI.VoiceChat.Services;

public sealed class AudioProcessingService
{
    public byte[] ConvertDiscordToWhisperFormat(byte[] pcm48kStereo)
    {
        if (pcm48kStereo is null || pcm48kStereo.Length == 0)
            return Array.Empty<byte>();

        var inputSampleCount = pcm48kStereo.Length / 4;
        var outputSampleCount = inputSampleCount / 3;
        var output = new byte[outputSampleCount * 2];

        for (var i = 0; i < outputSampleCount; i++)
        {
            var inputIndex = i * 3 * 4;
            if (inputIndex + 11 >= pcm48kStereo.Length)
                break;

            var sum = 0;
            for (var j = 0; j < 3; j++)
            {
                var frameIndex = inputIndex + (j * 4);
                short left = (short)(pcm48kStereo[frameIndex] | (pcm48kStereo[frameIndex + 1] << 8));
                short right = (short)(pcm48kStereo[frameIndex + 2] | (pcm48kStereo[frameIndex + 3] << 8));
                sum += (left + right) / 2;
            }

            short monoSample = (short)(sum / 3);
            var outputIndex = i * 2;
            output[outputIndex] = (byte)(monoSample & 0xFF);
            output[outputIndex + 1] = (byte)((monoSample >> 8) & 0xFF);
        }

        return output;
    }

    public byte[] ConvertWavToDiscordPcm(byte[] wavData)
    {
        if (!TryGetPcm16MonoData(wavData, out var dataOffset, out var dataLength))
            return Array.Empty<byte>();

        var pcm24kMono = new byte[dataLength];
        Array.Copy(wavData, dataOffset, pcm24kMono, 0, dataLength);

        var inputSampleCount = pcm24kMono.Length / 2;
        var outputSampleCount = inputSampleCount * 2;
        var output = new byte[outputSampleCount * 4];

        for (var i = 0; i < outputSampleCount; i++)
        {
            var inputIndex = i / 2;
            var isEven = (i % 2) == 0;
            short sample;

            if (isEven || inputIndex >= inputSampleCount - 1)
            {
                var byteIndex = inputIndex * 2;
                if (byteIndex + 1 < pcm24kMono.Length)
                    sample = (short)(pcm24kMono[byteIndex] | (pcm24kMono[byteIndex + 1] << 8));
                else
                    sample = 0;
            }
            else
            {
                var byteIndex1 = inputIndex * 2;
                var byteIndex2 = (inputIndex + 1) * 2;
                if (byteIndex2 + 1 < pcm24kMono.Length)
                {
                    short sample1 = (short)(pcm24kMono[byteIndex1] | (pcm24kMono[byteIndex1 + 1] << 8));
                    short sample2 = (short)(pcm24kMono[byteIndex2] | (pcm24kMono[byteIndex2 + 1] << 8));
                    sample = (short)((sample1 + sample2) / 2);
                }
                else
                {
                    sample = 0;
                }
            }

            var outputIndex = i * 4;
            output[outputIndex] = (byte)(sample & 0xFF);
            output[outputIndex + 1] = (byte)((sample >> 8) & 0xFF);
            output[outputIndex + 2] = (byte)(sample & 0xFF);
            output[outputIndex + 3] = (byte)((sample >> 8) & 0xFF);
        }

        return output;
    }

    private static bool TryGetPcm16MonoData(byte[]? wavData, out int dataOffset, out int dataLength)
    {
        dataOffset = 0;
        dataLength = 0;

        if (wavData is null || wavData.Length < 12 ||
            !wavData.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !wavData.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return false;

        ushort audioFormat = 0;
        ushort channels = 0;
        ushort bitsPerSample = 0;
        uint sampleRate = 0;
        var foundFormat = false;
        var foundData = false;

        for (var position = 12; position + 8 <= wavData.Length;)
        {
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wavData.AsSpan(position + 4, 4));
            var payloadStart = position + 8;
            var payloadEnd = (long)payloadStart + chunkSize;
            if (payloadEnd > wavData.Length || payloadEnd > int.MaxValue)
                return false;

            var chunkId = Encoding.ASCII.GetString(wavData, position, 4);
            if (chunkId == "fmt " && chunkSize >= 16)
            {
                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(wavData.AsSpan(payloadStart, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wavData.AsSpan(payloadStart + 2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(wavData.AsSpan(payloadStart + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wavData.AsSpan(payloadStart + 14, 2));
                foundFormat = true;
            }
            else if (chunkId == "data")
            {
                dataOffset = payloadStart;
                dataLength = (int)chunkSize;
                foundData = true;
            }

            var nextPosition = payloadEnd + (chunkSize & 1);
            if (nextPosition > wavData.Length || nextPosition <= position)
                return false;
            position = (int)nextPosition;
        }

        // Qwen service returns 24 kHz, mono, signed PCM16. The
        // chunk scan above deliberately accepts LIST/JUNK/INFO chunks between
        // fmt and data instead of assuming the 44-byte WAV layout.
        return foundFormat && foundData &&
            audioFormat == 1 && channels == 1 && bitsPerSample == 16 &&
            sampleRate == 24000 && dataLength >= 2 && (dataLength & 1) == 0;
    }

}
