using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;

namespace MeetingRecorder;

public enum WhisperModelSize
{
    Tiny,
    Base,
    Small,
    Medium,
}

/// <summary>
/// Whisperを使い、録音WAVからローカルで文字起こしを行う。
/// クラウドAPIは使わず完全オフライン・無料で動く。
/// </summary>
public static class Transcriber
{
    private const float NoSpeechThreshold = 0.6f;

    private static string ModelDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingRecorder", "models");

    private static string GetModelPath(WhisperModelSize size) =>
        Path.Combine(ModelDirectory, $"ggml-{size.ToString().ToLowerInvariant()}.bin");

    private static GgmlType ToGgmlType(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Tiny => GgmlType.Tiny,
        WhisperModelSize.Base => GgmlType.Base,
        WhisperModelSize.Small => GgmlType.Small,
        WhisperModelSize.Medium => GgmlType.Medium,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    /// <summary>モデルファイルのおおよそのサイズ(ダウンロード確認ダイアログの表示用)。</summary>
    public static string ApproxDownloadSize(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Tiny => "約75MB",
        WhisperModelSize.Base => "約150MB",
        WhisperModelSize.Small => "約500MB",
        WhisperModelSize.Medium => "約1.5GB",
        _ => "",
    };

    public static bool IsModelDownloaded(WhisperModelSize size) => File.Exists(GetModelPath(size));

    /// <summary>ダウンロード済みモデルファイルの実サイズ(バイト)。未ダウンロードならnull。</summary>
    public static long? GetDownloadedSizeBytes(WhisperModelSize size)
    {
        var path = GetModelPath(size);
        return File.Exists(path) ? new FileInfo(path).Length : null;
    }

    // モデルの読み込みはそれ自体に数秒〜十数秒かかるため、キューが動いている間は
    // 使い回して、ジョブのたびに読み直さないようにする。選択中のモデルが変わった場合は読み直す。
    private static WhisperFactory? _cachedFactory;
    private static string? _cachedModelPath;
    private static readonly SemaphoreSlim FactoryLock = new(1, 1);

    public static async Task EnsureModelDownloadedAsync(WhisperModelSize size, Action<long>? onBytesDownloaded = null)
    {
        var modelPath = GetModelPath(size);
        if (File.Exists(modelPath))
            return;

        Directory.CreateDirectory(ModelDirectory);

        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ToGgmlType(size));
        var tempPath = modelPath + ".tmp";
        using (var fileWriter = File.Create(tempPath))
        {
            var buffer = new byte[256 * 1024];
            long total = 0;
            int read;
            while ((read = await modelStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await fileWriter.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                total += read;
                onBytesDownloaded?.Invoke(total);
            }
        }

        File.Move(tempPath, modelPath, overwrite: true);
    }

    /// <summary>ダウンロード済みモデルファイルを削除する。使用中(キャッシュ済み)なら先に解放する。</summary>
    public static void DeleteModel(WhisperModelSize size)
    {
        var path = GetModelPath(size);
        if (_cachedModelPath == path)
        {
            _cachedFactory?.Dispose();
            _cachedFactory = null;
            _cachedModelPath = null;
        }

        if (File.Exists(path))
            File.Delete(path);
    }

    private static async Task<WhisperFactory> GetOrLoadFactoryAsync(WhisperModelSize size)
    {
        var modelPath = GetModelPath(size);
        if (_cachedFactory != null && _cachedModelPath == modelPath)
            return _cachedFactory;

        await FactoryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedFactory != null && _cachedModelPath == modelPath)
                return _cachedFactory;

            _cachedFactory?.Dispose();
            _cachedFactory = WhisperFactory.FromPath(modelPath);
            _cachedModelPath = modelPath;
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
        _cachedModelPath = null;
    }

    private readonly record struct RawSegment(TimeSpan Start, TimeSpan End, string Text, float NoSpeechProbability);

    private static string FormatText(RawSegment segment) =>
        segment.NoSpeechProbability > NoSpeechThreshold ? $"(不明瞭) {segment.Text}" : segment.Text;

    /// <summary>1本のWAVを文字起こしし、区間ごとのセグメント一覧を返す(ファイル書き出しはしない)。</summary>
    private static async Task<List<RawSegment>> TranscribeSegmentsAsync(
        string wavPath, WhisperFactory whisperFactory, Action<double>? onProgress, Action<string>? onPhase)
    {
        TimeSpan totalDuration;
        using var wavStream = new MemoryStream();
        using (var reader = new WaveFileReader(wavPath))
        {
            totalDuration = reader.TotalTime;
            onPhase?.Invoke("音声を変換中...");

            // Whisperは16kHzモノラルのWAVしか受け付けないため変換する
            var sampleProvider = reader.ToSampleProvider();
            if (reader.WaveFormat.Channels == 2)
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider);

            var resampler = new WdlResamplingSampleProvider(sampleProvider, 16000);
            WaveFileWriter.WriteWavFileToStream(wavStream, resampler.ToWaveProvider16());
        }

