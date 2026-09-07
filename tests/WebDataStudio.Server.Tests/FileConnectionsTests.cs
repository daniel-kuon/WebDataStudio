using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// A file's name decides which engine opens it, and the formats nothing here opens say why.
public class FileConnectionsTests
{
    [Theory]
    [InlineData("shop.db", FileConnectionKind.Sqlite)]
    [InlineData("shop.sqlite", FileConnectionKind.Sqlite)]
    [InlineData("shop.sqlite3", FileConnectionKind.Sqlite)]
    [InlineData("shop.db3", FileConnectionKind.Sqlite)]
    [InlineData("shop.s3db", FileConnectionKind.Sqlite)]
    [InlineData("analytics.duckdb", FileConnectionKind.DuckDb)]
    [InlineData("analytics.ddb", FileConnectionKind.DuckDb)]
    [InlineData("exports.parquet", FileConnectionKind.DataFile)]
    [InlineData("people.csv", FileConnectionKind.DataFile)]
    [InlineData("events.ndjson", FileConnectionKind.DataFile)]
    [InlineData("rows.csv.gz", FileConnectionKind.DataFile)]
    [InlineData("archive.zip", FileConnectionKind.Unsupported)]
    [InlineData("holiday.jpg", FileConnectionKind.Unsupported)]
    public void The_name_says_which_engine_opens_it(string name, FileConnectionKind expected) =>
        Assert.Equal(expected, FileConnections.KindOf(name));

    [Fact]
    public void A_real_database_file_is_opened_by_its_own_driver()
    {
        Assert.Equal("sqlite", FileConnections.EngineOf(FileConnectionKind.Sqlite));
        Assert.Equal("duckdb", FileConnections.EngineOf(FileConnectionKind.DuckDb));

        Assert.Equal("Data Source=/data/files/a/shop.db",
            FileConnections.ConnectionStringFor(FileConnectionKind.Sqlite, "/data/files/a/shop.db"));

        Assert.Equal("Data Source=/data/files/a/analytics.duckdb",
            FileConnections.ConnectionStringFor(FileConnectionKind.DuckDb, "/data/files/a/analytics.duckdb"));

        Assert.False(FileConnections.ReadOnlyByNature(FileConnectionKind.Sqlite));
        Assert.False(FileConnections.ReadOnlyByNature(FileConnectionKind.DuckDb));
    }

    /// A CSV is not a database, and the studio already has a way to read one: a storage connection
    /// over the folder it lies in, queried through DuckDB. So a data file becomes that rather than a
    /// second mechanism — the tree, the data tab and the query path stay exactly what they are.
    [Fact]
    public void A_data_file_becomes_a_storage_connection_over_its_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wds-kind-check");
        var file = Path.Combine(folder, "people.csv");

        Assert.Equal("storage", FileConnections.EngineOf(FileConnectionKind.DataFile));
        Assert.True(FileConnections.ReadOnlyByNature(FileConnectionKind.DataFile));

        var connectionString = FileConnections.ConnectionStringFor(FileConnectionKind.DataFile, file);

        Assert.StartsWith("file:///", connectionString);

        // What matters is not the spelling but that the storage layer gets the folder back out of
        // it — that is the connection the studio will open.
        var target = WebDataStudio.Server.Storage.StorageUrl.Parse(connectionString);

        // A folder URL keeps its trailing slash, which is what makes it a folder; the comparison is
        // about the folder, not about that.
        Assert.Equal(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(target.Container).TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal("", target.Prefix);
    }

    [Fact]
    public void The_formats_that_need_an_engine_we_do_not_have_say_so()
    {
        Assert.Contains("SQL Server", FileConnections.Refusal("db.mdf"));
        Assert.Contains("Windows", FileConnections.Refusal("contacts.accdb"));
        Assert.Contains("Windows", FileConnections.Refusal("old.mdb"));
        Assert.Null(FileConnections.Refusal("shop.sqlite3"));
        Assert.Null(FileConnections.Refusal("people.csv"));
    }

    [Fact]
    public void A_sqlite_file_is_recognised_by_its_header_rather_than_its_name()
    {
        var dir = Directory.CreateTempSubdirectory("wds-file-kind").FullName;
        var real = Path.Combine(dir, "real.db");
        var fake = Path.Combine(dir, "fake.db");
        var tiny = Path.Combine(dir, "tiny.db");

        File.WriteAllBytes(real, "SQLite format 3\0"u8.ToArray());
        File.WriteAllText(fake, "id,name\n1,ada\n");
        File.WriteAllBytes(tiny, [1, 2, 3]);

        try
        {
            Assert.True(FileConnections.LooksLikeSqlite(real));
            Assert.False(FileConnections.LooksLikeSqlite(fake));
            Assert.False(FileConnections.LooksLikeSqlite(tiny));
        }
        finally { TestDirectory.Remove(dir); }
    }

    /// A connection string in a URL has to say which engine it is for. The web app guesses in
    /// TypeScript; the server needs the same answer, because that is where a `?u=` entry arrives.
    [Theory]
    [InlineData("postgres://user:pw@host:5432/shop", "postgresql")]
    [InlineData("mysql://user:pw@host/shop", "mysql")]
    [InlineData("Server=box;Initial Catalog=shop;User Id=sa;Password=p", "sqlserver")]
    [InlineData("Host=box;Database=shop;Username=u;Password=p", "postgresql")]
    [InlineData("Server=box;Database=shop;User Id=sa;Password=p", "sqlserver")]
    [InlineData("Server=box;Database=shop;User=root;Password=p", "mysql")]
    [InlineData("Data Source=/data/shop.db", "sqlite")]
    [InlineData("mongodb://box:27017/events", "mongodb")]
    [InlineData("redis://box:6379", "redis")]
    public void A_connection_string_says_which_engine_it_is_for(string text, string engine) =>
        Assert.Equal(engine, FileConnections.EngineFromConnectionString(text));

    [Fact]
    public void A_connection_string_that_says_nothing_is_not_guessed_at()
    {
        Assert.Null(FileConnections.EngineFromConnectionString("something=else"));
        Assert.Null(FileConnections.EngineFromConnectionString(""));
    }
}
