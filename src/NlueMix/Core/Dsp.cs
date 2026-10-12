namespace NlueMix.Core;

/// <summary>
/// 单声道降混 + 重采样辅助。全部在 float 域完成，避免来回整数转换丢精度。
/// </summary>
public static class Dsp
{
    /// <summary>线性插值重采样。ratio = 输出采样率 / 输入采样率。</summary>
    public static float[] ResampleLinear(float[] input, int channels, double ratio, ref double carry)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (Math.Abs(ratio - 1.0) < 1e-9) return input;

        int inFrames = input.Length / channels;
        // ★ 修复：ratio > 1（例如手机报 44100 Hz）时上一帧留下的 carry 会是负数，
        //   而 (int)pos 对负数是向 0 截断，carry <= -1 时就会用负下标读 input →
        //   IndexOutOfRangeException，每个包丢一次、日志刷屏。
        if (carry < 0) carry = 0;
        int outFrames = (int)Math.Floor((inFrames - carry) / ratio);
        if (outFrames <= 0) { carry += inFrames; if (carry < 0) carry = 0; return Array.Empty<float>(); }

        var output = new float[outFrames * channels];
        double pos = carry;
        for (int i = 0; i < outFrames; i++)
        {
            int idx = (int)Math.Floor(pos);
            if (idx < 0) idx = 0;
            double frac = pos - idx;
            if (idx >= inFrames - 1)
            {
                for (int ch = 0; ch < channels; ch++)
                    output[i * channels + ch] = input[(inFrames - 1) * channels + ch];
            }
            else
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    float a = input[idx * channels + ch];
                    float b = input[(idx + 1) * channels + ch];
                    output[i * channels + ch] = (float)(a + (b - a) * frac);
                }
            }
            pos += ratio;
        }
        carry = pos - inFrames;
        return output;
    }

    /// <summary>把任意声道数降混到目标声道数。</summary>
    public static float[] ToChannels(float[] input, int inChannels, int outChannels)
    {
        if (inChannels == outChannels) return input;
        int frames = input.Length / inChannels;
        var output = new float[frames * outChannels];
        for (int i = 0; i < frames; i++)
        {
            float l = 0, r = 0;
            if (inChannels == 1) { l = r = input[i]; }
            else if (inChannels == 2) { l = input[i * 2]; r = input[i * 2 + 1]; }
            else
            {
                // 多声道：左右各自平均
                int half = inChannels / 2;
                for (int ch = 0; ch < half; ch++) l += input[i * inChannels + ch];
                for (int ch = half; ch < inChannels; ch++) r += input[i * inChannels + ch];
                l /= half; r /= (inChannels - half);
            }
            if (outChannels == 1) output[i] = (l + r) * 0.5f;
            else { output[i * 2] = l; output[i * 2 + 1] = r; }
        }
        return output;
    }

    /// <summary>16-bit LE PCM -> float32。</summary>
    public static float[] Pcm16ToFloat(ReadOnlySpan<byte> pcm)
    {
        int count = pcm.Length / 2;
        var output = new float[count];
        for (int i = 0; i < count; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            output[i] = s / 32768f;
        }
        return output;
    }

    /// <summary>float32 -> 16-bit LE PCM。</summary>
    public static void FloatToPcm16(ReadOnlySpan<float> input, Span<byte> output)
    {
        for (int i = 0; i < input.Length; i++)
        {
            int v = (int)MathF.Round(Math.Clamp(input[i], -1f, 1f) * 32767f);
            output[i * 2] = (byte)(v & 0xFF);
            output[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
    }

    /// <summary>把采样率转成人类可读，例如 48000 -> 48 kHz。</summary>
    public static string DescribeRate(int rate) => (rate / 1000.0).ToString("0.#") + " kHz";
}
