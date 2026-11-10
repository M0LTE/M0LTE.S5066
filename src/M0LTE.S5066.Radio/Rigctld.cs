using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace M0LTE.S5066.Radio;

/// <summary>
/// CAT control of the radio through Hamlib's rigctld, over its plain text TCP protocol
/// (core-architecture.md 12.13). Hamlib is not linked: rigctld is the operator's to run, with the
/// model number of their radio.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>On each connection, the first and every reconnection: unkey (when PTT is by CAT), set
/// the frequency, then the mode with a 3 kHz passband (again with the radio's normal passband if
/// that is refused), read both back and log one line saying what the radio set.</item>
/// <item>A lost or unreachable rigctld is one log line, then a quiet retry every few seconds; it
/// never stops the node. While it is gone, <see cref="Key"/> refuses, so a CAT-keyed node does
/// not transmit.</item>
/// <item>One request at a time: each command is sent and its reply read under one lock.</item>
/// </list>
/// </remarks>
public sealed class Rigctld : IDisposable
{
    private readonly CatSettings _settings;
    private readonly bool _ptt;
    private readonly Action<string>? _log;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _retry;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Link? _link;
    private bool _downLogged;

    // Whether a T 1 may have reached the radio since the last T 0 rigctld confirmed: set before
    // each key is sent, cleared by a confirmed unkey. With no connection and this clear, an
    // unkey has nothing to undo, so it does not open a connection just to say so.
    private volatile bool _mayBeKeyed;
    private Thread? _loop;
    private int _disposed;

