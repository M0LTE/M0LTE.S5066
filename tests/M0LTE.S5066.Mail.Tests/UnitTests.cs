using System.Text;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Mail.Tests;

/// <summary>Recipient to node mapping (interp V-0090).</summary>
public class RouteTableTests
{
    private static RouteTable Table(params (string Match, string Node)[] routes) =>
        new([.. routes.Select(r => new MailRoute(RouteTable.ParsePattern(r.Match), MailConfig.Address(r.Node, "node")))]);

    [Fact]
    public void An_address_beats_its_domain_which_beats_the_longest_wildcard()
    {
        RouteTable t = Table(("*.example.org", "0.0.0.1"), ("*.hf.example.org", "0.0.0.2"), ("far.hf.example.org", "0.0.0.3"), ("g4abc@far.hf.example.org", "0.0.0.4"));
        t.Lookup("g4abc@far.hf.example.org")!.Value.Dotted.Should().Be("0.0.0.4");
        t.Lookup("other@far.hf.example.org")!.Value.Dotted.Should().Be("0.0.0.3");
        t.Lookup("x@near.hf.example.org")!.Value.Dotted.Should().Be("0.0.0.2");
        t.Lookup("x@mail.example.org")!.Value.Dotted.Should().Be("0.0.0.1");
    }

    [Fact]
    public void Case_does_not_matter_and_a_wildcard_does_not_match_its_own_domain()
    {
        RouteTable t = Table(("*.Example.org", "0.0.0.1"), ("M0LTE@Far.Example.net", "0.0.0.2"));
        t.Lookup("x@SUB.example.ORG")!.Value.Dotted.Should().Be("0.0.0.1");
        t.Lookup("m0lte@far.example.NET")!.Value.Dotted.Should().Be("0.0.0.2");
        t.Lookup("x@example.org").Should().BeNull();
        t.Lookup("nobody@elsewhere.net").Should().BeNull();
        t.Lookup("no-at-sign").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("a,b@example.org")]
    [InlineData("x@*.example.org")]
    [InlineData("ex*ample.org")]
    [InlineData("@example.org")]
    [InlineData("a@b@example.org")]
    public void Bad_patterns_are_refused(string pattern)
    {
        Action act = () => RouteTable.ParsePattern(pattern);
        act.Should().Throw<MailConfigException>();
    }
}

/// <summary>Retries and when to give up (interp V-0092).</summary>
public class RetryScheduleTests
{
    [Fact]
    public void Waits_double_from_one_minute_up_to_an_hour()
    {
        Enumerable.Range(1, 9).Select(n => RetrySchedule.Delay(n).TotalMinutes).Should().Equal(1, 2, 4, 8, 16, 32, 60, 60, 60);
        RetrySchedule.Delay(1000).Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void A_mail_is_given_up_five_days_after_it_was_taken_in()
    {
        var created = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        RetrySchedule.Next(created, 3, created.AddMinutes(10)).Should().Be(created.AddMinutes(14));
        RetrySchedule.Next(created, 200, created.AddDays(5).AddSeconds(-1)).Should().Be(created.AddDays(5).AddSeconds(-1).AddHours(1));
        RetrySchedule.Next(created, 200, created.AddDays(5)).Should().BeNull();
    }
}

/// <summary>The spool keeps mail across a restart (interp V-0092).</summary>
public class SpoolTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static SpoolItem Item(string id, SpoolQueue q = SpoolQueue.Hf) =>
        new(id, q, T0, "m0lte@example.org", ["g4abc@far.example.net"], q == SpoolQueue.Hf ? "0.0.1.2" : null, "ABC123", null) { NextAttempt = T0 };

    [Fact]
    public void A_mail_survives_a_restart_with_its_record_and_octets()
    {
        string dir = Directory.CreateTempSubdirectory("s5066-mail-spool-").FullName;
        var s = new Spool(dir, T0);
        s.Add(Item("a1"), "Subject: hi\r\n\r\nbody\r\n"u8);
        s.Save(Item("a1") with { Failures = 2, NextAttempt = T0.AddMinutes(2), LastError = "relay down" });
        s.Add(Item("b1", SpoolQueue.Smtp), "x"u8);

        var again = new Spool(dir, T0.AddHours(1));
        SpoolItem got = again.List(SpoolQueue.Hf).Should().ContainSingle().Subject;
        got.Failures.Should().Be(2);
        got.NextAttempt.Should().Be(T0.AddMinutes(2));
        got.LastError.Should().Be("relay down");
        got.Recipients.Should().Equal("g4abc@far.example.net");
        got.Node.Should().Be("0.0.1.2");
        Encoding.ASCII.GetString(again.Message(got)).Should().Be("Subject: hi\r\n\r\nbody\r\n");
        again.List(SpoolQueue.Smtp).Should().ContainSingle().Which.Id.Should().Be("b1");

        again.Remove(got);
        new Spool(dir, T0).List(SpoolQueue.Hf).Should().BeEmpty();
    }

