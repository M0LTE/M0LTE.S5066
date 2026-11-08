namespace M0LTE.S5066.Core.Sis;

/// <summary>
/// A streaming decoder for the SIS Access Protocol byte stream: bytes in, primitives or typed
/// errors out. Sans-IO: feed it whatever chunks arrive with <see cref="Push"/>, then drain
/// <see cref="TryRead"/>. The result does not depend on how the bytes are chunked.
/// </summary>
/// <remarks>
/// Rules (matrix interp rows S-0075, S-0076 and S-0159; corpus-format.md section 3.7):
/// bytes that are not at a 90 EB preamble give one <see cref="SisErrorKind.BadSync"/> for the whole
/// run of junk, then the decoder scans forward byte by byte for the next preamble; a bad version or
/// a zero size gives its error and rescans from the byte after the first preamble byte; a
/// deprecated type, unknown type or wrong body length is reported once the whole body has arrived and
/// the body is skipped by its size field. A frame still arriving gives nothing, and a final lone 90
/// is held. The counting of invalid primitives and the unbind after too many (S-0077) belong to the
/// caller. Memory is bounded by one frame (at most 65540 bytes) plus what the caller has pushed and
/// not yet drained, so the caller should drain after every push. No size limit below the 16-bit
/// field applies (interp S-0166): an over-MTU S_UNIDATA_REQUEST must arrive whole to be rejected
/// with reason 4, so a corrupt size field holds the stream until its body has arrived.
/// </remarks>
public sealed class SisFrameDecoder
{
    private byte[] _buffer = new byte[256];
    private int _start;
    private int _end;
    private bool _scanning;

    /// <summary>The number of bytes held that have not yet produced a result.</summary>
    public int BufferedBytes => _end - _start;

    /// <summary>Adds received bytes.</summary>
    public void Push(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (_buffer.Length - _end < data.Length)
        {
            int live = _end - _start;
            if (_buffer.Length - live >= data.Length && _start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, live);
            }
            else
            {
                var bigger = new byte[Math.Max(_buffer.Length * 2, live + data.Length)];
                Buffer.BlockCopy(_buffer, _start, bigger, 0, live);
                _buffer = bigger;
            }

            _start = 0;
            _end = live;
        }

        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    /// <summary>Returns the next result, or false when more bytes are needed.</summary>
    public bool TryRead(out SisDecodeResult result)
    {
        while (_start < _end)
        {
            if (_scanning)
            {
                int i = _buffer.AsSpan(_start, _end - _start).IndexOf((ReadOnlySpan<byte>)[0x90, 0xEB]);
                if (i < 0)
                {
                    // Hold a final 90, it may begin a preamble.
                    _start = _buffer[_end - 1] == 0x90 ? _end - 1 : _end;
                    break;
                }

                _start += i;
                _scanning = false;
            }

            SisCodec.Step step = SisCodec.Next(_buffer.AsSpan(_start, _end - _start));
            if (step.Status == SisCodec.StepStatus.Need)
            {
                break;
            }

            _start += step.Consumed;
            if (step.Status == SisCodec.StepStatus.Error
                && step.Result!.Error is SisErrorKind.BadSync or SisErrorKind.BadVersion or SisErrorKind.BadSize)
            {
                _scanning = true;
            }

            result = step.Result!;
            return true;
        }

        if (_start == _end)
        {
            _start = _end = 0;
        }

        result = null!;
        return false;
    }
}
