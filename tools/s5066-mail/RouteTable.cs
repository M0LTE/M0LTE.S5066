using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Mail;

/// <summary>What a route matches.</summary>
internal enum RouteKind
{
    /// <summary>One mail address, user@domain.</summary>
    Address,

    /// <summary>Every address at one domain.</summary>
    Domain,

    /// <summary>Every address at any subdomain of a domain ("*.example.org"), not the domain itself.</summary>
    Subdomains,
}

/// <summary>A route pattern, lower case.</summary>
internal sealed record RoutePattern(RouteKind Kind, string Value);

/// <summary>One entry of the table: mail matching <paramref name="Pattern"/> goes to <paramref name="Node"/>.</summary>
internal sealed record MailRoute(RoutePattern Pattern, SisAddress Node);

/// <summary>
/// Which 5066 node a recipient's mail goes to (interp V-0090): an exact address first, then its
/// domain, then the longest "*." parent domain. Case does not matter. No match means the
/// address is not reachable over HF from here.
/// </summary>
internal sealed class RouteTable(IReadOnlyList<MailRoute> routes)
{
    public IReadOnlyList<MailRoute> Routes { get; } = routes;

    /// <summary>Reads "user@example.org", "example.org" or "*.example.org".</summary>
    public static RoutePattern ParsePattern(string text)
    {
        string t = text.Trim().ToLowerInvariant();
        if (t.Length == 0 || t.Any(c => c > 0x7E || c <= 0x20 || c is ',' or '<' or '>'))
        {
            throw new MailConfigException($"routes.match: an address, a domain or *.domain in plain ASCII: {text}");
        }

        if (t.StartsWith("*.", StringComparison.Ordinal))
        {
            return t.Length > 2 && !t[2..].Contains('@', StringComparison.Ordinal) && !t[2..].Contains('*', StringComparison.Ordinal)
                ? new RoutePattern(RouteKind.Subdomains, t[2..])
                : throw new MailConfigException($"routes.match: not *.domain: {text}");
        }

        if (t.Contains('*', StringComparison.Ordinal))
        {
            throw new MailConfigException($"routes.match: * only as the first label, as in *.example.org: {text}");
        }

        int at = t.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
        {
            return new RoutePattern(RouteKind.Domain, t);
        }

        return at > 0 && at == t.LastIndexOf('@') && at < t.Length - 1
            ? new RoutePattern(RouteKind.Address, t)
            : throw new MailConfigException($"routes.match: not user@domain: {text}");
    }

    /// <summary>The node for a recipient, or null when no route matches.</summary>
    public SisAddress? Lookup(string address)
    {
        string a = address.Trim().ToLowerInvariant();
        int at = a.LastIndexOf('@');
        if (at <= 0 || at == a.Length - 1)
        {
            return null;
        }

        string domain = a[(at + 1)..];
        foreach (MailRoute r in Routes)
        {
            if (r.Pattern.Kind == RouteKind.Address && r.Pattern.Value == a)
            {
                return r.Node;
            }
        }

        foreach (MailRoute r in Routes)
        {
            if (r.Pattern.Kind == RouteKind.Domain && r.Pattern.Value == domain)
            {
                return r.Node;
            }
        }

        MailRoute? best = null;
        foreach (MailRoute r in Routes)
        {
            if (r.Pattern.Kind == RouteKind.Subdomains && domain.EndsWith("." + r.Pattern.Value, StringComparison.Ordinal)
                && (best is null || r.Pattern.Value.Length > best.Pattern.Value.Length))
            {
                best = r;
            }
        }

        return best?.Node;
    }
}
