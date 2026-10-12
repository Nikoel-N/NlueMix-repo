using Concentus;
using Concentus.Enums;
using NlueMix.Core;

namespace NlueMix.Network;

/// <summary>
/// Opus 编解码封装。只在"手机端选了 Opus"这条路用得上；
/// PCM 直通是局域网默认，音质最好（音质损失只剩蓝牙那一次编码）。
/// </summary>
public static class OpusCodec
{
    public const int Bitrate = 256_000;

    public static IOpusEncoder CreateEncoder(int sampleRate, int channels)
    {
        var encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = Bitrate;
        encoder.Complexity = 10;
        encoder.UseVBR = true;
        return encoder;
    }

    public static IOpusDecoder CreateDecoder(int sampleRate, int channels)
        => OpusCodecFactory.CreateDecoder(sampleRate, channels);

    /// <summary>把 16-bit PCM 编成 Opus。</summary>
    public static byte[] Encode(IOpusEncoder encoder, ReadOnlySpan<short> pcm, int frameSamples, Span<byte> output)
    {
        int written = encoder.Encode(pcm, frameSamples, output, output.Length);
        return output.Slice(0, written).ToArray();
    }

    /// <summary>解码成 float32 交错样本。</summary>
    public static int Decode(IOpusDecoder decoder, ReadOnlySpan<byte> payload, int frameSamples, int channels, Span<float> output)
    {
        var shorts = new short[frameSamples * channels];
        int decoded = decoder.Decode(payload, shorts, frameSamples, false);
        if (decoded <= 0) return 0;
        int count = decoded * channels;
        for (int i = 0; i < count; i++) output[i] = shorts[i] / 32768f;
        return count;
    }

    /// <summary>解码丢失的包（丢包隐藏），让丢包听起来是"糊了一下"而不是"咔"。</summary>
    public static int DecodeLost(IOpusDecoder decoder, int frameSamples, int channels, Span<float> output)
    {
        try
        {
            var shorts = new short[frameSamples * channels];
            int decoded = decoder.Decode(ReadOnlySpan<byte>.Empty, shorts, frameSamples, true);
            if (decoded <= 0) return 0;
            int count = decoded * channels;
            for (int i = 0; i < count; i++) output[i] = shorts[i] / 32768f;
            return count;
        }
        catch (Exception ex)
        {
            Log.Warn("丢包隐藏失败: " + ex.Message);
            return 0;
        }
    }
}
