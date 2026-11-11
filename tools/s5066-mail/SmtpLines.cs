using System.Text;

namespace M0LTE.S5066.Mail;

/// <summary>A line longer than the reader allows.</summary>
internal sealed class SmtpLineTooLongException() : IOException("line too long");

/// <summary>
/// Reads CR LF terminated lines from an SMTP stream, as octets, with a length limit. The stream
/// can be replaced (after STARTTLS) once the buffer is empty.
/// </summary>
internal sealed class SmtpLines(Stream stream)
{
    private readonly byte[] _buffer = new byte[16384];
    private int _start;
    private int _end;

    public Stream Stream { get; private set; } = stream;

    /// <summary>True when octets the peer sent are waiting to be read.</summary>
    public bool Buffered => _end > _start;

    /// <summary>Uses a new stream from here on, for example the TLS stream over the old one.</summary>
    public void Replace(Stream stream)
    {
        if (Buffered)
        {
            throw new IOException("data arrived before the TLS handshake");
        }

        Stream = stream;
    }

    /// <summary>
    /// The next line without its CR LF, or null at the end of the stream. A bare LF also ends a
    /// line when <paramref name="bareLf"/>; otherwise only CR LF does.
    /// </summary>
    public async Task<byte[]?> ReadLineAsync(int maxLength, bool bareLf, CancellationToken ct)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await Stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
                if (_end == 0)
                {
                    return null;
                }
            }

            int lf = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            int take = lf < 0 ? _end - _start : lf - _start;
            if (line.Length + take > maxLength + 1)
            {
                // Skip the rest of the line, so the next read starts at the next line rather than
                // in the middle of this one (a split line could otherwise end DATA early).
                await SkipLineAsync(ct).ConfigureAwait(false);
                throw new SmtpLineTooLongException();
            }

            line.Write(_buffer, _start, take);
            if (lf < 0)
            {
                _start = _end;
                continue;
            }

            _start = lf + 1;
            byte[] l = line.ToArray();
            if (l.Length > 0 && l[^1] == '\r')
            {
                return l[..^1];
            }

            if (bareLf)
            {
                return l;
            }

            // A bare LF inside a line: keep it as data and read on (RFC 5321 2.3.8).
            line.WriteByte((byte)'\n');
        }
    }

    /// <summary>Discards octets up to and including the next LF, or to the end of the stream.</summary>
    private async Task SkipLineAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await Stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
                if (_end == 0)
                {
                    return;
                }
            }

            int lf = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (lf >= 0)
            {
                _start = lf + 1;
                return;
            }

            _start = _end;
        }
    }

    /// <summary>A line as text, for commands and replies.</summary>
    public async Task<string?> ReadTextLineAsync(int maxLength, CancellationToken ct) =>
        await ReadLineAsync(maxLength, bareLf: true, ct).ConfigureAwait(false) is { } l ? Encoding.Latin1.GetString(l) : null;

    /// <summary>Writes one line and its CR LF.</summary>
    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await Stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), ct).ConfigureAwait(false);
        await Stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>An SMTP reply: the code and its text lines.</summary>
internal sealed record SmtpReply(int Code, IReadOnlyList<string> Lines)
{
    public bool Positive => Code is >= 200 and < 400;

    public bool Transient => Code is >= 400 and < 500;

    public string Text => string.Join(" ", Lines);

    public override string ToString() => $"{Code} {Text}";

    /// <summary>Reads one reply, multi-line or not (RFC 5321 4.2.1).</summary>
    public static async Task<SmtpReply> ReadAsync(SmtpLines lines, CancellationToken ct)
    {
        var text = new List<string>();
        while (true)
        {
            string l = await lines.ReadTextLineAsync(2048, ct).ConfigureAwait(false) ?? throw new IOException("the server closed the connection");
            if (l.Length < 3 || !int.TryParse(l.AsSpan(0, 3), out int code) || code is < 200 or > 599)
            {
                throw new IOException($"not an SMTP reply: {l}");
            }

            text.Add(l.Length > 4 ? l[4..] : "");
            if (l.Length == 3 || l[3] == ' ')
            {
                return new SmtpReply(code, text);
            }

            if (l[3] != '-')
            {
                throw new IOException($"not an SMTP reply: {l}");
            }
        }
    }
}
