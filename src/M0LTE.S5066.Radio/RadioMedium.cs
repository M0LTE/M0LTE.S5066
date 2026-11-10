using M0LTE.Radio.Audio;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host.Audio;
using M0LTE.S5066.Host.Modem;
using M0LTE.S5066.Host.Station;
using Packet.SoundModem.Channel;

namespace M0LTE.S5066.Radio;

/// <summary>
/// A node's radio, put together from <see cref="RadioSettings"/> (core-architecture.md 12.11): the
/// sound card and CM108 PTT from pdn-soundmodem, the audio path (<see cref="RadioAudioChannel"/>),
/// the M0LTE.Ms110d adapter on it at the configured level, the optional busy detector and the
/// optional receive recording and the optional CW identification, all behind one
/// <see cref="IHostModem"/> for <c>NodeHost</c>.
/// </summary>
public sealed class RadioMedium : IDisposable
{
    private readonly WavRecorder? _recorder;

    private RadioMedium(RadioSettings settings, RadioAudioChannel audio, IHostModem modem, RadioBusy? busy, WavRecorder? recorder, CwIdSettings? cwId, CwIdSchedule? schedule)
    {
        Settings = settings;
        Audio = audio;
        Modem = modem;
        Busy = busy;
        _recorder = recorder;
        CwId = cwId;
        CwIdSchedule = schedule;
    }

    /// <summary>The identification settings, or null when the node sends no ID.</summary>
    public CwIdSettings? CwId { get; }

    /// <summary>The identification schedule, on the clock of <see cref="Open"/>'s time provider in UTC Unix ms, or null.</summary>
    public CwIdSchedule? CwIdSchedule { get; }

    /// <summary>The settings it was made from.</summary>
    public RadioSettings Settings { get; }

    /// <summary>The audio path.</summary>
    public RadioAudioChannel Audio { get; }

    /// <summary>The modem port to give <c>NodeHost</c>.</summary>
    public IHostModem Modem { get; }

    /// <summary>The busy detector, or null when busy_detect is off.</summary>
    public RadioBusy? Busy { get; }

    /// <summary>
    /// Opens the devices the settings name: the capture and playback devices through ALSA and the
    /// CM108's hidraw node, which is unkeyed at once.
    /// </summary>
    /// <exception cref="RadioException">A device could not be opened; the message says which and why.</exception>
    /// <param name="settings">The radio settings.</param>
    /// <param name="log">A sink for one-line log messages, or null.</param>
    /// <param name="cwId">The identification to key ahead of the node's transmissions, or null for none.</param>
    public static RadioMedium Open(RadioSettings settings, Action<string>? log = null, CwIdSettings? cwId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var opened = new List<IDisposable>();
        try
        {
            var input = Opening("capture_device", settings.CaptureDevice, () => new AlsaAudioInput(settings.CaptureDevice, settings.SampleRate));
            opened.Add(input);
            var output = Opening("playback_device", settings.PlaybackDevice, () => new AlsaAudioOutput(settings.PlaybackDevice, settings.SampleRate));
            opened.Add(output);
            var ptt = Opening("ptt_device", settings.PttDevice, () => new Cm108Ptt(settings.PttDevice, settings.PttGpio));
            opened.Add(ptt);
            foreach (string? warning in new[] { input.ConfigurationWarning, output.ConfigurationWarning })
            {
                if (warning is not null)
                {
                    log?.Invoke("radio: " + warning);
                }
            }

            return Create(settings, input, output, ptt, log, () => input.Xruns, () => output.Xruns, cwId);
        }
        catch
        {
            foreach (IDisposable d in opened)
            {
                d.Dispose();
            }

            throw;
        }
    }

