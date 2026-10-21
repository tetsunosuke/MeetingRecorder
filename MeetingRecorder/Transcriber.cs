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

    internal readonly record struct RawSegment(TimeSpan Start, TimeSpan End, string Text, float NoSpeechProbability);

    internal readonly record struct TrackResult(List<RawSegment> Segments, bool WasSilent);

    // これ未満のピークしかない音声は「無音」とみなして文字起こしをスキップする(約-50dBFS)。
    // 無音をWhisperに渡すと「ご視聴ありがとうございました」のような無関係な文言を幻覚しやすいため。
    private const float SilencePeakThreshold = 0.003f;

    // マイクとスピーカーの長さがこれ以上ずれていたら、時刻が詰まった旧形式の話者別ファイルとみなす。
    private static readonly TimeSpan MisalignedMinGap = TimeSpan.FromSeconds(3);
    private const double MisalignedMinGapRatio = 0.02;

    private static string FormatText(RawSegment segment) =>
        segment.NoSpeechProbability > NoSpeechThreshold ? $"(不明瞭) {segment.Text}" : segment.Text;

    /// <summary>通過するサンプルの絶対値の最大(ピーク)を記録するだけの中継。</summary>
    private sealed class PeakTrackingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;

        public PeakTrackingSampleProvider(ISampleProvider source) => _source = source;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public float Peak { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            for (var i = 0; i < read; i++)
            {
                var abs = Math.Abs(buffer[offset + i]);
                if (abs > Peak)
                    Peak = abs;
            }

            return read;
        }
    }

    /// <summary>
    /// 旧バージョン(v0.7.0〜v0.8.0)の録音では、スピーカー側の話者別ファイルに「音が鳴っていた間のデータだけ」が
    /// 詰めて書かれており、マイク側より大幅に短くなる。この場合タイムスタンプは信用できない。
    /// </summary>
    internal static bool IsMisalignedTrackPair(TimeSpan micDuration, TimeSpan speakerDuration)
    {
        var longer = micDuration > speakerDuration ? micDuration : speakerDuration;
        var gap = (micDuration - speakerDuration).Duration();
        var tolerance = TimeSpan.FromSeconds(Math.Max(MisalignedMinGap.TotalSeconds, longer.TotalSeconds * MisalignedMinGapRatio));

        // 片方が空(ほぼ0秒)の場合は「ずれ」ではなく「無音」なので、ここでは扱わない
        var bothHaveAudio = micDuration >= TimeSpan.FromSeconds(0.5) && speakerDuration >= TimeSpan.FromSeconds(0.5);
        return bothHaveAudio && gap > tolerance;
    }

    /// <summary>1本のWAVを文字起こしし、区間ごとのセグメント一覧を返す(ファイル書き出しはしない)。</summary>
    internal static async Task<TrackResult> TranscribeSegmentsAsync(
        string wavPath, WhisperFactory whisperFactory, Action<double>? onProgress, Action<string>? onPhase)
    {
        TimeSpan totalDuration;
        float peak;
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
            var peakTracker = new PeakTrackingSampleProvider(resampler);
            WaveFileWriter.WriteWavFileToStream(wavStream, peakTracker.ToWaveProvider16());
            peak = peakTracker.Peak;
        }

        if (peak < SilencePeakThreshold)
        {
            onProgress?.Invoke(1.0);
            return new TrackResult(new List<RawSegment>(), WasSilent: true);
        }

        // 長時間を1回で通すと、途中で一度「(英語)」のような幻覚が出ると、それが直前の文脈として
        // 次の認識に引き継がれて抜け出せなくなる(実際に30分以上続いた)。そこで数分ごとに区切り、
        // 区切りごとに認識器を作り直して文脈をリセットする。
        var pcm = wavStream.ToArray();
        var totalSamples = (pcm.Length - WavHeaderBytes) / 2;

        onPhase?.Invoke("文字起こし中...");
        var segments = new List<RawSegment>();
        long chunkStart = 0;
        while (chunkStart < totalSamples)
        {
            var chunkEnd = FindChunkEnd(pcm, chunkStart, totalSamples);
            var chunkOffset = TimeSpan.FromSeconds((double)chunkStart / WhisperSampleRate);

            if (ChunkPeak(pcm, chunkStart, chunkEnd) >= SilencePeakThreshold)
            {
                using var chunkStream = new MemoryStream();
                using (var writer = new WaveFileWriter(chunkStream, new WaveFormat(WhisperSampleRate, 16, 1)))
                    writer.Write(pcm, WavHeaderBytes + (int)(chunkStart * 2), (int)((chunkEnd - chunkStart) * 2));
                var chunkBytes = chunkStream.ToArray();

                using var processor = whisperFactory.CreateBuilder()
                    .WithLanguage("auto")
                    .Build();
                using var input = new MemoryStream(chunkBytes);
                await foreach (var segment in processor.ProcessAsync(input).ConfigureAwait(false))
                {
                    segments.Add(new RawSegment(
                        segment.Start + chunkOffset, segment.End + chunkOffset,
                        segment.Text.Trim(), segment.NoSpeechProbability));
                }
            }

            chunkStart = chunkEnd;
            if (totalDuration > TimeSpan.Zero)
                onProgress?.Invoke(Math.Clamp((double)chunkEnd / totalSamples, 0.0, 1.0));
        }

        return new TrackResult(RemoveHallucinations(segments), WasSilent: false);
    }

    private const int WhisperSampleRate = 16000;
    private const int WavHeaderBytes = 44;
    private const int ChunkTargetSeconds = 120;
    private const int ChunkSearchSeconds = 20;
    private const int MinTailSeconds = 30;

    /// <summary>目標の長さ付近で、できるだけ静かな位置(発話の途中で切らないため)を区切りにする。</summary>
    private static long FindChunkEnd(byte[] pcm, long start, long totalSamples)
    {
        var target = start + (long)ChunkTargetSeconds * WhisperSampleRate;
        if (totalSamples - target < (long)MinTailSeconds * WhisperSampleRate)
            return totalSamples;

        const int blockSamples = WhisperSampleRate / 4;
        var searchFrom = target - (long)ChunkSearchSeconds * WhisperSampleRate;
        var bestEnd = target;
        var bestEnergy = double.MaxValue;
        for (var blockStart = searchFrom; blockStart + blockSamples <= target; blockStart += blockSamples)
        {
            double energy = 0;
            for (var i = 0; i < blockSamples; i++)
            {
                var s = BitConverter.ToInt16(pcm, WavHeaderBytes + (int)((blockStart + i) * 2));
                energy += (double)s * s;
            }

            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                bestEnd = blockStart + blockSamples / 2;
            }
        }

        // タイムスタンプの表記が秒単位のまま揃うよう、区切りは整数秒の位置にする
        return bestEnd / WhisperSampleRate * WhisperSampleRate;
    }

    private static float ChunkPeak(byte[] pcm, long start, long end)
    {
        var peak = 0;
        for (var i = start; i < end; i++)
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, WavHeaderBytes + (int)(i * 2))));
        return peak / 32768f;
    }

    // 音声が途切れたり無音に近いとき、Whisperが学習データ由来の定型文を出力することがある。
    private static readonly HashSet<string> HallucinationPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        // 会議で普通に言いうる「ありがとうございました」「Thank you.」「Bye.」などは、ここに入れない
        "(英語)", "(音楽)", "(拍手)", "(笑)", "I went", "I end", "Thanks for watching!",
        "ご視聴ありがとうございました", "ご視聴ありがとうございました。", "チャンネル登録をお願いします",
    };

    // 同じ短い文が何度も連続するときは、幻覚のループとみなして最初の1つだけ残す
    private const int LoopRunLength = 4;

    // Whisperは非発話の区間に「(音声)」「[inaudible]」「*говорит по-английски*」のような括弧書きの注釈を出すことがある。
    // 文全体が括弧(または*)で囲まれているものは、発言ではなく注釈とみなして除く。
    private static readonly (char Open, char Close)[] AnnotationBrackets =
    {
        ('(', ')'), ('（', '）'), ('[', ']'), ('［', '］'), ('*', '*'), ('＊', '＊'),
    };

    private const int AnnotationMaxLength = 60;

    internal static bool IsAnnotationOnly(string text)
    {
        if (text.Length < 2 || text.Length > AnnotationMaxLength)
            return false;

        foreach (var (open, close) in AnnotationBrackets)
        {
            if (text[0] == open && text[^1] == close && text.IndexOf(close, 1) == text.Length - 1)
                return true;
        }

        return false;
    }

    internal static List<RawSegment> RemoveHallucinations(List<RawSegment> segments)
    {
        var result = new List<RawSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var text = segment.Text.Trim();
            if (text.Length == 0 || HallucinationPhrases.Contains(text) || IsAnnotationOnly(text))
                continue;
            result.Add(segment);
        }

        var deduped = new List<RawSegment>(result.Count);
        for (var i = 0; i < result.Count;)
        {
            var run = 1;
            while (i + run < result.Count && result[i + run].Text == result[i].Text)
                run++;

            if (run >= LoopRunLength)
                deduped.Add(result[i]);
            else
                deduped.AddRange(result.GetRange(i, run));
            i += run;
        }

        return deduped;
    }

    /// <summary>
    /// 1本のWAVをそのまま文字起こしする(話者分離なし)。
    /// 話者別ファイル(_mic/_speaker)が見つからない古い録音や任意のWAVを手動で文字起こしする場合に使う。
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

        var result = await TranscribeSegmentsAsync(wavPath, whisperFactory, onProgress, onPhase).ConfigureAwait(false);

        var sb = new StringBuilder();
        if (result.WasSilent)
            sb.AppendLine("※音声が検出されませんでした(無音)。録音デバイスの選択を確認してください。");
        foreach (var segment in result.Segments)
            sb.AppendLine($"[{segment.Start}-{segment.End}] {FormatText(segment)}");

        await File.WriteAllTextAsync(txtPath, sb.ToString(), Encoding.UTF8).ConfigureAwait(false);
        onProgress?.Invoke(1.0);
    }

    /// <summary>
    /// マイクとスピーカー出力を別々に文字起こしし、時系列順にマージして話者ラベル付きの
    /// 1本のテキストにまとめる。録音停止後の自動文字起こしはこちらを使う。
    /// </summary>
    /// <param name="mixedWavPath">ミックス済みの録音ファイル。話者別ファイルの時刻がずれている旧形式の録音のとき、
    /// 時刻だけは正しいこちらで代わりに文字起こしする(話者は分けられない)。</param>
    public static async Task TranscribeDiarizedToTextFileAsync(
        string micWavPath, string speakerWavPath, string txtPath, WhisperModelSize modelSize,
        Action<double>? onProgress = null,
        Action<string>? onPhase = null,
        Action<TimeSpan>? onDurationKnown = null,
        string? mixedWavPath = null)
    {
        TimeSpan micDuration, speakerDuration;
        using (var probe = new WaveFileReader(micWavPath)) micDuration = probe.TotalTime;
        using (var probe = new WaveFileReader(speakerWavPath)) speakerDuration = probe.TotalTime;

        onDurationKnown?.Invoke(micDuration > speakerDuration ? micDuration : speakerDuration);

        await EnsureModelDownloadedAsync(modelSize).ConfigureAwait(false);
        var whisperFactory = await GetOrLoadFactoryAsync(modelSize).ConfigureAwait(false);

        var sb = new StringBuilder();

        if (IsMisalignedTrackPair(micDuration, speakerDuration) &&
            !string.IsNullOrEmpty(mixedWavPath) && File.Exists(mixedWavPath))
        {
            var mixed = await TranscribeSegmentsAsync(mixedWavPath, whisperFactory, onProgress, onPhase).ConfigureAwait(false);

            sb.AppendLine("※旧バージョンで録音されたため、スピーカー側の話者別ファイルの時刻がずれています。" +
                          "話者は分けず、ミックスした音声で文字起こししました。");
            foreach (var segment in mixed.Segments)
                sb.AppendLine($"[{segment.Start}-{segment.End}] {FormatText(segment)}");

            await File.WriteAllTextAsync(txtPath, sb.ToString(), Encoding.UTF8).ConfigureAwait(false);
            onProgress?.Invoke(1.0);
            return;
        }

        // 進捗はマイク・スピーカーそれぞれの長さに応じた重み付けで合算する
        var totalSeconds = micDuration.TotalSeconds + speakerDuration.TotalSeconds;
        var micWeight = totalSeconds > 0 ? micDuration.TotalSeconds / totalSeconds : 0.5;

        onPhase?.Invoke("自分の声を文字起こし中...");
        var mic = await TranscribeSegmentsAsync(
            micWavPath, whisperFactory,
            fraction => onProgress?.Invoke(fraction * micWeight),
            onPhase).ConfigureAwait(false);

        onPhase?.Invoke("相手の声を文字起こし中...");
        var speaker = await TranscribeSegmentsAsync(
            speakerWavPath, whisperFactory,
            fraction => onProgress?.Invoke(micWeight + fraction * (1 - micWeight)),
            onPhase).ConfigureAwait(false);

        if (mic.WasSilent)
            sb.AppendLine("※マイク側の音声が検出されませんでした(無音)。マイクの選択やミュート状態を確認してください。");
        if (speaker.WasSilent)
            sb.AppendLine("※スピーカー側の音声が検出されませんでした(無音)。" +
                          "録音時に選んだ「スピーカー出力」が、会議アプリの出力先と一致しているか確認してください。");

        var merged = mic.Segments.Select(s => (Segment: s, Speaker: "自分"))
            .Concat(speaker.Segments.Select(s => (Segment: s, Speaker: "相手")))
            .OrderBy(s => s.Segment.Start)
            .ToList();

        foreach (var (segment, speakerLabel) in merged)
            sb.AppendLine($"[{segment.Start}-{segment.End}] [{speakerLabel}] {FormatText(segment)}");

        await File.WriteAllTextAsync(txtPath, sb.ToString(), Encoding.UTF8).ConfigureAwait(false);
        onProgress?.Invoke(1.0);
    }
}
