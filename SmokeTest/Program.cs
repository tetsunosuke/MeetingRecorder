using MeetingRecorder;

var wavPath = args.Length > 0 ? args[0] : throw new ArgumentException("wavパスを指定してください");
var txtPath = Path.ChangeExtension(wavPath, ".transcript.txt");
const WhisperModelSize modelSize = WhisperModelSize.Small;

Console.WriteLine($"Input:  {wavPath}");
Console.WriteLine($"Output: {txtPath}");
Console.WriteLine($"Ensuring model is downloaded ({modelSize}, {Transcriber.ApproxDownloadSize(modelSize)})...");

var sw = System.Diagnostics.Stopwatch.StartNew();
await Transcriber.EnsureModelDownloadedAsync(modelSize);
Console.WriteLine($"Model ready after {sw.Elapsed}");

sw.Restart();
await Transcriber.TranscribeToTextFileAsync(wavPath, txtPath, modelSize);
Console.WriteLine($"Transcription done in {sw.Elapsed}");

if (File.Exists(txtPath))
{
    Console.WriteLine("=== Transcript ===");
    Console.WriteLine(File.ReadAllText(txtPath));
}
else
{
    Console.WriteLine("FAILED: transcript not created.");
}
