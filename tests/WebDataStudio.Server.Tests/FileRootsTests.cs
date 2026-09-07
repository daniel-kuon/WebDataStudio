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
        Directory.CreateDirectory(Path.Combine(_dir, "data"));

        var settings = new Dictionary<string, string?>
        {
            ["DB_PATH"] = Path.Combine(_dir, "data", "wds.db"),
            ["WDS_FILE_ROOTS"] = extra,
        };

        return new FileRoots(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    [Fact]
    public void The_data_directory_is_always_a_root() =>
        Assert.Contains(Path.Combine(_dir, "data"), Roots().All);

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

    /// A root's own path is a folder somebody may open — otherwise the browser could not start
    /// anywhere.
    [Fact]
    public void A_root_itself_resolves()
    {
        var mounted = Directory.CreateDirectory(Path.Combine(_dir, "mounted")).FullName;

        Assert.NotNull(Roots(mounted).Resolve(mounted));
    }

    /// A folder whose name only starts like a root is a different folder: `/data-secret` is not
    /// inside `/data`.
    [Fact]
    public void A_name_that_starts_like_a_root_is_not_inside_it()
    {
        var mounted = Directory.CreateDirectory(Path.Combine(_dir, "mounted")).FullName;
        Directory.CreateDirectory(Path.Combine(_dir, "mounted-secret"));

        Assert.Null(Roots(mounted).Resolve(Path.Combine(_dir, "mounted-secret", "x.db")));
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
    public void A_root_that_is_not_there_is_not_offered()
    {
        var roots = Roots(Path.Combine(_dir, "never-created"));

        Assert.DoesNotContain(Path.Combine(_dir, "never-created"), roots.All);
    }

    [Fact]
    public void Uploads_live_under_the_data_directory() =>
        Assert.Equal(Path.Combine(_dir, "data", "files"), Roots().Uploads);
}