    [Fact]
    public void Half_written_mail_is_cleared_at_start()
    {
        string dir = Directory.CreateTempSubdirectory("s5066-mail-spool-").FullName;
        _ = new Spool(dir, T0);
        File.WriteAllText(Path.Combine(dir, "tmp", "x.eml"), "partial");
        File.WriteAllText(Path.Combine(dir, "hf", "y.eml"), "no record");
        var s = new Spool(dir, T0);
        Directory.EnumerateFiles(Path.Combine(dir, "tmp")).Should().BeEmpty();
        File.Exists(Path.Combine(dir, "hf", "y.eml")).Should().BeFalse();
        s.List(SpoolQueue.Hf).Should().BeEmpty();
    }

    [Fact]
    public void Received_MessageIDs_are_remembered_across_a_restart_for_a_week()
    {
        string dir = Directory.CreateTempSubdirectory("s5066-mail-spool-").FullName;
        var s = new Spool(dir, T0);
        s.Seen("0.0.1.2", "M1", T0).Should().BeFalse();
        s.Remember("0.0.1.2", "M1", T0);
        s.Seen("0.0.1.2", "M1", T0.AddDays(1)).Should().BeTrue();
        s.Seen("0.0.1.3", "M1", T0).Should().BeFalse("the same MessageID from another node is another mail");

        new Spool(dir, T0.AddDays(6)).Seen("0.0.1.2", "M1", T0.AddDays(6)).Should().BeTrue();
        new Spool(dir, T0.AddDays(8)).Seen("0.0.1.2", "M1", T0.AddDays(8)).Should().BeFalse();
    }
}

/// <summary>The configuration file.</summary>
public class MailConfigTests
{
    [Fact]
    public void The_shipped_example_parses()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "example.json");
        MailConfig c = MailConfig.Load(path);
        c.SisPort.Should().Be(5066);
        c.Relay!.Port.Should().Be(25);
        c.Listen!.Port.Should().Be(2525);
        c.Routes.Lookup("g4abc@far.example.net")!.Value.Should().Be(SisAddress.FromParts(0, 0, 1, 2));
    }

    [Theory]
    [InlineData("""{ "relay": { "host": "h" }, "colour": 1 }""", "unknown key colour")]
    [InlineData("""{ }""", "relay: needed")]
    [InlineData("""{ "relay": { "host": "h", "port": 0 } }""", "relay.port")]
    [InlineData("""{ "relay": { "host": "h" }, "routes": [ { "match": "x.org", "node": "16.0.0.1" } ] }""", "routes.node")]
    [InlineData("""{ "relay": { "host": "h" }, "listen": "localhost:25" }""", "listen")]
    public void Mistakes_are_named(string json, string what)
    {
        Action act = () => MailConfig.Parse(json);
        act.Should().Throw<MailConfigException>().Which.Message.Should().Contain(what);
    }

    [Fact]
    public void Listen_null_turns_the_listener_off()
    {
        MailConfig.Parse("""{ "relay": { "host": "h" }, "listen": null }""").Listen.Should().BeNull();
    }

    [Fact]
    public void A_credentials_file_others_can_read_is_refused()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes");
            return;
        }

        string f = Path.GetTempFileName();
        File.WriteAllText(f, "user\npass\n");
        File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        Action act = () => SmtpCredentials.Load(f);
        act.Should().Throw<MailConfigException>().Which.Message.Should().Contain("chmod 600");
        File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        SmtpCredentials.Load(f).Should().Be(new SmtpCredentials("user", "pass"));
    }
}

/// <summary>Bounces (interp V-0092).</summary>
public class DsnTests
{
    [Fact]
    public void A_bounce_is_a_delivery_status_report_with_the_original_headers()
    {
        byte[] original = "From: m0lte@example.org\r\nSubject: test\r\n\r\nsecret body\r\n"u8.ToArray();
        var t = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        string dsn = Encoding.ASCII.GetString(Dsn.Build("gw.example.org", "m0lte@example.org",
            [new FailedRecipient("g4abc@far.example.net", "5.1.1", "550 5.1.1 no such user")], original, t, t.AddMinutes(5)));
        dsn.Should().Contain("From: Mail Delivery System <MAILER-DAEMON@gw.example.org>\r\n");
        dsn.Should().Contain("To: <m0lte@example.org>\r\n");
        dsn.Should().Contain("Content-Type: multipart/report; report-type=delivery-status;");
        dsn.Should().Contain("Final-Recipient: rfc822; g4abc@far.example.net\r\nAction: failed\r\nStatus: 5.1.1\r\n");
        dsn.Should().Contain("Subject: test\r\n");
        dsn.Should().NotContain("secret body");
        dsn.Should().Contain("Arrival-Date: Wed, 11 Nov 2026 12:00:00 +0000");
    }
}
