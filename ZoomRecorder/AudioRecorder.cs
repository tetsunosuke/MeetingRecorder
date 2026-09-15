using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ZoomRecorder;

/// <summary>
/// マイク入力(WasapiCapture)とスピーカー出力(WasapiLoopbackCapture)を同時にキャプチャし、
/// 1本のWAVファイルにミックスして書き出す。仮想オーディオデバイスは使わない。
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private readonly WaveFormat _targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    private const int PumpIntervalMs = 100;

    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _loopbackBuffer;
    private MixingSampleProvider? _mixer;
    private WaveFileWriter? _writer;
    private System.Threading.Timer? _pumpTimer;
    private float[] _pumpBuffer = Array.Empty<float>();
    private readonly object _writeLock = new();

    public bool IsRecording { get; private set; }
    public string? OutputPath { get; private set; }
    public TimeSpan Elapsed => _writer?.TotalTime ?? TimeSpan.Zero;

    public event EventHandler<Exception>? ErrorOccurred;

    public void Start(string outputPath, MMDevice? micDevice, MMDevice? speakerDevice)
    {
        if (IsRecording)
            throw new InvalidOperationException("既に録音中です。");

        OutputPath = outputPath;

        using var enumerator = new MMDeviceEnumerator();
        var mic = micDevice ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        var speaker = speakerDevice ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        _micCapture = new WasapiCapture(mic);
        _loopbackCapture = new WasapiLoopbackCapture(speaker);

        // デバイスの既定フォーマットがWAVEFORMATEXTENSIBLEの場合、NAudioの一部環境で
        // AudioClient.Initializeが0x8007007A(ERROR_INSUFFICIENT_BUFFER)で失敗することがあるため、
        // 単純なIEEE Float形式(同じサンプルレート/チャンネル数)に明示的に差し替える。
        _micCapture.WaveFormat = SimplifyFormat(_micCapture.WaveFormat);
        _loopbackCapture.WaveFormat = SimplifyFormat(_loopbackCapture.WaveFormat);

        _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(5)
        };
        _loopbackBuffer = new BufferedWaveProvider(_loopbackCapture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(5)
        };

        _micCapture.DataAvailable += OnMicDataAvailable;
        _loopbackCapture.DataAvailable += OnLoopbackDataAvailable;
        _micCapture.RecordingStopped += OnCaptureStopped;
        _loopbackCapture.RecordingStopped += OnCaptureStopped;

        var micSample = ToTargetFormat(_micBuffer.ToSampleProvider());
        var loopbackSample = ToTargetFormat(_loopbackBuffer.ToSampleProvider());

        _mixer = new MixingSampleProvider(_targetFormat) { ReadFully = true };
        _mixer.AddMixerInput(micSample);
        _mixer.AddMixerInput(loopbackSample);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _writer = new WaveFileWriter(outputPath, _targetFormat);
        _pumpBuffer = new float[_targetFormat.SampleRate / (1000 / PumpIntervalMs) * _targetFormat.Channels];

        _micCapture.StartRecording();
        _loopbackCapture.StartRecording();

        IsRecording = true;
        _pumpTimer = new System.Threading.Timer(PumpAudio, null, PumpIntervalMs, PumpIntervalMs);
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e) =>
        _micBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);

    private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e) =>
        _loopbackBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            ErrorOccurred?.Invoke(this, e.Exception);
    }

    private void PumpAudio(object? state)
    {
        if (!IsRecording)
            return;

        lock (_writeLock)
        {
            if (_writer == null || _mixer == null)
                return;

            try
            {
                int read = _mixer.Read(_pumpBuffer, 0, _pumpBuffer.Length);
                if (read > 0)
                    _writer.WriteSamples(_pumpBuffer, 0, read);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, ex);
            }
        }
    }

    private static WaveFormat SimplifyFormat(WaveFormat format) =>
        WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);

    private ISampleProvider ToTargetFormat(ISampleProvider source)
    {
        ISampleProvider result = source;

        if (result.WaveFormat.SampleRate != _targetFormat.SampleRate)
            result = new WdlResamplingSampleProvider(result, _targetFormat.SampleRate);

        if (result.WaveFormat.Channels == 1 && _targetFormat.Channels == 2)
            result = new MonoToStereoSampleProvider(result);
        else if (result.WaveFormat.Channels == 2 && _targetFormat.Channels == 1)
            result = new StereoToMonoSampleProvider(result);

        return result;
    }

    public void Stop()
    {
        if (!IsRecording)
            return;

        IsRecording = false;

        _pumpTimer?.Dispose();
        _pumpTimer = null;

        // バッファに残っている分を最後まで書き出す
        if (_mixer != null && _writer != null)
        {
            lock (_writeLock)
            {
                int read;
                while ((read = _mixer.Read(_pumpBuffer, 0, _pumpBuffer.Length)) > 0)
                    _writer.WriteSamples(_pumpBuffer, 0, read);
            }
        }

        _micCapture?.StopRecording();
        _loopbackCapture?.StopRecording();

        if (_micCapture != null)
        {
            _micCapture.DataAvailable -= OnMicDataAvailable;
            _micCapture.RecordingStopped -= OnCaptureStopped;
        }
        if (_loopbackCapture != null)
        {
            _loopbackCapture.DataAvailable -= OnLoopbackDataAvailable;
            _loopbackCapture.RecordingStopped -= OnCaptureStopped;
        }

        _writer?.Dispose();
        _writer = null;

        _micCapture?.Dispose();
        _micCapture = null;
        _loopbackCapture?.Dispose();
        _loopbackCapture = null;
    }

    public void Dispose()
    {
        if (IsRecording)
            Stop();
    }
}
