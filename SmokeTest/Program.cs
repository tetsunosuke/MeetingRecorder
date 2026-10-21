using MeetingRecorder;
using NAudio.Wave;

// マイク/スピーカー分離まわりの自己診断テスト。
// 実デバイスにもWhisperモデルにも触れず、合成データだけで次を確認する(録音中のPCでも安全に実行できる)。
//   1. RecordingPump: スピーカー(ループバック)に無音区間があっても、3本のファイルが同じ長さ・同じ時刻軸になる
//   2. 旧形式(時刻が詰まった話者別ファイル)の判定
//   3. 空/無音の話者別ファイルは文字起こしをスキップする(Whisperに渡さない)

var failures = 0;
void Check(bool ok, string message)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {message}");
    if (!ok) failures++;
}

var dir = Path.Combine(Path.GetTempPath(), "mr_selftest_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);

try
{
    await TestRecordingPumpAlignmentAsync();
    TestMute();
    TestHallucinationFilter();
    TestMisalignedDetection();
    await TestSilentTrackSkippedAsync();
}
finally
{
    Directory.Delete(dir, recursive: true);
}

Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILED");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------------------------------------

Task TestRecordingPumpAlignmentAsync()
{
    Console.WriteLine("--- RecordingPump: loopbackに無音区間があっても時刻軸が揃う ---");

    var mixedPath = Path.Combine(dir, "t.wav");
    var micPath = Path.Combine(dir, "t_mic.wav");
    var speakerPath = Path.Combine(dir, "t_speaker.wav");

    // 実機に近い構成: マイクは48kHz連続、スピーカーは44.1kHzで「音が鳴った間だけ」データが来る
    var micBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
    { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(5) };
    var speakerBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2))
    { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(5) };

    long micIndex = 0, speakerIndex = 0;
    var now = TimeSpan.Zero;

    using (var pump = new RecordingPump(micBuffer, speakerBuffer, mixedPath, micPath, speakerPath, () => now))
    {
        for (var tick = 0; tick < 50; tick++) // 100ms x 50 = 5秒
        {
            AddTone(micBuffer, 48000, 0.1, 440, 0.3f, ref micIndex);

            // スピーカーは 2.0〜3.0秒 の間だけ音が鳴る(それ以外はデータが一切来ない)
            if (tick is >= 20 and < 30)
                AddTone(speakerBuffer, 44100, 0.1, 880, 0.3f, ref speakerIndex);

            now += TimeSpan.FromMilliseconds(100);
            pump.PumpOnce();
        }
    }

    var mixedDuration = DurationOf(mixedPath);
    var micDuration = DurationOf(micPath);
    var speakerDuration = DurationOf(speakerPath);
    Check(Math.Abs(mixedDuration.TotalSeconds - 5.0) < 0.05, $"ミックス済みが約5秒 ({mixedDuration.TotalSeconds:F2}s)");
    Check(Math.Abs(micDuration.TotalSeconds - 5.0) < 0.05, $"マイク別ファイルが約5秒 ({micDuration.TotalSeconds:F2}s)");
    Check(Math.Abs(speakerDuration.TotalSeconds - 5.0) < 0.05,
        $"スピーカー別ファイルも約5秒で詰まっていない ({speakerDuration.TotalSeconds:F2}s)");

    Check(Rms(speakerPath, 0.5, 1.5) < 0.01, "スピーカー別ファイル: 0.5〜1.5秒(鳴っていない)は無音");
    Check(Rms(speakerPath, 2.2, 2.8) > 0.1, "スピーカー別ファイル: 2.2〜2.8秒(鳴っている)は音がある");
    Check(Rms(speakerPath, 3.5, 4.5) < 0.01, "スピーカー別ファイル: 3.5〜4.5秒(鳴り終わった後)は無音");
    Check(Rms(micPath, 0.5, 4.5) > 0.1, "マイク別ファイル: 全区間で音がある");
    Check(Rms(mixedPath, 2.2, 2.8) > Rms(mixedPath, 0.5, 1.5) * 1.2, "ミックス済み: スピーカーが鳴っている区間のほうが大きい");

    return Task.CompletedTask;
}

