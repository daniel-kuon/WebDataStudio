namespace WebDataStudio.Server.Services;

/// The folders a file connection may name.
///
/// The studio's own data directory always, plus whatever `WDS_FILE_ROOTS` lists — a deployment that
/// mounts a share says so once, and nothing outside those folders is reachable, not through the
/// browse endpoint and not through a `?u=` file entry. One place decides it, because a path check
/// spread over three endpoints is a path check that is wrong in one of them.
public sealed class FileRoots
{
    private readonly List<string> _roots = [];

    public FileRoots(IConfiguration config)
    {
        var dbPath = config["DB_PATH"] ?? "/data/webdatastudio.db";

        if (Path.GetDirectoryName(Path.GetFullPath(dbPath)) is { Length: > 0 } data)
            _roots.Add(data);

        // A root that is not there is not offered: a deployment that names a share it did not mount
        // should see it missing from the list rather than an empty folder.
        foreach (var extra in (config["WDS_FILE_ROOTS"] ?? "")
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var full = Path.GetFullPath(extra);
            if (Directory.Exists(full) && !_roots.Contains(full)) _roots.Add(full);
        }

        Uploads = Path.Combine(_roots.Count > 0 ? _roots[0] : ".", "files");
    }

    /// Every folder a file may be named in, in the order a picker should offer them.
    public IReadOnlyList<string> All => _roots;

    /// Where uploaded databases are kept: inside the data directory, so a deployment that backs that
    /// up has them too.
    public string Uploads { get; }

    /// The absolute path, or null when it is not inside a root.
    ///
    /// `..` is resolved before the check — that is the whole reason this lives in one method — and a
    /// folder whose name merely starts like a root is not inside it: `/data-secret` is not `/data`.
    public string? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return _roots.Any(root =>
        {
            var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return full.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        })
            ? Path.GetFullPath(path)
            : null;
    }
}
