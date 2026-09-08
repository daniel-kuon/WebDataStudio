# A studio anybody brings their own data to — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a studio be put on the open internet as a viewer — no accounts, no configured connections — where every visitor brings their own database and sees nobody else's, without changing anything for the deployments the studio already serves.

**Architecture:** Two orthogonal settings families read once into option objects: `ConnectionScope` (where a new connection goes) and `StudioDoors` (which ways in exist). The endpoints ask them; `SessionConnections` grows a lifetime, a ceiling and a sweeper; `ConnectHosts` checks every connection target wherever it came from. The browser learns all of it from `GET /api/auth/me`.

**Tech Stack:** .NET 10 minimal APIs, xunit v3 + Microsoft.Testing.Platform (`dotnet run -- -filter "/*/*/Class/*"`), React 19 + Mantine 9 + vitest, Aspire 13.5 for the Nextended methods.

**Spec:** `docs/superpowers/specs/2026-09-07-a-studio-anybody-brings-data-to-design.md`

## Global Constraints

- Defaults, exactly: `WDS_CONNECTION_SCOPE=stored`, `WDS_ALLOW_ADD_CONNECTION=true`, `WDS_ALLOW_FILE_UPLOAD=true`, `WDS_ALLOW_FILE_BROWSE=true`, `WDS_SESSION_TTL_MINUTES=240`, `WDS_SESSION_MAX_CONNECTIONS=25`, `WDS_UPLOAD_MAX_MB=100`, `WDS_CONNECT_HOSTS` empty.
- A studio that sets none of them behaves exactly as 1.4.0 does. Every task ends with the existing suites green, which is the check on that.
- Every refusal names the setting that would allow it, in one English sentence.
- Connections are found only through `ConnectionRegistry.Find`/`All()`.
- `WDS_CONNECT_HOSTS` applies to every source: form, `test`, `?u=`, store, environment.
- Session files live under `<DB_PATH>/files/session/<key-hash>/<connection-id>/` and are swept with the session.
- Feature ids F30.46–F30.50 go in `docs/features.md` **and** in `docs/superpowers/specs/2026-08-18-webdatastudio-design.md`, or `FeatureCoverageTests` fails.
- Server tests in `tests/WebDataStudio.Server.Tests`, web tests next to their component. Nothing is committed with a red suite.

---

### Task 1: The two settings families

**Files:**
- Create: `src/WebDataStudio.Server/Services/StudioAccess.cs`
- Modify: `src/WebDataStudio.Server/Program.cs`
- Test: `tests/WebDataStudio.Server.Tests/StudioAccessTests.cs`

**Interfaces:**
- Produces:
  - `enum ConnectionScope { Stored, Session }`
  - `sealed class StudioAccess` with `Scope`, `MayAdd`, `MayUpload`, `MayBrowse`, `SessionTtl` (`TimeSpan?`), `MaxSessionConnections`, `UploadMaxBytes`, `static StudioAccess From(IConfiguration)`

- [ ] **Step 1: Write the failing test** — defaults are today's behaviour; `session` scope reads; a bad number falls back rather than throwing; `0` for the TTL means never.

- [ ] **Step 2: Run it** — `dotnet run -- -filter "/*/*/StudioAccessTests/*"`, fails: no such type.

- [ ] **Step 3: Implement.** One class, `From(IConfiguration)` in the shape of `UrlConnectionOptions.From`. `SessionTtl` is null when `WDS_SESSION_TTL_MINUTES=0`.

- [ ] **Step 4: Green, plus `UrlConnectionOptionsTests` and `FileRootsTests` untouched.**

- [ ] **Step 5: Commit** — `feat(access): where a connection goes, and which doors exist`

---

### Task 2: The doors, and the refusals that name them

**Files:**
- Modify: `src/WebDataStudio.Server/Endpoints/ConnectionEndpoints.cs` (`POST /`, `POST /import`, `POST /test`), `src/WebDataStudio.Server/Endpoints/ConnectionFileEndpoints.cs` (upload, browse)
- Test: `tests/WebDataStudio.Server.Tests/StudioDoorsTests.cs`

