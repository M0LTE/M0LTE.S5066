using System.Collections.Concurrent;
using M0LTE.Radio.Audio;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host.Modem;

namespace M0LTE.S5066.Radio.Tests;

/// <summary>What happened, in order, across the fake devices.</summary>
internal sealed class EventLog
{
    private readonly List<string> _events = [];

    public void Add(string e)
    {
        lock (_events)
        {
            _events.Add(e);
        }
    }

    /// <summary>The events so far, with each run of writes shown as one "write N" of their total.</summary>
    public string[] Snapshot()
    {
        lock (_events)
        {
            var collapsed = new List<string>();
            int run = -1;
            foreach (string e in _events)
            {
                if (e.StartsWith("write ", StringComparison.Ordinal))
                {
                    int n = int.Parse(e["write ".Length..], System.Globalization.CultureInfo.InvariantCulture);
                    if (run >= 0)
                    {
                        n += run;
                        collapsed.RemoveAt(collapsed.Count - 1);
                    }

                    run = n;
                    collapsed.Add($"write {n}");
                }
                else
                {
                    run = -1;
                    collapsed.Add(e);
                }
            }

            return [.. collapsed];
        }
    }
}

/// <summary>A capture device that returns queued audio, then blocks until more is queued or it is disposed.</summary>
internal sealed class FakeInput(int sampleRate) : IAudioInput, IDisposable
{
    private readonly BlockingCollection<float[]> _chunks = new();
    private float[] _current = [];
    private int _pos;

    public int SampleRate { get; } = sampleRate;

    public bool ThrowOnRead { get; set; }

    public void Queue(float[] samples)
    {
        // In 20 ms reads, as a card would deliver it.
        int chunk = SampleRate / 50;
        for (int i = 0; i < samples.Length; i += chunk)
        {
            _chunks.Add(samples.AsSpan(i, Math.Min(chunk, samples.Length - i)).ToArray());
        }
    }

    public int Read(Span<float> destination)
    {
        if (ThrowOnRead)
        {
            throw new InvalidOperationException("snd_pcm_readi: No such device");
        }

        if (_pos == _current.Length)
        {
            try
            {
                _current = _chunks.Take();
            }
            catch (InvalidOperationException)
            {
                return 0;
            }

            _pos = 0;
        }

        int n = Math.Min(destination.Length, _current.Length - _pos);
        _current.AsSpan(_pos, n).CopyTo(destination);
        _pos += n;
        return n;
    }

    public void Dispose() => _chunks.CompleteAdding();
}

/// <summary>A playback device that keeps what it was given.</summary>
internal sealed class FakeOutput(int sampleRate, EventLog log) : IAudioOutput
{
    private readonly List<float> _written = [];

    public int SampleRate { get; } = sampleRate;

    public bool Fail { get; set; }

    /// <summary>When set, each write takes as long as its audio lasts, as a real card's does.</summary>
    public bool RealTime { get; set; }

    public float[] Written
    {
        get
        {
            lock (_written)
            {
                return [.. _written];
            }
        }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        if (Fail)
        {
            throw new InvalidOperationException("snd_pcm_writei: No such device");
        }

        lock (_written)
        {
            _written.AddRange(samples.ToArray());
        }

        log.Add($"write {samples.Length}");
        if (RealTime)
        {
            Thread.Sleep(samples.Length * 1000 / SampleRate);
        }
    }

    public void Drain() => log.Add("drain");
}

/// <summary>A PTT line that logs, and can be made to fail or to wait on a gate before keying.</summary>
internal sealed class FakePtt(EventLog log) : IPttControl
{
    public bool Fail { get; set; }

    public ManualResetEventSlim? Gate { get; set; }

    public void Key()
    {
        Gate?.Wait(TimeSpan.FromSeconds(10));
        if (Fail)
        {
            throw new IOException("hidraw gone");
        }

        log.Add("key");
    }

    public void Unkey() => log.Add("unkey");
}

/// <summary>Modem events, kept.</summary>
internal sealed class RecordingEvents : IModemEvents
{
    public ConcurrentQueue<string> Events { get; } = new();

    public List<byte> Bytes { get; } = [];

    public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void BurstStarted() => Events.Enqueue("started");

    public void BytesReceived(ReadOnlyMemory<byte> bytes)
    {
        lock (Bytes)
        {
            Bytes.AddRange(bytes.Span);
        }

        Events.Enqueue("bytes");
    }

    public void BurstEnded(BurstEndReason reason, int wn, Interleaver il, int k, int? snrTenthDb, int blocks)
    {
        Events.Enqueue($"ended {reason} wn{wn} {il}");
        Ended.TrySetResult();
    }

    public void TransmitDone()
    {
        Events.Enqueue("done");
        Done.TrySetResult();
    }

    public void ChannelBusy(bool busy) => Events.Enqueue($"busy {busy}");
}

internal static class Signals
{
    public static float[] Tone(int rate, double hz, double seconds, double amplitude)
    {
        var s = new float[(int)(rate * seconds)];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        }

        return s;
    }

    public static float[] Noise(int rate, double seconds, double rms, int seed)
    {
        var random = new Random(seed);
        var s = new float[(int)(rate * seconds)];
        for (int i = 0; i < s.Length; i++)
        {
            // Box-Muller.
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            s[i] = (float)(rms * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        return s;
    }

    /// <summary>The frequency of a tone from its zero crossings.</summary>
    public static double Frequency(ReadOnlySpan<float> s, int rate)
    {
        int first = -1, last = -1, crossings = 0;
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i - 1] < 0 && s[i] >= 0)
            {
                if (first < 0)
                {
                    first = i;
                }
                else
                {
                    crossings++;
                }

                last = i;
            }
        }

        return crossings * (double)rate / (last - first);
    }

    public static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (float x in s)
        {
            sum += x * x;
        }

        return Math.Sqrt(sum / s.Length);
    }
}