    /// <summary>Puts the medium together on devices already open; for tests, with fakes.</summary>
    internal static RadioMedium Create(RadioSettings settings, IAudioInput input, IAudioOutput output, IPttControl ptt, Action<string>? log, Func<int>? captureXruns = null, Func<int>? playbackXruns = null,
        CwIdSettings? cwId = null, TimeProvider? clock = null)
    {
        var audio = new RadioAudioChannel(input, output, ptt, settings.TxDelayMs, settings.TxTailMs, log)
        {
            CaptureXruns = captureXruns,
            PlaybackXruns = playbackXruns,
        };
        WavRecorder? recorder = null;
        if (settings.RxRecord is { } path)
        {
            try
            {
                recorder = new WavRecorder(path, IAudioChannel.SampleRate);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                audio.Dispose();
                throw new RadioException($"host.modem.radio.rx_record: cannot write {path}: {e.Message}");
            }

            audio.Heard += chunk => recorder.Write(chunk.Span);
        }

        RadioBusy? busy = null;
        if (settings.BusyDetect)
        {
            busy = new RadioBusy(IAudioChannel.SampleRate);
            audio.Heard += chunk => busy.Process(chunk.Span);
            audio.Keyed += busy.SetKeyed;
        }

        CwIdSchedule? schedule = null;
        if (cwId is not null)
        {
            // Made once: the text never changes while the node runs.
            float[] id = Morse.Generate(cwId.Text, cwId.Wpm, cwId.ToneHz, cwId.Level ?? settings.TxLevel, settings.SampleRate);
            schedule = new CwIdSchedule(cwId.AtStart, cwId.EveryMinutes);
            TimeProvider time = clock ?? TimeProvider.System;
            audio.Identification = () =>
            {
                if (!schedule.Keyup(time.GetUtcNow().ToUnixTimeMilliseconds()))
                {
                    return null;
                }

                log?.Invoke($"radio: cw id \"{cwId.Text}\" ({cwId.DurationMs / 1000.0:0.0} s) before this transmission");
                return id;
            };
        }

        var modem = new RadioHostModem(new Ms110dHostModem(audio, (float)settings.TxLevel, log), busy, log);
        return new RadioMedium(settings, audio, modem, busy, recorder, cwId, schedule);
    }

    /// <summary>
    /// Settings that are accepted but probably wrong for this radio, one line each. Today one: with
    /// busy_detect off, the far node senses a burst only once its modem has the preamble, which here
    /// comes TXDELAY after the key; a contention slot no longer than that makes contending nodes
    /// collide (K-0048), as <c>NodeHost.ConfigWarnings</c> says for the preamble alone.
    /// </summary>
    public static IReadOnlyList<string> Warnings(RadioSettings settings, NodeConfig node, IModemPort modem)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(modem);
        var warnings = new List<string>();
        int preamble = modem.PlanBurst(node.Drc.DefaultSpeed.Wn, node.Drc.DefaultSpeed.Interleaver, 0).PreambleMs;
        int sensed = settings.TxDelayMs + preamble;
        if (!settings.BusyDetect && node.Csma.ContSlotWidthMs > preamble && node.Csma.ContSlotWidthMs <= sensed)
        {
            warnings.Add($"warning: csma.cont_slot_width_ms {node.Csma.ContSlotWidthMs} is not longer than txdelay_ms {settings.TxDelayMs} plus the {preamble} ms preamble, the soonest the far node can detect a burst with busy_detect off; use a longer slot (K-0048)");
        }

        return warnings;
    }

    /// <summary>Stops the audio, unkeys and closes the devices and the recording.</summary>
    public void Dispose()
    {
        Audio.Dispose();
        _recorder?.Dispose();
    }

    private static T Opening<T>(string key, string device, Func<T> open)
    {
        try
        {
            return open();
        }
        catch (DllNotFoundException)
        {
            throw new RadioException("cannot load libasound.so.2, the ALSA library: install it (Debian: apt install libasound2t64)");
        }
        catch (UnauthorizedAccessException e)
        {
            throw new RadioException($"host.modem.radio.{key}: no permission to open {device} ({e.Message}); see docs/s5066d-radio.md, \"Permissions\"");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            throw new RadioException($"host.modem.radio.{key}: cannot open {device}: {e.Message}");
        }
    }
}

/// <summary>A radio device could not be opened or used; the message says which and why.</summary>
public sealed class RadioException(string message) : Exception(message);
