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

    public static async Task TranscribeToTextFileAsync(string wavPath, string txtPath)
    {
        await EnsureModelDownloadedAsync();

        using var whisperFactory = WhisperFactory.FromPath(ModelPath);
        using var processor = whisperFactory.CreateBuilder()
            .WithLanguage("auto")
            .Build();

        using var wavStream = new MemoryStream();
        using (var reader = new WaveFileReader(wavPath))
        {
            // Whisperは16kHzモノラルのWAVしか受け付けないため変換する
            var sampleProvider = reader.ToSampleProvider();
            if (reader.WaveFormat.Channels == 2)
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider);

            var resampler = new WdlResamplingSampleProvider(sampleProvider, 16000);
            WaveFileWriter.WriteWavFileToStream(wavStream, resampler.ToWaveProvider16());
        }

        wavStream.Seek(0, SeekOrigin.Begin);

        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(wavStream))
            sb.AppendLine($"[{segment.Start}-{segment.End}] {segment.Text.Trim()}");

        await File.WriteAllTextAsync(txtPath, sb.ToString(), Encoding.UTF8);
    }
}
