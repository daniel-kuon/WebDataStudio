namespace WebDataStudio.Server.Services;

/// Where a connection somebody makes in the studio goes.
public enum ConnectionScope
{
    /// The connection store: written down, kept across restarts, everybody who may see it does.
    /// What every studio did before this setting existed.
    Stored,

    /// The browser that made it, and nobody else. Nothing on disk, gone when the process stops or
    /// the session expires — a studio put on the internet as a viewer wants this one.
    Session,
}

/// What a deployment lets people do in this studio: where a new connection goes, and which ways in
/// are open at all.
///
/// Two questions rather than one list of switches, because they are independent: a studio can keep
/// the form open and hold what it makes for one browser, or close the form and keep the links. Every
/// default is what the studio did before any of this existed, so a deployment that says nothing
/// notices nothing.
public sealed class StudioAccess
{
    /// Where a connection made here goes.
    public ConnectionScope Scope { get; private init; }

    /// Whether a connection string may be typed or pasted — which includes testing one, the
    /// cheapest door of them all: it opens an arbitrary connection and keeps nothing.
    public bool MayAdd { get; private init; } = true;

    /// Whether a database file may be sent from the browser.
    public bool MayUpload { get; private init; } = true;

    /// Whether the server's own folders may be walked.
    public bool MayBrowse { get; private init; } = true;

    /// How long a session may go quiet before its connections and files are dropped. Null means
    /// never, which is right for a studio one person runs and wrong for one anybody can reach.
    public TimeSpan? SessionTtl { get; private init; } = TimeSpan.FromMinutes(DefaultTtlMinutes);

    /// How many connections one browser may hold at once.
    public int MaxSessionConnections { get; private init; } = DefaultMaxConnections;

    /// The largest database file somebody may send.
    public long UploadMaxBytes { get; private init; } = DefaultUploadMegabytes * 1024L * 1024L;

    private const int DefaultTtlMinutes = 240;
    private const int DefaultMaxConnections = 25;
    private const int DefaultUploadMegabytes = 100;

    public static StudioAccess From(IConfiguration config)
    {
        var scope = string.Equals(config["WDS_CONNECTION_SCOPE"]?.Trim(), "session",
            StringComparison.OrdinalIgnoreCase)
            ? ConnectionScope.Session
            : ConnectionScope.Stored;

        // One combination cannot mean anything: connections held for one browser, and the same
        // connections written to the store for everybody. Said at start-up, where somebody is
        // looking, rather than silently resolved one way in the middle of a request.
        if (scope == ConnectionScope.Session
            && string.Equals(config["WDS_OPEN_FROM_URL_KEEP"], "store", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "WDS_CONNECTION_SCOPE=session holds a connection for the browser that made it, and "
                + "WDS_OPEN_FROM_URL_KEEP=store writes it down for everybody. Pick one: drop the "
                + "keep setting, or set the scope back to stored");

        var ttl = Positive(config["WDS_SESSION_TTL_MINUTES"], DefaultTtlMinutes, zeroMeans: 0);

        return new StudioAccess
        {
            Scope = scope,
            MayAdd = Open(config["WDS_ALLOW_ADD_CONNECTION"]),
            MayUpload = Open(config["WDS_ALLOW_FILE_UPLOAD"]),
            MayBrowse = Open(config["WDS_ALLOW_FILE_BROWSE"]),
            SessionTtl = ttl == 0 ? null : TimeSpan.FromMinutes(ttl),
            MaxSessionConnections =
                Positive(config["WDS_SESSION_MAX_CONNECTIONS"], DefaultMaxConnections),
            UploadMaxBytes =
                Positive(config["WDS_UPLOAD_MAX_MB"], DefaultUploadMegabytes) * 1024L * 1024L,
        };
    }

    /// A door closes on `false` and on nothing else: a typo in a compose file should not take a way
    /// in with it.
    private static bool Open(string? value) =>
        !string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    /// A number that has to be positive, or the default. `zeroMeans` lets one setting keep zero as a
    /// word of its own — the lifetime reads it as "never".
    private static int Positive(string? value, int fallback, int? zeroMeans = null)
    {
        if (!int.TryParse(value, out var number)) return fallback;
        if (number == 0 && zeroMeans is { } instead) return instead;

        return number > 0 ? number : fallback;
    }
}
