using System.Buffers.Binary;

namespace M0LTE.S5066.Radio.Tests;

/// <summary>rx_record's WAV file.</summary>
public class WavRecorderTests
{
    [Fact]
    public void The_file_is_16_bit_mono_with_lengths_kept_up_to_date()
    {
        string path = Path.Combine(Path.GetTempPath(), $"s5066-rx-{Guid.NewGuid():N}.wav");
        try
        {
            using (var wav = new WavRecorder(path, 9600))
            {
                wav.Write(Signals.Tone(9600, 1000, 1.5, 0.5));
                // Readable before the end: the header was brought up to date after the first second.
                byte[] early;
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    early = new byte[44];
                    f.ReadExactly(early);
                }

                BinaryPrimitives.ReadUInt32LittleEndian(early.AsSpan(40)).Should().Be(9600 * 3);
                wav.Write(new float[] { 2f, -2f });
            }

            byte[] all = File.ReadAllBytes(path);
            System.Text.Encoding.ASCII.GetString(all, 0, 4).Should().Be("RIFF");
            BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(22)).Should().Be(1, "mono");
            BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(24)).Should().Be(9600);
            BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(34)).Should().Be(16);
            BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(40)).Should().Be((uint)(all.Length - 44));
            all.Length.Should().Be(44 + (((9600 * 3 / 2) + 2) * 2));
            BinaryPrimitives.ReadInt16LittleEndian(all.AsSpan(all.Length - 4)).Should().Be(short.MaxValue, "clipped to full scale");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
