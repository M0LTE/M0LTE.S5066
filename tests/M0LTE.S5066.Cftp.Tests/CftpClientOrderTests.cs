using System.Net;
using System.Net.Sockets;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Cftp.Tests;

/// <summary>
/// Issue #119: the segments of a file reach the node in order even when the node has flow off
/// while they are queued (V-0017 to V-0019, S-0020, S-0021).
/// </summary>
public class CftpClientOrderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Segments_queued_while_flow_is_off_reach_the_node_in_order()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            Task<Socket> accept = listener.AcceptSocketAsync();
            await using SisClient sis = await SisClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using Socket node = await accept.WaitAsync(Timeout);
            await using var stream = new NetworkStream(node, ownsSocket: false);
            var decoder = new SisFrameDecoder();

            async Task<SisPrimitive> ReadAsync()
            {
                using var cts = new CancellationTokenSource(Timeout);
                var buffer = new byte[4096];
                while (true)
                {
                    if (decoder.TryRead(out SisDecodeResult r))
                    {
                        return r.Primitive ?? throw new InvalidDataException($"bad frame: {r.Error}");
                    }

                    int n = await stream.ReadAsync(buffer, cts.Token);
                    if (n == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    decoder.Push(buffer.AsSpan(0, n));
                }
            }

            Task<CftpClient> bind = CftpClient.BindAsync(sis);
            (await ReadAsync()).Should().BeOfType<SisBindRequest>();
            await stream.WriteAsync(SisCodec.Encode(new SisBindAccepted(CftpClient.Sap, 64)));
            await using CftpClient cftp = await bind.WaitAsync(Timeout);

            await stream.WriteAsync(SisCodec.Encode(new SisDataFlowOff()));
            using (var cts = new CancellationTokenSource(Timeout))
            {
                while (sis.FlowOn)
                {
                    await Task.Delay(10, cts.Token);
                }
            }

            // Many segments, all queued behind flow off; none is confirmed, so the file never completes.
            byte[] file = new byte[60 * 100];
            new Random(119).NextBytes(file);
            _ = cftp.SendFileAsync(SisAddress.FromParts(0, 0, 1, 2), "order"u8.ToArray(), file);
            await Task.Delay(300);
            await stream.WriteAsync(SisCodec.Encode(new SisDataFlowOn()));

            var segments = new List<int>();
            while (segments.Count < 100)
            {
                SisPrimitive p = await ReadAsync();
                if (p is SisUnidataRequest u && Rcop1Pdu.TryDecode(u.UPdu.Span, out Rcop1Pdu? pdu, out _))
                {
                    segments.Add(pdu!.Segment);
                }
            }

            segments.Should().Equal(Enumerable.Range(0, 100), "each segment is written after the one before it");
        }
        finally
        {
            listener.Stop();
        }
    }
}