        wavStream.Seek(0, SeekOrigin.Begin);

        using var processor = whisperFactory.CreateBuilder()
            .WithLanguage("auto")
            .Build();

        onPhase?.Invoke("文字起こし中...");
        var segments = new List<RawSegment>();
        await foreach (var segment in processor.ProcessAsync(wavStream).ConfigureAwait(false))
        {
            segments.Add(new RawSegment(segment.Start, segment.End, segment.Text.Trim(), segment.NoSpeechProbability));

            if (totalDuration > TimeSpan.Zero)
            {
                var fraction = Math.Clamp(segment.End.TotalSeconds / totalDuration.TotalSeconds, 0.0, 1.0);
                onProgress?.Invoke(fraction);
            }
        }

        return segments;
    }

    /// <summary>
    /// 1本のWAVをそのまま文字起こしする(話者分離なし)。手動でファイルを選んで再文字起こしする場合に使う。
    /// </summary>
    public static async Task TranscribeToTextFileAsync(
        string wavPath, string txtPath, WhisperModelSize modelSize,
        Action<double>? onProgress = null,
        Action<string>? onPhase = null,
        Action<TimeSpan>? onDurationKnown = null)
    {
        TimeSpan duration;
        using (var probe = new WaveFileReader(wavPath))
            duration = probe.TotalTime;
        onDurationKnown?.Invoke(duration);

        await EnsureModelDownloadedAsync(modelSize).ConfigureAwait(false);
        var whisperFactory = await GetOrLoadFactoryAsync(modelSize).ConfigureAwait(false);

        var segments = await TranscribeSegmentsAsync(wavPath, whisperFactory, onProgress, onPhase).ConfigureAwait(false);

        var sb = new StringBuilder();
        foreach (var segment in segments)
            sb.AppendLine($"[{segment.Start}-{segment.End}] {FormatText(segment)}");

        await File.WriteAllTextAsync(txtPath, sb.ToString(), Encoding.UTF8).ConfigureAwait(false);
        onProgress?.Invoke(1.0);
    }

    /// <summary>
    /// マイクとスピーカー出力を別々に文字起こしし、時系列順にマージして話者ラベル付きの
    /// 1本のテキストにまとめる。録音停止後の自動文字起こしはこちらを使う。
    /// </summary>
    public static async Task TranscribeDiarizedToTextFileAsync(
        string micWavPath, string speakerWavPath, string txtPath, WhisperModelSize modelSize,
        Action<double>? onProgress = null,
        Action<string>? onPhase = null,
        Action<TimeSpan>? onDurationKnown = null)
    {
        TimeSpan micDuration, speakerDuration;
        using (var probe = new WaveFileReader(micWavPath)) micDuration = probe.TotalTime;
        using (var probe = new WaveFileReader(speakerWavPath)) speakerDuration = probe.TotalTime;

        onDurationKnown?.Invoke(micDuration > speakerDuration ? micDuration : speakerDuration);

        await EnsureModelDownloadedAsync(modelSize).ConfigureAwait(false);
        var whisperFactory = await GetOrLoadFactoryAsync(modelSize).ConfigureAwait(false);

        // 進捗はマイク・スピーカーそれぞれの長さに応じた重み付けで合算する
        var totalSeconds = micDuration.TotalSeconds + speakerDuration.TotalSeconds;
        var micWeight = totalSeconds > 0 ? micDuration.TotalSeconds / totalSeconds : 0.5;

        onPhase?.Invoke("自分の声を文字起こし中...");
        var micSegments = await TranscribeSegmentsAsync(
            micWavPath, whisperFactory,
            fraction => onProgress?.Invoke(fraction * micWeight),
            onPhase).ConfigureAwait(false);

        onPhase?.Invoke("相手の声を文字起こし中...");
        var speakerSegments = await TranscribeSegmentsAsync(
            speakerWavPath, whisperFactory,
            fraction => onProgress?.Invoke(micWeight + fraction * (1 - micWeight)),
            onPhase).ConfigureAwait(false);

        var merged = micSegments.Select(s => (Segment: s, Speaker: "自分"))
            .Concat(speakerSegments.Select(s => (Segment: s, Speaker: "相手")))
            .OrderBy(s => s.Segment.Start)
            .ToList();

        var sb = new StringBuilder();
        foreach (var (segment, speaker) in merged)
            sb.AppendLine($"[{segment.Start}-{segment.End}] [{speaker}] {FormatText(segment)}");

        await File.WriteAllTextAsync(txtPath, sb.ToString(), Encoding.UTF8).ConfigureAwait(false);
        onProgress?.Invoke(1.0);
    }
}
