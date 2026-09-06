// One command to run the studio locally: `dotnet run --project samples/WebDataStudio.AppHost`.
// It starts PostgreSQL in a container, the studio from this repository's source, and the Vite dev
// server for the SPA — seeded with the same demo data the documentation screenshots show (DEMO on
// SQLite, PG on PostgreSQL), so what opens looks exactly like the docs.

var builder = DistributedApplication.CreateBuilder(args);

// Everything the studio keeps — its own database and the SQLite demo — lives in .data next to
// this project, so a re-run finds it again and git never sees it.
var data = Path.Combine(builder.AppHostDirectory, ".data");
Directory.CreateDirectory(data);

var repository = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
var seed = Path.Combine(repository, "web", "scripts", "demo-data");

// A named volume, so the seeded rows — and whatever you change in the studio — survive a restart.
// The seed itself runs once per script content: the studio remembers what it already ran.
var postgres = builder.AddPostgres("pg")
    .WithDataVolume("webdatastudio-demo-pg");

var studio = builder.AddProject<Projects.WebDataStudio_Server>("studio", launchProfileName: null)
    // Port 5000 on purpose: the Vite dev server proxies /api there (web/vite.config.ts).
    .WithHttpEndpoint(port: 5000)
    .WithEnvironment("DB_PATH", Path.Combine(data, "wds.db"))
    // The demo connections of the documentation: DEMO.sql (SQLite) and PG.sql (PostgreSQL) from
    // web/scripts/demo-data, run by the studio itself on first start.
    .WithEnvironment("WDS_CONN_DEMO", $"sqlite:///{Path.Combine(data, "demo.db").Replace('\\', '/')}")
    .WithEnvironment("WDS_CONN_PG", postgres.Resource.ConnectionStringExpression)
    .WithEnvironment("WDS_SEED_SQL", seed)
    .WaitFor(postgres);

// The SPA the way development serves it: Vite with hot reload, proxying /api to the studio — so
// nothing has to be built first. Run `npm install` in web/ once before the first start. (A
// Release build of the server serves the built SPA itself, on the studio endpoint above.)
builder.AddViteApp("web", Path.Combine(repository, "web"))
    .WaitFor(studio);

builder.Build().Run();
