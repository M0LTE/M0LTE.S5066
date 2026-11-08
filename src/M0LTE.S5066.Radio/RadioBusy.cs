using M0LTE.Dsp;
using Packet.SoundModem.Modems;

namespace M0LTE.S5066.Radio;

/// <summary>
/// Whether somebody else is on the channel, from in-band energy (core-architecture.md 12.11): the
/// received audio, band-passed to the 3 kHz channel, into pdn-soundmodem's
/// <see cref="EnergyBusyDetector"/>, which compares short-term power with a slowly adapting noise
/// floor. Our own transmission is left out, and so is a short tail after each unkey while the
/// receiver recovers; neither is fed to the detector, so neither moves its noise floor.
/// </summary>
/// <remarks>
/// It suits an additive path such as SSB, where a signal raises the power in the passband; the
/// detector's own notes explain why it does not suit open-squelch FM. It is the node's external
/// busy input (K-0006), never DCD: that comes from the modem alone.
/// </remarks>
public sealed class RadioBusy
{
    /// <summary>How long after our own unkey the channel's energy is not taken as somebody else's, as pdn-soundmodem's s5066 busy input does.</summary>
    public const int AfterKeyupMs = 250;

    private readonly int _rate;
    private readonly EnergyBusyDetector _detector;
    private readonly FirFilter _bandpass;
    private readonly object _gate = new();
    private bool _keyed;
    private long _quietSamples = long.MaxValue;
    private bool _reported;

    /// <summary>Creates the detector for audio at <paramref name="sampleRate"/>.</summary>
    public RadioBusy(int sampleRate)
    {
        _rate = sampleRate;
        _detector = new EnergyBusyDetector(sampleRate);
        // The 110D channel, as pdn-soundmodem's MS110D modem filters its own busy detector.
        _bandpass = new FirFilter(FilterDesign.BandPass(180, 3420, sampleRate, 96));
    }

    /// <summary>Raised with each change of the answer, on the thread that called <see cref="Process"/> or <see cref="SetKeyed"/>.</summary>
    public event Action<bool>? Changed;

    /// <summary>The answer now.</summary>
    public bool Busy
    {
        get
        {
            lock (_gate)
            {
                return _reported;
            }
        }
    }

    /// <summary>Our transmitter was keyed (true) or unkeyed (false).</summary>
    public void SetKeyed(bool keyed)
    {
        bool raise;
        lock (_gate)
        {
            _keyed = keyed;
            _quietSamples = 0;
            raise = Decide();
        }

        if (raise)
        {
            Changed?.Invoke(false);
        }
    }

    /// <summary>Takes one chunk of received audio.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        bool raise;
        bool busy;
        lock (_gate)
        {
            if (_keyed)
            {
                return;
            }

            long settle = (long)_rate * AfterKeyupMs / 1000;
            if (_quietSamples < settle)
            {
                // Still settling after our unkey: not fed, and not counted.
                _quietSamples += samples.Length;
                return;
            }

            foreach (float s in samples)
            {
                _detector.Process(_bandpass.Next(s));
            }

            raise = Decide();
            busy = _reported;
        }

        if (raise)
        {
            Changed?.Invoke(busy);
        }
    }

    /// <summary>Recomputes the answer under the lock; true when it changed.</summary>
    private bool Decide()
    {
        long settle = (long)_rate * AfterKeyupMs / 1000;
        bool busy = !_keyed && _quietSamples >= settle && _detector.Busy;
        if (busy == _reported)
        {
            return false;
        }

        _reported = busy;
        return true;
    }
}