void TestMute()
{
    Console.WriteLine("--- ミュート: 該当区間だけ無音になり、時刻軸は崩れない ---");

    var mixedPath = Path.Combine(dir, "m.wav");
    var micPath = Path.Combine(dir, "m_mic.wav");
    var speakerPath = Path.Combine(dir, "m_speaker.wav");

    var micBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
    { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(5) };
    var speakerBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2))
    { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(5) };

    long micIndex = 0, speakerIndex = 0;
    var now = TimeSpan.Zero;

    using (var pump = new RecordingPump(micBuffer, speakerBuffer, mixedPath, micPath, speakerPath, () => now))
    {
        for (var tick = 0; tick < 60; tick++) // 6秒、両方とも鳴りっぱなし
        {
            // 2〜4秒: マイクをミュート / 3〜5秒: スピーカーをミュート
            pump.MicMuted = tick is >= 20 and < 40;
            pump.SpeakerMuted = tick is >= 30 and < 50;

            AddTone(micBuffer, 48000, 0.1, 440, 0.3f, ref micIndex);
            AddTone(speakerBuffer, 44100, 0.1, 880, 0.3f, ref speakerIndex);

            now += TimeSpan.FromMilliseconds(100);
            pump.PumpOnce();
        }
    }

    Check(Math.Abs(DurationOf(micPath).TotalSeconds - 6.0) < 0.05, "ミュートしても長さは6秒のまま");
    Check(Rms(micPath, 0.3, 1.7) > 0.1, "マイク: ミュート前は音がある");
    Check(Rms(micPath, 2.2, 3.8) < 0.01, "マイク: 2〜4秒のミュート区間は無音");
    Check(Rms(micPath, 4.2, 5.8) > 0.1, "マイク: ミュート解除後は音が戻る");
    Check(Rms(speakerPath, 0.3, 2.7) > 0.1, "スピーカー: ミュート前は音がある");
    Check(Rms(speakerPath, 3.2, 4.8) < 0.01, "スピーカー: 3〜5秒のミュート区間は無音");
    Check(Rms(speakerPath, 5.2, 5.8) > 0.1, "スピーカー: ミュート解除後は音が戻る");
    Check(Rms(mixedPath, 3.2, 3.8) < 0.01, "ミックス済み: 両方ミュートの3〜4秒は無音");
    Check(Rms(mixedPath, 2.2, 2.8) > 0.05, "ミックス済み: マイクだけミュートの区間もスピーカーの音は残る");
}

void TestHallucinationFilter()
{
    Console.WriteLine("--- 幻覚(定型文・ループ)の除去 ---");
    static Transcriber.RawSegment Seg(int sec, string text) =>
        new(TimeSpan.FromSeconds(sec), TimeSpan.FromSeconds(sec + 1), text, 0f);

    var input = new List<Transcriber.RawSegment>
    {
        Seg(0, "ありがとうございました"), Seg(1, "(英語)"), Seg(2, "I went"), Seg(3, "I end"), Seg(4, "(音楽)"),
        Seg(5, "よろしくお願いします"),
        Seg(6, "ん"), Seg(7, "ん"), Seg(8, "ん"), Seg(9, "ん"), Seg(10, "ん"),
        Seg(11, "はい"), Seg(12, "はい"), Seg(13, "はい"),
    };
    var texts = Transcriber.RemoveHallucinations(input).Select(s => s.Text).ToList();

    Check(texts.Contains("ありがとうございました"), "会議で普通に言う「ありがとうございました」は残す");
    Check(!texts.Contains("(英語)") && !texts.Contains("I went") && !texts.Contains("I end") && !texts.Contains("(音楽)"),
        "定型の幻覚文((英語)/I went/I end/(音楽))は除去");
    Check(texts.Count(t => t == "ん") == 1, "同じ文が4回以上連続するループは最初の1つだけ残す");
    Check(texts.Count(t => t == "はい") == 3, "3回までの連続(相づち)は残す");
}

void TestMisalignedDetection()
{
    Console.WriteLine("--- 旧形式(時刻が詰まった話者別ファイル)の判定 ---");
    static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    Check(Transcriber.IsMisalignedTrackPair(S(3000), S(43)), "50分 vs 43秒(今回録音中の実例)はずれと判定");
    Check(!Transcriber.IsMisalignedTrackPair(S(3000), S(3000.02)), "ほぼ同じ長さはずれなし");
    Check(!Transcriber.IsMisalignedTrackPair(S(10), S(0)), "片方が空(0秒)は「ずれ」ではなく無音扱い");
    Check(!Transcriber.IsMisalignedTrackPair(S(5), S(5.2)), "数百msの差はずれなし");
    Check(Transcriber.IsMisalignedTrackPair(S(100), S(94)), "100秒 vs 94秒(6秒差)はずれと判定");
    Check(!Transcriber.IsMisalignedTrackPair(S(3000), S(2950)), "50分で50秒差(2%以内)は許容");
}

