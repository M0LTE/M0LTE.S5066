# CFTP: mail over STANAG 5066

`M0LTE.S5066.Cftp` sends and receives mail with the Compressed File Transfer Protocol of STANAG 5066 Annex V. It is an ordinary SIS application: it binds SAP 12 on any node that speaks Annex S, `s5066d` or another, and talks to the CFTP client on the far node. CFTP is meant for informal mail only.

## What happens to a mail

1. The mail is packed as four fields: a MessageID, the return path and recipients, the size, and the message itself as an SMTP server would receive it.
2. That is gzip-compressed and wrapped in a small file header (BFTPv1) whose file name is the MessageID.
3. The file is cut into pieces that fit the node's MTU, each with a 4-byte header, and sent ARQ to one node.
4. The far end puts the pieces back together and answers with a two-byte acknowledgement. Then it unpacks the mail.

If no acknowledgement comes within 5 minutes, the whole file goes again; after three tries the send fails. Files to one node go one at a time, and the pieces of a file reach the node in order.

## Using the library

```csharp
await using CftpClient cftp = await CftpClient.ConnectAsync("127.0.0.1", 5066);

var mail = new CftpMail(CftpMail.NewMessageId(),
    new CftpRecipients("m0lte@example.org", ["g4abc@example.org"]),
    File.ReadAllBytes("message.eml"));
CftpSendResult sent = await cftp.SendAsync(SisAddress.FromParts(0, 0, 1, 2), mail);

await foreach (CftpReceived r in cftp.Received.ReadAllAsync())
{
    Console.WriteLine($"{r.Mail?.MessageId} from {r.From.Value:x7}");
}
```

`CftpOptions` changes the acknowledgement wait, the retry delay, the number of tries, the priority (4 by default) and, for a far end known to take several files at once, how many go together.

By default a file is acknowledged as soon as its last byte is in. An application that stores what it receives can hold the acknowledgement until the file is safe, so a crash or a full disk never loses a file the far end thinks was delivered:

```csharp
var options = new CftpOptions { AcceptByApplication = true };
await using CftpClient cftp = await CftpClient.ConnectAsync("127.0.0.1", 5066, options);

await foreach (CftpReceived r in cftp.Received.ReadAllAsync())
{
    if (TrySave(r)) r.Accept();   // the acknowledgement goes now
    else r.Refuse();              // none goes; the far end sends the file again
}
```

A file not answered within the reassembly timeout (1 hour) is let go unacknowledged. A copy that comes again after it was accepted is acknowledged at once and not handed over twice. Interp V-0093 has the details.

The protocol itself is `CftpEngine`, which has no clock, socket or thread of its own, so it can be tested on a virtual clock and ported byte for byte; `CftpClient` drives it over `SisClient`.

## Trying it

`s5066-test` has a `cftp` activity (docs/s5066-test.md):

```
s5066-test cftp --node 127.0.0.1:5066 --to 0.0.1.2 --bytes 5000
s5066-test cftp --node 127.0.0.1:5066 --to 0.0.1.2 --path report.pdf --rcpt g4abc@example.org
```

## With a mail server

`s5066-mail` is a gateway built on this library: it hands mail that arrives over HF to an SMTP server and takes mail from one to send. See [s5066-mail.md](s5066-mail.md).

## Where the rules come from

Every behaviour cites a row of `matrix/V.tsv` in the conformance repository. Where Annex V is silent (timers, retries, duplicates, which bytes count as the Message field, file names) the choice is an `interp` row, V-0071 to V-0089. The byte formats are tested by the corpus kinds `rcop1`, `bftp1`, `cftp_mail`, `cftp_recipients` and `cftp_gzip`.