    /// <summary>Creates the client; nothing is sent until <see cref="Start"/>.</summary>
    /// <param name="settings">Where rigctld is and what to set.</param>
    /// <param name="pttByCat">Whether PTT goes through rigctld, so each connection starts by unkeying.</param>
    /// <param name="log">A sink for one-line log messages, or null.</param>
    public Rigctld(CatSettings settings, bool pttByCat, Action<string>? log)
        : this(settings, pttByCat, log, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5))
    {
    }

    /// <summary>Creates the client with other timings, for tests.</summary>
    internal Rigctld(CatSettings settings, bool pttByCat, Action<string>? log, TimeSpan timeout, TimeSpan retry)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _ptt = pttByCat;
        _log = log;
        _timeout = timeout;
        _retry = retry;
    }

    /// <summary>The settings it was made from.</summary>
    public CatSettings Settings => _settings;

    /// <summary>Whether rigctld is connected and the radio set up.</summary>
    public bool Connected => Volatile.Read(ref _link) is not null;

    /// <summary>
    /// Connects and sets the radio up, waiting for that first try so its log line comes before the
    /// node starts, then keeps the connection: a lost one is retried in the background.
    /// </summary>
    public void Start()
    {
        TryConnect();
        _loop = new Thread(Loop) { IsBackground = true, Name = "rigctld" };
        _loop.Start();
    }

    /// <summary>Keys the radio through rigctld.</summary>
    /// <exception cref="IOException">rigctld is not connected, or the key failed; the radio is not keyed.</exception>
    public void Key()
    {
        lock (_gate)
        {
            Link link = _link ?? throw new IOException($"CAT PTT: no answer from rigctld at {_settings.Address}, so not transmitting");
            int code;
            try
            {
                _mayBeKeyed = true;
                code = link.Set("T 1");
            }
            catch (IOException e)
            {
                Lose(e);
                throw new IOException($"CAT PTT: lost rigctld at {_settings.Address} ({e.Message}), so not transmitting", e);
            }

            if (code != 0)
            {
                throw new IOException($"CAT PTT: rigctld refused to key the radio (RPRT {code})");
            }
        }
    }

    /// <summary>
    /// Unkeys the radio through rigctld; with no connection, through one made just for this, so
    /// a connection that dropped while keyed is not the end of it. Works after <see cref="Dispose"/>.
    /// With no connection and no key sent since rigctld last confirmed an unkey, there is nothing
    /// to undo and nothing is sent.
    /// </summary>
    /// <exception cref="IOException">The unkey could not be sent or was refused.</exception>
    public void Unkey()
    {
        lock (_gate)
        {
            if (_link is { } link)
            {
                try
                {
                    Refused(link.Set("T 0"));
                    _mayBeKeyed = false;
                    return;
                }
                catch (RefusedException)
                {
                    throw;
                }
                catch (IOException e)
                {
                    Lose(e);
                }
            }

            if (!_mayBeKeyed)
            {
                return;
            }

            try
            {
                using Link once = Link.Open(_settings, _timeout);
                Refused(once.Set("T 0"));
                _mayBeKeyed = false;
            }
            catch (Exception e) when (e is IOException or SocketException && e is not RefusedException)
            {
                throw new IOException($"CAT PTT: cannot reach rigctld at {_settings.Address} to unkey ({e.Message}); check the radio is not left transmitting", e);
            }
        }

        static void Refused(int code)
        {
            if (code != 0)
            {
                throw new RefusedException($"CAT PTT: rigctld refused to unkey the radio (RPRT {code}); check the radio is not left transmitting");
            }
        }
    }

    /// <summary>Stops the retries and closes the connection. Does not unkey: the audio path does that first.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _loop?.Join(_timeout + _timeout);
        lock (_gate)
        {
            _link?.Dispose();
            _link = null;
        }
    }

    private void Loop()
    {
        while (!_stop.Token.WaitHandle.WaitOne(_retry))
        {
            if (Volatile.Read(ref _link) is null)
            {
                TryConnect();
            }
            else
            {
                lock (_gate)
                {
                    // rigctld gone while idle: noticed now rather than at the next key.
                    if (_link is { } link && link.PeerClosed())
                    {
                        Lose(new IOException("rigctld closed the connection"));
                    }
                }
            }
        }
    }

    private void TryConnect()
    {
        Link link;
        string line;
        try
        {
            link = Link.Open(_settings, _timeout);
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
            Down(e.Message);
            return;
        }

        try
        {
            line = SetUp(link);
        }
        catch (IOException e)
        {
            link.Dispose();
            Down(e.Message);
            return;
        }

        lock (_gate)
        {
            if (_stop.IsCancellationRequested)
            {
                link.Dispose();
                return;
            }

            _link = link;
            _downLogged = false;
        }

        _log?.Invoke(line);
    }

    /// <summary>Under the lock: drops a failed connection, saying so once.</summary>
    private void Lose(IOException e)
    {
        _link?.Dispose();
        _link = null;
        Down(e.Message);
    }

    private void Down(string reason)
    {
        if (_downLogged || _stop.IsCancellationRequested)
        {
            return;
        }

        _downLogged = true;
        _log?.Invoke($"radio: cat: no rigctld at {_settings.Address} ({reason}); trying again every {_retry.TotalSeconds:0.#} s"
            + (_ptt ? ", and not transmitting until it answers" : ""));
    }

    /// <summary>Unkeys, sets frequency, mode and passband, reads them back: the one line to log.</summary>
    private string SetUp(Link link)
    {
        var notes = new List<string>();
        if (_ptt)
        {
            if (link.Set("T 0") is int unkey and not 0)
            {
                notes.Add($"unkey refused (RPRT {unkey})");
            }
            else
            {
                _mayBeKeyed = false;
            }
        }

        if (link.Set(FormattableString.Invariant($"F {_settings.FrequencyHz}")) is int f and not 0)
        {
            notes.Add($"frequency refused (RPRT {f})");
        }

        int m = link.Set(FormattableString.Invariant($"M {CatSettings.Mode} {CatSettings.PassbandHz}"));
        if (m != 0)
        {
            // Some backends refuse a width they cannot set; 0 asks for the mode's normal one.
            int normal = link.Set($"M {CatSettings.Mode} 0");
            notes.Add(normal == 0 ? $"{CatSettings.PassbandHz} Hz passband refused (RPRT {m})" : $"mode {CatSettings.Mode} refused (RPRT {normal})");
        }

        string[]? freq = link.Get("f", 1);
        string[]? mode = link.Get("m", 2);
        var sb = new StringBuilder($"radio: cat: rigctld {_settings.Address}: ");
        sb.Append(freq is [var fr] && double.TryParse(fr, NumberStyles.Float, CultureInfo.InvariantCulture, out double hz)
            ? FormattableString.Invariant($"{hz:0} Hz")
            : "frequency not read back");
        if (freq is [var got] && double.TryParse(got, NumberStyles.Float, CultureInfo.InvariantCulture, out double gotHz) && Math.Abs(gotHz - _settings.FrequencyHz) >= 1)
        {
            sb.Append(CultureInfo.InvariantCulture, $" (asked for {_settings.FrequencyHz})");
        }

        if (mode is [var md, var pb])
        {
            sb.Append(", ").Append(md);
            if (md != CatSettings.Mode)
            {
                sb.Append(" (asked for ").Append(CatSettings.Mode).Append(')');
            }

            if (int.TryParse(pb, NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) && width > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $", passband {width} Hz");
                if (width != CatSettings.PassbandHz)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" (asked for {CatSettings.PassbandHz}; the radio set this)");
                }
            }
            else
            {
                sb.Append(", passband not reported");
            }
        }
        else
        {
            sb.Append(", mode not read back");
        }

        if (_ptt)
        {
            sb.Append(", PTT by CAT");
        }

        foreach (string note in notes)
        {
            sb.Append("; ").Append(note);
        }

        return sb.ToString();
    }

    private sealed class RefusedException(string message) : IOException(message);

    /// <summary>One TCP connection to rigctld.</summary>
    private sealed class Link : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly StreamReader _reader;
        private readonly NetworkStream _stream;

        private Link(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            _reader = new StreamReader(_stream, Encoding.ASCII, false, 256, leaveOpen: true);
        }

        public static Link Open(CatSettings settings, TimeSpan timeout)
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                try
                {
                    tcp.ConnectAsync(settings.Host, settings.Port, cts.Token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    throw new IOException($"no connection within {timeout.TotalSeconds:0.#} s");
                }

                tcp.ReceiveTimeout = (int)timeout.TotalMilliseconds;
                tcp.SendTimeout = (int)timeout.TotalMilliseconds;
                return new Link(tcp);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        /// <summary>A set command: rigctld's RPRT code, 0 for done.</summary>
        public int Set(string command)
        {
            string line = Exchange(command, 1)[0];
            return Code(line) ?? throw new IOException($"unexpected reply to {command.Split(' ')[0]}: {line}");
        }

        /// <summary>A get command: its reply lines, or null when rigctld answered with an error.</summary>
        public string[]? Get(string command, int lines)
        {
            string[] reply = Exchange(command, lines);
            return Code(reply[0]) is not null ? null : reply;
        }

        /// <summary>Whether rigctld has closed its end, without sending anything.</summary>
        public bool PeerClosed()
        {
            try
            {
                return _tcp.Client.Poll(0, SelectMode.SelectRead) && _tcp.Client.Available == 0;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                return true;
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _tcp.Dispose();
        }

        private static int? Code(string line) =>
            line.StartsWith("RPRT ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(5), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code) ? code : null;

        /// <summary>Sends one command and reads its reply: the given number of lines, or one RPRT line.</summary>
        private string[] Exchange(string command, int lines)
        {
            try
            {
                byte[] bytes = Encoding.ASCII.GetBytes(command + "\n");
                _stream.Write(bytes);
                var reply = new string[lines];
                for (int i = 0; i < lines; i++)
                {
                    reply[i] = _reader.ReadLine()?.Trim() ?? throw new IOException("rigctld closed the connection");
                    if (i == 0 && Code(reply[0]) is not null)
                    {
                        return [reply[0]];
                    }
                }

                return reply;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                throw new IOException(e.Message, e);
            }
        }
    }
}

/// <summary>PTT through rigctld (<c>ptt: "cat"</c>), for the radio's audio path.</summary>
internal sealed class CatPtt(Rigctld rig) : M0LTE.Radio.Audio.IPttControl
{
    public void Key() => rig.Key();

    public void Unkey() => rig.Unkey();
}
