using System.Net;
using System.Net.Sockets;
using System.Text;

namespace M0LTE.S5066.Host.Station;

/// <summary>
/// The station status page (core-architecture.md 12.12): plain HTTP/1.0 on one address, read
/// only. <c>GET /status.json</c> is <see cref="StationStatus.Snapshot"/>; <c>GET /</c> is a small
/// page that shows it and refreshes every two seconds. Nothing can be changed through it, and it
/// has no password, so it listens where it is told (localhost in the documented setting).
/// </summary>
public sealed class StatusServer : IAsyncDisposable
{
    private const int MaxRequestBytes = 8192;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly StationStatus _status;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;

    /// <summary>Starts listening.</summary>
    /// <param name="endpoint">Where to listen; port 0 picks a free port (<see cref="LocalEndpoint"/>).</param>
    /// <param name="status">What to show.</param>
    /// <exception cref="SocketException">The address cannot be used.</exception>
    public StatusServer(IPEndPoint endpoint, StationStatus status)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(status);
        _status = status;
        _listener = new TcpListener(endpoint);
        _listener.Start();
        _accept = AcceptLoop();
    }

    /// <summary>Where it listens.</summary>
    public IPEndPoint LocalEndpoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Stops listening and waits for the accept loop.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _accept.ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopped under the accept.
        }

        _stop.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(RequestTimeout);
                NetworkStream stream = client.GetStream();
                string? path = await ReadRequest(stream, timeout.Token).ConfigureAwait(false);
                (string status, string type, string body) = path switch
                {
                    null => ("400 Bad Request", "text/plain", "bad request\n"),
                    "/" or "/index.html" => ("200 OK", "text/html; charset=us-ascii", Page),
                    "/status.json" => ("200 OK", "application/json", _status.Snapshot().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n"),
                    _ => ("404 Not Found", "text/plain", "not found\n"),
                };
                byte[] payload = Encoding.UTF8.GetBytes(body);
                string head = $"HTTP/1.0 {status}\r\nContent-Type: {type}\r\nContent-Length: {payload.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // A client that went away or never finished its request.
            }
        }
    }

    /// <summary>Reads the request head; the path of a GET, or null for anything else.</summary>
    private static async Task<string?> ReadRequest(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        int have = 0;
        while (have < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(have), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            have += n;
            if (Encoding.ASCII.GetString(buffer, 0, have).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        string first = Encoding.ASCII.GetString(buffer, 0, have).Split("\r\n", 2)[0];
        string[] parts = first.Split(' ');
        if (parts.Length < 2 || parts[0] != "GET")
        {
            return null;
        }

        return parts[1].Split('?', 2)[0];
    }

    private const string Page = """
<!doctype html>
<html><head><meta charset="us-ascii"><title>s5066d status</title>
<style>
body{font:14px system-ui,sans-serif;margin:1.5em;color:#222}
table{border-collapse:collapse;margin:.5em 0 1.5em}
td,th{border-bottom:1px solid #ddd;padding:.25em .8em;text-align:left}
th{font-weight:600;color:#555}
.tx{color:#a33}.rx{color:#264}
#head span{margin-right:1.5em}
</style></head>
<body>
<h1>s5066d</h1>
<div id="head"></div>
<h2>Peers</h2><table id="peers"></table>
<h2>Last bursts</h2><table id="bursts"></table>
<h2>CW ID</h2><div id="cw"></div>
<p><a href="/status.json">status.json</a></p>
<script>
function v(x){return x===null||x===undefined?"-":String(x)}
function t(x){return x?x.substring(11,19):"-"}
function rows(el,head,list){
  var h="<tr>"+head.map(function(c){return "<th>"+c+"</th>"}).join("")+"</tr>";
  el.innerHTML=h+list.map(function(r){return "<tr>"+r.map(function(c){return "<td>"+c+"</td>"}).join("")+"</tr>"}).join("");
}
function show(s){
  document.getElementById("head").innerHTML=
    "<span>node <b>"+v(s.node)+"</b></span><span>"+(s.transmitting?"<b class=tx>transmitting</b>":"listening")+"</span>"+
    (s.ptt?"<span>PTT "+s.ptt+"</span>":"")+"<span>queued "+s.queued_arq+" ARQ, "+s.queued_non_arq+" non-ARQ</span><span>"+t(s.now)+" UTC</span>";
  rows(document.getElementById("peers"),["peer","link","sending","last sent","hearing","SNR dB","last heard","queued ARQ","queued non-ARQ"],
    s.peers.map(function(p){return [p.peer,p.link,v(p.tx_waveform),t(p.last_sent),v(p.rx_waveform),v(p.rx_snr_db),t(p.last_heard),p.queued_arq,p.queued_non_arq]}));
  rows(document.getElementById("bursts"),["time","","waveform","bytes","SNR dB","peers"],
    s.bursts.map(function(b){return [t(b.at),"<span class="+b.dir+">"+b.dir+"</span>",b.waveform,b.bytes,v(b.snr_db)+(b.cut_short?" (cut short)":""),b.peers.join(" ")]}));
  var c=s.cw_id;
  document.getElementById("cw").innerHTML=c?("\""+c.text+"\" at "+c.wpm+" wpm, "+c.tone_hz+" Hz, "+c.seconds+" s; sent "+c.sent+
    " times, last "+t(c.last_id)+(c.every_minutes?"; every "+c.every_minutes+" min, next with the first transmission from "+t(c.periodic_due_from):"")):"off";
}
function poll(){fetch("/status.json").then(function(r){return r.json()}).then(show).catch(function(){}).finally(function(){setTimeout(poll,2000)})}
poll();
</script>
</body></html>
""";
}
