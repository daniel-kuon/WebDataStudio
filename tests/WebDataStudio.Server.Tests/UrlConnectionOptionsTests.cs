using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// The switch in front of the URL half. Off unless a deployment says otherwise, and a connection
/// string has to be named on its own — a password in a URL is a password in browser history.
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
        Assert.False(options.Allows(UrlEntryKind.Download));
        Assert.False(options.Allows(UrlEntryKind.ConnectionString));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("nonsense")]
    public void Anything_that_is_not_a_kind_allows_nothing(string raw) =>
        Assert.False(Options(("WDS_OPEN_FROM_URL", raw)).Enabled);

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

    /// The Aspire package writes `connection-string`; somebody typing the variable by hand may well
    /// write it as one word.
    [Theory]
    [InlineData("connection-string")]
    [InlineData("connectionstring")]
    [InlineData("CONNECTION-STRING")]
    public void The_connection_string_kind_is_spelled_either_way(string word) =>
        Assert.True(Options(("WDS_OPEN_FROM_URL", word)).Allows(UrlEntryKind.ConnectionString));

    [Fact]
    public void A_download_needs_a_host_list_to_be_allowed_at_all()
    {
        Assert.False(Options(("WDS_OPEN_FROM_URL", "download")).HostAllowed("data.example"));

        var listed = Options(("WDS_OPEN_FROM_URL", "download"),
            ("WDS_OPEN_FROM_URL_HOSTS", "data.example, *.blob.core.windows.net"));

        Assert.True(listed.HostAllowed("data.example"));
        Assert.True(listed.HostAllowed("DATA.EXAMPLE"));
        Assert.True(listed.HostAllowed("wds.blob.core.windows.net"));
        Assert.False(listed.HostAllowed("evil.example"));

        // A wildcard is about subdomains: the bare domain is not one of them, and neither is a host
        // that merely ends with the same letters.
        Assert.False(listed.HostAllowed("blob.core.windows.net"));
        Assert.False(listed.HostAllowed("notdata.example"));
    }

    [Fact]
    public void Keeping_writing_and_the_size_cap_have_their_defaults()
    {
        var defaults = Options(("WDS_OPEN_FROM_URL", "true"));

        Assert.False(defaults.KeepInStore);
        Assert.False(defaults.Writable);
        Assert.Equal(512L * 1024 * 1024, defaults.MaxBytes);
    }

    [Fact]
    public void And_can_all_be_said()
    {
        var set = Options(("WDS_OPEN_FROM_URL", "true"), ("WDS_OPEN_FROM_URL_KEEP", "store"),
            ("WDS_OPEN_FROM_URL_WRITABLE", "true"), ("WDS_OPEN_FROM_URL_MAX_MB", "8"));

        Assert.True(set.KeepInStore);
        Assert.True(set.Writable);
        Assert.Equal(8L * 1024 * 1024, set.MaxBytes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("plenty")]
    public void A_size_cap_that_says_nothing_keeps_the_default(string raw) =>
        Assert.Equal(512L * 1024 * 1024,
            Options(("WDS_OPEN_FROM_URL", "true"), ("WDS_OPEN_FROM_URL_MAX_MB", raw)).MaxBytes);
}
