using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DynamicIsland.Services;

/// <summary>
/// Verjot's meeting recorder: the microphone and the computer's sound, as Verjot does it. Both are kept losslessly
/// while recording (a WAV per capture in the meeting's "work" folder); at the end they are mixed into one MP3
/// (192 kbps, as Verjot writes it) and the lossless files are removed. When anything goes wrong on the way, the
/// lossless files stay: a recording is never lost.
/// </summary>
public sealed class MeetingRecorder : IDisposable
{
    public enum Stage { Idle, Recording, Saving }
    public enum Outcome { None, TooShort, Saved, Failed }

    /// <summary>One capture: its file, when in the meeting it began (seconds), how long it is.</summary>
    private sealed class Part(string path, WaveFormat format, double offset)
    {
        public readonly string Path = path;
        public readonly WaveFormat Format = format;
        public readonly double Offset = offset;
        public double Seconds;
    }

    private readonly List<Part> _micParts = [], _systemParts = [];
    private readonly object _gate = new();
    private WasapiRecorder? _mic, _system;
    private WaveFileWriter? _micWriter, _systemWriter;
    private bool _stopping;
    private long _loudAt = Environment.TickCount64;

    private volatile float _micLevel, _systemLevel;

    public Stage Current { get; private set; } = Stage.Idle;
    public DateTime? StartedAt { get; private set; }
    /// <summary>The microphone is switched off; the computer's sound keeps recording.</summary>
    public bool MicrophoneOff { get; private set; }
    /// <summary>Moments marked with a bookmark, in seconds from the start.</summary>
    public List<double> Bookmarks { get; } = [];
    /// <summary>The meeting's folder (created when the recording starts).</summary>
    public string Folder { get; private set; } = "";
    /// <summary>The MP3 of the meeting that ended last.</summary>
    public string? OutputFile { get; private set; }
    /// <summary>What became of the meeting that ended last.</summary>
    public Outcome LastOutcome { get; private set; }
    /// <summary>What went wrong with the last meeting (the lossless files were kept).</summary>
    public string? LastError { get; private set; }

