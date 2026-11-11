using System.Net;
using System.Net.Sockets;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Cftp.Tests;

/// <summary>
/// Issue #122, interp V-0093: with <see cref="CftpOptions.AcceptByApplication"/> the client writes
/// the acknowledgement only when the application accepts the file, and never when it refuses.
/// </summary>
public class CftpClientAcceptTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly SisAddress Far = SisAddress.FromParts(0, 0, 1, 2);

    /// <summary>A scripted node on a loopback socket, with a bound client.</summary>
    private sealed class Node : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly SisFrameDecoder _decoder = new();
        private SisClient? _sis;
        private Socket? _socket;
        private NetworkStream? _stream;

        public CftpClient Cftp { get; private set; } = null!;

        public static async Task<Node> StartAsync(CftpOptions options)
        {
            var n = new Node();
            n._listener.Start();
            Task<Socket> accept = n._listener.AcceptSocketAsync();
            n._sis = await SisClient.ConnectAsync((IPEndPoint)n._listener.LocalEndpoint);
            n._socket = await accept.WaitAsync(Timeout);
            n._stream = new NetworkStream(n._socket, ownsSocket: false);
            Task<CftpClient> bind = CftpClient.BindAsync(n._sis, options);
            (await n.ReadAsync(Timeout)).Should().BeOfType<SisBindRequest>();
            await n._stream.WriteAsync(SisCodec.Encode(new SisBindAccepted(CftpClient.Sap, 2048)));
            n.Cftp = await bind.WaitAsync(Timeout);
            return n;
        }

        /// <summary>Hands the client a whole file from <see cref="Far"/> as one segment.</summary>
        public async Task DeliverFileAsync(byte uPduId)
        {
            var mail = new CftpMail("abc" + uPduId, new CftpRecipients("a@x", ["b@y"]), "Subject: t\r\n\r\nhi\r\n"u8.ToArray());
            byte[] bftp = new Bftp1Pdu(System.Text.Encoding.Latin1.GetBytes(mail.MessageId), mail.ToCompressedFile()).Encode();
            byte[] u = new Rcop1Pdu(0, uPduId, 0, bftp).Encode();
            await _stream!.WriteAsync(SisCodec.Encode(new SisUnidataIndication(4, CftpClient.Sap, SisAddress.FromParts(0, 0, 1, 1), 1, CftpClient.Sap, Far, u)));
        }

        /// <summary>The next primitive from the client, or null when none comes within <paramref name="wait"/>.</summary>
        public async Task<SisPrimitive?> ReadAsync(TimeSpan wait)
        {
            using var cts = new CancellationTokenSource(wait);
            var buffer = new byte[4096];
            while (true)
            {
                if (_decoder.TryRead(out SisDecodeResult r))
                {
                    return r.Primitive ?? throw new InvalidDataException($"bad frame: {r.Error}");
                }

                int n;
                try
                {
                    n = await _stream!.ReadAsync(buffer, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (n == 0)
                {
                    throw new EndOfStreamException();
                }

                _decoder.Push(buffer.AsSpan(0, n));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Cftp.DisposeAsync();
            await _sis!.DisposeAsync();
            await _stream!.DisposeAsync();
            _socket!.Dispose();
            _listener.Stop();
        }
    }

    [Fact]
    public async Task The_acknowledgement_is_written_only_after_the_application_accepts()
    {
        await using Node node = await Node.StartAsync(new CftpOptions { AcceptByApplication = true });
        await node.DeliverFileAsync(7);
        using var cts = new CancellationTokenSource(Timeout);
        CftpReceived r = await node.Cftp.Received.ReadAsync(cts.Token);
        r.AwaitsAccept.Should().BeTrue();
        r.Mail.Should().NotBeNull();

        (await node.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeNull("nothing is acknowledged before the application accepts");

        r.Accept().Should().BeTrue();
        SisUnidataRequest ack = (await node.ReadAsync(Timeout)).Should().BeOfType<SisUnidataRequest>().Subject;
        ack.DestAddress.Should().Be(Far);
        ack.UPdu.ToArray().Should().Equal(0x00, 0x07, 0x00, 0x00, 0x10, 0x0B);
        r.Accept().Should().BeFalse("it was answered already");
    }

    [Fact]
    public async Task A_refused_file_gets_no_acknowledgement()
    {
        await using Node node = await Node.StartAsync(new CftpOptions { AcceptByApplication = true });
        await node.DeliverFileAsync(3);
        using var cts = new CancellationTokenSource(Timeout);
        CftpReceived r = await node.Cftp.Received.ReadAsync(cts.Token);
        r.Refuse().Should().BeTrue();
        (await node.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeNull();
    }

    [Fact]
    public async Task Without_the_option_the_file_is_acknowledged_on_arrival_and_accept_does_nothing()
    {
        await using Node node = await Node.StartAsync(new CftpOptions());
        await node.DeliverFileAsync(1);
        (await node.ReadAsync(Timeout)).Should().BeOfType<SisUnidataRequest>();
        using var cts = new CancellationTokenSource(Timeout);
        CftpReceived r = await node.Cftp.Received.ReadAsync(cts.Token);
        r.AwaitsAccept.Should().BeFalse();
        r.Accept().Should().BeFalse();
        (await node.ReadAsync(TimeSpan.FromMilliseconds(300))).Should().BeNull();
    }
}
