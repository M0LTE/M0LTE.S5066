# s5066-mail: email over STANAG 5066

`s5066-mail` joins an ordinary mail server to a STANAG 5066 node. Mail arriving over HF by CFTP (Annex V, SAP 12) is handed to your SMTP server; mail your SMTP server hands to `s5066-mail` goes over HF to the station it is for. It works with any node that serves the SIS: s5066d, pdn-soundmodem, or another vendor's.

CFTP is for informal mail only [Ed4 V.2, p.V-1]. Over HF, think of it as a slow, reliable post: a short message with link set-up takes a minute or more.

## How it works

- **From HF:** each CFTP mail that arrives is saved to the spool, then given to the `relay` SMTP server with its envelope as sent: MAIL FROM is the return path and RCPT TO each recipient. The same mail sent twice by the far end (it never heard the acknowledgement) is forwarded only once.
- **To HF:** a small SMTP server (`listen`, localhost by default) takes mail for recipients that match a route and refuses the rest. Each recipient's address or domain says which 5066 node it lives behind. Mail for several nodes is split, one CFTP mail per node.
- **When it cannot go:** the mail stays in the spool (`/var/lib/s5066-mail`) and is tried again after 1 minute, then 2, 4 and so on up to once an hour, across restarts. A recipient the relay refuses outright, or mail still not delivered after 5 days, is bounced to the sender with a standard delivery report. Bounces of mail from HF go back over HF when the sender's domain has a route.
- **Size:** the listener advertises the most CFTP can carry at the node's MTU (about 130 MB at 2048 bytes). In practice keep HF mail small: every kilobyte costs airtime.
- **Logging:** one line per event: mail taken in, delivered, tried again, bounced.

## Install

From the packet-net apt repository:

```
sudo apt install s5066-mail
sudo cp /etc/s5066-mail/example.json /etc/s5066-mail/s5066-mail.json
sudo nano /etc/s5066-mail/s5066-mail.json
sudo systemctl enable --now s5066-mail
```

The service runs as the `s5066-mail` user and keeps its spool in `/var/lib/s5066-mail`.

## Configuration

```json
{
  "sis": "127.0.0.1:5066",
  "relay": { "host": "127.0.0.1", "port": 25, "starttls": false },
  "listen": "127.0.0.1:2525",
  "routes": [
    { "match": "far.example.net", "node": "0.0.1.2" },
    { "match": "*.hf.example.org", "node": "0.0.1.3" },
    { "match": "g4abc@example.com", "node": "0.0.1.4" }
  ]
}
```

| Key | Meaning | Default |
|---|---|---|
| `sis` | The node's SIS, `host:port` | `127.0.0.1:5066` |
| `relay` | Where mail from HF goes: `host`, `port` (25), `starttls` (false), `credentials` | required |
| `listen` | The SMTP listener for mail to HF, `address:port`; `null` for none | `127.0.0.1:2525` |
| `routes` | `{ "match", "node" }`: an address, a domain, or `*.domain` for its subdomains | none |
| `hostname` | The name in greetings and bounces | the machine's name |
| `spool` | Where waiting mail is kept | `/var/lib/s5066-mail` |

An address route beats a domain route, which beats the longest `*.domain`. Case does not matter.

`credentials` names a file with the user name on the first line and the password on the second. It must be readable by the service alone:

```
sudo install -m 600 -o s5066-mail -g s5066-mail /dev/null /etc/s5066-mail/relay-credentials
sudo nano /etc/s5066-mail/relay-credentials
```

With `starttls` true the relay must offer STARTTLS and present a valid certificate, or nothing is sent. AUTH uses PLAIN or LOGIN.

The listener has no authentication or TLS: keep it on localhost, or on a network you trust. It only takes mail for routed recipients, so it is not an open relay to the internet, but anyone who can reach it can send over your HF link.

## With Postfix

Send mail for the far station's domain to `s5066-mail`, in `/etc/postfix/transport`:

```
far.example.net    smtp:[127.0.0.1]:2525
```

then `postmap /etc/postfix/transport`, set `transport_maps = hash:/etc/postfix/transport` in `main.cf`, and add the domain to `relay_domains` if Postfix should take mail for it from other machines. Postfix on 127.0.0.1:25 is also the usual `relay` for mail from HF; it accepts from localhost by default.

## Two stations on one machine

This runs everything on one computer with no radio: two bench nodes joined by UDP audio, a gateway for each, and a test mail server that prints what it receives. Node `0.0.1.1` serves `a.test`, node `0.0.1.2` serves `b.test`.

Start the nodes, each in its own terminal (from a checkout of the repository):

```
s5066d src/s5066d/bench-node1.json      # node 0.0.1.1, SIS on 127.0.0.1:5066
s5066d src/s5066d/bench-node2.json      # node 0.0.1.2, SIS on 127.0.0.1:5067
```

Start a test mail server that prints every mail (`sudo apt install python3-aiosmtpd`):

```
python3 -m aiosmtpd -n -l 127.0.0.1:8025
```

`a.json`:

```json
{
  "sis": "127.0.0.1:5066", "spool": "/tmp/mail-a", "hostname": "gw-a.test",
  "relay": { "host": "127.0.0.1", "port": 8025 },
  "listen": "127.0.0.1:2525",
  "routes": [ { "match": "b.test", "node": "0.0.1.2" } ]
}
```

`b.json`:

```json
{
  "sis": "127.0.0.1:5067", "spool": "/tmp/mail-b", "hostname": "gw-b.test",
  "relay": { "host": "127.0.0.1", "port": 8025 },
  "listen": "127.0.0.1:2526",
  "routes": [ { "match": "a.test", "node": "0.0.1.1" } ]
}
```

Run `s5066-mail a.json` and `s5066-mail b.json`, then send a mail into station A:

```
printf 'From: alice@a.test\r\nTo: bob@b.test\r\nSubject: over HF\r\n\r\nHello Bob.\r\n' > hello.eml
curl smtp://127.0.0.1:2525 --mail-from alice@a.test --mail-rcpt bob@b.test --upload-file hello.eml
```

Gateway A logs that it took the mail and, a little later, that node 0.0.1.2 acknowledged it. Gateway B logs the mail from node 0.0.1.1, and the test server prints it with a `Received:` line added by gw-a.test on top. Send to `bob@elsewhere.test` and gateway A refuses it at once: there is no route.

## Where the rules come from

Annex V asks for received mail to be passed to an SMTP server in a normal SMTP dialogue, using the CFTP header for the envelope [Ed4 V.7 step 10, p.V-7], matrix row V-0069. How a gateway picks the node for a recipient, how it forwards, and how it retries and bounces are not in the annex; the choices above are interp rows V-0090 to V-0092 in `matrix/V.tsv` of the conformance repository. The CFTP protocol itself is described in [cftp.md](cftp.md).