**Interfaces:**
- Consumes: Task 1

- [ ] **Step 1: Write the failing test** — with each switch `false`: `POST /api/connections` → 403 naming `WDS_ALLOW_ADD_CONNECTION`; `POST /api/connections/test` → the same, because it is the cheapest door; `POST /api/connections/import` → the same; `POST /api/connections/file` → 403 naming `WDS_ALLOW_FILE_UPLOAD`; `GET /api/connections/browse` → 403 naming `WDS_ALLOW_FILE_BROWSE`. And with nothing configured, all five behave as they do today.

- [ ] **Step 2: Run it** — fails: everything is still allowed.

- [ ] **Step 3: Implement.** One `Results.Json(new { message = … }, statusCode: 403)` helper per family, asked before any work happens.

- [ ] **Step 4: Green, plus `ConnectionEndpointTests`, `FileConnectionEndpointTests`, `FileBrowseEndpointTests`.**

- [ ] **Step 5: Commit** — `feat(access): a door a deployment closed says which setting closed it`

---

### Task 3: In session scope the form and the upload belong to one browser

**Files:**
- Modify: `ConnectionEndpoints.cs` (`POST /`, `PUT /{id}`, `DELETE /{id}`, `POST /import`), `ConnectionFileEndpoints.cs` (upload), `src/WebDataStudio.Server/Services/UrlConnectionOpener.cs` (refuse `KEEP=store` under session scope)
- Test: `tests/WebDataStudio.Server.Tests/SessionScopeTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2; `SessionConnections.Add/For/Current`

- [ ] **Step 1: Write the failing test** — under `WDS_CONNECTION_SCOPE=session`: a connection made through the form is in *this* client's list and not in a second client's; nothing is written to the store (the store file has no rows); the owner may `DELETE` it; a second client may not; `PUT` renames one's own; an uploaded file is a session connection too; `WDS_OPEN_FROM_URL_KEEP=store` together with session scope is refused at start-up with a sentence naming both settings.

- [ ] **Step 2: Run it** — fails: everything lands in the store.

- [ ] **Step 3: Implement.** The endpoints route by `access.Scope`. `OpenedFromALink()` becomes a check on *ownership* rather than on `Source`: a session connection the current session owns may be edited and deleted; one it does not own is a 404, because it does not exist for them.

- [ ] **Step 4: Green, plus `SessionConnectionsTests`, `UrlConnectionEndpointTests`, `ConnectionEndpointTests`.**

- [ ] **Step 5: Commit** — `feat(access): in session scope what you add is yours alone`

---

### Task 4: A session that ends

**Files:**
- Modify: `src/WebDataStudio.Server/Services/SessionConnections.cs`, `Program.cs` (the sweeper)
- Create: `src/WebDataStudio.Server/Services/SessionSweeper.cs`
- Test: `tests/WebDataStudio.Server.Tests/SessionLifetimeTests.cs`

**Interfaces:**
- Produces: `SessionConnections.Sweep(DateTimeOffset now)` → how many sessions went; `SessionConnections.Forget(string key)`; `POST /api/connections/forget`
- Consumes: Task 1 (`SessionTtl`, `MaxSessionConnections`)

- [ ] **Step 1: Write the failing test** — a key not seen inside the TTL is swept and its files are gone; a key seen since is kept; `Sweep` with no TTL does nothing; the ceiling refuses the twenty-sixth connection with a sentence naming `WDS_SESSION_MAX_CONNECTIONS`; `POST /api/connections/forget` empties the list and clears the cookie; a `?u=` file entry naming a path inside the uploads tree is refused.

- [ ] **Step 2: Run it** — fails: nothing expires.

- [ ] **Step 3: Implement.** A last-seen stamp per key (`Remember` already runs on every request); `Sweep` drops quiet keys and deletes `<uploads>/session/<hash>`; a `BackgroundService` calls it every five minutes; the uploads-tree rule sits in `UrlConnectionOpener.FromFile`.

- [ ] **Step 4: Green, plus the three connection suites.**

- [ ] **Step 5: Commit** — `feat(access): a session that ends, and a button that ends it now`

---

### Task 5: A cap on what a visitor may upload

**Files:**
- Modify: `ConnectionFileEndpoints.cs`, `Program.cs` (Kestrel's own limit)
- Test: `tests/WebDataStudio.Server.Tests/UploadLimitTests.cs`

- [ ] **Step 1: Write the failing test** — a file over `WDS_UPLOAD_MAX_MB` is refused with a sentence naming the setting and nothing is kept; one under it is fine; the default is 100 MB rather than Kestrel's 30.

- [ ] **Step 2: Run it** — fails: a 40 MB body dies in Kestrel with a 413 nobody wrote.

- [ ] **Step 3: Implement.** `MaxRequestBodySize` from the setting plus a check on `file.Length` before the copy.

- [ ] **Step 4: Green, plus `FileConnectionEndpointTests`.**

- [ ] **Step 5: Commit** — `feat(access): a size a visitor may not exceed`

---

### Task 6: The hosts the studio may connect to at all

**Files:**
- Create: `src/WebDataStudio.Server/Services/ConnectHosts.cs`
- Modify: `src/WebDataStudio.Server/Services/SessionFactory.cs`, `ConnectionEndpoints.cs` (`POST /test`)
- Test: `tests/WebDataStudio.Server.Tests/ConnectHostsTests.cs`

**Interfaces:**
- Produces: `ConnectHosts.From(IConfiguration)`, `Allows(string connectionString, string engine)`, `HostOf(string connectionString)` → `string?`

- [ ] **Step 1: Write the failing test** — empty list allows everything; a named host is allowed and another refused with a sentence naming `WDS_CONNECT_HOSTS`; `*.example.com` matches one subdomain level and not two; a `postgres://user:pw@host:5432/db` authority is read; `Host=`, `Server=`, `Data Source=` are read; a file-shaped `Data Source=/data/x.db` is not a host and stays allowed; a connection string with no readable host is refused while the list is set; the check applies through `SessionFactory` (so a stored connection is checked too) and through `POST /test`.

