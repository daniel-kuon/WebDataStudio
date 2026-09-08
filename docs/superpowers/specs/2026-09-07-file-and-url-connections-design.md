# A database file as a connection, and connections from the URL

Two features that lean on each other. The first makes a file on somebody's machine into a connection
the studio can open. The second lets a link do it, so a studio can be handed out the way a document
viewer is — and that one has to be off unless a deployment says otherwise, because a link that opens
a database is exactly as dangerous as it sounds.

## Why

The Add-Connection form already has a **File path** field for SQLite and DuckDB. It takes a path *on
the server*, typed as text, which is right for a mounted volume and useless for the file on the
laptop in front of you: the container cannot see it. So the studio can browse a folder full of
Parquet files (a storage connection) but cannot open the `.sqlite3` somebody just downloaded.

And a studio behind a URL is already how people use it. Being able to write the database into that
URL turns it into something else: send a colleague a link, they see the data, nothing was installed
and nothing was configured.

## What is being built

### 1. A file as a connection

Two ways to name a file, both in the Add-Connection form.

**Upload.** A browser file dialog; the file goes to `POST /api/connections/file` as multipart and is
stored under `<DB_PATH>/files/<connection-id>/<original name>`. The connection points at that copy.
`DELETE /api/connections/{id}` deletes the copy with it — an uploaded database is part of its
connection, not a stray file somebody has to remember.

**Browse what the server can see.** `GET /api/connections/browse?path=…` lists directories and
candidate files. It answers only inside the roots a deployment allows: `<DB_PATH>` always, plus
whatever `WDS_FILE_ROOTS` names (a list of paths, empty by default). Outside them it answers 403
rather than a listing — a file browser on a server is a way to read the server, and this one only
reaches where it was pointed.

**Which files.** The extension picks the engine:

| Extension | Engine | How |
|---|---|---|
| `.db` `.sqlite` `.sqlite3` `.db3` `.s3db` | SQLite | `Data Source=<path>` — the driver exists |
| `.duckdb` `.ddb` | DuckDB | the file is the database — the driver exists |
| `.parquet` `.csv` `.tsv` `.ndjson` `.jsonl` `.json` `.xlsx` | DuckDB, read-only | a view over the file through the table function `StorageReader` already picks per extension |

A file whose extension says SQLite is checked for the `SQLite format 3` header before a connection is
made, so a mis-named file produces a sentence rather than a driver error. A data file is opened
read-only: a view over a CSV is not something to write through, and saying so up front beats a failed
`UPDATE`.

Deliberately not supported, and the reason said out loud in the docs rather than left as a gap:

* **`.mdf`** — a SQL Server data file cannot be opened on its own. It needs a running SQL Server to
  `ATTACH` it, and `AttachDbFilename` is LocalDB, which is Windows-only; the image is Linux. Shipping
  a second engine inside the studio to read one file format is not a trade this makes.
* **`.accdb` / `.mdb`** — Access needs the ACE driver, which exists only on Windows and only as an
  install of its own.

### 2. Connections from the URL

```
https://studio.example/?u=/data/reports.duckdb
https://studio.example/?u=https://data.example/exports/2026-09.parquet
https://studio.example/?u=Host=db;Database=shop;Username=reader;Password=…
https://studio.example/?u=sales:/data/sales.sqlite3,https://data.example/orders.parquet
```

Comma-separated, each entry optionally `label:` in front of it. Three kinds, and a deployment says
which of them it allows:

* **file** — a path the studio can already reach, under the same roots the browser above uses.
* **download** — an `http(s)` URL to a database or data file. The studio fetches it into
  `<DB_PATH>/files/url/<hash>` and opens it. Only from hosts `WDS_OPEN_FROM_URL_HOSTS` names; without
  that list, `download` is refused even when it is switched on. An unrestricted fetcher inside a
  network is a way to reach that network's own addresses, and a deployment has to say which hosts it
  meant.
* **connection-string** — a full connection string. The most useful and the worst-kept: it lands in
  browser history, in proxy logs, in a screenshot. Its own word in the switch, never included in
  `true`.

**Where such a connection lives.** A third source in `ConnectionRegistry.All()`, next to the
environment definitions and the stored ones — which is where it belongs: every path that opens a
session goes through `Find`, and `All()` already filters per request through `CurrentUser`.

The store behind it is keyed by a session cookie (`wds_session`, `HttpOnly`, `SameSite=Lax`, set by a
middleware when a request has none). A cookie rather than the signed-in account, because a studio
handed out as a viewer usually has no accounts at all — `CurrentUser.User` is null in anonymous mode.
Consequences, all of them wanted:

* another person on the same studio does not see it, not in the list and not by guessing its id;
* nothing is written to disk, so a password from a URL does not outlive the process;
* a server restart drops them — opening the link again restores them, which is what a link is for.

`WDS_OPEN_FROM_URL_KEEP=store` is the other branch: the connection is written to the ordinary
connection store, visible and persistent like any other. That is what a single-user desktop build
wants and what a shared deployment must not have, which is why the default is `session`.

**Opening is idempotent per session.** The client sends `?u=` once and may send it again after a
reload; the same entry resolves to the same connection rather than a second one beside it. The id is
derived from what the entry says, so a link opened twice is one connection, and a link with a
different label is a different connection.

