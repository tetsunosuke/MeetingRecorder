using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingRecorder;

/// <summary>
/// 録音とは別に、選択中のデバイスに実際に音声が流れているかを
/// リアルタイムに監視するための軽量キャプチャ。録音開始前のプレビュー用。
/// </summary>
public sealed class AudioLevelMonitor : IDisposable
{
    private WasapiCapture? _capture;
    private float _level;

    /// <summary>直近のピークレベル(0.0〜1.0)。UIスレッドからポーリングして参照する。</summary>
    public float Level => Volatile.Read(ref _level);

    public void Start(MMDevice device, bool loopback)
    {
        Stop();

        var capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(capture.WaveFormat.SampleRate, capture.WaveFormat.Channels);
        capture.DataAvailable += OnDataAvailable;

        try
        {
            capture.StartRecording();
            _capture = capture;
        }
        catch
        {
            capture.Dispose();
            Volatile.Write(ref _level, 0f);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        float peak = 0f;
        for (int i = 0; i + 4 <= e.BytesRecorded; i += 4)
        {
            var sample = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
            if (sample > peak)
                peak = sample;
        }

        Volatile.Write(ref _level, peak);
    }

    public void Stop()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            try { _capture.StopRecording(); } catch { /* ignore */ }
            _capture.Dispose();
            _capture = null;
        }

        Volatile.Write(ref _level, 0f);
    }

    public void Dispose() => Stop();
}
