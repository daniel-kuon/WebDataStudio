namespace WebDataStudio.Server.Services;

/// The three things a `?u=` entry can be, each its own word in the switch.
public enum UrlEntryKind
{
    /// A path the studio can already reach, inside the folders WDS_FILE_ROOTS allows.
    File,

    /// An http(s) URL to a database or data file, which the studio fetches.
    Download,

    /// A whole connection string, credentials and all.
    ConnectionString,
}

/// What a deployment lets the studio's own URL open.
///
/// Off unless it says otherwise, because a link that opens a database is exactly as dangerous as it
/// sounds. `true` deliberately means only the two kinds that carry no credentials: a connection
/// string in a URL lands in browser history, in proxy logs and in screenshots, so it has to be
/// named.
public sealed class UrlConnectionOptions
{
    private readonly HashSet<UrlEntryKind> _allowed = [];
    private string[] _hosts = [];

    /// Whether `?u=` is looked at at all.
    public bool Enabled => _allowed.Count > 0;

    /// The hosts a download may come from. Empty means no download is allowed, whatever the switch
    /// says.
    public IReadOnlyList<string> Hosts => _hosts;

    /// Whether a connection opened this way is written to the connection store — visible to
    /// everybody, kept across restarts — rather than held for this browser only.
    public bool KeepInStore { get; private init; }

    /// Whether it may write. A viewer does not.
    public bool Writable { get; private init; }

    /// The largest file a download may fetch.
    public long MaxBytes { get; private init; }

    public bool Allows(UrlEntryKind kind) => _allowed.Contains(kind);

    /// A download is refused until a deployment names the hosts: a fetcher inside a network that
    /// takes its address from a URL is a way to reach that network's own addresses.
    public bool HostAllowed(string host) => _hosts.Any(pattern =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
              && host.Length > pattern.Length - 1
            : host.Equals(pattern, StringComparison.OrdinalIgnoreCase));

    public static UrlConnectionOptions From(IConfiguration config)
    {
        var raw = (config["WDS_OPEN_FROM_URL"] ?? "false").Trim();

        var options = new UrlConnectionOptions
        {
            KeepInStore = string.Equals(config["WDS_OPEN_FROM_URL_KEEP"], "store",
                StringComparison.OrdinalIgnoreCase),
            Writable = string.Equals(config["WDS_OPEN_FROM_URL_WRITABLE"], "true",
                StringComparison.OrdinalIgnoreCase),
            MaxBytes = (int.TryParse(config["WDS_OPEN_FROM_URL_MAX_MB"], out var mb) && mb > 0 ? mb : 512)
                       * 1024L * 1024L,
        };

        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
        {
            options._allowed.Add(UrlEntryKind.File);
            options._allowed.Add(UrlEntryKind.Download);
        }
        else
        {
            foreach (var word in raw.Split(',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (word.ToLowerInvariant())
                {
                    case "file": options._allowed.Add(UrlEntryKind.File); break;
                    case "download": options._allowed.Add(UrlEntryKind.Download); break;
                    case "connection-string" or "connectionstring":
                        options._allowed.Add(UrlEntryKind.ConnectionString);
                        break;
                }
            }
        }

        options._hosts = (config["WDS_OPEN_FROM_URL_HOSTS"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return options;
    }
}