**Read-only by default.** `WDS_OPEN_FROM_URL_WRITABLE=true` allows writes — for both values of `KEEP`, because where the
connection is kept says nothing about whether it may write. A viewer does not write,
and the connection carries `ReadOnly = true` the way `WDS_READONLY` already does — enforced in the
driver, not by hiding buttons.

### Settings

| Variable | Default | What it does |
|---|---|---|
| `WDS_OPEN_FROM_URL` | `false` | `false`: `?u=` is ignored entirely. A comma list of `file`, `download`, `connection-string`. `true` means `file,download` — the two that carry no credentials |
| `WDS_OPEN_FROM_URL_HOSTS` | empty | Hosts `download` may fetch from, `*.` wildcards allowed. Empty means `download` refuses |
| `WDS_OPEN_FROM_URL_KEEP` | `session` | `session`: in memory, private to the browser that opened it. `store`: written to the connection store like any other |
| `WDS_OPEN_FROM_URL_WRITABLE` | `false` | Whether a connection opened this way may write |
| `WDS_OPEN_FROM_URL_MAX_MB` | `512` | The largest file a `download` entry may fetch |
| `WDS_FILE_ROOTS` | empty | Extra roots the file browser and `file` entries may reach, besides `<DB_PATH>` |

### The Aspire integration

```csharp
db.WithWebDataStudio(studio => studio.WithOpenFromUrl(
    files: true,
    downloads: true,
    connectionStrings: false,
    hosts: ["data.example", "*.blob.core.windows.net"],
    keep: UrlConnections.Session,
    writable: false));
```

One method, the five settings as its parameters, refusing at app-host build time what the studio
would refuse at runtime: `downloads: true` with no hosts is an `ArgumentException` where the stack is
described, not a message in a log three minutes later.

## The order it is built in

The file half first, on its own: the roots, the engine-by-extension, the upload and its deletion, the
browser. The URL half is then the same machinery reached from a query string plus the session store
and the switch — and it has something to be tested against, because "does `?u=/data/x.sqlite3` work"
is only a question once opening `/data/x.sqlite3` by hand works.

## Data flow

```
?u=…  ──▶  UrlConnections (server, on the first request of a session)
             │  parses, checks each entry against WDS_OPEN_FROM_URL
             │  download → fetch into <DB_PATH>/files/url/<hash> (hosts checked first)
             │  file      → path checked against the allowed roots
             │  string    → taken as it is
             ▼
       SessionConnections[cookie]  ──▶  ConnectionRegistry.All()  ──▶  every endpoint
                                            ▲
                     environment + store ───┘
```

The client's part is small: it sends `?u=` once (the shell already reads the query string for other
things), the server answers with the ids and labels it opened, and the explorer shows them like any
other connection with a badge that says where they came from. An entry that was refused comes back
with a reason, shown as one line: `download is not allowed on this studio`,
`data.example is not in WDS_OPEN_FROM_URL_HOSTS`.

## Error handling

Every refusal is a sentence naming the setting that caused it, because the person reading it is
usually the person who can change it. A URL with three entries where one is refused opens the other
two and says what happened to the third — an all-or-nothing answer would be a worse link.

A download that fails (host unreachable, 404, a file that is not a database) refuses that entry and
says which. The download has a size cap (`WDS_OPEN_FROM_URL_MAX_MB`, default 512) so a link cannot
fill the studio's disk, and the fetch has the same deadline the storage calls got in 1.3.0.

## Testing

* **Extension to engine**, including the read-only data files and the SQLite header check.
* **Upload, open, delete** through the real endpoints: the file is there, the connection reads it, the
  file is gone with the connection.
* **Browse** answers inside `<DB_PATH>` and 403 outside it, with `..` in the path and with a symlink
  pointing out.
* **`?u=` parsing**: labels, several entries, an empty entry, a percent-encoded connection string.
* **The switch**: every kind refused when off, `true` allowing exactly `file,download`,
  `connection-string` only when named.
* **Hosts**: an allowed host, a wildcard, a host that is not in the list, a redirect that leaves the
  list (refused — a redirect is a second host).
* **Session isolation**: two clients with different cookies do not see each other's URL connections;
  `keep=store` writes one that both see.
* **Read-only**: a write through a URL connection is refused in the driver, and allowed with
  `WDS_OPEN_FROM_URL_WRITABLE=true`.
* Web tests for the form's two new paths and for the "opened from a link" badge.

## Documentation

`docs/guide/connections.md` and its German mirror get the file section; `docs/guide/environment.md`
gets the five variables in the table it already keeps; `docs/features.md` and this repository's
feature spec get one row each; the Aspire package's README and both docs pages get `WithOpenFromUrl`.
The security note — a link that carries a connection string carries a password — belongs in
`docs/guide/deploy.md` next to the exposure section, not only in the table.

## What this does not do

* No file browser above the allowed roots, no "browse the whole container".
* No `.mdf`, no Access, for the reasons above.
* No sharing of a session connection with another person: that is what a stored connection is.
* No expiry timer on session connections in this change. They end with the process, and a deployment
  that wants them gone sooner restarts it. If a public studio ever needs a sweep, it is a small
  addition on top of a store that already knows when each entry arrived.
