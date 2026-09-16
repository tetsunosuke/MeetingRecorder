using MeetingRecorder;

var wavPath = args.Length > 0 ? args[0] : throw new ArgumentException("wavパスを指定してください");
var txtPath = Path.ChangeExtension(wavPath, ".transcript.txt");

Console.WriteLine($"Input:  {wavPath}");
Console.WriteLine($"Output: {txtPath}");
Console.WriteLine("Ensuring model is downloaded (small, ~500MB)...");

var sw = System.Diagnostics.Stopwatch.StartNew();
await Transcriber.EnsureModelDownloadedAsync();
Console.WriteLine($"Model ready after {sw.Elapsed}");

sw.Restart();
await Transcriber.TranscribeToTextFileAsync(wavPath, txtPath);
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