    public bool IsRecording => Current == Stage.Recording;
    public TimeSpan Elapsed => StartedAt is { } at ? DateTime.Now - at : TimeSpan.Zero;
    public float MicLevel => _micLevel;
    public float SystemLevel => _systemLevel;
    /// <summary>How long neither the microphone nor the computer made a sound.</summary>
    public TimeSpan SilentFor => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _loudAt));

    public event Action? Changed;

    // ---------------------------------------------------------------- start / stop

    public bool Start(out string? problem)
    {
        problem = null;
        if (IsRecording) return false;

        string root = VerjotFiles.Root;
        string name = "Verjot" + DateTime.Now.ToString("yyyyMMddHHmm");   // Verjot's name: when the meeting began
        Folder = Path.Combine(root, name);
        for (int i = 2; Directory.Exists(Folder); i++) Folder = Path.Combine(root, name + "-" + i);   // two in one minute: never over the other
        Directory.CreateDirectory(Path.Combine(Folder, "work"));
        _micParts.Clear(); _systemParts.Clear(); Bookmarks.Clear();
        OutputFile = null; LastError = null; MicrophoneOff = false;

        StartedAt = DateTime.Now;
        Current = Stage.Recording;
        _loudAt = Environment.TickCount64;
        bool mic = StartMicrophone(null, out string? micProblem);
        bool system = StartSystem(out string? systemProblem);
        problem = !mic ? micProblem : !system ? systemProblem : null;
        if (!mic && !system)
        {
            Directory.Delete(Folder, recursive: true);
            Current = Stage.Idle;
            StartedAt = null;
            Folder = "";
            return false;
        }
        Changed?.Invoke();
        return true;
    }

    private bool StartMicrophone(MMDevice? device, out string? problem)
    {
        problem = null;
        try
        {
            device ??= DefaultDevice(DataFlow.Capture);
            var recorder = device == null ? new WasapiRecorderBuilder().Build() : new WasapiRecorderBuilder().WithDevice(device).Build();
            return StartCapture(recorder, isMicrophone: true, out problem);
        }
        catch (Exception ex) { problem = ex.Message; Log.Write("meeting: microphone: " + ex.GetType().Name + ": " + ex.Message); return false; }
    }

    private bool StartSystem(out string? problem)
    {
        problem = null;
        try
        {
            var recorder = new WasapiRecorderBuilder().WithLoopbackCapture().Build();
            return StartCapture(recorder, isMicrophone: false, out problem);
        }
        catch (Exception ex) { problem = ex.Message; Log.Write("meeting: computer sound: " + ex.GetType().Name + ": " + ex.Message); return false; }
    }

    private bool StartCapture(WasapiRecorder recorder, bool isMicrophone, out string? problem)
    {
        problem = null;
        var format = recorder.WaveFormat;
        var parts = isMicrophone ? _micParts : _systemParts;
        var part = new Part(Path.Combine(Folder, "work", $"{(isMicrophone ? "mic" : "system")}-{parts.Count + 1}.wav"), format, Elapsed.TotalSeconds);
        var writer = new WaveFileWriter(part.Path, format);
        parts.Add(part);
        recorder.DataAvailable += (buffer, flags, _, _) => OnData(buffer, writer, format, part, isMicrophone, flags);
        recorder.RecordingStopped += (_, e) => OnCaptureStopped(isMicrophone, e.Exception);
        try { recorder.StartRecording(); }
        catch (Exception ex)
        {
            writer.Dispose();
            File.Delete(part.Path);
            parts.Remove(part);
            recorder.Dispose();
            throw new InvalidOperationException("StartRecording: " + ex.Message, ex);
        }
        if (isMicrophone) { _mic = recorder; _micWriter = writer; }
        else { _system = recorder; _systemWriter = writer; }
        return true;
    }

    private static MMDevice? DefaultDevice(DataFlow flow)
    {
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                if (enumerator.TryGetDefaultAudioEndpoint(flow, role, out var device) && device != null) return device;
            }
            catch { /* no such device in this role */ }
        }
        return null;
    }

    private void OnData(ReadOnlySpan<byte> buffer, WaveFileWriter writer, WaveFormat format, Part part, bool isMicrophone, AudioClientBufferFlags flags)
    {
        if (buffer.Length == 0) return;
        lock (_gate)
        {
            try
            {
                writer.Write(buffer.ToArray(), 0, buffer.Length);
                part.Seconds = (double)writer.Length / Math.Max(1, format.AverageBytesPerSecond);
            }
            catch (Exception ex) { Log.Write("meeting: write: " + ex.GetType().Name + ": " + ex.Message); }
        }
        float peak = (flags & AudioClientBufferFlags.Silent) == 0 ? Peak(buffer, format) : 0;
        if (isMicrophone) _micLevel = peak; else _systemLevel = peak;
        if (peak > 0.004f) Interlocked.Exchange(ref _loudAt, Environment.TickCount64);
    }

    private static float Peak(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        bool single = format.Encoding == WaveFormatEncoding.IeeeFloat || format.BitsPerSample == 32;
        int bytes = single ? 4 : format.BitsPerSample == 8 ? 1 : 2;
        int channels = Math.Max(1, format.Channels);
        int frames = buffer.Length / (bytes * channels);
        float peak = 0;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int at = (i * channels + c) * bytes;
                sum += single && bytes == 4 ? BitConverter.ToSingle(buffer.Slice(at, 4)) : bytes == 2 ? BitConverter.ToInt16(buffer.Slice(at, 2)) / 32768f : 0;
            }
            peak = Math.Max(peak, Math.Abs(sum / channels));
        }
        return peak;
    }

    private void OnCaptureStopped(bool isMicrophone, Exception? error)
    {
        if (error != null) Log.Write($"meeting: {(isMicrophone ? "microphone" : "computer sound")} stopped: {error.GetType().Name}: {error.Message}");
        if (_stopping || !IsRecording) return;
        // a device went away or the stream broke: the rest of the meeting goes on, on the device that is default now
        _ = RestartAsync(isMicrophone);
    }

    private async Task RestartAsync(bool isMicrophone)
    {
        var recorder = isMicrophone ? _mic : _system;
        var writer = isMicrophone ? _micWriter : _systemWriter;
        if (isMicrophone) { _mic = null; _micWriter = null; } else { _system = null; _systemWriter = null; }
        try { recorder?.Dispose(); } catch { /* already gone */ }
        lock (_gate) { try { writer?.Dispose(); } catch { /* already closed */ } }
        await Task.Delay(500);
        if (!IsRecording || (isMicrophone && MicrophoneOff)) return;
        if (isMicrophone) StartMicrophone(null, out _); else StartSystem(out _);
    }

    /// <summary>The microphone off or on; the computer's sound is not touched.</summary>
    public bool ToggleMicrophone()
    {
        if (!IsRecording) return false;
        if (!MicrophoneOff)
        {
            MicrophoneOff = true;
            var recorder = _mic;
            _mic = null;
            var writer = _micWriter;
            _micWriter = null;
            _stopping = true;
            try { recorder?.StopRecording(); } catch { /* already stopped */ }
            try { recorder?.Dispose(); } catch { /* already gone */ }
            _stopping = false;
            lock (_gate) { try { writer?.Dispose(); } catch { /* already closed */ } }
            _micLevel = 0;
        }
        else
        {
            MicrophoneOff = false;
            if (!StartMicrophone(null, out var problem) && problem != null) LastError = problem;
        }
        Changed?.Invoke();
        return MicrophoneOff;
    }

    public void Bookmark() => Bookmarks.Add(Elapsed.TotalSeconds);

    /// <summary>Ends the meeting and writes its MP3 (a moment of work).</summary>
    public async Task<bool> StopAsync()
    {
        if (!IsRecording) return false;
        var started = StartedAt!.Value;
        Current = Stage.Saving;
        _stopping = true;
        StopCapture(ref _mic, _micWriter);
        StopCapture(ref _system, _systemWriter);
        _stopping = false;
        Changed?.Invoke();

        // a slip of the finger: Verjot keeps nothing shorter than 5 seconds
        if (DateTime.Now - started < TimeSpan.FromSeconds(5))
        {
            try { Directory.Delete(Folder, recursive: true); } catch { /* a temp folder: nothing was kept anyway */ }
            Current = Stage.Idle;
            StartedAt = null;
            Folder = "";
            LastOutcome = Outcome.TooShort;
            Changed?.Invoke();
            return false;
        }

        bool ok;
        try { ok = await Task.Run(Export); }
        catch (Exception ex) { Log.Error("meeting: export", ex); LastError = ex.Message; ok = false; }
        if (!ok && LastError == null) LastError = "export";
        LastOutcome = ok ? Outcome.Saved : Outcome.Failed;
        Current = Stage.Idle;
        StartedAt = null;
        Changed?.Invoke();
        return ok;
    }

    private void StopCapture(ref WasapiRecorder? recorder, WaveFileWriter? writer)
    {
        var rec = recorder;
        recorder = null;
        if (rec != null)
        {
            var done = new ManualResetEventSlim();
            rec.RecordingStopped += (_, _) => done.Set();
            try { rec.StopRecording(); } catch (Exception ex) { Log.Write("meeting: stop: " + ex.Message); }
            try { done.Wait(TimeSpan.FromSeconds(2)); } catch { /* disposed first */ }
            try { rec.Dispose(); } catch { /* already gone */ }
        }
        lock (_gate) { try { writer?.Dispose(); } catch { /* already closed */ } }
        if (ReferenceEquals(writer, _micWriter)) _micWriter = null;
        if (ReferenceEquals(writer, _systemWriter)) _systemWriter = null;
    }

    /// <summary>The program is closing with a meeting running: stop and write it out now, on the calling thread.</summary>
    public bool StopAndWait()
    {
        if (!IsRecording) return false;
        _stopping = true;
        StopCapture(ref _mic, _micWriter);
        StopCapture(ref _system, _systemWriter);
        _stopping = false;
        try
        {
            bool ok = Export();
            LastOutcome = ok ? Outcome.Saved : Outcome.Failed;
            Current = Stage.Idle;
            StartedAt = null;
            return ok;
        }
        catch (Exception ex) { Log.Error("meeting: export on exit", ex); LastError = ex.Message; return false; }
    }

    public void Dispose()
    {
        _stopping = true;
        StopCapture(ref _mic, _micWriter);
        StopCapture(ref _system, _systemWriter);
        _stopping = false;
    }

    // ---------------------------------------------------------------- after the meeting: one MP3

    private const int Rate = 48000;
    private const int Bitrate = 192000;

    private bool Export()
    {
        string work = Path.Combine(Folder, "work");
        if (_micParts.Count == 0 && _systemParts.Count == 0) { LastError = "nothing was captured"; return false; }
        // every pass opens the captures anew: a pass reads its track to the end
        (TimelineTrack? Microphone, TimelineTrack? System) Open() =>
            (_micParts.Count > 0 ? new TimelineTrack(_micParts) : null, _systemParts.Count > 0 ? new TimelineTrack(_systemParts) : null);

        // how loud the two tracks are together
        double peak = 0;
        long total = 0;
        {
            var (microphone, system) = Open();
            try
            {
                var measure = new Mix(microphone, system, 1);
                var buffer = new float[Rate * 2];
                int read;
                while ((read = measure.Read(buffer.AsSpan())) > 0)
                {
                    total += read;
                    for (int i = 0; i < read; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
                }
            }
            finally { microphone?.Dispose(); system?.Dispose(); }
        }
        if (total == 0) { LastError = "nothing was captured"; return false; }
        float gain = peak > 0.98 ? (float)(0.98 / peak) : 1;

        // 16-bit PCM first: it is what Windows' MP3 encoder takes
        string wav = Path.Combine(work, "mixed.wav");
        string mp3 = Path.Combine(Folder, Path.GetFileName(Folder) + ".mp3");
        {
            var (microphone, system) = Open();
            try
            {
                WaveFileWriter.CreateWaveFile16(wav, new Mix(microphone, system, gain));
                try
                {
                    using var input = new WaveFileReader(wav);
                    MediaFoundationEncoder.EncodeToMp3(input, mp3, Bitrate);
                }
                catch
                {
                    try { File.Delete(mp3); } catch { /* a half-written file: nothing is lost, the captures are kept */ }
                    throw;
                }
                OutputFile = mp3;
            }
            finally { microphone?.Dispose(); system?.Dispose(); }
        }

        // the moments marked with a bookmark, next to the recording
        if (Bookmarks.Count > 0)
        {
            string md = Path.Combine(Folder, Path.GetFileName(Folder) + ".md");
            File.WriteAllText(md, "# " + Path.GetFileName(Folder) + "\n\n- " + V.T("录制时间", "Recorded") + ": " + StartedAt!.Value.ToString("yyyy-MM-dd HH:mm") + "\n"
                + "- " + V.T("时长", "Length") + ": " + Clock(Math.Max(Bookmarks[^1], (DateTime.Now - StartedAt.Value).TotalSeconds)) + "\n\n"
                + "## " + V.T("重点时刻", "Marked moments") + "\n\n" + string.Join("", Bookmarks.Select(t => "- " + Clock(t) + "\n")));
        }
        try { Directory.Delete(work, recursive: true); } catch { /* the lossless files are simply left there */ }
        LastError = null;
        return true;
    }

    private static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss");

    /// <summary>One track of the meeting: its captures placed at the moments they began (silence in between).</summary>
    private sealed class TimelineTrack : ISampleProvider, IDisposable
    {
        private sealed class Piece : IDisposable
        {
            public required WaveFileReader Reader;
            public required ISampleProvider Source;
            public required long Start;     // frames
            public required long Frames;
            public void Dispose() => Reader.Dispose();
        }

        private readonly List<Piece> _pieces = [];
        private int _index;
        private long _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

        public TimelineTrack(List<Part> parts)
        {
            foreach (var part in parts.OrderBy(p => p.Offset))
            {
                var reader = new WaveFileReader(part.Path);
                ISampleProvider source = reader.ToSampleProvider();
                if (source.WaveFormat.Channels == 1) source = source.ToStereo();
                if (source.WaveFormat.SampleRate != Rate) source = new WdlResamplingSampleProvider(source, Rate);
                long frames = (long)(part.Seconds * Rate);
                if (frames <= 0) { reader.Dispose(); continue; }
                _pieces.Add(new Piece { Reader = reader, Source = source, Start = (long)(part.Offset * Rate), Frames = frames });
            }
        }

        public int Read(Span<float> buffer)
        {
            int frames = buffer.Length / 2, written = 0;
            while (written < frames && _index < _pieces.Count)
            {
                var piece = _pieces[_index];
                if (_position < piece.Start)
                {
                    int gap = (int)Math.Min(piece.Start - _position, frames - written);
                    buffer.Slice(written * 2, gap * 2).Clear();
                    _position += gap;
                    written += gap;
                    continue;
                }
                if (_position >= piece.Start + piece.Frames) { _index++; continue; }
                int want = (int)Math.Min(piece.Start + piece.Frames - _position, frames - written);
                int got = piece.Source.Read(buffer.Slice(written * 2, want * 2)) / 2;
                if (got <= 0) { _index++; continue; }
                _position += got;
                written += got;
            }
            return written * 2;
        }

        public void Dispose() { foreach (var piece in _pieces) piece.Dispose(); }
    }

    /// <summary>The microphone and the computer's sound summed, at one volume.</summary>
    private sealed class Mix(TimelineTrack? microphone, TimelineTrack? system, float gain) : ISampleProvider
    {
        private float[]? _micScratch, _systemScratch;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

        public int Read(Span<float> buffer)
        {
            _micScratch ??= new float[buffer.Length];
            _systemScratch ??= new float[buffer.Length];
            var mic = _micScratch.AsSpan(0, buffer.Length);
            var computer = _systemScratch.AsSpan(0, buffer.Length);
            mic.Clear();
            computer.Clear();
            int fromMic = microphone?.Read(mic) ?? 0;
            int fromSystem = system?.Read(computer) ?? 0;
            int n = Math.Max(fromMic, fromSystem);
            for (int i = 0; i < n; i++)
                buffer[i] = (float)(((i < fromMic ? mic[i] : 0) + (i < fromSystem ? computer[i] : 0)) * gain);
            return n;
        }
    }
}
