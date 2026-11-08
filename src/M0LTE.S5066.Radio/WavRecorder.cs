using System.Buffers.Binary;

namespace M0LTE.S5066.Radio;

/// <summary>
/// Writes mono audio to a 16-bit PCM WAV file as it comes (rx_record, core-architecture.md 12.11).
/// The header's lengths are brought up to date about once a second and at the end, so a file cut
/// off by a crash or a kill is still readable up to the last update.
/// </summary>
public sealed class WavRecorder : IDisposable
{
    private const int HeaderBytes = 44;
    private readonly FileStream _file;
    private readonly int _rate;
    private readonly object _gate = new();
    private byte[] _buffer = [];
    private long _dataBytes;
    private long _sinceHeader;
    private bool _disposed;

    /// <summary>Creates (or replaces) the file.</summary>
    public WavRecorder(string path, int sampleRate)
    {
        _rate = sampleRate;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _file.Write(Header(0));
    }

    /// <summary>Appends samples, clipped to full scale.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            int bytes = samples.Length * 2;
            if (_buffer.Length < bytes)
            {
                _buffer = new byte[bytes];
            }

            for (int i = 0; i < samples.Length; i++)
            {
                float s = Math.Clamp(samples[i], -1f, 1f);
                BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(i * 2), (short)MathF.Round(s * short.MaxValue));
            }

            _file.Write(_buffer, 0, bytes);
            _dataBytes += bytes;
            _sinceHeader += samples.Length;
            if (_sinceHeader >= _rate)
            {
                _sinceHeader = 0;
                UpdateHeader();
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            UpdateHeader();
            _file.Dispose();
        }
    }

    private void UpdateHeader()
    {
        long at = _file.Position;
        _file.Position = 0;
        _file.Write(Header(_dataBytes));
        _file.Position = at;
        _file.Flush();
    }

    private byte[] Header(long dataBytes)
    {
        uint data = (uint)Math.Min(dataBytes, uint.MaxValue - HeaderBytes);
        var h = new byte[HeaderBytes];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), data + HeaderBytes - 8);
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)_rate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), (uint)_rate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), data);
        return h;
    }
}
