namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// What every sublayer offers the dispatcher. A sublayer never calls another sublayer: it enqueues
/// a primitive on <see cref="CoreContext.Enqueue"/> and returns (core-architecture.md 3.5 rule 2).
/// </summary>
internal interface ISublayer
{
    /// <summary>The configuration was replaced. Must not emit or enqueue (core-architecture.md 7.3).</summary>
    void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig);

    /// <summary>One of this sublayer's timers expired; the table has already removed it.</summary>
    void OnTimer(TimerName name);
}

/// <summary>The Subnetwork Interface Sublayer (Annexes A and S).</summary>
internal interface ISisSublayer : ISublayer
{
    void OnConnect(int conn);

    void OnBytes(int conn, ReadOnlyMemory<byte> bytes);

    void OnDisconnect(int conn);

    void On(ToSis primitive);
}

/// <summary>The Channel Access Sublayer (Annex B).</summary>
internal interface ICasSublayer : ISublayer
{
    void On(ToCas primitive);
}

/// <summary>The Data Transfer Sublayer (Annex C) and the receive side of the modem.</summary>
internal interface IDtsSublayer : ISublayer
{
    void OnBurstStarted();

    void OnBytesReceived(ReadOnlyMemory<byte> bytes);

    void OnBurstEnded(BurstEnded burst);

    void OnTransmitDone();

    void On(ToDts primitive);
}

/// <summary>
/// The DTS tells the MAC what it heard and what it holds (core-architecture.md 4.1). These calls
/// are synchronous, update only the MAC's channel model and queue flag, and never emit an output
/// or enqueue a primitive: the one exception to the FIFO rule.
/// </summary>
internal interface IMacObserver
{
    /// <summary>A valid header with EOT &gt; 0 gave this predicted end time (C-0842, K-0021; EOT 0 is ignored, C-0105).</summary>
    void EotHeard(long predictedEndTime);

    /// <summary>A D_PDU was heard, whatever its destination, for the two-node rule (K-0068, K-0072).</summary>
    void DpduHeard(uint src, uint dst, bool dstIsGroup);

    /// <summary>Whether the planner could place at least one item now (K-0096).</summary>
    void QueueState(bool nonEmpty);
}

/// <summary>
/// The Annex K MAC. Its entry calls and <see cref="ISublayer.OnTimer"/> only record facts; it
/// produces outputs in exactly one place, <see cref="AfterInput"/> (core-architecture.md 3.5 rule 4).
/// </summary>
internal interface IMacSublayer : ISublayer, IMacObserver
{
    void OnBurstStarted();

    void OnBurstEnded(BurstEnded burst);

    void OnTransmitDone();

    void OnChannelBusy(bool busy);

    /// <summary>
    /// Called once per input, after the FIFO is empty. Takes every transition the recorded facts
    /// and the queue state allow, chaining through zero-duration waits, and emits the resulting
    /// outputs. It may call <see cref="CoreContext.DrainPrimitives"/> once it has asked the DTS to
    /// reject expired C_PDUs, before it builds a transmission.
    /// </summary>
    void AfterInput();
}