async Task TestSilentTrackSkippedAsync()
{
    Console.WriteLine("--- 空/無音の話者別ファイルはWhisperに渡さない ---");

    // 1) 旧バージョンで実際にできていた「ヘッダだけで音声データが0件」のファイル
    var emptyPath = Path.Combine(dir, "empty_speaker.wav");
    new WaveFileWriter(emptyPath, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).Dispose();
    var empty = await Transcriber.TranscribeSegmentsAsync(emptyPath, null!, null, null);
    Check(empty.WasSilent && empty.Segments.Count == 0, "音声データ0件のファイル(旧バージョンで実際にできていた58バイトのWAV相当)は無音扱いでスキップ");

    // 2) 長さはあるが全部ゼロ(新しい録音で、相手が一度も話さなかった場合のスピーカー別ファイル)
    var zerosPath = Path.Combine(dir, "zeros.wav");
    using (var w = new WaveFileWriter(zerosPath, RecordingPump.TrackFormat))
        w.Write(new byte[44100 * 2 * 3], 0, 44100 * 2 * 3); // 3秒分の無音
    var zeros = await Transcriber.TranscribeSegmentsAsync(zerosPath, null!, null, null);
    Check(zeros.WasSilent, "全部ゼロ(3秒)は無音扱いでスキップ");

    // 3) 普通の音は無音扱いされない(=Whisperの段階まで進む。ここではモデルを渡していないのでNullReferenceExceptionになる)
    var tonePath = Path.Combine(dir, "tone.wav");
    using (var w = new WaveFileWriter(tonePath, RecordingPump.TrackFormat))
    {
        var bytes = new byte[44100 * 2];
        for (var i = 0; i < 44100; i++)
        {
            var s = (short)(Math.Sin(2 * Math.PI * 440 * i / 44100.0) * 0.2 * short.MaxValue);
            bytes[2 * i] = (byte)(s & 0xFF);
            bytes[2 * i + 1] = (byte)((s >> 8) & 0xFF);
        }

        w.Write(bytes, 0, bytes.Length);
    }

    var reachedWhisper = false;
    try
    {
        await Transcriber.TranscribeSegmentsAsync(tonePath, null!, null, null);
    }
    catch (NullReferenceException)
    {
        reachedWhisper = true;
    }

    Check(reachedWhisper, "音のあるファイルは無音扱いされず、Whisperの段階まで進む");
}

// ---------------------------------------------------------------------------------------------

static void AddTone(BufferedWaveProvider buffer, int sampleRate, double seconds, double hz, float amplitude, ref long index)
{
    var frames = (int)(sampleRate * seconds);
    var bytes = new byte[frames * 2 * sizeof(float)];
    for (var f = 0; f < frames; f++, index++)
    {
        var sample = (float)(Math.Sin(2 * Math.PI * hz * index / sampleRate) * amplitude);
        BitConverter.GetBytes(sample).CopyTo(bytes, f * 8);
        BitConverter.GetBytes(sample).CopyTo(bytes, f * 8 + 4);
    }

    buffer.AddSamples(bytes, 0, bytes.Length);
}

static TimeSpan DurationOf(string path)
{
    using var reader = new WaveFileReader(path);
    return reader.TotalTime;
}

static double Rms(string path, double fromSeconds, double toSeconds)
{
    using var reader = new WaveFileReader(path);
    reader.CurrentTime = TimeSpan.FromSeconds(fromSeconds);
    var samples = reader.ToSampleProvider();
    var count = (int)((toSeconds - fromSeconds) * reader.WaveFormat.SampleRate) * reader.WaveFormat.Channels;
    var buffer = new float[count];
    var read = samples.Read(buffer, 0, count);
    if (read == 0)
        return 0;

    double sum = 0;
    for (var i = 0; i < read; i++)
        sum += buffer[i] * buffer[i];
    return Math.Sqrt(sum / read);
}
