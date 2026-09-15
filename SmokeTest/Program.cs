using NAudio.CoreAudioApi;
using NAudio.Wave;

var enumerator = new MMDeviceEnumerator();
var mic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
Console.WriteLine($"Mic: {mic.FriendlyName}");

// AUTOCONVERTPCM/SRC flags are what NAudio's WasapiCapture always adds for shared mode.
// Loopback capture doesn't use them and works. Test capture WITHOUT them, using the
// device's exact native mix format (no conversion needed at all).
var noFlags = new NoConvertCapture(mic);
Console.WriteLine($"WaveFormat: {noFlags.WaveFormat}");
try
{
    bool gotData = false;
    long bytes = 0;
    noFlags.DataAvailable += (_, e) => { gotData = true; bytes += e.BytesRecorded; };
    noFlags.StartRecording();
    Thread.Sleep(800);
    noFlags.StopRecording();
    Console.WriteLine($"NoConvertCapture OK, gotData={gotData}, bytes={bytes}");
}
catch (Exception ex)
{
    Console.WriteLine($"NoConvertCapture FAILED: {ex.GetType().Name}: {ex.Message}");
}

sealed class NoConvertCapture : WasapiCapture
{
    public NoConvertCapture(MMDevice device) : base(device) { }
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.None;
}
