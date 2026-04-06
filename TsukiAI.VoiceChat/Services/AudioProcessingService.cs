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

    public byte[] ConvertVoiceVoxWavToDiscordPcm(byte[] wavData)
    {
        if (wavData is null || wavData.Length <= 44)
            return Array.Empty<byte>();

        var pcm24kMono = new byte[wavData.Length - 44];
        Array.Copy(wavData, 44, pcm24kMono, 0, pcm24kMono.Length);

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
}
