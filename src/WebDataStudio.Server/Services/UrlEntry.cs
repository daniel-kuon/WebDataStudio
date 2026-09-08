using System.Security.Cryptography;
using System.Text;

namespace WebDataStudio.Server.Services;

/// One entry of `?u=`: what it is, what it says, and what to call it.
public sealed record UrlEntry(UrlEntryKind Kind, string Value, string? Label)
{
    /// The connection's id, derived from the entry rather than invented. So the same link opened
    /// twice is one connection, and a link with a different label is a different one.
    public string Id => "url-" + Fingerprint($"{Label}\u0000{Value}");

    public static IReadOnlyList<UrlEntry> ParseAll(string? parameter) =>
        (parameter ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Parse)
        .OfType<UrlEntry>()
        .ToList();

    public static UrlEntry? Parse(string? entry)
    {
        // The whole entry may be percent-encoded, which is how a connection string keeps its commas
        // and semicolons: `Server=host,1433` would otherwise be two entries.
        var text = Uri.UnescapeDataString((entry ?? "").Replace('+', ' ')).Trim();
        if (text.Length == 0) return null;

        var (label, value) = SplitLabel(text);

        return new UrlEntry(KindOf(value), value, label);
    }

    /// An http(s) URL is something to fetch; a database URL — `postgres://…` — and anything with
    /// keywords in it is a connection string; everything else is a path.
    private static UrlEntryKind KindOf(string value)
    {
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return UrlEntryKind.Download;

        return value.Contains("://", StringComparison.Ordinal) || value.Contains('=')
            ? UrlEntryKind.ConnectionString
            : UrlEntryKind.File;
    }

    /// `sales:/data/x.db` is a label and a path; `C:\data\x.db` and `https://…` are not.
    ///
    /// A label is letters, digits, dash and underscore, and never one character — that rules out a
    /// Windows drive. A scheme is not a label either: what follows a scheme's colon is `//`, and a
    /// path does not start that way.
    private static (string? Label, string Value) SplitLabel(string text)
    {
        var colon = text.IndexOf(':');
        if (colon <= 1) return (null, text);

        var head = text[..colon];
        if (!head.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')) return (null, text);

        var rest = text[(colon + 1)..];
        if (rest.StartsWith("//", StringComparison.Ordinal)) return (null, text);

        return (head, rest);
    }

    /// Short, stable and not reversible: the id shows up in the connection list, and a connection
    /// string is not something to put there.
    private static string Fingerprint(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
}