- [ ] **Step 2: Run it** — fails: no such type.

- [ ] **Step 3: Implement.** One host extractor, the wildcard rule copied from `UrlConnectionOptions.HostAllowed` so both lists behave the same.

- [ ] **Step 4: Green, plus the driver suites that open sessions.**

- [ ] **Step 5: Commit** — `feat(access): the hosts a studio may reach, whatever the string says`

---

### Task 7: What the browser shows

**Files:**
- Modify: `src/WebDataStudio.Server/Endpoints/AuthEndpoints.cs` (`/auth/me` gains `access`), `web/src/api.ts`, `web/src/connections/ConnectionsPage.tsx`, `web/src/connections/ConnectionForm.tsx`, `web/src/connections/FilePicker.tsx`, `web/src/explorer/ExplorerTree.tsx`
- Create: `web/src/connections/BringYourOwn.tsx`, `web/src/connections/BringYourOwn.test.tsx`
- Test: `web/src/connections/BringYourOwn.test.tsx`, `web/src/connections/ConnectionsPage.test.tsx`

**Interfaces:**
- Produces: `Me.access: { scope: "Stored" | "Session"; mayAdd: boolean; mayUpload: boolean; mayBrowse: boolean }`, `forgetSession()` in `api.ts`

- [ ] **Step 1: Write the failing test** — the panel names the three ways that are open and leaves out the ones that are closed; with `scope: "Session"` the page says the connections belong to this browser and offers **Forget my connections**; with `mayAdd: false` there is no **Add** button; with `mayBrowse: false` no **Browse the server**; with `mayUpload: false` no file field.

- [ ] **Step 2: Run it** — `npx vitest run src/connections`, fails: no such module.

