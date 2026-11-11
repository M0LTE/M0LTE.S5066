using System.Security.Cryptography;

namespace M0LTE.S5066.Cftp;

/// <summary>Settings of a CFTP client. The defaults are the readings of interp rows V-0074 and V-0076 to V-0078, V-0088.</summary>
public sealed record CftpOptions
{
    /// <summary>How long a sender waits for the acknowledgement once the node has confirmed every segment (interp V-0076).</summary>
    public TimeSpan AckWait { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>How long a sender waits before sending again after a segment was rejected (interp V-0076).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Attempts at one file before the transfer fails (interp V-0076).</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>How long a partly received file is kept with no new segment (interp V-0078).</summary>
    public TimeSpan ReassemblyTimeout { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Files in transfer at once to one destination: 1 is stop-and-wait (V-0023); more only for a
    /// receiver known to take concurrent files (V-0024, interp V-0088). 1 to 256.
    /// </summary>
    public int ConcurrentFiles { get; init; } = 1;

    /// <summary>SIS priority of CFTP U_PDUs: 4, the Annex F default for CFTP (interp V-0074).</summary>
    public byte Priority { get; init; } = 4;

    /// <summary>
    /// When true, a completed file is not acknowledged until the application accepts it with
    /// <see cref="CftpEngine.Accept"/>, so the acknowledgement means the file is stored, not just
    /// reassembled (interp V-0093). A file refused with <see cref="CftpEngine.Refuse"/>, or not
    /// answered within <see cref="ReassemblyTimeout"/>, is not acknowledged, and the sender sends it
    /// again (interp V-0076). False (the default) acknowledges on the last byte, as before.
    /// </summary>
    public bool AcceptByApplication { get; init; }
}

/// <summary>Something the engine asks its owner to do or tells it.</summary>
public abstract record CftpAction;

/// <summary>
/// Send this U_PDU to SAP 12 of <paramref name="Destination"/>, ARQ and in order (interp V-0074).
/// A file segment asks for node delivery confirmation, and its outcome goes back through
/// <see cref="CftpEngine.SendDone"/> with <paramref name="SendId"/>; an acknowledgement asks for none
/// and needs no answer.
/// </summary>
public sealed record CftpSendUPdu(long SendId, uint Destination, byte[] UPdu, bool IsAck) : CftpAction;

/// <summary>A transfer ended: acknowledged, or failed after its attempts.</summary>
public sealed record CftpTransferDone(long TransferId, bool Acknowledged, int Attempts, string? Failure) : CftpAction;

/// <summary>
/// A whole file arrived (V-0039). It has been acknowledged (V-0045), unless <see cref="AwaitsAccept"/>:
/// then the acknowledgement waits for <see cref="CftpEngine.Accept"/> with <see cref="ReceiveId"/>
/// (interp V-0093).
/// </summary>
public sealed record CftpFileReceived(uint Source, byte ConnectionId, byte UPduId, byte[] FileName, byte[] FileData) : CftpAction
{
    /// <summary>Names this file to <see cref="CftpEngine.Accept"/> and <see cref="CftpEngine.Refuse"/>; 0 when it was acknowledged already.</summary>
    public long ReceiveId { get; init; }

    /// <summary>True when the acknowledgement waits for the application (<see cref="CftpOptions.AcceptByApplication"/>).</summary>
    public bool AwaitsAccept { get; init; }
}

/// <summary>Something worth a log line that changes nothing for the application.</summary>
public sealed record CftpNote(string Text) : CftpAction;

/// <summary>
/// The CFTP sender and receiver as a deterministic state machine: no clock, socket or thread of
/// its own. Its owner feeds it the time in milliseconds with every call, gives it the U_PDUs that
/// arrive on SAP 12 and the outcome of each segment it sent, calls <see cref="Tick"/> at
/// <see cref="NextDeadline"/>, and carries out the actions it drains. It works on BFTPv1 files
/// (name and data); <see cref="CftpMail"/> makes and reads the compressed mail files they carry.
/// </summary>
/// <remarks>
/// Sending: each file gets the next U_PDU ID for its destination, from 0, wrapping at 255
/// (V-0016, interp V-0080), connection ID 0 (interp V-0081), and goes as a BFTPv1 PDU cut into
/// segments of at most MTU - 4 octets numbered from 0 (V-0017 to V-0019, V-0039, interp V-0071,
/// V-0072). One file at a time per destination unless <see cref="CftpOptions.ConcurrentFiles"/>
/// says otherwise (V-0023, V-0024). The file is resent whole under the same ID when its
/// acknowledgement does not come or a segment is rejected (interp V-0076).
/// Receiving: files are kept apart by source, connection ID and U_PDU ID (V-0025, interp V-0088),
/// reassembled (V-0040, interp V-0073) and acknowledged on their last byte (V-0045), or once the
/// application accepts them with <see cref="CftpOptions.AcceptByApplication"/> (interp V-0093);
/// duplicates acknowledged but not delivered twice (interp V-0077).
/// </remarks>
public sealed class CftpEngine
{
    /// <summary>The most segments a file can have: the segment number is 16 bits (interp V-0087).</summary>
    public const int MaxSegments = 65536;

    private readonly CftpOptions _options;
    private readonly List<CftpAction> _actions = [];
    private readonly Dictionary<uint, Destination> _destinations = [];
    private readonly Dictionary<long, Transfer> _bySend = [];
    private readonly Dictionary<(uint Src, byte Conn, byte Id), Partial> _partials = [];
    private readonly Dictionary<(uint Src, byte Conn, byte Id), byte[]> _lastCompleted = [];
    private readonly Dictionary<long, Held> _held = [];
    private readonly Dictionary<(uint Src, byte Conn, byte Id), long> _heldByKey = [];
    private long _nextReceiveId = 1;
    private long _nextTransferId = 1;
    private long _nextSendId = 1;

    /// <summary>Creates an engine.</summary>
    public CftpEngine(CftpOptions? options = null)
    {
        _options = options ?? new CftpOptions();
        if (_options.ConcurrentFiles is < 1 or > 256 || _options.MaxAttempts < 1)
        {
            throw new ArgumentException("ConcurrentFiles is 1 to 256 and MaxAttempts at least 1");
        }
    }

    /// <summary>The earliest time <see cref="Tick"/> has work, or null when nothing waits on a timer.</summary>
    public long? NextDeadline
    {
        get
        {
            long? d = null;
            foreach (Destination dest in _destinations.Values)
            {
                foreach (Transfer t in dest.Active)
                {
                    if (t.Deadline is { } x && (d is null || x < d))
                    {
                        d = x;
                    }
                }
            }

            foreach (Partial p in _partials.Values)
            {
                long x = p.LastHeard + Ms(_options.ReassemblyTimeout);
                if (d is null || x < d)
                {
                    d = x;
                }
            }

            foreach (Held h in _held.Values)
            {
                if (d is null || h.Deadline < d)
                {
                    d = h.Deadline;
                }
            }

            return d;
        }
    }

    /// <summary>Returns and clears the actions produced since the last call, in order.</summary>
    public IReadOnlyList<CftpAction> Drain()
    {
        CftpAction[] a = [.. _actions];
        _actions.Clear();
        return a;
    }

    /// <summary>
    /// Queues a file for <paramref name="destination"/> (one node, interp V-0074) and returns the
    /// transfer ID that its <see cref="CftpTransferDone"/> will carry. <paramref name="mtu"/> is the
    /// bind's MTU (interp V-0072).
    /// </summary>
    public long Submit(uint destination, ReadOnlyMemory<byte> fileName, ReadOnlyMemory<byte> fileData, int mtu, long now)
    {
        if (mtu <= Rcop1Pdu.HeaderBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(mtu), "the MTU must leave room for data after the 4-octet RCOPv1 header");
        }

        var pdu = new Bftp1Pdu(fileName, fileData);
        long per = mtu - Rcop1Pdu.HeaderBytes;
        long segments = Math.Max(1, (pdu.Length + per - 1) / per);
        if (segments > MaxSegments)
        {
            throw new ArgumentException($"the file needs {segments} segments; at most {MaxSegments} fit the 16-bit segment number (interp V-0087)");
        }

        byte[] bytes = pdu.Encode();
        var parts = new byte[segments][];
        for (int i = 0; i < segments; i++)
        {
            int start = (int)(i * per);
            parts[i] = bytes.AsSpan(start, (int)Math.Min(per, bytes.Length - start)).ToArray();
        }

        Destination dest = Dest(destination);
        var t = new Transfer(_nextTransferId++, destination, parts);
        dest.Queue.Enqueue(t);
        Pump(dest, now);
        return t.Id;
    }

    /// <summary>The node's answer to a file segment: confirmed, or rejected with the A.2.2.8 reason (0 for a failure to send at all).</summary>
    public void SendDone(long sendId, bool confirmed, byte reason, long now)
    {
        if (!_bySend.Remove(sendId, out Transfer? t) || t.Done)
        {
            return;
        }

        t.Outstanding--;
        if (!confirmed)
        {
            t.Rejected = true;
            t.LastReason = reason;
        }

        if (t.Outstanding > 0)
        {
            return;
        }

        if (!t.Rejected)
        {
            t.Deadline = now + Ms(_options.AckWait);
            t.Waiting = Wait.Ack;
        }
        else if (t.Attempts >= _options.MaxAttempts)
        {
            Finish(t, false, $"a segment was rejected (reason {t.LastReason}) on each of {t.Attempts} attempts", now);
        }
        else
        {
            t.Deadline = now + Ms(_options.RetryDelay);
            t.Waiting = Wait.Retry;
        }
    }

    /// <summary>A U_PDU that arrived on SAP 12 from <paramref name="source"/>.</summary>
    public void Receive(uint source, ReadOnlySpan<byte> uPdu, long now)
    {
        if (!Rcop1Pdu.TryDecode(uPdu, out Rcop1Pdu? pdu, out _))
        {
            Note($"a U_PDU of {uPdu.Length} octets from {source:x7} is too short for RCOPv1; ignored");
            return;
        }

        if (pdu!.IsAck)
        {
            OnAck(source, pdu, now);
            return;
        }

        var key = (source, pdu.ConnectionId, pdu.UPduId);
        if (pdu.Segment == 0 && _partials.TryGetValue(key, out Partial? old) && old.Segments.ContainsKey(0))
        {
            // A second segment 0: the sender is sending the file again from the start (interp V-0077).
            _partials.Remove(key);
        }

        if (!_partials.TryGetValue(key, out Partial? p))
        {
            p = new Partial();
            _partials[key] = p;
        }

        p.LastHeard = now;
        p.Segments[pdu.Segment] = pdu.Data.ToArray();
        TryComplete(key, p, now);
    }

    /// <summary>
    /// The application has stored a file it was given with <see cref="CftpFileReceived.AwaitsAccept"/>:
    /// acknowledge it now (V-0045, interp V-0093), and treat the same file sent again as a duplicate
    /// (interp V-0077). Returns false when <paramref name="receiveId"/> waits for nothing (answered
    /// already, or timed out), in which case nothing is sent.
    /// </summary>
    public bool Accept(long receiveId, long now)
    {
        _ = now;
        if (!_held.Remove(receiveId, out Held? h))
        {
            return false;
        }

        _heldByKey.Remove(h.Key);
        _lastCompleted[h.Key] = h.Hash;
        _actions.Add(new CftpSendUPdu(_nextSendId++, h.Key.Src, Rcop1Pdu.Ack(h.Key.Conn, h.Key.Id).Encode(), IsAck: true));
        return true;
    }

    /// <summary>
    /// The application could not store a file it was given with <see cref="CftpFileReceived.AwaitsAccept"/>:
    /// it is not acknowledged, so the sender sends it again (interp V-0076, V-0093), and it is then
    /// delivered again. Returns false when <paramref name="receiveId"/> waits for nothing.
    /// </summary>
    public bool Refuse(long receiveId, long now)
    {
        _ = now;
        if (!_held.Remove(receiveId, out Held? h))
        {
            return false;
        }

        _heldByKey.Remove(h.Key);
        Note($"file from {h.Key.Src:x7} (connection {h.Key.Conn}, U_PDU ID {h.Key.Id}) refused by the application; not acknowledged (interp V-0093)");
        return true;
    }

    /// <summary>
    /// Runs every timer due at or before <paramref name="now"/>, in a total order that does not
    /// depend on how a dictionary happens to enumerate, so a port produces the same actions in
    /// the same order: transfers by (deadline, transfer ID), then partly received files by
    /// (deadline, source, connection ID, U_PDU ID), then files awaiting the application by
    /// (deadline, receive ID).
    /// </summary>
    public void Tick(long now)
    {
        List<Transfer> due = [.. _destinations.Values
            .SelectMany(d => d.Active)
            .Where(t => t.Deadline is { } d && d <= now)
            .OrderBy(t => t.Deadline!.Value)
            .ThenBy(t => t.Id)];
        foreach (Transfer t in due)
        {
            if (t.Done || t.Deadline is not { } d || d > now)
            {
                continue;
            }

            t.Deadline = null;
            if (t.Waiting == Wait.Ack && t.Attempts >= _options.MaxAttempts)
            {
                Finish(t, false, $"no acknowledgement after {t.Attempts} attempts", now);
            }
            else
            {
                if (t.Waiting == Wait.Ack)
                {
                    Note($"no acknowledgement for U_PDU ID {t.UPduId} to {t.Destination:x7}; sending it again");
                }

                StartAttempt(t);
            }
        }

        long timeout = Ms(_options.ReassemblyTimeout);
        var stale = _partials
            .Where(kv => kv.Value.LastHeard + timeout <= now)
            .OrderBy(kv => kv.Value.LastHeard)
            .ThenBy(kv => kv.Key.Src)
            .ThenBy(kv => kv.Key.Conn)
            .ThenBy(kv => kv.Key.Id)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in stale)
        {
            _partials.Remove(key);
            Note($"dropped a partly received file from {key.Src:x7} (connection {key.Conn}, U_PDU ID {key.Id}): no segment for {_options.ReassemblyTimeout}");
        }

        foreach (long id in _held.Where(kv => kv.Value.Deadline <= now).OrderBy(kv => kv.Value.Deadline).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToList())
        {
            Held h = _held[id];
            _held.Remove(id);
            _heldByKey.Remove(h.Key);
            Note($"file from {h.Key.Src:x7} (connection {h.Key.Conn}, U_PDU ID {h.Key.Id}) not accepted by the application within {_options.ReassemblyTimeout}; not acknowledged (interp V-0093)");
        }
    }

