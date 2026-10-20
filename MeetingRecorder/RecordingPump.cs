using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingRecorder;

/// <summary>
/// マイク/スピーカーの各バッファから、実時間(ウォールクロック)に合わせた長さだけを読み出して
/// ミックス済みファイルと話者別ファイルを書き出す。
/// WASAPIのループバックは「音が鳴っていない間はデータが一切届かない」ため、生データをそのまま
/// ファイルに書くと無音区間が詰まって時刻がずれる(全く鳴らなければ空ファイルになる)。
/// BufferedWaveProviderは不足分を無音で埋めて返すので、そこから読めば3本とも同じ長さ・同じ時刻軸になる。
/// </summary>
internal sealed class RecordingPump : IDisposable
{
    public static readonly WaveFormat MixedFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    /// <summary>話者別ファイルは文字起こしと聞き返しができれば十分なので、16bitモノラルでサイズを抑える。</summary>
    public static readonly WaveFormat TrackFormat = new(44100, 16, 1);

    private const int ChunkFrames = 44100;

    private readonly ISampleProvider _mic;
    private readonly ISampleProvider _loopback;
    private readonly WaveFileWriter _mixedWriter;
    private readonly WaveFileWriter _micWriter;
    private readonly WaveFileWriter _speakerWriter;
    private readonly Func<TimeSpan> _clock;
    private readonly object _lock = new();

    private readonly float[] _micBuffer = new float[ChunkFrames * 2];
    private readonly float[] _loopbackBuffer = new float[ChunkFrames * 2];
    private readonly float[] _mixBuffer = new float[ChunkFrames * 2];
    private readonly byte[] _trackBytes = new byte[ChunkFrames * 2];

    private long _framesWritten;
    private bool _disposed;

    /// <param name="clock">録音開始からの経過時間。省略時はStopwatch(テストでは固定の時計を渡せる)。</param>
    public RecordingPump(
        BufferedWaveProvider micBuffer, BufferedWaveProvider loopbackBuffer,
        string mixedPath, string micPath, string speakerPath,
        Func<TimeSpan>? clock = null)
    {
        _mic = ToMixedFormat(micBuffer.ToSampleProvider());
        _loopback = ToMixedFormat(loopbackBuffer.ToSampleProvider());

        _mixedWriter = new WaveFileWriter(mixedPath, MixedFormat);
        _micWriter = new WaveFileWriter(micPath, TrackFormat);
        _speakerWriter = new WaveFileWriter(speakerPath, TrackFormat);

        if (clock != null)
        {
            _clock = clock;
        }
        else
        {
            var sw = Stopwatch.StartNew();
            _clock = () => sw.Elapsed;
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock)
                return _disposed ? TimeSpan.Zero : _mixedWriter.TotalTime;
        }
    }

    /// <summary>これまでに書いた分から現在時刻までの不足分を、3本のファイルに同じ長さだけ追記する。</summary>
    public void PumpOnce()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            var targetFrames = (long)(_clock().TotalSeconds * MixedFormat.SampleRate);
            var remaining = targetFrames - _framesWritten;

            while (remaining > 0)
            {
                var frames = (int)Math.Min(remaining, ChunkFrames);
                WriteChunk(frames);
                _framesWritten += frames;
                remaining -= frames;
            }
        }
    }

    private void WriteChunk(int frames)
    {
        var samples = frames * 2;
        ReadFully(_mic, _micBuffer, samples);
        ReadFully(_loopback, _loopbackBuffer, samples);

        for (var i = 0; i < samples; i++)
            _mixBuffer[i] = _micBuffer[i] + _loopbackBuffer[i];

        _mixedWriter.WriteSamples(_mixBuffer, 0, samples);
        WriteTrack(_micWriter, _micBuffer, frames);
        WriteTrack(_speakerWriter, _loopbackBuffer, frames);
    }

    private void WriteTrack(WaveFileWriter writer, float[] stereo, int frames)
    {
        for (var f = 0; f < frames; f++)
        {
            var mono = 0.5f * (stereo[2 * f] + stereo[2 * f + 1]);
            var s = (short)(Math.Clamp(mono, -1f, 1f) * short.MaxValue);
            _trackBytes[2 * f] = (byte)(s & 0xFF);
            _trackBytes[2 * f + 1] = (byte)((s >> 8) & 0xFF);
        }

        writer.Write(_trackBytes, 0, frames * 2);
    }

    /// <summary>要求した数に満たない分は無音で埋める(時刻軸を崩さないため)。</summary>
    private static void ReadFully(ISampleProvider provider, float[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = provider.Read(buffer, total, count - total);
            if (read <= 0)
                break;
            total += read;
        }

        if (total < count)
            Array.Clear(buffer, total, count - total);
    }

    private static ISampleProvider ToMixedFormat(ISampleProvider source)
    {
        var result = source;

        if (result.WaveFormat.SampleRate != MixedFormat.SampleRate)
            result = new WdlResamplingSampleProvider(result, MixedFormat.SampleRate);

        if (result.WaveFormat.Channels == 1)
            result = new MonoToStereoSampleProvider(result);

        return result;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _mixedWriter.Dispose();
            _micWriter.Dispose();
            _speakerWriter.Dispose();
        }
    }
}
