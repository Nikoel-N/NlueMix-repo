using NlueMix.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NlueMix.Audio;

/// <summary>
/// 本机声音来源。用 WASAPI 环回（Loopback）抓取指定播放设备上正在播放的全部声音，
/// 重采样到引擎统一的 48 kHz 立体声，再送进混音环。
/// </summary>
public sealed class SystemSource : IDisposable
{
    private readonly AudioRing _ring;
    private readonly int _sampleRate;
    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _provider;
    private float[] _pump = new float[19200];
    private long _lastActivityTicks;

    public SystemSource(AudioRing ring, int engineSampleRate)
    {
        _ring = ring;
        _sampleRate = engineSampleRate;
    }

    public string DeviceName { get; private set; } = "-";
    public string CaptureFormat { get; private set; } = "-";
    public string EngineFormat { get; private set; } = "-";
    public bool IsRunning => _capture is not null;
    public double PeakDb { get; private set; } = -96;

    /// <summary>最近是否抓到过声音。</summary>
    public bool IsProducing => Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks) < 1500;

    public void Start(string? deviceId)
    {
        Stop();

        var device = AudioDevices.Resolve(deviceId) ?? AudioDevices.DefaultRenderDevice();
        if (device is null) throw new InvalidOperationException("找不到可用的播放设备，无法抓取本机声音。");

        var capture = new WasapiLoopbackCapture(device);
        _capture = capture;
        DeviceName = device.FriendlyName;

        var fmt = capture.WaveFormat;
        CaptureFormat = Dsp.DescribeRate(fmt.SampleRate) + " · " + fmt.Channels + " ch · " + fmt.BitsPerSample + " bit";

        var buffer = new BufferedWaveProvider(fmt) { DiscardOnBufferOverflow = true, ReadFully = true };
        _buffer = buffer;

        ISampleProvider provider = buffer.ToSampleProvider();
        if (fmt.Channels == 1) provider = new MonoToStereoSampleProvider(provider);
        else if (fmt.Channels > 2) provider = new MultiToStereoSampleProvider(provider);
        if (fmt.SampleRate != _sampleRate) provider = new WdlResamplingSampleProvider(provider, _sampleRate);
        _provider = provider;
        EngineFormat = Dsp.DescribeRate(_sampleRate) + " · 2 ch · float32";

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
        capture.StartRecording();
        Log.Info("本机声音采集已启动: " + DeviceName + " (" + CaptureFormat + ")");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            var buffer = _buffer;
            var provider = _provider;
            if (buffer is null || provider is null || e.BytesRecorded <= 0) return;

            buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

            // 该设备实际可用样本数（可能比请求的少），据此决定泵多少
            int framesPerChannel = Math.Max(1, e.BytesRecorded / (_capture!.WaveFormat.BlockAlign));
            int wantedSamples = framesPerChannel * _sampleRate / _capture.WaveFormat.SampleRate * 2;
            if (wantedSamples <= 0) return;
            if (_pump.Length < wantedSamples) _pump = new float[wantedSamples];

            int read = provider.Read(_pump, 0, wantedSamples);
            if (read <= 0) return;

            _ring.Write(_pump.AsSpan(0, read));

            double peak = 0;
            for (int i = 0; i < read; i++)
            {
                float a = MathF.Abs(_pump[i]);
                if (a > peak) peak = a;
            }
            PeakDb = peak > 0 ? 20 * Math.Log10(peak) : -96;
            if (peak > 0.0008) Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);
        }
        catch (Exception ex)
        {
            Log.Error("本机声音采集回调异常: " + ex.Message);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Log.Warn("本机声音采集已停止: " + e.Exception.Message);
    }

    public void Stop()
    {
        var capture = _capture;
        _capture = null;
        _buffer = null;
        _provider = null;
        if (capture is null) return;
        try
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.StopRecording();
            capture.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("停止本机声音采集出错: " + ex.Message);
        }
    }

    public void Dispose() => Stop();
}

/// <summary>单声道 -> 立体声。</summary>
internal sealed class MonoToStereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float[] _temp = new float[4096];

    public MonoToStereoSampleProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(_source.WaveFormat.SampleRate, 2);

    public int Read(float[] buffer, int offset, int count)
    {
        int framesWanted = count / 2;
        if (_temp.Length < framesWanted) _temp = new float[framesWanted];
        int framesRead = _source.Read(_temp, 0, framesWanted);
        for (int i = 0; i < framesRead; i++)
        {
            buffer[offset + i * 2] = _temp[i];
            buffer[offset + i * 2 + 1] = _temp[i];
        }
        return framesRead * 2;
    }
}

/// <summary>多声道 -> 立体声。</summary>
internal sealed class MultiToStereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _inChannels;
    private float[] _temp = new float[8192];

    public MultiToStereoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _inChannels = source.WaveFormat.Channels;
    }

    public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(_source.WaveFormat.SampleRate, 2);

    public int Read(float[] buffer, int offset, int count)
    {
        int framesWanted = count / 2;
        int needed = framesWanted * _inChannels;
        if (_temp.Length < needed) _temp = new float[needed];
        int read = _source.Read(_temp, 0, needed);
        int framesRead = read / _inChannels;
        for (int i = 0; i < framesRead; i++)
        {
            float l = 0, r = 0;
            int half = _inChannels / 2;
            for (int ch = 0; ch < half; ch++) l += _temp[i * _inChannels + ch];
            for (int ch = half; ch < _inChannels; ch++) r += _temp[i * _inChannels + ch];
            buffer[offset + i * 2] = l / half;
            buffer[offset + i * 2 + 1] = r / (_inChannels - half);
        }
        return framesRead * 2;
    }
}
