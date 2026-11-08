using System.Globalization;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Samples.HfChat;

// hfchat: HF Operator Chat (Annex O) over a node's SIS.
// Usage: hfchat [--node host:port] [--priority 0-15] [--repeats 0-15]
string node = "127.0.0.1:5066";
byte priority = HfChatSession.DefaultPriority;
byte repeats = 1;
for (int i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--node": node = args[i + 1]; break;
        case "--priority": priority = byte.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        case "--repeats": repeats = byte.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        default:
            Console.Error.WriteLine("usage: hfchat [--node host:port] [--priority 0-15] [--repeats 0-15]");
            return 2;
    }
}

int colon = node.LastIndexOf(':');
await using SisClient client = await SisClient.ConnectAsync(node[..colon], int.Parse(node[(colon + 1)..], CultureInfo.InvariantCulture));
HfChatSession chat = await HfChatSession.StartAsync(client);
chat.Priority = priority;
chat.GroupRepeats = repeats;
chat.LineAdded += line => Console.WriteLine($"[{line.Time:HH:mm:ss}] {line.Text}");
Console.WriteLine($"hfchat on SAP {HfChatSession.Sap}, MTU {chat.Mtu}. Commands: /to w.x.y.z, /all, /group w.x.y.z, /history, /quit");

SisAddress to = HfChatSession.Broadcast;
while (Console.ReadLine() is { } line)
{
    if (line == "/quit")
    {
        break;
    }

    if (line == "/history")
    {
        foreach (ChatLine h in chat.History)
        {
            Console.WriteLine($"[{h.Time:HH:mm:ss}] {h.Text}");
        }
    }
    else if (line == "/all")
    {
        to = HfChatSession.Broadcast;
    }
    else if ((line.StartsWith("/to ", StringComparison.Ordinal) || line.StartsWith("/group ", StringComparison.Ordinal))
        && SisAddress.TryParse(line[(line.IndexOf(' ') + 1)..], line.StartsWith("/group", StringComparison.Ordinal), 7, out SisAddress a))
    {
        to = a;
    }
    else if (line.StartsWith('/'))
    {
        Console.WriteLine("unknown command");
    }
    else
    {
        _ = SendAsync(chat, to, line);
    }
}

return 0;

// Sends without holding up the keyboard; the outcome is shown as a history line.
static async Task SendAsync(HfChatSession chat, SisAddress to, string text)
{
    try
    {
        await chat.SendAsync(to, text);
    }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException)
    {
        Console.WriteLine(e.Message);
    }
}
