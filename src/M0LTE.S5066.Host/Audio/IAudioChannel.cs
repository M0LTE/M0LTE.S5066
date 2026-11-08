namespace M0LTE.S5066.Host.Audio;

/// <summary>
/// A mono audio path at <see cref="SampleRate"/> for an audio modem such as
/// <see cref="Modem.Ms110dHostModem"/> (core-architecture.md 12.6). Received audio arrives as a
/// continuous, real-time stream of chunks; transmitted audio is queued whole, a burst at a time.
/// </summary>
public interface IAudioChannel
{
    /// <summary>The sample rate of both directions: 9600 Hz, the MS110D modem's native rate.</summary>
    public const int SampleRate = 9600;

    /// <summary>
    /// Starts the channel. <paramref name="received"/> is called with each chunk of received audio,
    /// in order, always from one thread at a time, for as long as the channel runs, silence included.
    /// </summary>
    void Start(Action<ReadOnlyMemory<float>> received);

    /// <summary>
    /// Plays one burst. Returns at once; <paramref name="played"/> is called once the last sample
    /// has left the output (with any known output latency), from the thread that calls
    /// <c>received</c>, between two of its calls.
    /// </summary>
    void Play(float[] samples, Action played);
}
