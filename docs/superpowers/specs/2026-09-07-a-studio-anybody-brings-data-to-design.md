# A studio anybody brings their own data to

**Status:** approved in conversation, 2026-09-07
**Follows:** `2026-09-07-file-and-url-connections-design.md` (shipped as 1.4.0)

## What this is for

Two deployments that the studio cannot tell apart today.

The first is the one it was built for: an app host or a compose file writes the connections down, a
team opens the studio, everybody sees the same databases. Nothing here changes for it.

The second is a studio put on the open internet as a viewer. No accounts, no connections
configured. A visitor brings a database — a connection string, a file from their machine, a link
with `?u=` — looks at it, and leaves. The next visitor sees none of it. That deployment is
impossible right now for one reason: everything a person creates in the UI goes into the shared
connection store, permanently, and no setting stops them.

## The two questions, kept apart

Today one fact is spread over two would-be settings ("may somebody add a connection" and "is adding
locked"). Instead:

**Where does a new connection go?** — `WDS_CONNECTION_SCOPE`

| Value | Meaning |
| --- | --- |
| `stored` (default) | The connection store: written down, kept across restarts, everybody sees it. Today's behaviour, unchanged. |
| `session` | The browser that made it, and nobody else. Nothing on disk, gone when the process stops or the session expires. |

**Which doors exist at all?** — three switches, all `true` by default, so a deployment that says
nothing keeps every way in it has now.

| Variable | Default | The door it closes |
| --- | --- | --- |
| `WDS_ALLOW_ADD_CONNECTION` | `true` | The form: a connection string typed or pasted. Also `POST /api/connections/import` and `POST /api/connections/test`. |
| `WDS_ALLOW_FILE_UPLOAD` | `true` | A database file from the visitor's own machine. |
| `WDS_ALLOW_FILE_BROWSE` | `true` | Walking the server's own folders (`WDS_FILE_ROOTS`). |

"Nobody may add anything" is those three set to `false` — not a fourth value of the scope. A
refusal names the setting that would allow it, the way every other refusal in the studio does.

`POST /api/connections/test` is behind `WDS_ALLOW_ADD_CONNECTION` on purpose. It opens an arbitrary
connection and stores nothing, which makes it the cheapest door of the four: gating the form and
leaving `test` open would be a lock on an open frame.

## What a session is, and when it ends

`SessionConnections` already keys connections by a `wds_session` cookie and holds them in memory.
For a studio that is open for weeks, two things are missing.

- **An end.** `WDS_SESSION_TTL_MINUTES`, 240 by default, `0` meaning never. Every request stamps
  its own key as seen; a sweeper drops what has gone quiet and deletes the files that belonged to
  it. Without this a public studio leaks memory and disk for every visitor it ever had.
- **A ceiling.** `WDS_SESSION_MAX_CONNECTIONS`, 25 by default. The refusal says the number, so it
  reads as a limit rather than a bug.

A session connection can be deleted by the browser that owns it — today `PUT` and `DELETE` refuse
every session connection with "opened from a link", which is right for a link and wrong once the
form makes them too. `POST /api/connections/forget` drops the whole session and clears the cookie:
one button for "I am done here".

The cookie separates visitors; it is not a security boundary. Somebody who copies another person's
cookie gets that person's connections, which is why it is `HttpOnly` and `Secure` over https, and
why this is written down here rather than implied.

## Files a visitor brings

In `stored` scope an upload keeps landing in `<DB_PATH>/files/<connection-id>/`, as it does now.

In `session` scope it lands under `<DB_PATH>/files/session/<session-key-hash>/<connection-id>/`, and
the sweeper removes that whole subtree when the session ends.

Two limits that do not exist yet:

- `WDS_UPLOAD_MAX_MB`, 100 by default, refused before the body is read rather than after. Today the
  only cap is Kestrel's 30 MB default, which is an accident rather than a decision — the setting
  raises Kestrel's own limit to match.
- A `?u=` file entry may not name a path inside the uploads tree. The folder names are GUIDs and
  not guessable, but a path that leaks would otherwise hand one visitor another visitor's file, and
  a rule is cheaper than trusting that.

## Where the studio may connect to

A studio anybody can type a connection string into is an outbound connector from wherever it runs.
A visitor can reach addresses only the server can reach, and hammer a database that is not theirs.
`WDS_OPEN_FROM_URL_HOSTS` does not help: it covers downloads, not connection targets.

`WDS_CONNECT_HOSTS` — empty by default, meaning no restriction — is a comma-separated allow-list of
hosts every connection target is checked against, wherever the connection came from: the form,
`test`, `?u=`, a stored connection, an environment connection. `*.example.com` matches one level of
subdomain, the same rule the download list already uses. A refused host says which setting refused
it.

The host is read out of the connection string per engine (`Host=`, `Server=`, `Data Source=`, the
authority of a `postgres://`-style URL). A connection string the studio cannot read a host from is
refused when the list is set — guessing would make the list a suggestion.

This is the setting a public deployment must not skip. Network-level egress restriction is the
other half, and the deploy guide says so.

## What the browser shows

For a viewer instance the empty state *is* the product. With no connections and `session` scope, the
explorer shows a short panel instead of an empty tree: paste a connection string, drop a file, or
open a link. That panel is the first thing a visitor sees and the only instruction they get.

Everything else follows the doors: a closed door hides its button rather than showing one that
answers with a refusal. **Add** disappears with `WDS_ALLOW_ADD_CONNECTION`, the file field with
`WDS_ALLOW_FILE_UPLOAD`, **Browse the server** with `WDS_ALLOW_FILE_BROWSE`. A `GET /api/me` field
carries the four facts, because the browser cannot read environment variables and a hidden button
has to be hidden by the server's answer.

In `session` scope the connection list says, once, that what you add here belongs to this browser
and goes away with it. Next to it the **Forget my connections** button.

## The Aspire side

The switches one to one, in the package's own voice — negative names for gates that default to on,
which is what `WithoutAssistantTools()` already does:

```csharp
studio.WithConnectionScope(ConnectionScope.Session)
      .WithoutAddingConnections()
      .WithoutFileUpload()
      .WithoutFileBrowse()
      .WithConnectHosts("db.example", "*.internal.example")
      .WithSessionLifetime(minutes: 240, maxConnections: 25)
      .WithUploadLimit(megabytes: 100);
```

And the whole viewer stack as one call, because a deployment that wants this wants all of it:

```csharp
studio.AsPublicViewer(connectionStrings: true, upload: true, hosts: ["db.example"]);
```

`AsPublicViewer` sets the scope to `session`, closes the server browser, turns `?u=` on for files
and — when asked — connection strings, keeps everything read-only, leaves the MCP endpoint off and
sets the lifetime and the caps. It throws when the same studio is also given `WithLogin(...)` or
`WithConnection(...)`: a viewer with accounts and shared connections is a contradiction, and the
place to find that out is the app host, not the running container.

## Features

| Id | What |
| --- | --- |
| F30.46 | Where a connection somebody adds goes: the shared store, or the browser that made it |
| F30.47 | Which ways in a deployment leaves open: the form, an upload, the server's folders |
| F30.48 | A session that ends: a lifetime, a ceiling, a sweeper, and a button to forget it now |
| F30.49 | The hosts the studio may connect to at all, whatever the connection string says |
| F30.50 | The empty state of a studio somebody brings their own data to |

## Not in this

- Per-visitor rate limiting. Worth having on a public instance, and a separate piece of work: it
  belongs in front of the query endpoints as much as in front of `test`, and it needs a decision
  about what to do behind a proxy that hides the address.
- Accounts for a public instance. The point of this one is that there are none.
- A "share this connection as a link" button. That is a password handed on with a friendly face.