    private static long Ms(TimeSpan t) => (long)t.TotalMilliseconds;

    private Destination Dest(uint address)
    {
        if (!_destinations.TryGetValue(address, out Destination? d))
        {
            d = new Destination();
            _destinations[address] = d;
        }

        return d;
    }

    private void Note(string text) => _actions.Add(new CftpNote(text));

    private void Pump(Destination dest, long now)
    {
        _ = now;
        while (dest.Active.Count < _options.ConcurrentFiles && dest.Queue.TryDequeue(out Transfer? t))
        {
            t.UPduId = dest.NextId;
            dest.NextId = (byte)(dest.NextId + 1);
            dest.Active.Add(t);
            StartAttempt(t);
        }
    }

    private void StartAttempt(Transfer t)
    {
        t.Attempts++;
        t.Rejected = false;
        t.Waiting = Wait.None;
        t.Deadline = null;
        t.Outstanding = t.Segments.Length;
        for (int i = 0; i < t.Segments.Length; i++)
        {
            long id = _nextSendId++;
            _bySend[id] = t;
            byte[] u = new Rcop1Pdu(0, t.UPduId, (ushort)i, t.Segments[i]).Encode();
            _actions.Add(new CftpSendUPdu(id, t.Destination, u, IsAck: false));
        }
    }

