using WebDataStudio.Server.Drivers.Storage;

namespace WebDataStudio.Server.Services;

/// What kind of database a file is.
public enum FileConnectionKind
{
    /// A SQLite database, opened by the SQLite driver.
    Sqlite,

    /// A DuckDB database, opened by the DuckDB driver.
    DuckDb,

    /// A file that is data rather than a database — a Parquet, a CSV, an NDJSON. Not an engine of its
    /// own: the studio already reads these as a storage connection over the folder they lie in.
    DataFile,

    /// Nothing here opens this.
    Unsupported,
}

/// A file as a connection: which engine opens it, what that engine's connection string is, and which
/// files this studio cannot open however they are named.
public static class FileConnections
{
    private static readonly string[] SqliteSuffixes = [".db", ".sqlite", ".sqlite3", ".db3", ".s3db"];
    private static readonly string[] DuckDbSuffixes = [".duckdb", ".ddb"];

    /// The first sixteen bytes of every SQLite database.
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    public static FileConnectionKind KindOf(string path)
    {
        var name = Path.GetFileName(path);

        if (SqliteSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            return FileConnectionKind.Sqlite;

        if (DuckDbSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            return FileConnectionKind.DuckDb;

        // Whatever DuckDB reads as a table — the same list the storage connections use, compression
        // suffixes and all, so a `.csv.gz` means the same thing wherever the studio meets one.
        return StorageReader.CanRead(name) ? FileConnectionKind.DataFile : FileConnectionKind.Unsupported;
    }

    /// A data file is a storage connection rather than a fourth kind of engine: the tree, the data
    /// tab and the query path already know how to read a folder of files through DuckDB, and one file
    /// is a folder with one file in it.
    public static string EngineOf(FileConnectionKind kind) => kind switch
    {
        FileConnectionKind.Sqlite => "sqlite",
        FileConnectionKind.DuckDb => "duckdb",
        _ => "storage",
    };

    /// Whether the file can only be read. A view over a CSV is not something to write through, and
    /// saying so when the connection is made beats a failed UPDATE later.
    public static bool ReadOnlyByNature(FileConnectionKind kind) => kind == FileConnectionKind.DataFile;

    public static string ConnectionStringFor(FileConnectionKind kind, string path) => kind switch
    {
        FileConnectionKind.DataFile => FolderUrlOf(path),
        _ => $"Data Source={path}",
    };

    /// `file:///…` for the folder a data file lies in. Built with Uri rather than by hand: on Linux
    /// the path already starts with a slash, and `"file:///" + path` then names a host called `tmp`.
    private static string FolderUrlOf(string path) =>
        new Uri(Path.GetDirectoryName(Path.GetFullPath(path)) + Path.DirectorySeparatorChar).AbsoluteUri;

    /// Checked because a `.db` is whatever somebody renamed, and a header check is a sentence where
    /// the driver would give a stack trace.
    public static bool LooksLikeSqlite(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[SqliteHeader.Length];

        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
               && head.SequenceEqual(SqliteHeader);
    }

    /// Formats that need an engine this studio does not carry, and the reason said rather than left
    /// as a gap.
    public static string? Refusal(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mdf" or ".ldf" =>
            "a SQL Server data file cannot be opened on its own — it needs a running SQL Server to "
            + "attach it. Attach it to a server and add that server as a connection",
        ".accdb" or ".mdb" =>
            "Access databases need the ACE driver, which exists only on Windows and only as an "
            + "install of its own",
        _ => null,
    };

    /// Which engine a connection string is for, or null when it does not say.
    ///
    /// The same rules the form uses in the browser (`engineFromConnectionString`), because a
    /// connection string that arrives through `?u=` reaches the server first and nothing there was
    /// guessing yet.
    public static string? EngineFromConnectionString(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return null;

        var scheme = trimmed.IndexOf("://", StringComparison.Ordinal) is var at and > 0
            ? trimmed[..at].ToLowerInvariant()
            : null;

        if (scheme is not null && Schemes.TryGetValue(scheme, out var byScheme)) return byScheme;

        var lower = trimmed.ToLowerInvariant();

        if (lower.Contains("initial catalog=")) return "sqlserver";
        if (lower.Contains("host=") && lower.Contains("username=")) return "postgresql";
        if (lower.Contains("server=") && lower.Contains("user id=")) return "sqlserver";
        if (lower.Contains("server=") && lower.Contains("user=")) return "mysql";
        if (lower.StartsWith("data source=", StringComparison.Ordinal)) return "sqlite";

        return null;
    }

    private static readonly Dictionary<string, string> Schemes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["postgres"] = "postgresql", ["postgresql"] = "postgresql",
        ["mysql"] = "mysql", ["mariadb"] = "mysql",
        ["sqlserver"] = "sqlserver", ["mssql"] = "sqlserver",
        ["sqlite"] = "sqlite", ["oracle"] = "oracle", ["duckdb"] = "duckdb",
        ["clickhouse"] = "clickhouse", ["mongodb"] = "mongodb", ["mongodb+srv"] = "mongodb",
        ["redis"] = "redis", ["rediss"] = "redis",
        // A data file behind a URL is a storage connection, the same as one on disk.
        ["s3"] = "storage", ["azblob"] = "storage", ["gs"] = "storage", ["file"] = "storage",
    };
}
