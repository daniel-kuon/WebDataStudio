using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// What `?u=` carries, taken apart: the label is optional, and the kind is read off the value.
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
    public void A_windows_path_is_a_file_and_its_drive_is_not_a_label()
    {
        var entry = UrlEntry.Parse(@"C:\data\shop.db")!;

        Assert.Equal(UrlEntryKind.File, entry.Kind);
        Assert.Null(entry.Label);
        Assert.Equal(@"C:\data\shop.db", entry.Value);
    }

    [Theory]
    [InlineData("https://data.example/orders.parquet")]
    [InlineData("http://data.example/orders.parquet")]
    [InlineData("HTTPS://data.example/orders.parquet")]
    public void An_http_url_is_a_download(string value)
    {
        var entry = UrlEntry.Parse(value)!;

        Assert.Equal(UrlEntryKind.Download, entry.Kind);
        Assert.Null(entry.Label);
    }

    [Fact]
    public void Anything_with_keywords_in_it_is_a_connection_string()
    {
        Assert.Equal(UrlEntryKind.ConnectionString,
            UrlEntry.Parse("Host=db;Database=shop;Username=reader;Password=p")!.Kind);

        Assert.Equal(UrlEntryKind.ConnectionString,
            UrlEntry.Parse("Data Source=/data/shop.db")!.Kind);
    }

    /// A URL form is a connection string too — `postgres://…` is not something to download.
    [Fact]
    public void A_database_url_is_a_connection_string_rather_than_a_download()
    {
        Assert.Equal(UrlEntryKind.ConnectionString,
            UrlEntry.Parse("postgres://user:pw@host:5432/shop")!.Kind);

        Assert.Equal(UrlEntryKind.ConnectionString,
            UrlEntry.Parse("mongodb://box:27017/events")!.Kind);
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
    public void A_label_may_name_a_download_too()
    {
        var entry = UrlEntry.Parse("orders:https://data.example/a.parquet")!;

        Assert.Equal("orders", entry.Label);
        Assert.Equal(UrlEntryKind.Download, entry.Kind);
        Assert.Equal("https://data.example/a.parquet", entry.Value);
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
    public void Nothing_at_all_is_no_entries()
    {
        Assert.Empty(UrlEntry.ParseAll(""));
        Assert.Empty(UrlEntry.ParseAll("  "));
        Assert.Null(UrlEntry.Parse(""));
    }

    /// `Server=host,1433` is how SQL Server writes a port, and a raw comma would split the entry —
    /// so the whole entry may arrive percent-encoded.
    [Fact]
    public void A_connection_string_may_hold_a_comma_when_it_is_percent_encoded()
    {
        var entries = UrlEntry.ParseAll(
            "Server%3Dhost%2C1433%3BDatabase%3Dshop%3BUser+Id%3Dsa%3BPassword%3Dp");

        var only = Assert.Single(entries);

        Assert.Equal(UrlEntryKind.ConnectionString, only.Kind);
        Assert.Contains("host,1433", only.Value);
        Assert.Contains("User Id=sa", only.Value);
    }

    /// The same entry twice is the same connection: the id comes out of what the entry says, so a
    /// link opened again does not pile up.
    [Fact]
    public void The_same_entry_has_the_same_id_and_a_different_label_does_not()
    {
        var first = UrlEntry.Parse("/data/a.db")!;
        var again = UrlEntry.Parse("/data/a.db")!;
        var labelled = UrlEntry.Parse("sales:/data/a.db")!;

        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.Id, labelled.Id);
        Assert.StartsWith("url-", first.Id);
    }
}
