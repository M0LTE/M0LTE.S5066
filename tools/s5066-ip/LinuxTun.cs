using System.Runtime.InteropServices;
using System.Text;

namespace M0LTE.S5066.Ip;

/// <summary>
/// A Linux tun device (IFF_TUN, IFF_NO_PI: bare IP datagrams, no packet information header) made
/// through /dev/net/tun with ioctl. Needs CAP_NET_ADMIN. The device lives while this object holds
/// it open, so routes on it go when the process ends; the operator adds them (docs/s5066-ip.md).
/// </summary>
internal sealed partial class LinuxTun : IPacketDevice
{
    private const int ORdWr = 2;
    private const int OCloExec = 0x80000;
    private const ulong TunSetIff = 0x400454CA; // _IOW('T', 202, int)
    private const ulong SiocGIfFlags = 0x8913;
    private const ulong SiocSIfFlags = 0x8914;
    private const ulong SiocSIfMtu = 0x8922;
    private const short IffTun = 0x0001;
    private const short IffNoPi = 0x1000;
    private const short IffUp = 0x0001;
    private const short PollIn = 0x0001;
    private const int AfInet = 2;
    private const int SockDgram = 2;
    private const int Eintr = 4;
    private const int Eagain = 11;

    private readonly int _fd;
    private bool _disposed;

    private LinuxTun(int fd, string name)
    {
        _fd = fd;
        Name = name;
    }

    public string Name { get; }

    /// <summary>Opens (or attaches to) the tun device <paramref name="name"/> and brings it up.</summary>
    /// <exception cref="IOException">The kernel refused; the message says why.</exception>
    public static LinuxTun Open(string name)
    {
        var req = Ifreq.For(name);
        int fd = open("/dev/net/tun", ORdWr | OCloExec);
        if (fd < 0)
        {
            throw Error("open /dev/net/tun");
        }

        req.Short = IffTun | IffNoPi;
        if (ioctl(fd, TunSetIff, ref req) < 0)
        {
            var e = Error($"create tun device {name} (needs CAP_NET_ADMIN)");
            close(fd);
            throw e;
        }

        var tun = new LinuxTun(fd, req.Name);
        try
        {
            WithSocket(s =>
            {
                var f = Ifreq.For(tun.Name);
                if (ioctl(s, SiocGIfFlags, ref f) < 0)
                {
                    throw Error($"read the flags of {tun.Name}");
                }

                f.Short |= IffUp;
                if (ioctl(s, SiocSIfFlags, ref f) < 0)
                {
                    throw Error($"bring {tun.Name} up");
                }
            });
        }
        catch
        {
            tun.Dispose();
            throw;
        }

        return tun;
    }

    public void SetMtu(int mtu) => WithSocket(s =>
    {
        var f = Ifreq.For(Name);
        f.Int = mtu;
        if (ioctl(s, SiocSIfMtu, ref f) < 0)
        {
            throw Error($"set the MTU of {Name} to {mtu}");
        }
    });

    public unsafe int Read(Span<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = new PollFd { Fd = _fd, Events = PollIn };
            int r = poll(ref p, 1, 250);
            if (r < 0 && Marshal.GetLastPInvokeError() == Eintr)
            {
                continue;
            }

            if (r < 0)
            {
                throw Error($"poll {Name}");
            }

            if (r == 0)
            {
                continue;
            }

            fixed (byte* b = buffer)
            {
                nint n = read(_fd, b, (nuint)buffer.Length);
                if (n >= 0)
                {
                    return (int)n;
                }
            }

            int err = Marshal.GetLastPInvokeError();
            if (err is not (Eintr or Eagain))
            {
                throw Error($"read {Name}");
            }
        }
    }

    public unsafe void Write(ReadOnlySpan<byte> datagram)
    {
        fixed (byte* b = datagram)
        {
            if (write(_fd, b, (nuint)datagram.Length) < 0)
            {
                throw Error($"write {Name}");
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            close(_fd);
        }
    }

    private static void WithSocket(Action<int> action)
    {
        int s = socket(AfInet, SockDgram, 0);
        if (s < 0)
        {
            throw Error("open a control socket");
        }

        try
        {
            action(s);
        }
        finally
        {
            close(s);
        }
    }

    private static IOException Error(string what)
    {
        int errno = Marshal.GetLastPInvokeError();
        return new IOException($"{what}: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno})");
    }

    /// <summary>struct ifreq: a 16-byte name and a 24-byte union, of which the first short or int is used here.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private unsafe struct Ifreq
    {
        [FieldOffset(0)] public fixed byte NameBytes[16];
        [FieldOffset(16)] public short Short;
        [FieldOffset(16)] public int Int;

        public string Name
        {
            get
            {
                fixed (byte* p = NameBytes)
                {
                    return Marshal.PtrToStringAnsi((nint)p) ?? "";
                }
            }
        }

        public static Ifreq For(string name)
        {
            byte[] b = Encoding.ASCII.GetBytes(name);
            if (b.Length > 15)
            {
                throw new ArgumentException("an interface name is at most 15 characters", nameof(name));
            }

            var r = default(Ifreq);
            for (int i = 0; i < b.Length; i++)
            {
                r.NameBytes[i] = b[i];
            }

            return r;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short REvents;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctl(int fd, ulong request, ref Ifreq arg);

    [LibraryImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static partial int socket(int domain, int type, int protocol);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int poll(ref PollFd fds, ulong nfds, int timeout);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial nint read(int fd, byte* buf, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint write(int fd, byte* buf, nuint count);
}
