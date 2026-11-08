namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// One input in, run to completion (core-architecture.md 3.5). The four rules, which bind the C++
/// port too:
/// <list type="number">
/// <item>Fixed entry order: each input kind is handed to the sublayers in the order of the table in 3.5.</item>
/// <item>Run to completion with a FIFO of primitives: after the entry calls the FIFO is drained in order until empty.</item>
/// <item>Per-D_PDU processing order inside the DTS (the DTS's own job).</item>
/// <item>The MAC acts last: <see cref="IMacSublayer.AfterInput"/> once per input, after the FIFO is empty.</item>
/// </list>
/// </summary>
internal sealed class Dispatcher
{
    private readonly CoreContext _context;
    private readonly ISisSublayer _sis;
    private readonly ICasSublayer _cas;
    private readonly IDtsSublayer _dts;
    private readonly IMacSublayer _mac;
    private bool _handling;
    private bool _draining;
    private bool _inAfterInput;

    public Dispatcher(CoreContext context, ISisSublayer sis, ICasSublayer cas, IDtsSublayer dts, IMacSublayer mac)
    {
        _context = context;
        _sis = sis;
        _cas = cas;
        _dts = dts;
        _mac = mac;
        _context.DrainHandler = DrainOnRequest;
    }

    public void Handle(CoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_handling)
        {
            throw new InvalidOperationException("an input was handed to the core while it was handling another");
        }

        if (input.T < _context.Now)
        {
            throw new InvalidOperationException($"input time {input.T} is before the core's time {_context.Now}");
        }

        _handling = true;
        try
        {
            _context.Now = input.T;
            Enter(input);
            Drain();
            _inAfterInput = true;
            try
            {
                _mac.AfterInput();
            }
            finally
            {
                _inAfterInput = false;
            }

            if (_context.HasPendingPrimitives)
            {
                throw new InvalidOperationException("a primitive was enqueued after the MAC acted; the MAC must act last");
            }
        }
        finally
        {
            _handling = false;
        }
    }

    /// <summary>The entry table of core-architecture.md 3.5 rule 1.</summary>
    private void Enter(CoreInput input)
    {
        switch (input)
        {
            case ConfigInput c:
            {
                NodeConfig old = _context.Config;
                _context.Config = c.NewConfig ?? throw new ArgumentException("config input without a configuration", nameof(input));
                Quiet("OnConfigChanged", () =>
                {
                    _sis.OnConfigChanged(old, c.NewConfig);
                    _cas.OnConfigChanged(old, c.NewConfig);
                    _dts.OnConfigChanged(old, c.NewConfig);
                    _mac.OnConfigChanged(old, c.NewConfig);
                });
                break;
            }

            case TimerExpired e:
                _context.Timers.Expire(e.Name, _context.Now);
                if (e.Name.Owner == Sublayer.Mac)
                {
                    Quiet("the MAC's OnTimer", () => _mac.OnTimer(e.Name));
                }
                else
                {
                    Owner(e.Name).OnTimer(e.Name);
                }

                break;
            case BurstStarted:
                _dts.OnBurstStarted();
                Quiet("the MAC's OnBurstStarted", _mac.OnBurstStarted);
                break;
            case BytesReceived b:
                _dts.OnBytesReceived(b.Bytes);
                break;
            case BurstEnded b:
                _dts.OnBurstEnded(b);
                Quiet("the MAC's OnBurstEnded", () => _mac.OnBurstEnded(b));
                break;
            case TransmitDone:
                _dts.OnTransmitDone();
                Quiet("the MAC's OnTransmitDone", _mac.OnTransmitDone);
                break;
            case ChannelBusy c:
                Quiet("the MAC's OnChannelBusy", () => _mac.OnChannelBusy(c.Busy));
                break;
            case SisConnect c:
                _sis.OnConnect(c.Conn);
                break;
            case SisBytes b:
                _sis.OnBytes(b.Conn, b.Bytes);
                break;
            case SisDisconnect d:
                _sis.OnDisconnect(d.Conn);
                break;
            case ShutdownInput:
                _sis.OnShutdown();
                break;
            default:
                throw new ArgumentException($"unknown input {input.GetType().Name}", nameof(input));
        }
    }

    private ISublayer Owner(TimerName name) => name.Owner switch
    {
        Sublayer.Sis => _sis,
        Sublayer.Cas => _cas,
        Sublayer.Dts => _dts,
        _ => _mac,
    };

    /// <summary>
    /// Runs calls that the design says only record facts: configuration changes (7.3) and the
    /// MAC's entry calls and OnTimer (3.5 rule 4). An output or primitive from one of them is a bug,
    /// caught here so a later item cannot drift from the order the C++ port must reproduce.
    /// </summary>
    private void Quiet(string what, Action call)
    {
        int outputs = _context.OutputCount;
        int pending = _context.PendingCount;
        call();
        if (_context.OutputCount != outputs || _context.PendingCount != pending)
        {
            throw new InvalidOperationException($"{what} emitted an output or enqueued a primitive; it may only record facts");
        }
    }

    private void DrainOnRequest()
    {
        if (!_inAfterInput)
        {
            throw new InvalidOperationException("only the MAC's AfterInput may ask for the FIFO to be drained");
        }

        Drain();
    }

    private void Drain()
    {
        if (_draining)
        {
            throw new InvalidOperationException("the FIFO is already being drained");
        }

        _draining = true;
        try
        {
            while (_context.TryDequeue(out InternalPrimitive p))
            {
                switch (p)
                {
                    case ToSis s:
                        _sis.On(s);
                        break;
                    case ToCas c:
                        _cas.On(c);
                        break;
                    case ToDts d:
                        _dts.On(d);
                        break;
                    default:
                        throw new InvalidOperationException($"primitive {p.GetType().Name} has no destination");
                }
            }
        }
        finally
        {
            _draining = false;
        }
    }
}
