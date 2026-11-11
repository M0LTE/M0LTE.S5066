using System.Collections.Concurrent;

namespace M0LTE.S5066.Ip;

/// <summary>
/// Where IP datagrams come from and go to: the link between the IP client and the host's router
/// (U-0026). On Linux a tun device (<see cref="LinuxTun"/>); in tests an in-memory pair.
/// One read or write is one whole datagram.
/// </summary>
internal interface IPacketDevice : IDisposable
{
    /// <summary>The device name, for the log.</summary>
    string Name { get; }

    /// <summary>Blocks until a datagram arrives and copies it into <paramref name="buffer"/>; returns its length.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    int Read(Span<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Hands one datagram to the host.</summary>
    void Write(ReadOnlySpan<byte> datagram);

    /// <summary>Sets the link MTU the host's IP layer sees.</summary>
    void SetMtu(int mtu);
}

/// <summary>An in-memory device for tests: the host side injects and collects datagrams.</summary>
internal sealed class MemoryPacketDevice(string name = "mem0") : IPacketDevice
{
    private readonly BlockingCollection<byte[]> _fromHost = [];

    public string Name => name;

    /// <summary>Datagrams the client wrote towards the host.</summary>
    public BlockingCollection<byte[]> ToHost { get; } = [];

    /// <summary>The last MTU set, or 0.</summary>
    public int Mtu { get; private set; }

    /// <summary>The host sends a datagram into the client.</summary>
    public void Inject(byte[] datagram) => _fromHost.Add(datagram);

    public int Read(Span<byte> buffer, CancellationToken cancellationToken)
    {
        byte[] d = _fromHost.Take(cancellationToken);
        d.CopyTo(buffer);
        return d.Length;
    }

    public void Write(ReadOnlySpan<byte> datagram) => ToHost.Add(datagram.ToArray());

    public void SetMtu(int mtu) => Mtu = mtu;

    public void Dispose()
    {
        _fromHost.Dispose();
        ToHost.Dispose();
    }
}
