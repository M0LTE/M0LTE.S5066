using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// Interp S-0166 (issue #80): a receiver applies no size limit below the 16-bit Size_of_Primitive
/// field, because an over-MTU S_UNIDATA_REQUEST must be read whole to be rejected with reason 4
/// (A-0045, A-0106, interp A-0115). The price is that a corrupt size field holds the stream until
/// that many bytes have arrived (S-0159).
/// </summary>
public class SisFrameDecoderSizeTests
{
    [Fact]
    public void A_frame_far_over_the_mtu_decodes_whole_so_it_can_be_rejected_with_reason_4()
    {
        var d = new SisFrameDecoder();
        var request = new SisUnidataRequest(0, 5, SisAddress.FromParts(0, 0, 1, 2), ServiceType.Default, 0, new byte[4000]);
        d.Push(SisCodec.Encode(request));

        d.TryRead(out SisDecodeResult r).Should().BeTrue();
        r.Primitive.Should().BeOfType<SisUnidataRequest>().Which.UPdu.Length.Should().Be(4000);
        d.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public void A_corrupt_size_field_holds_the_stream_until_its_body_arrives_then_is_skipped()
    {
        // A keep-alive (type 23, body 1 byte) whose size field says 65535: the 65535 bytes after the
        // header are its body, whatever they hold; then it is a body of the wrong length (S-0076,
        // S-0159) and the next frame decodes.
        byte[] keepAlive = SisCodec.Encode(new SisKeepAlive());
        byte[] corrupt = [0x90, 0xEB, 0x00, 0xFF, 0xFF, keepAlive[5]];
        var d = new SisFrameDecoder();
        d.Push(corrupt);

        // Valid frames sent after it are swallowed as its body.
        int pushed = 1;
        while (pushed + keepAlive.Length <= 65535)
        {
            d.Push(keepAlive);
            pushed += keepAlive.Length;
            d.TryRead(out _).Should().BeFalse("the frame is still arriving");
        }

        d.Push(new byte[65535 - pushed]);
        d.TryRead(out SisDecodeResult r).Should().BeTrue();
        r.Error.Should().Be(SisErrorKind.BadLength);

        d.Push(keepAlive);
        d.TryRead(out r).Should().BeTrue();
        r.Primitive.Should().BeOfType<SisKeepAlive>();
        d.BufferedBytes.Should().Be(0);
    }
}