- [ ] **Step 3: Implement.** The empty state shows `BringYourOwn` when the list is empty; the buttons follow the four facts from `/auth/me`.

- [ ] **Step 4: Green** — `npx vitest run && npm run build`.

- [ ] **Step 5: Commit** — `feat(access): the first thing a visitor sees`

---

### Task 8: Docs, both languages

**Files:**
- Modify: `docs/guide/connections.md`, `docs/guide/de/connections.md`, `docs/guide/environment.md`, `docs/guide/de/environment.md`, `docs/guide/deploy.md`, `docs/features.md`, `docs/superpowers/specs/2026-08-18-webdatastudio-design.md`, `CHANGELOG.md`

- [ ] **Step 1: Write it.** `connections.md` + German: a section **A studio anybody brings their own data to** — the scope, the three doors, what a session is and when it ends, the forget button. `environment.md` + German: the eight variables with the defaults from the Global Constraints. `deploy.md`: the public-viewer recipe as a compose snippet plus the two sentences about egress and `WDS_CONNECT_HOSTS`. `features.md` and the design spec: F30.46–F30.50. `CHANGELOG.md`: an `## Unreleased` section.

- [ ] **Step 2: Check** — `node scripts/check-links.mjs docs` and `dotnet run -- -filter "/*/*/FeatureCoverageTests/*"`.

- [ ] **Step 3: Commit** — `docs: a studio anybody brings their own data to`

---

### Task 9: The Aspire side

**Files (in `C:\dev\privat\github\Nextended`):**
- Create: `Nextended.Aspire.Hosting.WebDataStudio/Builders/WebDataStudioAccessExtensions.cs`
- Test: `Tests/Nextended.Aspire.Hosting.WebDataStudio.Tests/WebDataStudioAccessTests.cs`
- Modify: `Nextended.Aspire.Hosting.WebDataStudio/README.md`, `docs/projects/aspire-webdatastudio.md`

**Interfaces:**
- Produces: `enum ConnectionScope { Stored, Session }`, `WithConnectionScope`, `WithoutAddingConnections`, `WithoutFileUpload`, `WithoutFileBrowse`, `WithConnectHosts(params string[])`, `WithSessionLifetime(int minutes = 240, int maxConnections = 25)`, `WithUploadLimit(int megabytes)`, `AsPublicViewer(bool connectionStrings = false, bool upload = true, IEnumerable<string>? hosts = null)`

- [ ] **Step 1: Write the failing test** — each method writes its variable; `AsPublicViewer` writes the whole set (scope `session`, browse off, `?u=` on, read-only, lifetime and caps); it throws when the studio also has `WithLogin(...)` or `WithConnection(...)`; `WithSessionLifetime(0)` means never and says so.

- [ ] **Step 2: Run it** — `dotnet test Tests/Nextended.Aspire.Hosting.WebDataStudio.Tests/… --filter "FullyQualifiedName~WebDataStudioAccessTests"`, fails to compile.

- [ ] **Step 3: Implement**, in the voice of `WebDataStudioUrlExtensions`.

- [ ] **Step 4: Green, then `dotnet run --project tools/ApiRef` and `pwsh tools/Update-PackageDocs.ps1`.**

- [ ] **Step 5: Leave it uncommitted** — Nextended is committed by its owner.

---

### Task 10: The whole thing, and the release

- [ ] **Step 1:** `dotnet test`; `cd web && npx vitest run && npm run build`; `node scripts/check-links.mjs docs`; `node --test "scripts/**/*.test.mjs"`.
- [ ] **Step 2:** the server suite in `mcr.microsoft.com/dotnet/sdk:10.0`; only the fourteen known sandbox failures are allowed.
- [ ] **Step 3:** a public-viewer studio started by hand — no connections, `?u=` on, browse off — and a database brought in through each of the three open doors, with a second browser proving it sees none of them.
- [ ] **Step 4:** `CHANGELOG.md` section renamed to `1.5.0`, the Umbrel and CasaOS manifests bumped, push, watch, tag.
