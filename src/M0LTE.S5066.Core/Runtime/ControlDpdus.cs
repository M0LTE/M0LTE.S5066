using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>The planner classes ahead of client data (core-architecture.md 5.3; C-1041), in fill order.</summary>
internal enum ControlClass
{
    /// <summary>MANAGEMENT (type 6) D_PDUs, ahead of every other type (C-0297).</summary>
    Management,

    /// <summary>WARNING (type 15) D_PDUs, oldest first.</summary>
    Warning,
}

/// <summary>
/// A queued D_PDU the DTS generated itself, not client data: built at placement time, when the
/// planner stamps its EOT (C-0104) and gives it any EOW owed to its destination (C-1079).
/// </summary>
internal abstract class ControlItem(uint dst)
{
    public abstract ControlClass Class { get; }

    /// <summary>The individual address it goes to.</summary>
    public uint Dst { get; } = dst;

    /// <summary>The queue's allocation sequence number, the age tiebreak (C-0853).</summary>
    public long Seq { get; set; }

    /// <summary>The encoded size, set at the start of each build.</summary>
    public int Size { get; set; }

    /// <summary>Called once, when a transmission places it.</summary>
    public Action? Placed { get; init; }

    /// <summary>The D_PDU with the given generic header.</summary>
    public abstract Dpdu Build(DpduHeader header);
}

/// <summary>
/// A MANAGEMENT acknowledgement: type 6 with ACK set, VALID MESSAGE clear, EXTENDED MESSAGE clear
/// and no extended bytes (C-0296, C-0302, C-0304), carrying the frame ID of the D_PDU it
/// acknowledges (C-1073).
/// </summary>
internal sealed class ManagementAck(uint dst, int frameId) : ControlItem(dst)
{
    public override ControlClass Class => ControlClass.Management;

    public int FrameId { get; } = frameId;

    public override Dpdu Build(DpduHeader header) => new Management(header, false, false, true, FrameId, Octets.Empty);
}

/// <summary>A WARNING D_PDU (C-0376, C-0377): the received type and the reason (C-0378, C-0380, C-0381, C-0382).</summary>
internal sealed class WarningItem(uint dst, int receivedFrameType, int reason) : ControlItem(dst)
{
    public override ControlClass Class => ControlClass.Warning;

    public int ReceivedFrameType { get; } = receivedFrameType;

    public int Reason { get; } = reason;

    public override Dpdu Build(DpduHeader header) => new Warning(header, ReceivedFrameType, Reason);
}
