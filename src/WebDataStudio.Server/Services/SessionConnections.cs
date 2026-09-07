using System.Collections.Concurrent;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Services;

/// Connections that belong to one browser and to this process.
///
/// A studio handed out as a viewer usually has no accounts at all, so the owner cannot be the
/// signed-in user — `CurrentUser.User` is null in anonymous mode. The key is a cookie this class
/// sets instead. Nothing here is written to disk: a password that came in through a URL does not
/// outlive the process, which is the point of the default.
public sealed class SessionConnections(IHttpContextAccessor accessor)
{
    public const string CookieName = "wds_session";

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConnectionSpec>> _byKey =
        new(StringComparer.Ordinal);

    /// Every key that has connections or has been seen. For tests and for a status page; not for
    /// deciding anything.
    public IEnumerable<string> Keys => _byKey.Keys;

    /// The key for the request in flight, setting the cookie where the browser has none.
    public static string Key(HttpContext context)
    {
        if (context.Items.TryGetValue(CookieName, out var pending) && pending is string already)
            return already;

        if (context.Request.Cookies.TryGetValue(CookieName, out var existing)
            && existing is { Length: > 0 })
            return existing;

        var fresh = Guid.NewGuid().ToString("n");

        context.Response.Cookies.Append(CookieName, fresh, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
        });

        // The cookie is on its way out, so this request already knows its own key.
        context.Items[CookieName] = fresh;
        return fresh;
    }

    /// Keeps one connection for one browser. The same id twice is the same connection, so a link
    /// opened again does not pile up — and a connection made in the form arrives without an id,
    /// which is where it gets one.
    public ConnectionSpec Add(string key, ConnectionSpec spec)
    {
        var session = spec with
        {
            Id = spec.Id is { Length: > 0 } given ? given : Guid.NewGuid().ToString("n"),
            Source = ConnectionSource.Session,
        };

        _byKey.GetOrAdd(key, _ => new ConcurrentDictionary<string, ConnectionSpec>(StringComparer.Ordinal))
            [session.Id] = session;

        return session;
    }

    public IReadOnlyList<ConnectionSpec> For(string key) =>
        _byKey.TryGetValue(key, out var found) ? found.Values.ToList() : [];

    /// What the request in flight may see. No context — a background service, a test that has no
    /// request — means none, which is the safe answer rather than everybody's.
    public IReadOnlyList<ConnectionSpec> Current
    {
        get
        {
            if (accessor.HttpContext is not { } context) return [];

            var key = context.Items.TryGetValue(CookieName, out var pending) && pending is string fresh
                ? fresh
                : context.Request.Cookies.TryGetValue(CookieName, out var cookie) ? cookie : null;

            return key is { Length: > 0 } ? For(key) : [];
        }
    }

    /// Drops one connection from one browser. False when it was not there, which the caller
    /// answers as a 404: a connection somebody else owns does not exist for you.
    public bool Remove(string key, string id) =>
        _byKey.TryGetValue(key, out var found) && found.TryRemove(id, out _);

    /// A folder name for one session's files. The key itself is a cookie value and has no business
    /// being a path on disk.
    public static string FolderFor(string key) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key)))[..16];

    /// Remembers a key even before it has connections, so `Keys` says who is here.
    public void Remember(string key) =>
        _byKey.GetOrAdd(key, _ => new ConcurrentDictionary<string, ConnectionSpec>(StringComparer.Ordinal));
}