    private void Finish(Transfer t, bool acknowledged, string? failure, long now)
    {
        t.Done = true;
        t.Deadline = null;
        Destination dest = _destinations[t.Destination];
        dest.Active.Remove(t);
        foreach (long s in _bySend.Where(kv => kv.Value == t).Select(kv => kv.Key).ToList())
        {
            _bySend.Remove(s);
        }

        _actions.Add(new CftpTransferDone(t.Id, acknowledged, t.Attempts, failure));
        Pump(dest, now);
    }

    private void OnAck(uint source, Rcop1Pdu ack, long now)
    {
        Transfer? t = _destinations.TryGetValue(source, out Destination? d)
            ? d.Active.FirstOrDefault(x => x.UPduId == ack.UPduId && ack.ConnectionId == 0)
            : null;
        if (t is null)
        {
            Note($"an acknowledgement from {source:x7} (connection {ack.ConnectionId}, U_PDU ID {ack.UPduId}) matches no file in transfer; ignored");
            return;
        }

        Finish(t, true, null, now);
    }

    private void TryComplete((uint Src, byte Conn, byte Id) key, Partial p, long now)
    {
        // Join the segments numbered from 0 with no gap; the BFTPv1 header says where the PDU
        // ends (interp V-0073).
        using var ms = new MemoryStream();
        long? need = null;
        for (int i = 0; i < MaxSegments && p.Segments.TryGetValue((ushort)i, out byte[]? seg); i++)
        {
            ms.Write(seg);
            if (need is null)
            {
                try
                {
                    need = Bftp1Pdu.DeclaredLength(ms.GetBuffer().AsSpan(0, (int)ms.Length));
                }
                catch (CftpFormatException e)
                {
                    _partials.Remove(key);
                    Note($"a file from {key.Src:x7} (connection {key.Conn}, U_PDU ID {key.Id}) is not BFTPv1: {e.Message}; dropped");
                    return;
                }
            }

            if (need is { } n && ms.Length >= n)
            {
                break;
            }
        }

        if (need is not { } total || ms.Length < total)
        {
            return;
        }

        _partials.Remove(key);
        byte[] bytes = ms.GetBuffer().AsSpan(0, (int)total).ToArray();
        if (ms.Length > total)
        {
            Note($"{ms.Length - total} octets after the end of a file from {key.Src:x7} ignored (interp V-0073)");
        }

        byte[] hash = SHA256.HashData(bytes);
        bool duplicate = _lastCompleted.TryGetValue(key, out byte[]? last) && last.AsSpan().SequenceEqual(hash);
        if (_options.AcceptByApplication && !duplicate)
        {
            Hold(key, hash, bytes, now);
            return;
        }

        // The last byte is in: acknowledge it, with its connection and U_PDU ID (V-0045, interp V-0075).
        _actions.Add(new CftpSendUPdu(_nextSendId++, key.Src, Rcop1Pdu.Ack(key.Conn, key.Id).Encode(), IsAck: true));

        if (duplicate)
        {
            Note($"file from {key.Src:x7} (connection {key.Conn}, U_PDU ID {key.Id}) received again; acknowledged, not delivered twice (interp V-0077)");
            return;
        }

        _lastCompleted[key] = hash;
        Bftp1Pdu.TryDecode(bytes, out Bftp1Pdu? pdu, out _);
        _actions.Add(new CftpFileReceived(key.Src, key.Conn, key.Id, pdu!.FileName.ToArray(), pdu.FileData.ToArray()));
    }

