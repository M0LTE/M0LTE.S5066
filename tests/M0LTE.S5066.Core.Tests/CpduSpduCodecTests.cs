namespace M0LTE.S5066.Core.Tests;

public class CpduSpduCodecTests
{
    [Fact]
    public void Cpdu_encoder_refuses_what_a_node_never_sends()
    {
        // B-0125 Short without Ed4, B-0127 LINK, B-0104 ALE types, field overflow.
        Action[] bad =
        [
            () => CpduCodec.Encode(new PhysicalLinkRequest(true, false, false)),
            () => CpduCodec.Encode(new PhysicalLinkAccepted(false, true, true)),
            () => CpduCodec.Encode(new PhysicalLinkResume()),
            () => CpduCodec.Encode(new AleRelink(0)),
            () => CpduCodec.Encode(new PhysicalLinkRejected(16)),
            () => CpduCodec.Encode(new PhysicalLinkBreakRequest(8, 0)),
        ];
        foreach (Action a in bad)
        {
            a.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void Spdu_encoder_refuses_values_that_do_not_fit()
    {
        Action[] bad =
        [
            () => SpduCodec.Encode(new SpduData(16, 0, 0, false, null, default)),
            () => SpduCodec.Encode(new SpduData(0, 16, 0, false, null, default)),
            () => SpduCodec.Encode(new SpduData(0, 0, 0, false, new SpduTtd(16, 0), default)),
            () => SpduCodec.Encode(new SpduDeliveryFail(16, 0, 0, default)),
        ];
        foreach (Action a in bad)
        {
            a.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void Decoded_records_compare_by_value_including_payload_bytes()
    {
        CpduCodec.Decode([0x00, 1, 2]).Value.Should().Be(new CpduData(new byte[] { 1, 2 }));
        SpduCodec.Decode([0x15, 0x37, 9]).Value.Should().Be(new SpduDeliveryConfirm(5, 3, 7, new byte[] { 9 }));
    }
}
