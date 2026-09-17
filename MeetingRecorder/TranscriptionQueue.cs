using System.Diagnostics;
using System.Threading.Channels;

namespace MeetingRecorder;

/// <summary>
/// 文字起こしジョブを1件ずつ順番に処理するキュー。
/// 複数の録音が連続しても同時に複数のWhisper推論を走らせない(CPU/メモリの奪い合いを避ける)。
/// バックグラウンドスレッドで動くため、呼び出し側(UI)は必ずイベントをマーシャリングすること。
/// </summary>
public sealed class TranscriptionQueue
{
    public readonly record struct Status(string? CurrentFileName, string Phase, double Fraction, TimeSpan? Eta, int PendingCount);

    private sealed record Job(string DisplayWavPath, string TxtPath, string? SingleSourcePath, string? MicSourcePath, string? SpeakerSourcePath);

    private readonly Channel<Job> _channel = Channel.CreateUnbounded<Job>();
    private int _pendingCount;

    // 「音声の長さ1秒あたり何秒かかるか」の実測値(このセッション内での移動平均)。
    // 初期値はsmallモデルのCPU実行でよくある目安(だいたい等倍〜数倍)。
    private double _secondsPerAudioSecond = 1.2;

    public event EventHandler<Status>? StatusChanged;
    public event EventHandler<(string WavPath, Exception Error)>? JobFailed;
    public event EventHandler<string>? JobCompleted;

    public TranscriptionQueue()
    {
        _ = Task.Run(RunAsync);
    }

    /// <summary>1本のWAVをそのまま文字起こしする(話者分離なし)。</summary>
    public void Enqueue(string wavPath)
    {
        var txtPath = Path.ChangeExtension(wavPath, ".txt");
        EnqueueJob(new Job(wavPath, txtPath, wavPath, null, null));
    }

    /// <summary>マイクとスピーカーを別々に文字起こしし、話者ラベル付きでマージする。
    /// <paramref name="displayWavPath"/>はテキストファイル名や通知に使う代表パス(通常はミックス済みの録音ファイル)。</summary>
    public void EnqueueDiarized(string displayWavPath, string micWavPath, string speakerWavPath)
    {
        var txtPath = Path.ChangeExtension(displayWavPath, ".txt");
        EnqueueJob(new Job(displayWavPath, txtPath, null, micWavPath, speakerWavPath));
    }

    private void EnqueueJob(Job job)
    {
        Interlocked.Increment(ref _pendingCount);
        _channel.Writer.TryWrite(job);
    }

    private async Task RunAsync()
    {
        await foreach (var job in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var fileName = Path.GetFileName(job.DisplayWavPath);
            var sw = Stopwatch.StartNew();

            double? audioDurationSeconds = null;
            TimeSpan? estimatedTotal = null;

            Publish(fileName, "準備中...", 0, null);

            try
            {
                void OnDurationKnown(TimeSpan duration)
                {
                    audioDurationSeconds = duration.TotalSeconds;
                    estimatedTotal = TimeSpan.FromSeconds(duration.TotalSeconds * _secondsPerAudioSecond);
                    Publish(fileName, "音声を変換中...", 0, estimatedTotal);
                }

                void OnPhase(string phase) => Publish(fileName, phase, 0, EtaFromEstimate(sw, estimatedTotal));

                void OnProgress(double fraction)
                {
                    // ある程度進んだらセグメントの実測ペースの方が正確なのでそちらを優先する
                    var eta = fraction > 0.1
                        ? TimeSpan.FromSeconds(Math.Max(0, (sw.Elapsed.TotalSeconds / fraction) - sw.Elapsed.TotalSeconds))
                        : EtaFromEstimate(sw, estimatedTotal);
                    Publish(fileName, "文字起こし中...", fraction, eta);
                }

                if (job.SingleSourcePath != null)
                {
                    await Transcriber.TranscribeToTextFileAsync(
                        job.SingleSourcePath, job.TxtPath,
                        onProgress: OnProgress, onPhase: OnPhase, onDurationKnown: OnDurationKnown)
                        .ConfigureAwait(false);
                }
                else
                {
                    await Transcriber.TranscribeDiarizedToTextFileAsync(
                        job.MicSourcePath!, job.SpeakerSourcePath!, job.TxtPath,
                        onProgress: OnProgress, onPhase: OnPhase, onDurationKnown: OnDurationKnown)
                        .ConfigureAwait(false);
                }

                // 実測値で目安の比率を更新(次回以降の推定精度を上げる)
                if (audioDurationSeconds is > 0.5)
                {
                    var observed = sw.Elapsed.TotalSeconds / audioDurationSeconds.Value;
                    _secondsPerAudioSecond = (_secondsPerAudioSecond + observed) / 2.0;
                }

                JobCompleted?.Invoke(this, job.DisplayWavPath);
            }
            catch (Exception ex)
            {
                JobFailed?.Invoke(this, (job.DisplayWavPath, ex));
            }
            finally
            {
                Interlocked.Decrement(ref _pendingCount);
                Publish(null, "", 0, null);
            }
        }
    }

    private static TimeSpan? EtaFromEstimate(Stopwatch sw, TimeSpan? estimatedTotal) =>
        estimatedTotal.HasValue
            ? TimeSpan.FromSeconds(Math.Max(0, estimatedTotal.Value.TotalSeconds - sw.Elapsed.TotalSeconds))
            : null;

    private void Publish(string? fileName, string phase, double fraction, TimeSpan? eta) =>
        StatusChanged?.Invoke(this, new Status(fileName, phase, fraction, eta, Volatile.Read(ref _pendingCount)));
}
