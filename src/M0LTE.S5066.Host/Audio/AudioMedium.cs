using System.Diagnostics;

namespace M0LTE.S5066.Host.Audio;

/// <summary>
/// An in-process audio channel shared by several stations, in real time (core-architecture.md
/// 12.8): every 20 ms each station hears the sum of what the other stations are playing, plus a
/// little Gaussian noise. A test aid for running two hosts over the real modem in one process.
/// </summary>
public sealed class AudioMedium : IDisposable
{
    /// <summary>Samples per 20 ms tick at 9600 Hz.</summary>
    public const int ChunkSamples = IAudioChannel.SampleRate / 50;

    private readonly object _gate = new();
    private readonly List<Station> _stations = [];
    private readonly Random _noise;
    private readonly float _noiseRms;
    private Thread? _thread;
    private volatile bool _stop;

    /// <summary>Creates the medium.</summary>
    /// <param name="noiseRms">RMS of the added noise, as a fraction of full scale. The modulator's default amplitude is 0.5.</param>
    /// <param name="seed">Seed of the noise generator.</param>
    public AudioMedium(float noiseRms = 0.005f, int seed = 1)
    {
        _noiseRms = noiseRms;
        _noise = new Random(seed);
    }

    /// <summary>Adds a station. Stations added after the medium starts join at the next tick.</summary>
    public IAudioChannel CreateChannel()
    {
        var s = new Station(this);
        lock (_gate)
        {
            _stations.Add(s);
        }

        return s;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stop = true;
        _thread?.Join();
    }

    private void EnsureRunning()
    {
        lock (_gate)
        {
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "AudioMedium" };
                _thread.Start();
            }
        }
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        long ticks = 0;
        while (!_stop)
        {
            long due = clock.ElapsedMilliseconds / 20;
            if (ticks >= due)
            {
                Thread.Sleep(2);
                continue;
            }

            ticks++;
            Station[] stations;
            lock (_gate)
            {
                stations = [.. _stations];
            }

            var outputs = new float[stations.Length][];
            var finished = new List<Action>();
            for (int i = 0; i < stations.Length; i++)
            {
                outputs[i] = stations[i].NextOutput(finished);
            }

            for (int i = 0; i < stations.Length; i++)
            {
                if (stations[i].Received is not { } rx)
                {
                    continue;
                }

                float[] input = new float[ChunkSamples];
                for (int n = 0; n < ChunkSamples; n++)
                {
                    input[n] = _noiseRms * Gaussian();
                }

                for (int j = 0; j < stations.Length; j++)
                {
                    if (j != i)
                    {
                        float[] o = outputs[j];
                        for (int n = 0; n < ChunkSamples; n++)
                        {
                            input[n] += o[n];
                        }
                    }
                }

                rx(input);
            }

            foreach (Action a in finished)
            {
                a();
            }
        }
    }

    private float Gaussian()
    {
        double u1 = 1.0 - _noise.NextDouble();
        double u2 = _noise.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
    }

    private sealed class Station(AudioMedium medium) : IAudioChannel
    {
        private readonly Queue<(float[] Samples, Action Played)> _queue = new();
        private float[]? _current;
        private Action? _currentPlayed;
        private int _pos;

        public Action<ReadOnlyMemory<float>>? Received { get; private set; }

        public void Start(Action<ReadOnlyMemory<float>> received)
        {
            Received = received;
            medium.EnsureRunning();
        }

        public void Play(float[] samples, Action played)
        {
            lock (_queue)
            {
                _queue.Enqueue((samples, played));
            }
        }

        public float[] NextOutput(List<Action> finished)
        {
            var o = new float[ChunkSamples];
            int n = 0;
            lock (_queue)
            {
                while (n < ChunkSamples)
                {
                    if (_current is null)
                    {
                        if (!_queue.TryDequeue(out var next))
                        {
                            break;
                        }

                        (_current, _currentPlayed, _pos) = (next.Samples, next.Played, 0);
                    }

                    int take = Math.Min(ChunkSamples - n, _current.Length - _pos);
                    _current.AsSpan(_pos, take).CopyTo(o.AsSpan(n));
                    _pos += take;
                    n += take;
                    if (_pos == _current.Length)
                    {
                        finished.Add(_currentPlayed!);
                        _current = null;
                    }
                }
            }

            return o;
        }
    }
}
