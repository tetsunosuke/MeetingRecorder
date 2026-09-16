using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;

namespace MeetingRecorder;

/// <summary>
/// Whisper(small モデル)を使い、録音WAVからローカルで文字起こしを行う。
/// クラウドAPIは使わず完全オフライン・無料で動く。
/// </summary>
public static class Transcriber
{
    private static string ModelDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingRecorder", "models");

    private static string ModelPath => Path.Combine(ModelDirectory, "ggml-small.bin");

    public static bool IsModelDownloaded => File.Exists(ModelPath);

    // モデル(約500MB)の読み込みはそれ自体に数秒〜十数秒かかるため、キューが動いている間は
    // 使い回して、ジョブのたびに読み直さないようにする。
    private static WhisperFactory? _cachedFactory;
    private static readonly SemaphoreSlim FactoryLock = new(1, 1);

    public static async Task EnsureModelDownloadedAsync()
    {
        if (IsModelDownloaded)
            return;

        Directory.CreateDirectory(ModelDirectory);

        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Small);
        var tempPath = ModelPath + ".tmp";
        using (var fileWriter = File.Create(tempPath))
            await modelStream.CopyToAsync(fileWriter);

        File.Move(tempPath, ModelPath, overwrite: true);
    }

    private static async Task<WhisperFactory> GetOrLoadFactoryAsync()
    {
        if (_cachedFactory != null)
            return _cachedFactory;

        await FactoryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _cachedFactory ??= WhisperFactory.FromPath(ModelPath);
            return _cachedFactory;
        }
        finally
        {
            FactoryLock.Release();
        }
    }

    /// <summary>アプリ終了時にモデルを解放する。</summary>
    public static void ReleaseModel()
    {
        _cachedFactory?.Dispose();
        _cachedFactory = null;
    }

    /// <param name="onProgress">0.0〜1.0の進捗を報告するコールバック(文字起こしフェーズ中のみ呼ばれる)。</param>
    /// <param name="onPhase">現在のフェーズを表す短い文言("モデルを準備中..."など)を報告するコールバック。</param>
    /// <param name="onDurationKnown">音声の長さが判明した時点(処理の最初)で一度だけ呼ばれる。
    /// これを使えば、実際の文字起こしが始まる前から目安の所要時間を計算できる。</param>
    /// 呼び出し元のスレッドからそのまま呼ばれるため、UIスレッドへのマーシャリングは呼び出し側の責任。
    public static async Task TranscribeToTextFileAsync(
        string wavPath, string txtPath,
        Action<double>? onProgress = null,
        Action<string>? onPhase = null,
        Action<TimeSpan>? onDurationKnown = null)
    {
        TimeSpan totalDuration;
        using var wavStream = new MemoryStream();
        using (var reader = new WaveFileReader(wavPath))
        {
            totalDuration = reader.TotalTime;
            onDurationKnown?.Invoke(totalDuration);

            onPhase?.Invoke("音声を変換中...");

            // Whisperは16kHzモノラルのWAVしか受け付けないため変換する
            var sampleProvider = reader.ToSampleProvider();
            if (reader.WaveFormat.Channels == 2)
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider);

            var resampler = new WdlResamplingSampleProvider(sampleProvider, 16000);
            WaveFileWriter.WriteWavFileToStream(wavStream, resampler.ToWaveProvider16());
        }

        wavStream.Seek(0, SeekOrigin.Begin);

        onPhase?.Invoke("モデルを準備中...");
        await EnsureModelDownloadedAsync().ConfigureAwait(false);
        var whisperFactory = await GetOrLoadFactoryAsync().ConfigureAwait(false);

        using var processor = whisperFactory.CreateBuilder()
            .WithLanguage("auto")
            .Build();

        onPhase?.Invoke("文字起こし中...");
        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(wavStream).ConfigureAwait(false))
        {
            sb.AppendLine($"[{segment.Start}-{segment.End}] {segment.Text.Trim()}");

            if (totalDuration > TimeSpan.Zero)
            {
                var fraction = Math.Clamp(segment.End.TotalSeconds / totalDuration.TotalSeconds, 0.0, 1.0);
                onProgress?.Invoke(fraction);
            }
        }

        await File.WriteAllTextAsync(txtPath, sb.ToString(), Encoding.UTF8).ConfigureAwait(false);
        onProgress?.Invoke(1.0);
    }
}
