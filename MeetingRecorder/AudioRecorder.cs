using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingRecorder;

/// <summary>
/// マイク入力(WasapiCapture)とスピーカー出力(WasapiLoopbackCapture)を同時にキャプチャし、
/// ミックス済み1本 + 話者別(マイク/スピーカー)2本のWAVファイルとして書き出す。
/// 仮想オーディオデバイスは使わない。
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private const int PumpIntervalMs = 100;

    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _loopbackBuffer;
    private RecordingPump? _pump;
    private System.Threading.Timer? _pumpTimer;

    public bool IsRecording { get; private set; }
    public string? OutputPath { get; private set; }

    /// <summary>マイク単独の録音ファイル(話者分離用)。ミックス済みファイルと同じ長さ・同じ時刻軸。</summary>
    public string? MicOutputPath { get; private set; }

    /// <summary>スピーカー出力単独の録音ファイル(話者分離用)。ミックス済みファイルと同じ長さ・同じ時刻軸。</summary>
    public string? SpeakerOutputPath { get; private set; }

    public TimeSpan Elapsed => _pump?.Elapsed ?? TimeSpan.Zero;

    public event EventHandler<Exception>? ErrorOccurred;

    public void Start(string outputPath, MMDevice? micDevice, MMDevice? speakerDevice)
    {
        if (IsRecording)
            throw new InvalidOperationException("既に録音中です。");

        OutputPath = outputPath;
        MicOutputPath = InsertSuffix(outputPath, "_mic");
        SpeakerOutputPath = InsertSuffix(outputPath, "_speaker");

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

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _pump = new RecordingPump(_micBuffer, _loopbackBuffer, outputPath, MicOutputPath, SpeakerOutputPath);

        _micCapture.DataAvailable += OnMicDataAvailable;
        _loopbackCapture.DataAvailable += OnLoopbackDataAvailable;
        _micCapture.RecordingStopped += OnCaptureStopped;
        _loopbackCapture.RecordingStopped += OnCaptureStopped;

        _micCapture.StartRecording();
        _loopbackCapture.StartRecording();

        IsRecording = true;
        _pumpTimer = new System.Threading.Timer(PumpAudio, null, PumpIntervalMs, PumpIntervalMs);
    }

    // ここではバッファに積むだけにする。ファイルへの書き出しは必ずRecordingPump経由(実時間に揃えた
    // 長さで書く)にしないと、音が鳴っていない間データが来ないループバックの時刻軸が詰まってしまう。
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

        try
        {
            _pump?.PumpOnce();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private static WaveFormat SimplifyFormat(WaveFormat format) =>
        WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);

    private static string InsertSuffix(string path, string suffix)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, $"{name}{suffix}{ext}");
    }

    public void Stop()
    {
        if (!IsRecording)
            return;

        IsRecording = false;

        _pumpTimer?.Dispose();
        _pumpTimer = null;

        _micCapture?.StopRecording();
        _loopbackCapture?.StopRecording();

        // キャプチャ停止直後に届く最後のDataAvailableがバッファに乗るまで少し待ってから、
        // 現在時刻までの残りを書き切る。
        Thread.Sleep(150);
        _pump?.PumpOnce();

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

        _pump?.Dispose();
        _pump = null;

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
