using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace M0LTE.S5066.Host.Audio;

/// <summary>
/// Audio over UDP: 9600 Hz mono, little-endian 32-bit float, one datagram per 20 ms. It sends a
/// continuous stream to a peer (silence between bursts), so two hosts on different machines, or
/// a host and an audio bridge, can be joined without a sound card. Received datagrams are passed
/// on as they arrive; the sender's pacing is the receiver's clock.
/// </summary>
public sealed class UdpAudioChannel : IAudioChannel, IDisposable
{
    private const int ChunkSamples = IAudioChannel.SampleRate / 50;

    private readonly UdpClient _udp;
    private readonly IPEndPoint _peer;
    private readonly Queue<(float[] Samples, Action Played)> _queue = new();
    private readonly object _rxGate = new();
    private readonly CancellationTokenSource _stop = new();
    private Action<ReadOnlyMemory<float>>? _received;
    private Thread? _sender;
    private Task? _receiver;

    /// <summary>Creates the channel.</summary>
    /// <param name="listen">Local endpoint to receive on.</param>
    /// <param name="peer">Endpoint to send to.</param>
    public UdpAudioChannel(IPEndPoint listen, IPEndPoint peer)
    {
        ArgumentNullException.ThrowIfNull(listen);
        ArgumentNullException.ThrowIfNull(peer);
        _udp = new UdpClient(listen);
        _peer = peer;
    }

    /// <inheritdoc/>
    public void Start(Action<ReadOnlyMemory<float>> received)
    {
        _received = received;
        _sender = new Thread(SendLoop) { IsBackground = true, Name = "UdpAudio TX" };
        _sender.Start();
        _receiver = Task.Run(ReceiveLoopAsync);
    }

    /// <inheritdoc/>
    public void Play(float[] samples, Action played)
    {
        lock (_queue)
        {
            _queue.Enqueue((samples, played));
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stop.Cancel();
        _sender?.Join();
        _udp.Dispose();
        try
        {
            _receiver?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }

    private void SendLoop()
    {
        var clock = Stopwatch.StartNew();
        long ticks = 0;
        float[]? current = null;
        Action? currentPlayed = null;
        int pos = 0;
        var chunk = new float[ChunkSamples];
        while (!_stop.IsCancellationRequested)
        {
            if (ticks >= clock.ElapsedMilliseconds / 20)
            {
                Thread.Sleep(2);
                continue;
            }

            ticks++;
            Array.Clear(chunk);
            Action? done = null;
            int n = 0;
            lock (_queue)
            {
                while (n < ChunkSamples)
                {
                    if (current is null)
                    {
                        if (!_queue.TryDequeue(out var next))
                        {
                            break;
                        }

                        (current, currentPlayed, pos) = (next.Samples, next.Played, 0);
                    }

                    int take = Math.Min(ChunkSamples - n, current.Length - pos);
                    current.AsSpan(pos, take).CopyTo(chunk.AsSpan(n));
                    pos += take;
                    n += take;
                    if (pos == current.Length)
                    {
                        done += currentPlayed;
                        current = null;
                    }
                }
            }

            try
            {
                _udp.Send(MemoryMarshal.AsBytes(chunk.AsSpan()), _peer);
            }
            catch (SocketException)
            {
                // A peer that is not up yet; the stream carries on.
            }

            if (done is not null)
            {
                // Played is raised between two receive callbacks, as IAudioChannel asks.
                lock (_rxGate)
                {
                    done();
                }
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await _udp.ReceiveAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (_stop.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            float[] samples = MemoryMarshal.Cast<byte, float>(r.Buffer.AsSpan(0, r.Buffer.Length & ~3)).ToArray();
            lock (_rxGate)
            {
                _received?.Invoke(samples);
            }
        }
    }
}
