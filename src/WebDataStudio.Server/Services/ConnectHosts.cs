namespace WebDataStudio.Server.Services;

/// The hosts this studio may open a connection to at all.
///
/// A studio anybody may type a connection string into is an outbound connector from wherever it
/// runs: a visitor can reach addresses only the server can reach, and hammer a database that is not
/// theirs. `WDS_OPEN_FROM_URL_HOSTS` does not help — that one is about fetching a file over http.
/// This list is about connection targets, and it holds wherever the connection came from: the form,
/// a test, a link, the store, the environment.
///
/// Empty by default, which is no restriction: a studio inside a network that already decides where
/// it may go needs nothing here. Set it on anything a stranger can reach.
public sealed class ConnectHosts
{
    private string[] _hosts = [];

    /// Whether there is a list at all.
    public bool Restricted => _hosts.Length > 0;

    public static ConnectHosts From(IConfiguration config) => new()
    {
        _hosts = (config["WDS_CONNECT_HOSTS"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };

    /// The sentence saying why this connection may not be opened, or null when it may.
    public string? Refuse(string engine, string connectionString)
    {
        if (!Restricted) return null;

        // A file is not a network target: a SQLite or DuckDB database has no host for a list to be
        // about, and a folder of files is reached through the storage credentials instead.
        if (WithoutAHost.Contains(engine)) return null;

        if (HostOf(engine, connectionString) is not { Length: > 0 } host)
            return "this studio cannot read a host out of that connection string, and it only "
                   + "opens connections to the hosts named in WDS_CONNECT_HOSTS";

        return Allows(host)
            ? null
            : $"{host} is not one of the hosts this studio may connect to (WDS_CONNECT_HOSTS)";
    }

    /// The same wildcard rule the download list uses, so the two settings behave alike:
    /// `*.example.com` matches one level of subdomain and not the domain itself.
    public bool Allows(string host) => _hosts.Any(pattern =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
              && host.Length > pattern.Length - 1
            : host.Equals(pattern, StringComparison.OrdinalIgnoreCase));

    /// The host a connection string names, or null when it names none this studio can read.
    ///
    /// The six engines with a network endpoint are read by the same code that puts an SSH tunnel in
    /// front of one, so a host is read the same way wherever the studio needs it. Oracle and a
    /// bucket URL are read here, because neither is tunnelled.
    public static string? HostOf(string engine, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        if (WithoutAHost.Contains(engine)) return null;

        try
        {
            if (ConnectionEndpoint.Of(engine, connectionString) is { Host.Length: > 0 } endpoint)
                return endpoint.Host;
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException or FormatException)
        {
            // Not an engine with a tunnelled endpoint, or a string the builder cannot read. Both
            // fall through to the two cases below.
        }

        // A URL is a URL whatever the engine: `oracle://host/service`, `s3://bucket`, and the
        // provider forms of everything else.
        if (Uri.TryCreate(connectionString.Trim(), UriKind.Absolute, out var url)
            && url.Host is { Length: > 0 } fromUrl)
            return fromUrl;

        // Oracle's easy connect: `Data Source=host:1521/service`. Read here rather than guessed by
        // the tunnel code, which does not carry Oracle.
        foreach (var part in connectionString.Split(';', StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0) continue;

            if (!Keys.Contains(part[..equals].Trim())) continue;

            var value = part[(equals + 1)..].Trim();
            // `host:1521/service`, `host,1433`, `host/service` — the host is what comes first.
            var host = value.Split(':', '/', ',', '\\')[0].Trim();

            if (host.Length > 0 && !host.Contains(' ')) return host;
        }

        return null;
    }

    /// Engines whose connection string names a file or a bucket rather than a server.
    private static readonly HashSet<string> WithoutAHost =
        new(StringComparer.OrdinalIgnoreCase) { "sqlite", "duckdb", "storage" };

    /// The keys that carry a host in the engines this has to read by hand.
    private static readonly HashSet<string> Keys =
        new(StringComparer.OrdinalIgnoreCase) { "Data Source", "Host", "Server", "Endpoint" };
}
