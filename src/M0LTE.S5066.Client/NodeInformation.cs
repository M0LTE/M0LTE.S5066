using System.Buffers.Binary;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Client;

/// <summary>One peer record of <see cref="NodeInformation"/> (docs/management-messages.md, MSG TYPE 2).</summary>
/// <param name="Address">The peer.</param>
/// <param name="SoftLink">0 none, 1 being made or held off, 2 established, 3 closing.</param>
/// <param name="TxWn">Waveform number used to send to it.</param>
/// <param name="TxInterleaver">Interleaver code used to send to it.</param>
/// <param name="HeardWn">Waveform of the last burst heard from it, or null if never heard.</param>
/// <param name="HeardInterleaver">Interleaver code of that burst, or null if never heard.</param>
/// <param name="SnrTenthDb">That burst's SNR in tenths of a dB, or null.</param>
/// <param name="SecondsSinceHeard">Whole seconds since that burst ended, or null if never heard.</param>
public sealed record PeerInformation(SisAddress Address, byte SoftLink, byte TxWn, byte TxInterleaver,
    byte? HeardWn, byte? HeardInterleaver, short? SnrTenthDb, int? SecondsSinceHeard);

/// <summary>The node information message (MSG TYPE 2; S-0038, S-0164; docs/management-messages.md).</summary>
/// <param name="Address">The node's own address.</param>
/// <param name="TxWn">The waveform number it sends with.</param>
/// <param name="TxInterleaver">The interleaver code it sends with: 0 ultra short to 5 very long.</param>
/// <param name="Peers">The peer records, in address order.</param>
public sealed record NodeInformation(SisAddress Address, byte TxWn, byte TxInterleaver, IReadOnlyList<PeerInformation> Peers)
{
    /// <summary>The MSG TYPE of the request and its answer.</summary>
    public const byte MsgType = 2;

    /// <summary>Parses a MSG BODY. Bytes after the ones described are ignored, as the page asks.</summary>
    /// <exception cref="FormatException">The body is too short.</exception>
    public static NodeInformation Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 7)
        {
            throw new FormatException("node information body shorter than 7 bytes");
        }

        int n = body[6];
        if (body.Length < 7 + (13 * n))
        {
            throw new FormatException("node information body shorter than its peer count");
        }

        var peers = new List<PeerInformation>(n);
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> r = body.Slice(7 + (13 * i), 13);
            bool heard = r[7] != 255;
            short snr = BinaryPrimitives.ReadInt16BigEndian(r[9..]);
            ushort age = BinaryPrimitives.ReadUInt16BigEndian(r[11..]);
            peers.Add(new PeerInformation(SisAddress.Read(r), r[4], r[5], r[6],
                heard ? r[7] : null, heard ? r[8] : null, snr == short.MinValue ? null : snr, age == 0xFFFF ? null : age));
        }

        return new NodeInformation(SisAddress.Read(body), body[4], body[5], peers);
    }
}

/// <summary>Management helpers on <see cref="SisClient"/>.</summary>
public static class SisClientManagement
{
    /// <summary>
    /// Asks the node for its node information (MSG TYPE 2) and returns the first answer. Other
    /// management messages read meanwhile are dropped, so do not read
    /// <see cref="SisClient.ManagementMessages"/> elsewhere at the same time.
    /// </summary>
    public static async Task<NodeInformation> GetNodeInformationAsync(this SisClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        await client.SendManagementAsync(NodeInformation.MsgType, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        await foreach (SisManagementMsgIndication m in client.ManagementMessages.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (m.MsgType == NodeInformation.MsgType)
            {
                return NodeInformation.Parse(m.Body.Span);
            }
        }

        throw new IOException("the SIS connection closed");
    }
}