    /// <summary>A complete new file waits for the application before it is acknowledged (interp V-0093).</summary>
    private void Hold((uint Src, byte Conn, byte Id) key, byte[] hash, byte[] bytes, long now)
    {
        if (_heldByKey.TryGetValue(key, out long waiting))
        {
            // The sender sent it again before the application answered: the answer to the first
            // copy decides. A different file under the same IDs is not acknowledged either; the
            // sender sends it again once the first is answered.
            Note(_held[waiting].Hash.AsSpan().SequenceEqual(hash)
                ? $"file from {key.Src:x7} (connection {key.Conn}, U_PDU ID {key.Id}) received again while the application has it; not delivered twice (interp V-0093)"
                : $"another file from {key.Src:x7} (connection {key.Conn}, U_PDU ID {key.Id}) while the application has one under those IDs; dropped unacknowledged (interp V-0093)");
            return;
        }

        long id = _nextReceiveId++;
        _held[id] = new Held(key, hash, now + Ms(_options.ReassemblyTimeout));
        _heldByKey[key] = id;
        Bftp1Pdu.TryDecode(bytes, out Bftp1Pdu? pdu, out _);
        _actions.Add(new CftpFileReceived(key.Src, key.Conn, key.Id, pdu!.FileName.ToArray(), pdu.FileData.ToArray())
        {
            ReceiveId = id,
            AwaitsAccept = true,
        });
    }

    private sealed record Held((uint Src, byte Conn, byte Id) Key, byte[] Hash, long Deadline);

    private enum Wait
    {
        None,
        Ack,
        Retry,
    }

    private sealed class Destination
    {
        public byte NextId { get; set; }

        public Queue<Transfer> Queue { get; } = new();

        public List<Transfer> Active { get; } = [];
    }

    private sealed class Transfer(long id, uint destination, byte[][] segments)
    {
        public long Id { get; } = id;

        public uint Destination { get; } = destination;

        public byte[][] Segments { get; } = segments;

        public byte UPduId { get; set; }

        public int Attempts { get; set; }

        public int Outstanding { get; set; }

        public bool Rejected { get; set; }

        public byte LastReason { get; set; }

        public bool Done { get; set; }

        public Wait Waiting { get; set; }

        public long? Deadline { get; set; }
    }

    private sealed class Partial
    {
        public long LastHeard { get; set; }

        public Dictionary<ushort, byte[]> Segments { get; } = [];
    }
}
