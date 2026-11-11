using System.Text;
using M0LTE.S5066.Cftp;

namespace M0LTE.S5066.Cftp.Tests;

/// <summary>The mail layer: SMTP stuffing, the compressed mail file, MessageIDs and safe names.</summary>
public class CftpMailTests
{
    private static readonly byte[] Message = Encoding.ASCII.GetBytes(
        "From: <a@example.org>\r\nTo: <b@example.org>\r\nSubject: hi\r\n\r\n.starts with a dot\r\n..two dots\r\nend\r\n");

    [Fact]
    public void Stuffing_doubles_leading_dots_and_adds_the_terminator()
    {
        // Interp V-0083, V-0051: SMTP DATA octets, dot-stuffed, ending CR LF . CR LF.
        byte[] f = SmtpData.Stuff(Message);
        Encoding.ASCII.GetString(f).Should().Contain("\r\n..starts with a dot\r\n...two dots\r\n");
        f.AsSpan().EndsWith(SmtpData.Terminator).Should().BeTrue();
        SmtpData.Unstuff(f).Should().Equal(Message);
    }

    [Fact]
    public void A_message_without_a_final_CR_LF_gets_one()
    {
        SmtpData.Stuff("x"u8).Should().Equal("x\r\n.\r\n"u8.ToArray());
    }

    [Fact]
    public void A_field_without_the_terminator_is_taken_as_it_is()
    {
        SmtpData.Unstuff("..abc\r\n"u8).Should().Equal("..abc\r\n"u8.ToArray());
    }

    [Fact]
    public void A_mail_round_trips_through_the_compressed_file()
    {
        var mail = new CftpMail("Abc123", new CftpRecipients("", ["b@example.org", "c@example.org"]), Message);
        byte[] file = mail.ToCompressedFile();

        // V-0046, interp V-0082: one gzip member.
        file[..2].Should().Equal(0x1F, 0x8B);
        CftpCompression.TryDecompress(file, out byte[]? plain, out _).Should().BeTrue();
        Encoding.ASCII.GetString(plain!).Should().StartWith("Abc123\n,b@example.org,c@example.org\n");

        CftpMail.TryFromCompressedFile(file, out CftpMail? back, out _, out _).Should().BeTrue();
        back!.MessageId.Should().Be("Abc123");
        back.Envelope.ReturnPath.Should().BeEmpty();
        back.Envelope.Recipients.Should().Equal("b@example.org", "c@example.org");
        back.Message.ToArray().Should().Equal(Message);
    }

    [Fact]
    public void A_file_that_is_not_a_mail_reports_why()
    {
        CftpMail.TryFromCompressedFile("not gzip"u8.ToArray(), out _, out CftpErrorKind e, out string? what).Should().BeFalse();
        e.Should().Be(CftpErrorKind.BadCompression);
        what.Should().NotBeNull();

        byte[] noRecipient = CftpCompression.Compress("id\nonly@example.org\n0\n"u8);
        CftpMail.TryFromCompressedFile(noRecipient, out _, out e, out _).Should().BeFalse();
        e.Should().Be(CftpErrorKind.InvalidField);
    }

    [Fact]
    public void Generated_MessageIDs_are_20_letters_and_digits_and_differ()
    {
        string a = CftpMail.NewMessageId();
        a.Should().HaveLength(20).And.MatchRegex("^[A-Za-z0-9]+$");
        CftpMail.NewMessageId().Should().NotBe(a);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("dot.ted")]
    public void A_MessageID_we_send_is_letters_and_digits(string id)
    {
        var mail = new CftpMail(id, new CftpRecipients("a@b", ["c@d"]), Message);
        mail.Invoking(m => m.ToMailFile()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Limits_on_MessageID_and_RecipientList_are_enforced_on_sending()
    {
        // V-0056, V-0060.
        new CftpMail(new string('a', 255), new CftpRecipients("a@b", ["c@d"]), Message).ToMailFile().MessageId.Should().HaveLength(255);
        new CftpMail(new string('a', 256), new CftpRecipients("a@b", ["c@d"]), Message).Invoking(m => m.ToMailFile()).Should().Throw<ArgumentException>();
        new CftpRecipients("a@b", [new string('c', 10236)]).Invoking(r => r.Encode()).Should().Throw<ArgumentException>();
        new CftpRecipients("a@b", [new string('c', 10235)]).Encode().Should().HaveLength(10239);
        new CftpRecipients("a@b", []).Invoking(r => r.Encode()).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("MSG0001", "MSG0001")]
    [InlineData("../etc/passwd", "cftp_.._etc_passwd")]
    [InlineData("a b/c", "a_b_c")]
    [InlineData("", "cftp_")]
    public void Received_names_are_made_safe(string sent, string stored)
    {
        // Interp V-0084.
        CftpFileNames.Safe(sent).Should().Be(stored);
    }
}
