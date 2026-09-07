# A database file as a connection, and connections from the URL — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Open a SQLite, DuckDB or data file as a connection — uploaded from the browser or picked inside allowed server roots — and, when a deployment switches it on, open connections named in the studio's own URL.

**Architecture:** One server-side unit decides what a file is (`FileConnections`: extension → engine, connection string, header check) and one decides where files may come from (`FileRoots`). The upload and browse endpoints sit on top of them. The URL half reuses both: `UrlConnectionOptions` reads the switches, `UrlConnections` turns one `?u=` entry into a `ConnectionSpec`, and `SessionConnections` holds the results keyed by a `wds_session` cookie so that `ConnectionRegistry.All()` gains a third source next to environment and store.

**Tech Stack:** .NET 10 minimal APIs, xunit v3 + Microsoft.Testing.Platform (`dotnet run -- -filter "/*/*/Class/*"`), React 19 + Mantine 9 + vitest, Aspire 13.5 for the Nextended method.

**Spec:** `docs/superpowers/specs/2026-09-07-file-and-url-connections-design.md`

## Global Constraints

- Connections are found only through `ConnectionRegistry.Find`/`All()`; anything new must appear there or it does not exist for the rest of the studio.
- `ConnectionSource` gains one value: `Session`. `Environment` stays read-only in the UI, `Stored` keeps its behaviour.
- Defaults, exactly: `WDS_OPEN_FROM_URL=false`, `WDS_OPEN_FROM_URL_HOSTS` empty, `WDS_OPEN_FROM_URL_KEEP=session`, `WDS_OPEN_FROM_URL_WRITABLE=false`, `WDS_OPEN_FROM_URL_MAX_MB=512`, `WDS_FILE_ROOTS` empty.
- `true` for `WDS_OPEN_FROM_URL` means `file,download` and never `connection-string`.
- A refusal names the setting that caused it, in one sentence, in English (the server's language everywhere else).
- Uploaded files live under `<DB_PATH>/files/<connection-id>/`; downloads under `<DB_PATH>/files/url/<hash>/`.
- Data files (`.parquet`, `.csv`, `.tsv`, `.ndjson`, `.jsonl`, `.json`, `.xlsx`) open read-only, whatever the writable switch says.
- Tests: server tests in `tests/WebDataStudio.Server.Tests`, web tests next to their component, `node --test "scripts/**/*.test.mjs"` unaffected. Every task ends green before its commit.
- No `.mdf`, no Access: refused with the reason from the spec.

---

### Task 1: What a file is — extension, engine, connection string

**Files:**
- Create: `src/WebDataStudio.Server/Services/FileConnections.cs`
- Test: `tests/WebDataStudio.Server.Tests/FileConnectionsTests.cs`

**Interfaces:**
- Consumes: `WebDataStudio.Server.Drivers.Storage.StorageReader.CanRead(string)`
- Produces:
  - `FileConnections.KindOf(string path) → FileConnectionKind` where `enum FileConnectionKind { Sqlite, DuckDb, DataFile, Unsupported }`
  - `FileConnections.ConnectionStringFor(FileConnectionKind kind, string path) → string`
  - `FileConnections.EngineOf(FileConnectionKind kind) → string` (`"sqlite"` / `"duckdb"`)
  - `FileConnections.ReadOnlyByNature(FileConnectionKind kind) → bool` (true for `DataFile`)
  - `FileConnections.LooksLikeSqlite(string path) → bool`
  - `FileConnections.Refusal(string path) → string?` — the sentence for `.mdf`, `.accdb`, `.mdb`, else null

- [x] **Step 1: Write the failing test**

```csharp
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// A file's name decides which engine opens it, and the ones nothing here opens say why.
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
    [InlineData("notes.txt", FileConnectionKind.DataFile)]
    [InlineData("archive.zip", FileConnectionKind.Unsupported)]
    public void The_name_says_which_engine_opens_it(string name, FileConnectionKind expected) =>
        Assert.Equal(expected, FileConnections.KindOf(name));

    [Fact]
    public void A_data_file_is_read_only_and_opened_by_duckdb()
    {
        Assert.Equal("duckdb", FileConnections.EngineOf(FileConnectionKind.DataFile));
        Assert.True(FileConnections.ReadOnlyByNature(FileConnectionKind.DataFile));
        Assert.False(FileConnections.ReadOnlyByNature(FileConnectionKind.Sqlite));
    }

    [Fact]
    public void Each_kind_has_the_connection_string_its_driver_wants()
    {
        Assert.Equal("Data Source=/data/files/a/shop.db",
            FileConnections.ConnectionStringFor(FileConnectionKind.Sqlite, "/data/files/a/shop.db"));

        Assert.Equal("Data Source=/data/files/a/analytics.duckdb",
            FileConnections.ConnectionStringFor(FileConnectionKind.DuckDb, "/data/files/a/analytics.duckdb"));

        // A data file has no database of its own: DuckDB opens in memory and reads the file.
        Assert.Equal("Data Source=:memory:",
            FileConnections.ConnectionStringFor(FileConnectionKind.DataFile, "/data/files/a/people.csv"));
    }

    [Fact]
    public void The_formats_that_need_an_engine_we_do_not_have_say_so()
    {
        Assert.Contains("SQL Server", FileConnections.Refusal("db.mdf"));
        Assert.Contains("Windows", FileConnections.Refusal("contacts.accdb"));
        Assert.Null(FileConnections.Refusal("shop.sqlite3"));
    }

    [Fact]
    public void A_sqlite_file_is_recognised_by_its_header_rather_than_its_name()
    {
        var dir = Directory.CreateTempSubdirectory("wds-file-kind").FullName;
        var real = Path.Combine(dir, "real.db");
        var fake = Path.Combine(dir, "fake.db");

        File.WriteAllBytes(real, "SQLite format 3\0"u8.ToArray());
        File.WriteAllText(fake, "id,name\n1,ada\n");

        try
        {
            Assert.True(FileConnections.LooksLikeSqlite(real));
            Assert.False(FileConnections.LooksLikeSqlite(fake));
        }
        finally { TestDirectory.Remove(dir); }
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/FileConnectionsTests/*"`
Expected: compile error — `FileConnections` does not exist.

- [x] **Step 3: Write minimal implementation**

```csharp
using WebDataStudio.Server.Drivers.Storage;

namespace WebDataStudio.Server.Services;

/// What kind of database a file is, and what a driver needs to open it.
public enum FileConnectionKind { Sqlite, DuckDb, DataFile, Unsupported }

/// A file as a connection: which engine, which connection string, and which files this studio
/// cannot open however they are named.
public static class FileConnections
{
    private static readonly string[] SqliteSuffixes = [".db", ".sqlite", ".sqlite3", ".db3", ".s3db"];
    private static readonly string[] DuckDbSuffixes = [".duckdb", ".ddb"];

    public static FileConnectionKind KindOf(string path)
    {
        var name = Path.GetFileName(path);

        if (SqliteSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            return FileConnectionKind.Sqlite;

        if (DuckDbSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            return FileConnectionKind.DuckDb;

        // Whatever DuckDB reads as a table — the same list the storage connections use, so a CSV
        // means the same thing wherever the studio meets one.
        if (StorageReader.CanRead(name) || name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return FileConnectionKind.DataFile;

        return FileConnectionKind.Unsupported;
    }

    public static string EngineOf(FileConnectionKind kind) =>
        kind == FileConnectionKind.Sqlite ? "sqlite" : "duckdb";

    /// A data file is a view over something the studio does not own; writing through it is not a
    /// thing DuckDB offers, so the connection says read-only rather than failing on the first UPDATE.
    public static bool ReadOnlyByNature(FileConnectionKind kind) => kind == FileConnectionKind.DataFile;

    public static string ConnectionStringFor(FileConnectionKind kind, string path) =>
        kind == FileConnectionKind.DataFile ? "Data Source=:memory:" : $"Data Source={path}";

    /// The first sixteen bytes of every SQLite database. Checked because a `.db` is whatever
    /// somebody renamed, and a header check is a sentence where the driver would give a stack.
    public static bool LooksLikeSqlite(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[16];
        return stream.Read(head, 0, head.Length) == head.Length
               && "SQLite format 3\0"u8.SequenceEqual(head);
    }

    /// Formats that need an engine this studio does not carry.
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
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/FileConnectionsTests/*"`
Expected: PASS, 5 tests plus the theory's cases.

- [x] **Step 5: Commit**

```bash
git add src/WebDataStudio.Server/Services/FileConnections.cs tests/WebDataStudio.Server.Tests/FileConnectionsTests.cs
git commit -m "feat(connections): what kind of database a file is"
```

---

### Task 2: Where a file may come from

**Files:**
- Create: `src/WebDataStudio.Server/Services/FileRoots.cs`
- Test: `tests/WebDataStudio.Server.Tests/FileRootsTests.cs`

**Interfaces:**
- Produces:
  - `new FileRoots(IConfiguration config)` — reads `DB_PATH` and `WDS_FILE_ROOTS`
  - `FileRoots.All → IReadOnlyList<string>` (absolute, existing)
  - `FileRoots.Resolve(string path) → string?` — the absolute path when it is inside a root, else null
  - `FileRoots.Uploads → string` — `<DB_PATH>/files`

- [x] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// A file browser on a server is a way to read the server. This one reaches where it was pointed
/// and nowhere else.
public class FileRootsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-roots").FullName;

    public void Dispose() => TestDirectory.Remove(_dir);

    private FileRoots Roots(string? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["DB_PATH"] = Path.Combine(_dir, "data", "wds.db"),
            ["WDS_FILE_ROOTS"] = extra,
        };

        Directory.CreateDirectory(Path.Combine(_dir, "data"));

        return new FileRoots(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    [Fact]
    public void The_data_directory_is_always_a_root()
    {
        var roots = Roots();
        Assert.Contains(Path.Combine(_dir, "data"), roots.All);
    }

    [Fact]
    public void A_path_inside_a_root_resolves_and_one_outside_does_not()
    {
        var mounted = Directory.CreateDirectory(Path.Combine(_dir, "mounted")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;
        var roots = Roots(mounted);

        File.WriteAllText(Path.Combine(mounted, "shop.db"), "x");
        File.WriteAllText(Path.Combine(outside, "secret.db"), "x");

        Assert.NotNull(roots.Resolve(Path.Combine(mounted, "shop.db")));
        Assert.Null(roots.Resolve(Path.Combine(outside, "secret.db")));
    }

    [Fact]
    public void Climbing_out_of_a_root_does_not_work()
    {
        var mounted = Directory.CreateDirectory(Path.Combine(_dir, "mounted")).FullName;
        var roots = Roots(mounted);

        Assert.Null(roots.Resolve(Path.Combine(mounted, "..", "outside", "secret.db")));
        Assert.Null(roots.Resolve(Path.Combine(mounted, "..", "..")));
    }

    [Fact]
    public void Several_roots_are_a_list()
    {
        var one = Directory.CreateDirectory(Path.Combine(_dir, "one")).FullName;
        var two = Directory.CreateDirectory(Path.Combine(_dir, "two")).FullName;

        var roots = Roots($"{one},{two}");

        Assert.Contains(one, roots.All);
        Assert.Contains(two, roots.All);
    }

    [Fact]
    public void Uploads_live_under_the_data_directory()
    {
        Assert.Equal(Path.Combine(_dir, "data", "files"), Roots().Uploads);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/FileRootsTests/*"`
Expected: compile error — `FileRoots` does not exist.

- [x] **Step 3: Write minimal implementation**

```csharp
namespace WebDataStudio.Server.Services;

/// The folders a file connection may name.
///
/// The studio's own data directory always, plus whatever `WDS_FILE_ROOTS` lists — a deployment that
/// mounts a share says so once, and nothing outside those folders is reachable through the browse
/// endpoint or through a `?u=` file entry.
public sealed class FileRoots
{
    private readonly List<string> _roots = [];

    public FileRoots(IConfiguration config)
    {
        var dbPath = config["DB_PATH"] ?? "/data/webdatastudio.db";
        var data = Path.GetDirectoryName(Path.GetFullPath(dbPath));

        if (data is { Length: > 0 }) _roots.Add(data);

        foreach (var extra in (config["WDS_FILE_ROOTS"] ?? "")
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _roots.Add(Path.GetFullPath(extra));
        }

        Uploads = Path.Combine(_roots.Count > 0 ? _roots[0] : ".", "files");
    }

    public IReadOnlyList<string> All => _roots;

    /// Where uploaded databases are kept: inside the data directory, so a deployment that backs that
    /// up has them.
    public string Uploads { get; }

    /// The absolute path, or null when it is not inside a root. `..` is resolved before the check,
    /// which is the whole point of doing this in one place.
    public string? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var full = Path.GetFullPath(path);

        return _roots.Any(root =>
            full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            ? full
            : null;
    }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/FileRootsTests/*"`
Expected: PASS, 5 tests.

- [x] **Step 5: Commit**

```bash
git add src/WebDataStudio.Server/Services/FileRoots.cs tests/WebDataStudio.Server.Tests/FileRootsTests.cs
git commit -m "feat(connections): the folders a file connection may name"
```

---

### Task 3: Uploading a database file

**Files:**
- Create: `src/WebDataStudio.Server/Endpoints/ConnectionFileEndpoints.cs`
- Modify: `src/WebDataStudio.Server/Endpoints/ConnectionEndpoints.cs` (the `DELETE /{id}` handler deletes the uploaded copy)
- Modify: `src/WebDataStudio.Server/Program.cs` (register `FileRoots`, map the new endpoints)
- Test: `tests/WebDataStudio.Server.Tests/FileConnectionEndpointTests.cs`

**Interfaces:**
- Consumes: `FileConnections`, `FileRoots` from Tasks 1–2; `ConnectionStore.Add(ConnectionSpec)`
- Produces:
  - `POST /api/connections/file` — multipart, field `file`, optional `name`; answers `ConnectionDto`
  - `ConnectionFileEndpoints.UploadDirectoryFor(FileRoots roots, string connectionId) → string`

- [x] **Step 1: Write the failing test**

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// A file on somebody's machine becomes a connection, and stops being one when the connection goes.
public class FileConnectionEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-file-upload").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
            })));

    private static MultipartFormDataContent Sqlite(string name)
    {
        // A real SQLite database: header plus the page a fresh file has.
        var bytes = new byte[4096];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(bytes, 0);

        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", name);
        return content;
    }

    [Fact]
    public async Task An_uploaded_file_becomes_a_connection_the_studio_can_see()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/connections/file", Sqlite("shop.sqlite3"), Ct);
        response.EnsureSuccessStatusCode();

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var id = created.RootElement.GetProperty("id").GetString()!;

        Assert.Equal("sqlite", created.RootElement.GetProperty("engine").GetString());
        Assert.Equal("shop.sqlite3", created.RootElement.GetProperty("name").GetString());

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        Assert.Contains(list.RootElement.EnumerateArray(),
            c => c.GetProperty("id").GetString() == id);

        // The copy is under the data directory, not wherever the browser had it.
        var files = Directory.GetFiles(Path.Combine(_dir, "files"), "*.sqlite3", SearchOption.AllDirectories);
        Assert.Single(files);
    }

    [Fact]
    public async Task Deleting_the_connection_deletes_the_file_it_was_given()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/connections/file", Sqlite("gone.db"), Ct);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var id = created.RootElement.GetProperty("id").GetString()!;

        var deleted = await client.DeleteAsync($"/api/connections/{id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "files"), "gone.db", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_says_so()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("id,name\n1,ada\n")), "file", "shop.db");

        var response = await client.PostAsync("/api/connections/file", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("SQLite", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_format_that_needs_an_engine_we_do_not_have_is_refused_with_the_reason()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[16]), "file", "db.mdf");

        var response = await client.PostAsync("/api/connections/file", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("SQL Server", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_data_file_opens_read_only()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("id,name\n1,ada\n")), "file", "people.csv");

        var response = await client.PostAsync("/api/connections/file", content, Ct);
        response.EnsureSuccessStatusCode();

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("duckdb", created.RootElement.GetProperty("engine").GetString());
        Assert.True(created.RootElement.GetProperty("readOnly").GetBoolean());
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/FileConnectionEndpointTests/*"`
Expected: FAIL — 404 on `/api/connections/file`.

- [x] **Step 3: Write minimal implementation**

`ConnectionFileEndpoints.cs`:

```csharp
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// A file as a connection: uploaded from a browser, or picked where the server can see it.
public static class ConnectionFileEndpoints
{
    public static string UploadDirectoryFor(FileRoots roots, string connectionId) =>
        Path.Combine(roots.Uploads, connectionId);

    public static void MapConnectionFileEndpoints(this WebApplication app)
    {
        // 2 GB: a development database is big, and the cap exists so a mistake is a message rather
        // than a full disk.
        app.MapPost("/api/connections/file", async (HttpRequest request, FileRoots roots,
            ConnectionStore store, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { message = "send the file as multipart/form-data" });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files["file"];
            if (file is null || file.Length == 0) return Results.BadRequest(new { message = "no file was sent" });

            if (FileConnections.Refusal(file.FileName) is { } refusal)
                return Results.BadRequest(new { message = refusal });

            var kind = FileConnections.KindOf(file.FileName);
            if (kind == FileConnectionKind.Unsupported)
                return Results.BadRequest(new
                {
                    message = $"'{Path.GetExtension(file.FileName)}' is not a database this studio opens",
                });

            // The id names the folder, so two uploads of the same name do not fight.
            var id = Guid.NewGuid().ToString("n");
            var directory = UploadDirectoryFor(roots, id);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Path.GetFileName(file.FileName));

            await using (var target = File.Create(path))
                await file.CopyToAsync(target, ct);

            if (kind == FileConnectionKind.Sqlite && !FileConnections.LooksLikeSqlite(path))
            {
                Directory.Delete(directory, true);
                return Results.BadRequest(new
                {
                    message = "this file is not a SQLite database — its first bytes are not 'SQLite format 3'",
                });
            }

            var name = form["name"].ToString() is { Length: > 0 } given ? given : Path.GetFileName(file.FileName);

            var spec = store.Add(new ConnectionSpec(id, name, FileConnections.EngineOf(kind),
                FileConnections.ConnectionStringFor(kind, path),
                FileConnections.ReadOnlyByNature(kind), null, null, ConnectionSource.Stored));

            return Results.Ok(ConnectionRegistry.ToDto(spec));
        }).DisableAntiforgery();
    }
}
```

`ConnectionEndpoints.cs`, inside `api.MapDelete("/{id}", …)` after `store.Delete(id)` — and the handler gains `FileRoots roots` as a parameter:

```csharp
            // An uploaded database belongs to its connection: it goes with it rather than staying
            // behind as a file nobody can name any more.
            var uploaded = ConnectionFileEndpoints.UploadDirectoryFor(roots, id);
            if (Directory.Exists(uploaded)) Directory.Delete(uploaded, true);
```

`Program.cs`, next to the other singletons (near line 140) and the other `Map…Endpoints()` calls:

```csharp
builder.Services.AddSingleton<FileRoots>();
...
app.MapConnectionFileEndpoints();
```

- [x] **Step 4: Run test to verify it passes**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/FileConnectionEndpointTests/*"`
Expected: PASS, 5 tests.

- [x] **Step 5: Commit**

```bash
git add src/WebDataStudio.Server/Endpoints/ConnectionFileEndpoints.cs src/WebDataStudio.Server/Endpoints/ConnectionEndpoints.cs src/WebDataStudio.Server/Program.cs tests/WebDataStudio.Server.Tests/FileConnectionEndpointTests.cs
git commit -m "feat(connections): upload a database file and open it"
```

---

### Task 4: Browsing what the server can see

**Files:**
- Modify: `src/WebDataStudio.Server/Endpoints/ConnectionFileEndpoints.cs`
- Test: `tests/WebDataStudio.Server.Tests/FileBrowseEndpointTests.cs`

**Interfaces:**
- Produces: `GET /api/connections/browse?path=…` → `{ path, roots: string[], parent: string?, directories: [{name, path}], files: [{name, path, size, engine}] }`

- [x] **Step 1: Write the failing test**

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// The picker for files the container already has, and the fence around it.
public class FileBrowseEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-browse").FullName;
    private readonly string _mounted;
    private readonly string _outside;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FileBrowseEndpointTests()
    {
        _mounted = Directory.CreateDirectory(Path.Combine(_dir, "mounted")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;

        File.WriteAllText(Path.Combine(_mounted, "shop.sqlite3"), "SQLite format 3\0");
        File.WriteAllText(Path.Combine(_mounted, "notes.zip"), "x");
        Directory.CreateDirectory(Path.Combine(_mounted, "reports"));
        File.WriteAllText(Path.Combine(_outside, "secret.db"), "x");
    }

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "data", "wds.db"),
                ["WDS_FILE_ROOTS"] = _mounted,
            })));

    [Fact]
    public async Task A_root_lists_its_folders_and_the_files_a_driver_opens()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var page = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/connections/browse?path={Uri.EscapeDataString(_mounted)}", Ct));

        var directories = page.RootElement.GetProperty("directories").EnumerateArray()
            .Select(d => d.GetProperty("name").GetString()).ToList();
        var files = page.RootElement.GetProperty("files").EnumerateArray().ToList();

        Assert.Contains("reports", directories);

        // The zip is listed with no engine rather than hidden: seeing it and being told nothing
        // opens it beats wondering where the file went.
        Assert.Contains(files, f => f.GetProperty("name").GetString() == "shop.sqlite3"
                                    && f.GetProperty("engine").GetString() == "sqlite");
        Assert.Contains(files, f => f.GetProperty("name").GetString() == "notes.zip"
                                    && f.GetProperty("engine").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Without_a_path_it_answers_with_the_roots()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var page = JsonDocument.Parse(await client.GetStringAsync("/api/connections/browse", Ct));

        var roots = page.RootElement.GetProperty("roots").EnumerateArray()
            .Select(r => r.GetString()).ToList();

        Assert.Contains(_mounted, roots);
    }

    [Fact]
    public async Task Outside_the_roots_it_refuses_rather_than_lists()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/connections/browse?path={Uri.EscapeDataString(_outside)}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("WDS_FILE_ROOTS", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Climbing_out_with_dot_dot_refuses_too()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var climb = Path.Combine(_mounted, "..", "outside");

        var response = await client.GetAsync(
            $"/api/connections/browse?path={Uri.EscapeDataString(climb)}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/FileBrowseEndpointTests/*"`
Expected: FAIL — 404 on `/api/connections/browse`.

- [x] **Step 3: Write minimal implementation**

Inside `MapConnectionFileEndpoints`:

```csharp
        app.MapGet("/api/connections/browse", (string? path, FileRoots roots) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.Ok(new { path = (string?)null, roots = roots.All, parent = (string?)null,
                    directories = Array.Empty<object>(), files = Array.Empty<object>() });

            if (roots.Resolve(path) is not { } resolved || !Directory.Exists(resolved))
                return Results.Json(new
                {
                    message = "this folder is not one this studio may read; a deployment names the "
                              + "folders it may in WDS_FILE_ROOTS",
                }, statusCode: StatusCodes.Status403Forbidden);

            var parent = Path.GetDirectoryName(resolved) is { } up && roots.Resolve(up) is { } allowed
                ? allowed
                : null;

            return Results.Ok(new
            {
                path = resolved,
                roots = roots.All,
                parent,
                directories = Directory.EnumerateDirectories(resolved)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new { name = Path.GetFileName(d), path = d }),
                files = Directory.EnumerateFiles(resolved)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new
                    {
                        name = Path.GetFileName(f),
                        path = f,
                        size = new FileInfo(f).Length,
                        engine = FileConnections.KindOf(f) is var kind && kind != FileConnectionKind.Unsupported
                            ? FileConnections.EngineOf(kind)
                            : null,
                    }),
            });
        });
```

- [x] **Step 4: Run test to verify it passes**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/FileBrowseEndpointTests/*"`
Expected: PASS, 4 tests.

- [x] **Step 5: Commit**

```bash
git add src/WebDataStudio.Server/Endpoints/ConnectionFileEndpoints.cs tests/WebDataStudio.Server.Tests/FileBrowseEndpointTests.cs
git commit -m "feat(connections): pick a database file the server can see"
```

---

### Task 5: The form gets both ways in

**Files:**
- Create: `web/src/connections/FilePicker.tsx`, `web/src/connections/FilePicker.test.tsx`
- Modify: `web/src/connections/ConnectionForm.tsx`, `web/src/api.ts`
- Test: `web/src/connections/FilePicker.test.tsx`

**Interfaces:**
- Consumes: the two endpoints from Tasks 3–4
- Produces:
  - `api.ts`: `uploadConnectionFile(file: File, name?: string): Promise<ConnectionDto>`, `browseFiles(path?: string): Promise<BrowseDto>` with `interface BrowseDto { path: string | null; roots: string[]; parent: string | null; directories: {name: string; path: string}[]; files: {name: string; path: string; size: number; engine: string | null}[] }`
  - `FilePicker` component: props `{ onUploaded: (created: ConnectionDto) => void; onPicked: (path: string, engine: string | null) => void }`

- [x] **Step 1: Write the failing test**

```tsx
// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { FilePicker } from "./FilePicker";

vi.mock("../api", () => ({
  uploadConnectionFile: vi.fn(async (file: File) => ({
    id: "abc", name: file.name, engine: "sqlite", readOnly: false,
  })),
  browseFiles: vi.fn(async (path?: string) => path === undefined
    ? { path: null, roots: ["/data"], parent: null, directories: [], files: [] }
    : {
        path: "/data", roots: ["/data"], parent: null,
        directories: [{ name: "reports", path: "/data/reports" }],
        files: [{ name: "shop.sqlite3", path: "/data/shop.sqlite3", size: 4096, engine: "sqlite" }],
      }),
}));

afterEach(cleanup);

const show = (props: Partial<Parameters<typeof FilePicker>[0]> = {}) =>
  render(
    <MantineProvider>
      <FilePicker onUploaded={() => {}} onPicked={() => {}} {...props} />
    </MantineProvider>,
  );

describe("the file picker", () => {
  it("uploads the file that was chosen", async () => {
    const onUploaded = vi.fn();
    show({ onUploaded });

    const input = screen.getByLabelText("Database file") as HTMLInputElement;
    fireEvent.change(input, {
      target: { files: [new File(["x"], "shop.sqlite3", { type: "application/octet-stream" })] },
    });

    await waitFor(() => expect(onUploaded).toHaveBeenCalledWith(
      expect.objectContaining({ id: "abc", engine: "sqlite" })));
  });

  it("browses the server and hands back the path that was picked", async () => {
    const onPicked = vi.fn();
    show({ onPicked });

    fireEvent.click(screen.getByRole("button", { name: "Browse the server" }));
    fireEvent.click(await screen.findByText("/data"));
    fireEvent.click(await screen.findByText("shop.sqlite3"));

    await waitFor(() => expect(onPicked).toHaveBeenCalledWith("/data/shop.sqlite3", "sqlite"));
  });
});
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd web && npx vitest run src/connections/FilePicker.test.tsx`
Expected: FAIL — cannot resolve `./FilePicker`.

- [x] **Step 3: Write minimal implementation**

`api.ts`:

```ts
export const uploadConnectionFile = (file: File, name?: string): Promise<ConnectionDto> => {
  const body = new FormData();
  body.append("file", file);
  if (name) body.append("name", name);
  return fetch(`${base}/connections/file`, { method: "POST", body }).then(r => ok<ConnectionDto>(r));
};

export interface BrowseDto {
  path: string | null;
  roots: string[];
  parent: string | null;
  directories: { name: string; path: string }[];
  files: { name: string; path: string; size: number; engine: string | null }[];
}

export const browseFiles = (path?: string): Promise<BrowseDto> =>
  fetch(`${base}/connections/browse${path ? `?path=${encodeURIComponent(path)}` : ""}`)
    .then(r => ok<BrowseDto>(r));
```

`FilePicker.tsx` — a file input labelled `Database file`, a `Browse the server` button that opens a
Mantine `Modal` listing `roots` (when no path yet), then `directories` and `files` for the path in
hand; picking a file calls `onPicked(path, engine)` and closes; a file with `engine === null` is
listed disabled. Upload calls `uploadConnectionFile` and hands the created connection to
`onUploaded`. Errors from either call are shown in an `Alert` inside the component rather than thrown.

`ConnectionForm.tsx`: render `<FilePicker …/>` above the connection-string textarea when the chosen
engine has the `file` field (`ENGINES.find(e => e.id === value.engine)?.fields[0].key === "file"`),
or unconditionally in the dialog's header — the picked path fills `connectionString` as
`Data Source=<path>` and sets the engine the server reported; an upload closes the dialog through
`onUploaded`, because the connection already exists at that point.

- [x] **Step 4: Run test to verify it passes**

Run: `cd web && npx vitest run src/connections/FilePicker.test.tsx && npx tsc -b && npx vitest run`
Expected: PASS — the two picker tests, then the whole suite green.

- [x] **Step 5: Commit**

```bash
git add web/src/connections/FilePicker.tsx web/src/connections/FilePicker.test.tsx web/src/connections/ConnectionForm.tsx web/src/api.ts
git commit -m "feat(connections): a file dialog and a server browser in the form"
```

---

### Task 6: The switches for the URL half

**Files:**
- Create: `src/WebDataStudio.Server/Services/UrlConnectionOptions.cs`
- Test: `tests/WebDataStudio.Server.Tests/UrlConnectionOptionsTests.cs`

**Interfaces:**
- Produces:
  - `enum UrlEntryKind { File, Download, ConnectionString }`
  - `UrlConnectionOptions.From(IConfiguration) → UrlConnectionOptions`
  - properties: `bool Enabled`, `bool Allows(UrlEntryKind)`, `IReadOnlyList<string> Hosts`, `bool HostAllowed(string host)`, `bool KeepInStore`, `bool Writable`, `long MaxBytes`

- [x] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// The switch in front of the URL half. Off unless a deployment says otherwise, and a connection
/// string has to be named on its own.
public class UrlConnectionOptionsTests
{
    private static UrlConnectionOptions Options(params (string Key, string Value)[] settings) =>
        UrlConnectionOptions.From(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build());

    [Fact]
    public void Off_by_default()
    {
        var options = Options();

        Assert.False(options.Enabled);
        Assert.False(options.Allows(UrlEntryKind.File));
        Assert.False(options.Allows(UrlEntryKind.ConnectionString));
    }

    [Fact]
    public void True_means_the_two_that_carry_no_credentials()
    {
        var options = Options(("WDS_OPEN_FROM_URL", "true"));

        Assert.True(options.Allows(UrlEntryKind.File));
        Assert.True(options.Allows(UrlEntryKind.Download));
        Assert.False(options.Allows(UrlEntryKind.ConnectionString));
    }

    [Fact]
    public void A_list_says_exactly_what_is_allowed()
    {
        var options = Options(("WDS_OPEN_FROM_URL", "file, connection-string"));

        Assert.True(options.Allows(UrlEntryKind.File));
        Assert.False(options.Allows(UrlEntryKind.Download));
        Assert.True(options.Allows(UrlEntryKind.ConnectionString));
    }

    [Fact]
    public void A_download_needs_a_host_list_to_be_allowed_at_all()
    {
        Assert.False(Options(("WDS_OPEN_FROM_URL", "download")).HostAllowed("data.example"));

        var listed = Options(("WDS_OPEN_FROM_URL", "download"),
            ("WDS_OPEN_FROM_URL_HOSTS", "data.example, *.blob.core.windows.net"));

        Assert.True(listed.HostAllowed("data.example"));
        Assert.True(listed.HostAllowed("wds.blob.core.windows.net"));
        Assert.False(listed.HostAllowed("evil.example"));
        Assert.False(listed.HostAllowed("blob.core.windows.net"));
    }

    [Fact]
    public void Keeping_writing_and_the_size_cap_have_their_defaults()
    {
        var defaults = Options(("WDS_OPEN_FROM_URL", "true"));

        Assert.False(defaults.KeepInStore);
        Assert.False(defaults.Writable);
        Assert.Equal(512L * 1024 * 1024, defaults.MaxBytes);

        var set = Options(("WDS_OPEN_FROM_URL", "true"), ("WDS_OPEN_FROM_URL_KEEP", "store"),
            ("WDS_OPEN_FROM_URL_WRITABLE", "true"), ("WDS_OPEN_FROM_URL_MAX_MB", "8"));

        Assert.True(set.KeepInStore);
        Assert.True(set.Writable);
        Assert.Equal(8L * 1024 * 1024, set.MaxBytes);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/UrlConnectionOptionsTests/*"`
Expected: compile error — `UrlConnectionOptions` does not exist.

- [x] **Step 3: Write minimal implementation**

```csharp
namespace WebDataStudio.Server.Services;

/// The three kinds a `?u=` entry can be, each its own word in the switch.
public enum UrlEntryKind { File, Download, ConnectionString }

/// What a deployment allows the URL to open.
///
/// Off unless it says otherwise, and `true` deliberately excludes the connection string: a password
/// in a URL is a password in browser history, so that one has to be named.
public sealed class UrlConnectionOptions
{
    private readonly HashSet<UrlEntryKind> _allowed = [];
    private string[] _hosts = [];

    public bool Enabled => _allowed.Count > 0;
    public IReadOnlyList<string> Hosts => _hosts;
    public bool KeepInStore { get; private init; }
    public bool Writable { get; private init; }
    public long MaxBytes { get; private init; }

    public bool Allows(UrlEntryKind kind) => _allowed.Contains(kind);

    /// A download is refused until a deployment names the hosts: an unrestricted fetcher inside a
    /// network is a way to reach that network.
    public bool HostAllowed(string host) => _hosts.Any(pattern =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : host.Equals(pattern, StringComparison.OrdinalIgnoreCase));

    public static UrlConnectionOptions From(IConfiguration config)
    {
        var raw = (config["WDS_OPEN_FROM_URL"] ?? "false").Trim();
        var megabytes = int.TryParse(config["WDS_OPEN_FROM_URL_MAX_MB"], out var mb) && mb > 0 ? mb : 512;

        var options = new UrlConnectionOptions
        {
            KeepInStore = string.Equals(config["WDS_OPEN_FROM_URL_KEEP"], "store", StringComparison.OrdinalIgnoreCase),
            Writable = string.Equals(config["WDS_OPEN_FROM_URL_WRITABLE"], "true", StringComparison.OrdinalIgnoreCase),
            MaxBytes = megabytes * 1024L * 1024L,
        };

        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw.Length == 0)
            return options;

        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
        {
            options._allowed.Add(UrlEntryKind.File);
            options._allowed.Add(UrlEntryKind.Download);
        }
        else
        {
            foreach (var word in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (word.ToLowerInvariant())
                {
                    case "file": options._allowed.Add(UrlEntryKind.File); break;
                    case "download": options._allowed.Add(UrlEntryKind.Download); break;
                    case "connection-string" or "connectionstring": options._allowed.Add(UrlEntryKind.ConnectionString); break;
                }
            }
        }

        options._hosts = (config["WDS_OPEN_FROM_URL_HOSTS"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return options;
    }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/UrlConnectionOptionsTests/*"`
Expected: PASS, 5 tests.

- [x] **Step 5: Commit**

```bash
git add src/WebDataStudio.Server/Services/UrlConnectionOptions.cs tests/WebDataStudio.Server.Tests/UrlConnectionOptionsTests.cs
git commit -m "feat(connections): the switch in front of connections from the URL"
```

---

### Task 7: Reading one `?u=` entry

**Files:**
- Create: `src/WebDataStudio.Server/Services/UrlEntry.cs`
- Test: `tests/WebDataStudio.Server.Tests/UrlEntryTests.cs`

**Interfaces:**
- Produces:
  - `record UrlEntry(UrlEntryKind Kind, string Value, string? Label)`
  - `UrlEntry.Parse(string entry) → UrlEntry?` (null for an empty entry)
  - `UrlEntry.ParseAll(string parameter) → IReadOnlyList<UrlEntry>`

- [x] **Step 1: Write the failing test**

```csharp
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// What `?u=` carries, taken apart. The label is optional, the kind is read off the value.
public class UrlEntryTests
{
    [Fact]
    public void A_path_is_a_file()
    {
        var entry = UrlEntry.Parse("/data/shop.sqlite3")!;

        Assert.Equal(UrlEntryKind.File, entry.Kind);
        Assert.Equal("/data/shop.sqlite3", entry.Value);
        Assert.Null(entry.Label);
    }

    [Fact]
    public void An_http_url_is_a_download()
    {
        var entry = UrlEntry.Parse("https://data.example/orders.parquet")!;

        Assert.Equal(UrlEntryKind.Download, entry.Kind);
        Assert.Equal("https://data.example/orders.parquet", entry.Value);
    }

    [Fact]
    public void Anything_with_keywords_in_it_is_a_connection_string()
    {
        var entry = UrlEntry.Parse("Host=db;Database=shop;Username=reader;Password=p")!;

        Assert.Equal(UrlEntryKind.ConnectionString, entry.Kind);
    }

    [Fact]
    public void A_label_in_front_names_the_connection()
    {
        var entry = UrlEntry.Parse("sales:/data/sales.sqlite3")!;

        Assert.Equal("sales", entry.Label);
        Assert.Equal("/data/sales.sqlite3", entry.Value);
        Assert.Equal(UrlEntryKind.File, entry.Kind);
    }

    [Fact]
    public void A_windows_path_is_not_a_label()
    {
        var entry = UrlEntry.Parse(@"C:\data\shop.db")!;

        Assert.Null(entry.Label);
        Assert.Equal(UrlEntryKind.File, entry.Kind);
    }

    [Fact]
    public void A_scheme_is_not_a_label_either()
    {
        Assert.Null(UrlEntry.Parse("https://data.example/a.parquet")!.Label);
        Assert.Equal(UrlEntryKind.Download, UrlEntry.Parse("orders:https://data.example/a.parquet")!.Kind);
        Assert.Equal("orders", UrlEntry.Parse("orders:https://data.example/a.parquet")!.Label);
    }

    [Fact]
    public void Several_entries_and_an_empty_one()
    {
        var entries = UrlEntry.ParseAll("/data/a.db,,https://data.example/b.parquet");

        Assert.Equal(2, entries.Count);
        Assert.Equal(UrlEntryKind.File, entries[0].Kind);
        Assert.Equal(UrlEntryKind.Download, entries[1].Kind);
    }

    [Fact]
    public void A_connection_string_may_hold_a_comma_when_it_is_percent_encoded()
    {
        // Server=host,1433 is how SQL Server writes a port, and a raw comma would split the entry.
        var entries = UrlEntry.ParseAll("Server%3Dhost%2C1433%3BDatabase%3Dshop%3BUser+Id%3Dsa%3BPassword%3Dp");

        var only = Assert.Single(entries);
        Assert.Equal(UrlEntryKind.ConnectionString, only.Kind);
        Assert.Contains("host,1433", only.Value);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/UrlEntryTests/*"`
Expected: compile error — `UrlEntry` does not exist.

- [x] **Step 3: Write minimal implementation**

```csharp
namespace WebDataStudio.Server.Services;

/// One entry of `?u=`: what it is, what it says, and what to call it.
public sealed record UrlEntry(UrlEntryKind Kind, string Value, string? Label)
{
    public static IReadOnlyList<UrlEntry> ParseAll(string parameter) =>
        (parameter ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Parse)
        .OfType<UrlEntry>()
        .ToList();

    public static UrlEntry? Parse(string entry)
    {
        // The whole entry may be percent-encoded, which is how a connection string keeps its commas
        // and semicolons: Server=host,1433 would otherwise be two entries.
        var text = Uri.UnescapeDataString((entry ?? "").Replace('+', ' ')).Trim();
        if (text.Length == 0) return null;

        var (label, value) = SplitLabel(text);

        var kind = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? UrlEntryKind.Download
            : value.Contains('=')
                ? UrlEntryKind.ConnectionString
                : UrlEntryKind.File;

        return new UrlEntry(kind, value, label);
    }

    /// `sales:/data/x.db` is a label and a path; `C:\data\x.db` and `https://…` are not. A label is
    /// letters, digits, dash and underscore, and never one character on Windows.
    ///
    /// A label that happens to be an engine name — `postgres:Host=…` — is how a connection string
    /// says which engine it is for, and the opener reads it that way before it guesses.
    private static (string? Label, string Value) SplitLabel(string text)
    {
        var colon = text.IndexOf(':');
        if (colon <= 1) return (null, text);

        var head = text[..colon];
        if (!head.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')) return (null, text);

        // A scheme is not a label.
        if (text[(colon + 1)..].StartsWith("//", StringComparison.Ordinal)
            && head is "http" or "https") return (null, text);

        return (head, text[(colon + 1)..]);
    }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/UrlEntryTests/*"`
Expected: PASS, 8 tests.

- [x] **Step 5: Commit**

```bash
git add src/WebDataStudio.Server/Services/UrlEntry.cs tests/WebDataStudio.Server.Tests/UrlEntryTests.cs
git commit -m "feat(connections): read what ?u= carries"
```

---

### Task 8: A connection that belongs to one browser

**Files:**
- Create: `src/WebDataStudio.Server/Services/SessionConnections.cs`
- Modify: `src/WebDataStudio.Server/Models/Connection.cs` (`ConnectionSource` gains `Session`), `src/WebDataStudio.Server/Services/ConnectionRegistry.cs` (third source), `src/WebDataStudio.Server/Program.cs` (cookie middleware + DI)
- Test: `tests/WebDataStudio.Server.Tests/SessionConnectionsTests.cs`

**Interfaces:**
- Produces:
  - `SessionConnections.Key(HttpContext) → string` — reads or sets the `wds_session` cookie
  - `SessionConnections.Add(string key, ConnectionSpec spec) → ConnectionSpec` — idempotent by id
  - `SessionConnections.For(string key) → IReadOnlyList<ConnectionSpec>`
  - `SessionConnections.Current → IReadOnlyList<ConnectionSpec>` — for the request in flight
  - `ConnectionRegistry.All()` includes `SessionConnections.Current`

- [x] **Step 1: Write the failing test**

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// A connection that came out of a link belongs to the browser that opened it: nobody else sees it,
/// and nothing about it is written down.
public class SessionConnectionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-session-conns").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
            })));

    [Fact]
    public async Task Every_client_gets_a_session_cookie()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/connections", Ct);

        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("wds_session=", StringComparison.Ordinal));
    }

    [Fact]
    public void A_session_connection_is_seen_only_under_its_own_key()
    {
        using var factory = Factory();
        _ = factory.CreateClient();

        var sessions = factory.Services.GetRequiredService<SessionConnections>();

        var spec = new ConnectionSpec("s1", "SHOP", "sqlite", "Data Source=:memory:",
            true, null, null, ConnectionSource.Session);

        sessions.Add("cookie-a", spec);

        Assert.Single(sessions.For("cookie-a"));
        Assert.Empty(sessions.For("cookie-b"));
    }

    [Fact]
    public void Adding_the_same_id_twice_is_one_connection()
    {
        using var factory = Factory();
        _ = factory.CreateClient();

        var sessions = factory.Services.GetRequiredService<SessionConnections>();
        var spec = new ConnectionSpec("same", "SHOP", "sqlite", "Data Source=:memory:",
            true, null, null, ConnectionSource.Session);

        sessions.Add("cookie-a", spec);
        sessions.Add("cookie-a", spec with { Name = "SHOP again" });

        var only = Assert.Single(sessions.For("cookie-a"));
        Assert.Equal("SHOP again", only.Name);
    }

    [Fact]
    public async Task Two_clients_do_not_see_each_others_session_connections()
    {
        using var factory = Factory();
        var sessions = factory.Services.GetRequiredService<SessionConnections>();

        using var first = factory.CreateClient();
        using var second = factory.CreateClient();

        // Each client gets its cookie by asking once; the key is then known to the server.
        await first.GetAsync("/api/connections", Ct);
        await second.GetAsync("/api/connections", Ct);

        var keys = sessions.Keys.ToList();
        Assert.Equal(2, keys.Count);

        sessions.Add(keys[0], new ConnectionSpec("only-mine", "MINE", "sqlite",
            "Data Source=:memory:", true, null, null, ConnectionSource.Session));

        static async Task<int> CountAsync(HttpClient client)
        {
            using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
            return list.RootElement.GetArrayLength();
        }

        var owner = keys[0];
        var counts = new[] { await CountAsync(first), await CountAsync(second) };

        // One of them sees it, the other does not — which one depends on which cookie came first.
        Assert.Contains(1, counts);
        Assert.Contains(0, counts);
        Assert.NotNull(owner);
    }

    [Fact]
    public async Task A_session_connection_cannot_be_edited_or_deleted_like_a_stored_one()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await client.GetAsync("/api/connections", Ct);

        var sessions = factory.Services.GetRequiredService<SessionConnections>();
        var key = sessions.Keys.First();

        sessions.Add(key, new ConnectionSpec("from-link", "LINK", "sqlite",
            "Data Source=:memory:", true, null, null, ConnectionSource.Session));

        var deleted = await client.DeleteAsync("/api/connections/from-link", Ct);

        // It is not stored, so there is nothing to delete: the answer says that rather than 500.
        Assert.Equal(HttpStatusCode.BadRequest, deleted.StatusCode);
        Assert.Contains("opened from a link", await deleted.Content.ReadAsStringAsync(Ct));
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/SessionConnectionsTests/*"`
Expected: compile error — `SessionConnections` does not exist, `ConnectionSource.Session` does not exist.

- [x] **Step 3: Write minimal implementation**

`Connection.cs`: `public enum ConnectionSource { Environment, Stored, Session }`

`SessionConnections.cs`:

```csharp
using System.Collections.Concurrent;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Services;

/// Connections that belong to one browser and to this process.
///
/// A studio handed out as a viewer usually has no accounts, so the owner cannot be the signed-in
/// user: the key is a cookie this class sets. Nothing here is written to disk — a password that came
/// in through a URL does not outlive the process, which is the point.
public sealed class SessionConnections(IHttpContextAccessor accessor)
{
    public const string CookieName = "wds_session";

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConnectionSpec>> _byKey = new();

    public IEnumerable<string> Keys => _byKey.Keys;

    /// The key for the request in flight, setting the cookie when the browser has none.
    public static string Key(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var existing) && existing.Length > 0)
            return existing;

        var fresh = Guid.NewGuid().ToString("n");

        context.Response.Cookies.Append(CookieName, fresh, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
        });

        // The cookie is on the way out, so the same request already knows its key.
        context.Items[CookieName] = fresh;
        return fresh;
    }

    public ConnectionSpec Add(string key, ConnectionSpec spec)
    {
        var session = spec with { Source = ConnectionSource.Session };
        _byKey.GetOrAdd(key, _ => new ConcurrentDictionary<string, ConnectionSpec>())[session.Id] = session;
        return session;
    }

    public IReadOnlyList<ConnectionSpec> For(string key) =>
        _byKey.TryGetValue(key, out var found) ? found.Values.ToList() : [];

    /// What the request in flight may see. No context — a background service, a test — means none.
    public IReadOnlyList<ConnectionSpec> Current
    {
        get
        {
            if (accessor.HttpContext is not { } context) return [];

            var key = context.Items.TryGetValue(CookieName, out var fresh) && fresh is string text
                ? text
                : context.Request.Cookies.TryGetValue(CookieName, out var cookie) ? cookie : null;

            return key is { Length: > 0 } ? For(key) : [];
        }
    }
}
```

`ConnectionRegistry`: take `SessionConnections? sessions = null` in the constructor and start `All()`
from `_environment.Concat(_store.List()).Concat(sessions?.Current ?? [])`.

`ConnectionEndpoints`: `MapPut` and `MapDelete` refuse `ConnectionSource.Session` the way they refuse
`Environment`, with `Results.BadRequest(new { message = "this connection was opened from a link; it ends with the session" })`.

`Program.cs`: register `SessionConnections` as a singleton, and one middleware line before the
endpoints so every request has a key: `app.Use(async (ctx, next) => { SessionConnections.Key(ctx); await next(); });`

- [x] **Step 4: Run test to verify it passes**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/SessionConnectionsTests/*"`
Expected: PASS, 5 tests.

- [x] **Step 5: Commit**

```bash
git add src/WebDataStudio.Server/Services/SessionConnections.cs src/WebDataStudio.Server/Services/ConnectionRegistry.cs src/WebDataStudio.Server/Models/Connection.cs src/WebDataStudio.Server/Endpoints/ConnectionEndpoints.cs src/WebDataStudio.Server/Program.cs tests/WebDataStudio.Server.Tests/SessionConnectionsTests.cs
git commit -m "feat(connections): a connection that belongs to one browser"
```

---

### Task 9: Opening what the URL named

**Files:**
- Create: `src/WebDataStudio.Server/Services/UrlConnectionOpener.cs`, `src/WebDataStudio.Server/Endpoints/UrlConnectionEndpoints.cs`
- Modify: `src/WebDataStudio.Server/Program.cs` (DI + map + `AddHttpClient`)
- Test: `tests/WebDataStudio.Server.Tests/UrlConnectionEndpointTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2, 6, 7, 8
- Produces:
  - `record OpenedConnection(string? Id, string Label, string? Refused)`
  - `UrlConnectionOpener.OpenAsync(IReadOnlyList<UrlEntry> entries, string sessionKey, CancellationToken ct) → IReadOnlyList<OpenedConnection>`
  - `POST /api/connections/from-url` with body `{ "u": "…" }` → `{ opened: OpenedConnection[] }`

- [x] **Step 1: Write the failing test**

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// `?u=` opened for real: what the switch allows, what it refuses, and the sentence that says why.
public class UrlConnectionEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-url-conns").FullName;
    private readonly string _files;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public UrlConnectionEndpointTests()
    {
        _files = Directory.CreateDirectory(Path.Combine(_dir, "mounted")).FullName;

        var bytes = new byte[4096];
        System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(bytes, 0);
        File.WriteAllBytes(Path.Combine(_files, "shop.sqlite3"), bytes);
        File.WriteAllText(Path.Combine(_files, "people.csv"), "id,name\n1,ada\n");
    }

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        var config = new Dictionary<string, string?>
        {
            ["DB_PATH"] = Path.Combine(_dir, "data", "wds.db"),
            ["WDS_FILE_ROOTS"] = _files,
        };

        foreach (var (key, value) in settings) config[key] = value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config)));
    }

    private static async Task<JsonDocument> OpenAsync(HttpClient client, string u)
    {
        var response = await client.PostAsJsonAsync("/api/connections/from-url", new { u }, Ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Off_by_default_nothing_opens()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var answer = await OpenAsync(client, Path.Combine(_files, "shop.sqlite3"));
        var only = answer.RootElement.GetProperty("opened").EnumerateArray().Single();

        Assert.Contains("WDS_OPEN_FROM_URL", only.GetProperty("refused").GetString());

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        Assert.Equal(0, list.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task A_file_inside_a_root_opens_read_only_and_is_visible_to_this_client()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"));
        using var client = factory.CreateClient();

        using var answer = await OpenAsync(client, $"sales:{Path.Combine(_files, "shop.sqlite3")}");
        var only = answer.RootElement.GetProperty("opened").EnumerateArray().Single();

        Assert.Null(only.GetProperty("refused").GetString());
        Assert.Equal("sales", only.GetProperty("label").GetString());

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        var connection = list.RootElement.EnumerateArray().Single();

        Assert.Equal("sales", connection.GetProperty("name").GetString());
        Assert.Equal("sqlite", connection.GetProperty("engine").GetString());
        Assert.True(connection.GetProperty("readOnly").GetBoolean());
    }

    [Fact]
    public async Task A_file_outside_the_roots_is_refused_with_the_setting_that_would_allow_it()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"));
        using var client = factory.CreateClient();

        var outside = Path.Combine(_dir, "elsewhere.sqlite3");
        File.WriteAllText(outside, "SQLite format 3\0");

        using var answer = await OpenAsync(client, outside);
        var only = answer.RootElement.GetProperty("opened").EnumerateArray().Single();

        Assert.Contains("WDS_FILE_ROOTS", only.GetProperty("refused").GetString());
    }

    [Fact]
    public async Task A_connection_string_needs_its_own_word_in_the_switch()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "true"));
        using var client = factory.CreateClient();

        using var refused = await OpenAsync(client, "Host=db;Database=shop;Username=u;Password=p");
        Assert.Contains("connection-string", refused.RootElement
            .GetProperty("opened").EnumerateArray().Single().GetProperty("refused").GetString());

        using var allowing = Factory(("WDS_OPEN_FROM_URL", "connection-string"));
        using var second = allowing.CreateClient();

        using var opened = await OpenAsync(second, "shop:Data Source=:memory:");
        Assert.Null(opened.RootElement.GetProperty("opened").EnumerateArray()
            .Single().GetProperty("refused").GetString());
    }

    [Fact]
    public async Task One_refused_entry_does_not_take_the_others_with_it()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"));
        using var client = factory.CreateClient();

        using var answer = await OpenAsync(client,
            $"{Path.Combine(_files, "shop.sqlite3")},{Path.Combine(_dir, "nope.sqlite3")}");

        var opened = answer.RootElement.GetProperty("opened").EnumerateArray().ToList();

        Assert.Equal(2, opened.Count);
        Assert.Null(opened[0].GetProperty("refused").GetString());
        Assert.NotNull(opened[1].GetProperty("refused").GetString());
    }

    [Fact]
    public async Task Opening_the_same_link_twice_is_one_connection()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"));
        using var client = factory.CreateClient();

        var u = Path.Combine(_files, "shop.sqlite3");
        using var _ = await OpenAsync(client, u);
        using var __ = await OpenAsync(client, u);

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        Assert.Equal(1, list.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Writing_is_allowed_only_where_the_deployment_says_so()
    {
        using var writable = Factory(("WDS_OPEN_FROM_URL", "file"), ("WDS_OPEN_FROM_URL_WRITABLE", "true"));
        using var client = writable.CreateClient();

        using var _ = await OpenAsync(client, Path.Combine(_files, "shop.sqlite3"));

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        Assert.False(list.RootElement.EnumerateArray().Single().GetProperty("readOnly").GetBoolean());
    }

    [Fact]
    public async Task A_data_file_stays_read_only_even_when_writing_is_allowed()
    {
        using var writable = Factory(("WDS_OPEN_FROM_URL", "file"), ("WDS_OPEN_FROM_URL_WRITABLE", "true"));
        using var client = writable.CreateClient();

        using var _ = await OpenAsync(client, Path.Combine(_files, "people.csv"));

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        Assert.True(list.RootElement.EnumerateArray().Single().GetProperty("readOnly").GetBoolean());
    }

    [Fact]
    public async Task Keeping_them_in_the_store_is_the_other_branch()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"), ("WDS_OPEN_FROM_URL_KEEP", "store"));
        using var first = factory.CreateClient();
        using var second = factory.CreateClient();

        using var _ = await OpenAsync(first, Path.Combine(_files, "shop.sqlite3"));

        // Stored means everybody sees it, which is exactly what session does not do.
        using var list = JsonDocument.Parse(await second.GetStringAsync("/api/connections", Ct));
        Assert.Equal(1, list.RootElement.GetArrayLength());
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/UrlConnectionEndpointTests/*"`
Expected: FAIL — 404 on `/api/connections/from-url`.

- [x] **Step 3: Write minimal implementation**

`UrlConnectionOpener.cs`: for each entry — check `options.Allows(kind)` (refusal naming
`WDS_OPEN_FROM_URL`), then per kind:

- `File`: `roots.Resolve(value)` (refusal naming `WDS_FILE_ROOTS`), `FileConnections.Refusal`,
  `KindOf`, the SQLite header check, then a spec whose id is `"url-" + Hash(entry)`.
- `Download`: `options.HostAllowed(uri.Host)` (refusal naming `WDS_OPEN_FROM_URL_HOSTS`), fetch with
  `IHttpClientFactory` into `<uploads>/url/<hash>/<name>` with `options.MaxBytes` and the storage
  deadline, then the same as `File`.
- `ConnectionString`: the engine has to be guessed, and the server has no guesser — the web app's
  `engineFromConnectionString` is TypeScript. This task adds
  `FileConnections.EngineFromConnectionString(string) → string?` next to the rest of it, with the
  same rules as the web helper (a `postgres://`-style scheme; `Initial Catalog=` → sqlserver;
  `Host=` with `Username=` → postgresql; `Server=` with `User Id=` → sqlserver; `Server=` with
  `User=` → mysql; a leading `Data Source=` → sqlite). When nothing matches, the entry is refused
  with `"this connection string does not say which engine it is for; name it as the label, for
  example postgres:Host=…"`. Two more cases in `FileConnectionsTests` cover a match and that
  refusal, and a label that is an engine name is what the opener uses before guessing.

`ReadOnly` is `options.Writable is false || FileConnections.ReadOnlyByNature(kind)`. The spec goes to
`sessions.Add(key, spec)` or `store.Add(spec)` depending on `options.KeepInStore`.

`UrlConnectionEndpoints.cs`:

```csharp
app.MapPost("/api/connections/from-url", async (FromUrlRequest body, HttpContext ctx,
    UrlConnectionOpener opener, CancellationToken ct) =>
{
    var entries = UrlEntry.ParseAll(body.U ?? "");
    if (entries.Count == 0) return Results.Ok(new { opened = Array.Empty<OpenedConnection>() });

    return Results.Ok(new { opened = await opener.OpenAsync(entries, SessionConnections.Key(ctx), ct) });
});
```

- [x] **Step 4: Run test to verify it passes**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/UrlConnectionEndpointTests/*"`
Expected: PASS, 9 tests.

- [x] **Step 5: Commit**

```bash
git add src/WebDataStudio.Server/Services/UrlConnectionOpener.cs src/WebDataStudio.Server/Endpoints/UrlConnectionEndpoints.cs src/WebDataStudio.Server/Program.cs tests/WebDataStudio.Server.Tests/UrlConnectionEndpointTests.cs
git commit -m "feat(connections): open what the URL named"
```

---

### Task 10: Fetching a database over http

**Files:**
- Modify: `src/WebDataStudio.Server/Services/UrlConnectionOpener.cs`
- Test: `tests/WebDataStudio.Server.Tests/UrlDownloadTests.cs`

**Interfaces:**
- Consumes: `IHttpClientFactory` named client `"url-connections"`
- Produces: nothing new; the `Download` branch of `OpenAsync` behaves as tested here

- [x] **Step 1: Write the failing test**

The test serves the file from a second in-process host, so the fetch is a real one over a socket:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// A link that points at a database file: fetched, capped, and only from hosts the deployment named.
public class UrlDownloadTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-url-download").FullName;
    private WebApplication _origin = null!;
    private string _originUrl = "";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var bytes = new byte[4096];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(bytes, 0);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _origin = builder.Build();

        _origin.MapGet("/shop.sqlite3", () => Results.Bytes(bytes, "application/octet-stream"));
        _origin.MapGet("/huge.sqlite3", () => Results.Bytes(new byte[3 * 1024 * 1024], "application/octet-stream"));

        await _origin.StartAsync(Ct);
        _originUrl = _origin.Urls.First();
    }

    public async ValueTask DisposeAsync()
    {
        await _origin.StopAsync(Ct);
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        var config = new Dictionary<string, string?> { ["DB_PATH"] = Path.Combine(_dir, "data", "wds.db") };
        foreach (var (key, value) in settings) config[key] = value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config)));
    }

    private static async Task<JsonElement> OpenAsync(HttpClient client, string u)
    {
        var response = await client.PostAsJsonAsync("/api/connections/from-url", new { u }, Ct);
        response.EnsureSuccessStatusCode();
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return answer.RootElement.GetProperty("opened").EnumerateArray().Single().Clone();
    }

    [Fact]
    public async Task A_download_from_a_named_host_becomes_a_connection()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "download"),
            ("WDS_OPEN_FROM_URL_HOSTS", "127.0.0.1"));
        using var client = factory.CreateClient();

        var opened = await OpenAsync(client, $"{_originUrl}/shop.sqlite3");

        Assert.Null(opened.GetProperty("refused").GetString());

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        Assert.Equal("sqlite", list.RootElement.EnumerateArray().Single().GetProperty("engine").GetString());

        // The copy is the studio's, not a stream it re-reads on every query.
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_dir, "data", "files", "url"),
            "*.sqlite3", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Without_a_host_list_a_download_is_refused()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "download"));
        using var client = factory.CreateClient();

        var opened = await OpenAsync(client, $"{_originUrl}/shop.sqlite3");

        Assert.Contains("WDS_OPEN_FROM_URL_HOSTS", opened.GetProperty("refused").GetString());
    }

    [Fact]
    public async Task A_host_that_is_not_in_the_list_is_refused()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "download"),
            ("WDS_OPEN_FROM_URL_HOSTS", "data.example"));
        using var client = factory.CreateClient();

        var opened = await OpenAsync(client, $"{_originUrl}/shop.sqlite3");

        Assert.Contains("127.0.0.1", opened.GetProperty("refused").GetString());
    }

    [Fact]
    public async Task A_file_larger_than_the_cap_is_refused_rather_than_kept()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "download"),
            ("WDS_OPEN_FROM_URL_HOSTS", "127.0.0.1"), ("WDS_OPEN_FROM_URL_MAX_MB", "1"));
        using var client = factory.CreateClient();

        var opened = await OpenAsync(client, $"{_originUrl}/huge.sqlite3");

        Assert.Contains("WDS_OPEN_FROM_URL_MAX_MB", opened.GetProperty("refused").GetString());

        var kept = Directory.Exists(Path.Combine(_dir, "data", "files", "url"))
            ? Directory.GetFiles(Path.Combine(_dir, "data", "files", "url"), "*", SearchOption.AllDirectories)
            : [];

        Assert.Empty(kept);
    }

    [Fact]
    public async Task A_url_that_answers_nothing_useful_says_so()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "download"),
            ("WDS_OPEN_FROM_URL_HOSTS", "127.0.0.1"));
        using var client = factory.CreateClient();

        var opened = await OpenAsync(client, $"{_originUrl}/not-there.sqlite3");

        Assert.Contains("404", opened.GetProperty("refused").GetString());
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/UrlDownloadTests/*"`
Expected: FAIL — the download branch refuses everything or throws.

- [x] **Step 3: Write minimal implementation**

In `UrlConnectionOpener`, the `Download` branch:

```csharp
    private async Task<OpenedConnection> DownloadAsync(UrlEntry entry, string sessionKey, CancellationToken ct)
    {
        if (!Uri.TryCreate(entry.Value, UriKind.Absolute, out var uri))
            return Refused(entry, "this is not a URL the studio can fetch");

        if (!options.HostAllowed(uri.Host))
            return Refused(entry, options.Hosts.Count == 0
                ? "a download needs the hosts it may fetch from in WDS_OPEN_FROM_URL_HOSTS"
                : $"{uri.Host} is not in WDS_OPEN_FROM_URL_HOSTS");

        var name = Path.GetFileName(uri.LocalPath) is { Length: > 0 } file ? file : "download.db";
        var directory = Path.Combine(roots.Uploads, "url", Hash(entry));
        var path = Path.Combine(directory, name);

        // Already fetched: the same link twice is the same connection, not a second download.
        if (!File.Exists(path))
        {
            using var client = clients.CreateClient("url-connections");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
                return Refused(entry, $"{uri} answered {(int)response.StatusCode}");

            if (response.Content.Headers.ContentLength is { } declared && declared > options.MaxBytes)
                return Refused(entry, $"the file is larger than WDS_OPEN_FROM_URL_MAX_MB allows");

            Directory.CreateDirectory(directory);

            try
            {
                await using var target = File.Create(path);
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await CopyCappedAsync(source, target, options.MaxBytes, ct);
            }
            catch (TooLargeException)
            {
                Directory.Delete(directory, true);
                return Refused(entry, "the file is larger than WDS_OPEN_FROM_URL_MAX_MB allows");
            }
        }

        return OpenFile(entry, path, sessionKey);
    }
```

`CopyCappedAsync` copies in 80 KB chunks and throws the private `TooLargeException` past the cap, so
a server that declares no length cannot fill the disk either. The named client gets
`HttpClient.Timeout = TimeSpan.FromMinutes(10)` and `AllowAutoRedirect = false` — a redirect is a
second host, and this list is about hosts.

- [x] **Step 4: Run test to verify it passes**

Run: `cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/UrlDownloadTests/*"`
Expected: PASS, 5 tests.

- [x] **Step 5: Commit**

```bash
git add src/WebDataStudio.Server/Services/UrlConnectionOpener.cs src/WebDataStudio.Server/Program.cs tests/WebDataStudio.Server.Tests/UrlDownloadTests.cs
git commit -m "feat(connections): fetch a database a link points at"
```

---

### Task 11: The studio acts on its own URL

**Files:**
- Modify: `web/src/api.ts`, `web/src/dock/DockShell.tsx` (the start-up effect at line 544, where
  `loadPreferences()` already runs), `web/src/explorer/ExplorerTree.tsx` (the badge)
- Create: `web/src/connections/openFromUrl.ts`, `web/src/connections/openFromUrl.test.ts`
- Test: `web/src/connections/openFromUrl.test.ts`

**Interfaces:**
- Produces:
  - `api.ts`: `openFromUrl(u: string): Promise<{ opened: { id: string | null; label: string; refused: string | null }[] }>`
  - `openFromUrl.ts`: `parameterFrom(search: string): string | null`, `announce(opened): string[]` — the lines to show

- [x] **Step 1: Write the failing test**

```ts
import { describe, expect, it } from "vitest";
import { announce, parameterFrom } from "./openFromUrl";

describe("what the studio does with its own URL", () => {
  it("finds the parameter and leaves other queries alone", () => {
    expect(parameterFrom("?u=/data/shop.db")).toBe("/data/shop.db");
    expect(parameterFrom("?tab=query&u=/data/shop.db")).toBe("/data/shop.db");
    expect(parameterFrom("?tab=query")).toBeNull();
    expect(parameterFrom("")).toBeNull();
  });

  it("says what was opened and what was not", () => {
    const lines = announce([
      { id: "a", label: "sales", refused: null },
      { id: null, label: "orders", refused: "download is not allowed on this studio" },
    ]);

    expect(lines).toEqual(["orders: download is not allowed on this studio"]);
  });
});
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd web && npx vitest run src/connections/openFromUrl.test.ts`
Expected: FAIL — cannot resolve `./openFromUrl`.

- [x] **Step 3: Write minimal implementation**

```ts
/// The studio's own URL as a way to open connections, where the deployment allows it.
///
/// The parameter is read once at start-up and posted to the server, which decides what is allowed:
/// the browser is not the place for that decision, and a client-side check would be a suggestion.
export interface OpenedConnection { id: string | null; label: string; refused: string | null }

export function parameterFrom(search: string): string | null {
  const value = new URLSearchParams(search).get("u");
  return value && value.length > 0 ? value : null;
}

/// Only the refusals are worth a line: what opened is in the tree, where it can be seen.
export const announce = (opened: OpenedConnection[]): string[] =>
  opened.filter(o => o.refused).map(o => `${o.label}: ${o.refused}`);
```

Start-up path: `web/src/dock/DockShell.tsx`, in the same effect that calls `loadPreferences()`
(line 544) — `parameterFrom(location.search)`; when it
returns something, `await openFromUrl(value)`, then refresh the connection list, show
`announce(...)` lines as a warning notification, and remove `u` from the URL with
`history.replaceState` so a reload does not carry a password around a second time. Connections whose
`source` is `Session` get a badge reading `from a link` in the explorer, next to the existing
environment badge.

- [x] **Step 4: Run test to verify it passes**

Run: `cd web && npx vitest run src/connections/openFromUrl.test.ts && npx tsc -b && npx vitest run`
Expected: PASS — two tests, then the whole suite.

- [x] **Step 5: Commit**

```bash
git add web/src/connections/openFromUrl.ts web/src/connections/openFromUrl.test.ts web/src/api.ts web/src/shell/DockShell.tsx web/src/explorer/ExplorerTree.tsx
git commit -m "feat(connections): the studio opens what its own URL names"
```

---

### Task 12: Docs, both languages

**Files:**
- Modify: `docs/guide/connections.md`, `docs/guide/de/connections.md`, `docs/guide/environment.md`, `docs/guide/de/environment.md`, `docs/guide/deploy.md`, `docs/features.md`, `docs/superpowers/specs/2026-08-18-webdatastudio-design.md`, `CHANGELOG.md`

- [x] **Step 1: Write the docs**

- `connections.md` + German: a section **A database file** — upload, browse, the extension table from
  the spec, the read-only note for data files, and the two refusals (`.mdf`, Access) with their
  reasons. A section **A link that opens a connection** — the four example URLs, what each kind
  needs, that it is off by default, and that a session connection ends with the session.
- `environment.md` + German: the six variables in the table it already keeps, defaults exactly as in
  the Global Constraints above.
- `deploy.md`: three sentences in the exposure section — a link carrying a connection string carries
  a password; `WDS_OPEN_FROM_URL=true` deliberately excludes it; a download needs a host list, and why.
- `features.md`: two rows, `F29.5` (a file as a connection) and `F29.6` (connections from the URL),
  and the same two ids in the design spec's table so `FeatureCoverageTests` stays green.
- `CHANGELOG.md`: an `## Unreleased` section above `## 1.3.0` with both features under **Reading
  data** and the switches named.

- [x] **Step 2: Check the links and the coverage test**

Run: `node scripts/check-links.mjs docs && cd tests/WebDataStudio.Server.Tests && dotnet run -- -filter "/*/*/FeatureCoverageTests/*"`
Expected: "all documentation links resolve" and 5 tests passing.

- [x] **Step 3: Commit**

```bash
git add docs CHANGELOG.md
git commit -m "docs: a database file as a connection, and connections from the URL"
```

---

### Task 13: The Aspire integration

**Files (in `C:\dev\privat\github\Nextended`):**
- Modify: `Nextended.Aspire.Hosting.WebDataStudio/Builders/WebDataStudioBuilderExtensions.cs`
- Create: `Nextended.Aspire.Hosting.WebDataStudio/Config/UrlConnections.cs`
- Test: `Tests/Nextended.Aspire.Hosting.WebDataStudio.Tests/OpenFromUrlTests.cs`
- Modify: `Nextended.Aspire.Hosting.WebDataStudio/README.md`, `docs/projects/aspire-webdatastudio.md`, `docs/de/projects/aspire-webdatastudio.md`

**Interfaces:**
- Produces:
  - `enum UrlConnections { Session, Store }`
  - `WithOpenFromUrl(this IResourceBuilder<WebDataStudioResource> builder, bool files = true, bool downloads = false, bool connectionStrings = false, IEnumerable<string>? hosts = null, UrlConnections keep = UrlConnections.Session, bool writable = false, int maxMegabytes = 512)`

- [x] **Step 1: Write the failing test**

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Nextended.Aspire.Hosting.WebDataStudio.Tests;

/// The studio's URL switch, said in the app host rather than in six environment variables.
public class OpenFromUrlTests
{
    private static IDistributedApplicationBuilder Builder() => DistributedApplication.CreateBuilder([]);

    [Fact]
    public void The_words_the_studio_expects_come_out_of_the_parameters()
    {
        var builder = Builder();

        var studio = builder.AddWebDataStudio("studio")
            .WithOpenFromUrl(files: true, downloads: true, connectionStrings: false,
                hosts: ["data.example", "*.blob.core.windows.net"], keep: UrlConnections.Session,
                writable: false, maxMegabytes: 64);

        var env = EnvOf(studio.Resource);

        Assert.Equal("file,download", env["WDS_OPEN_FROM_URL"]);
        Assert.Equal("data.example,*.blob.core.windows.net", env["WDS_OPEN_FROM_URL_HOSTS"]);
        Assert.Equal("session", env["WDS_OPEN_FROM_URL_KEEP"]);
        Assert.Equal("false", env["WDS_OPEN_FROM_URL_WRITABLE"]);
        Assert.Equal("64", env["WDS_OPEN_FROM_URL_MAX_MB"]);
    }

    [Fact]
    public void A_connection_string_has_to_be_asked_for_by_name()
    {
        var builder = Builder();

        var studio = builder.AddWebDataStudio("studio")
            .WithOpenFromUrl(files: false, connectionStrings: true);

        Assert.Equal("connection-string", EnvOf(studio.Resource)["WDS_OPEN_FROM_URL"]);
    }

    [Fact]
    public void A_download_without_hosts_is_refused_where_the_stack_is_described()
    {
        var builder = Builder();

        var error = Assert.Throws<ArgumentException>(() =>
            builder.AddWebDataStudio("studio").WithOpenFromUrl(downloads: true));

        Assert.Contains("hosts", error.Message);
    }

    [Fact]
    public void Allowing_nothing_is_a_mistake_worth_saying()
    {
        var builder = Builder();

        Assert.Throws<ArgumentException>(() =>
            builder.AddWebDataStudio("studio")
                .WithOpenFromUrl(files: false, downloads: false, connectionStrings: false));
    }

    private static Dictionary<string, string> EnvOf(IResource resource)
    {
        var collected = new Dictionary<string, object>();

        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            annotation.Callback(new EnvironmentCallbackContext(
                new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), resource, collected))
                .GetAwaiter().GetResult();

        return collected.ToDictionary(e => e.Key, e => e.Value.ToString() ?? "");
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd C:\dev\privat\github\Nextended && dotnet test Tests/Nextended.Aspire.Hosting.WebDataStudio.Tests/Nextended.Aspire.Hosting.WebDataStudio.Tests.csproj --filter "FullyQualifiedName~OpenFromUrlTests"`
Expected: compile error — `WithOpenFromUrl` does not exist.

- [x] **Step 3: Write minimal implementation**

```csharp
/// Where a connection opened from the URL is kept.
public enum UrlConnections { Session, Store }
```

```csharp
    /// <summary>
    /// Lets the studio's own URL open connections: <c>?u=/data/shop.sqlite3</c>.
    /// </summary>
    /// <remarks>
    /// Off in the studio unless this is called. Each kind is its own parameter because each is its
    /// own risk: a file the container can already read is nothing, a download is a fetcher inside
    /// your network (hence <paramref name="hosts"/>, which is required), and a connection string in
    /// a URL is a password in browser history.
    /// </remarks>
    public static IResourceBuilder<WebDataStudioResource> WithOpenFromUrl(
        this IResourceBuilder<WebDataStudioResource> builder,
        bool files = true, bool downloads = false, bool connectionStrings = false,
        IEnumerable<string>? hosts = null, UrlConnections keep = UrlConnections.Session,
        bool writable = false, int maxMegabytes = 512)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var kinds = new List<string>();
        if (files) kinds.Add("file");
        if (downloads) kinds.Add("download");
        if (connectionStrings) kinds.Add("connection-string");

        if (kinds.Count == 0)
            throw new ArgumentException(
                "WithOpenFromUrl with nothing allowed is the same as not calling it; allow files, "
                + "downloads or connection strings", nameof(files));

        var allowed = (hosts ?? []).Where(h => h is { Length: > 0 }).ToArray();

        if (downloads && allowed.Length == 0)
            throw new ArgumentException(
                "a download needs the hosts it may fetch from: without them the studio would fetch "
                + "whatever a link says, including addresses only this network can reach",
                nameof(hosts));

        return builder
            .WithEnvironment("WDS_OPEN_FROM_URL", string.Join(',', kinds))
            .WithEnvironment("WDS_OPEN_FROM_URL_HOSTS", string.Join(',', allowed))
            .WithEnvironment("WDS_OPEN_FROM_URL_KEEP", keep == UrlConnections.Store ? "store" : "session")
            .WithEnvironment("WDS_OPEN_FROM_URL_WRITABLE", writable ? "true" : "false")
            .WithEnvironment("WDS_OPEN_FROM_URL_MAX_MB", maxMegabytes.ToString(CultureInfo.InvariantCulture));
    }
```

- [x] **Step 4: Run test to verify it passes, and the docs generator**

Run: `cd C:\dev\privat\github\Nextended && dotnet test Tests/Nextended.Aspire.Hosting.WebDataStudio.Tests/Nextended.Aspire.Hosting.WebDataStudio.Tests.csproj --filter "FullyQualifiedName~OpenFromUrlTests" && dotnet run --project tools/ApiRef && pwsh -NoProfile -File tools/Update-PackageDocs.ps1`
Expected: 4 tests passing, the API reference pages regenerated, no drift.

- [x] **Step 5: Commit (in the Nextended repository)**

```bash
git add Nextended.Aspire.Hosting.WebDataStudio Tests/Nextended.Aspire.Hosting.WebDataStudio.Tests/OpenFromUrlTests.cs docs
git commit -m "feat(webdatastudio): WithOpenFromUrl, the studio's URL switch in the app host"
```

---

### Task 14: The whole thing, on both platforms

**Files:** none — this is the gate before the branch is done.

- [ ] **Step 1: The four steps the workflow runs**

```bash
cd C:/dev/privat/github/WebDataStudio
dotnet test
cd web && npx vitest run && npm run build
cd .. && node scripts/check-links.mjs docs && node --test "scripts/**/*.test.mjs"
```

Expected: 0 failed, the web suite green, `tsc -b && vite build` through, links resolving.

- [ ] **Step 2: The same server suite on Linux, because two bugs this month were platform-shaped**

```bash
docker run --rm -v "C:/dev/privat/github/WebDataStudio":/src -v //var/run/docker.sock:/var/run/docker.sock \
  -e HOME=/tmp -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal \
  mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'cd /src && dotnet test'
```

Expected: the only failures are the fourteen that need Azurite and an SSH server the nested sandbox
cannot provide (`AzureBlobObjectStoreTests`, `SshTunnelTests`). Anything else is a real failure.

- [ ] **Step 3: Push both repositories and watch the run**

```bash
git push
gh run list --limit 4
```

Expected: `Test` and `Publish Docker image` green.
