using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// Two questions kept apart: where a new connection goes, and which ways in exist at all. A studio
/// that says nothing about either behaves as it always did — that is what most of these check.
public class StudioAccessTests
{
    private static StudioAccess Access(params (string Key, string Value)[] settings) =>
        StudioAccess.From(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build());

    [Fact]
    public void Nothing_configured_is_the_studio_as_it_was()
    {
        var access = Access();

        Assert.Equal(ConnectionScope.Stored, access.Scope);
        Assert.True(access.MayAdd);
        Assert.True(access.MayUpload);
        Assert.True(access.MayBrowse);
    }

    [Fact]
    public void The_scope_says_where_a_new_connection_goes()
    {
        Assert.Equal(ConnectionScope.Session,
            Access(("WDS_CONNECTION_SCOPE", "session")).Scope);

        // Spelled either way, because a person writing a compose file types what reads best.
        Assert.Equal(ConnectionScope.Session,
            Access(("WDS_CONNECTION_SCOPE", "Session")).Scope);

        Assert.Equal(ConnectionScope.Stored,
            Access(("WDS_CONNECTION_SCOPE", "stored")).Scope);
    }

    /// A word nobody meant is the shared store, which is the safe end of the two: a visitor's
    /// connection showing up for everybody is a surprise, the other way round is a lost tab.
    [Fact]
    public void A_scope_nobody_recognises_is_the_store()
    {
        Assert.Equal(ConnectionScope.Stored, Access(("WDS_CONNECTION_SCOPE", "ephemeral")).Scope);
        Assert.Equal(ConnectionScope.Stored, Access(("WDS_CONNECTION_SCOPE", "")).Scope);
    }

    [Theory]
    [InlineData("WDS_ALLOW_ADD_CONNECTION")]
    [InlineData("WDS_ALLOW_FILE_UPLOAD")]
    [InlineData("WDS_ALLOW_FILE_BROWSE")]
    public void A_door_closes_only_when_a_deployment_says_false(string key)
    {
        bool Open(StudioAccess access) => key switch
        {
            "WDS_ALLOW_ADD_CONNECTION" => access.MayAdd,
            "WDS_ALLOW_FILE_UPLOAD" => access.MayUpload,
            _ => access.MayBrowse,
        };

        Assert.False(Open(Access((key, "false"))));
        Assert.False(Open(Access((key, "False"))));
        Assert.True(Open(Access((key, "true"))));
        // Anything else leaves the door as it was, rather than closing it on a typo.
        Assert.True(Open(Access((key, "no"))));
    }

    [Fact]
    public void The_session_lifetime_is_four_hours_unless_it_is_said_otherwise()
    {
        Assert.Equal(TimeSpan.FromMinutes(240), Access().SessionTtl);
        Assert.Equal(TimeSpan.FromMinutes(30), Access(("WDS_SESSION_TTL_MINUTES", "30")).SessionTtl);
    }

    /// Zero is a deployment saying "never expire", which is what a studio one person runs on their
    /// own machine wants.
    [Fact]
    public void Zero_minutes_means_a_session_never_expires()
    {
        Assert.Null(Access(("WDS_SESSION_TTL_MINUTES", "0")).SessionTtl);
    }

    [Fact]
    public void A_lifetime_that_is_not_a_number_falls_back_rather_than_refusing_to_start()
    {
        Assert.Equal(TimeSpan.FromMinutes(240), Access(("WDS_SESSION_TTL_MINUTES", "soon")).SessionTtl);
        Assert.Equal(TimeSpan.FromMinutes(240), Access(("WDS_SESSION_TTL_MINUTES", "-5")).SessionTtl);
    }

    [Fact]
    public void The_ceiling_and_the_upload_size_have_defaults_and_can_be_said()
    {
        Assert.Equal(25, Access().MaxSessionConnections);
        Assert.Equal(100L * 1024 * 1024, Access().UploadMaxBytes);

        Assert.Equal(3, Access(("WDS_SESSION_MAX_CONNECTIONS", "3")).MaxSessionConnections);
        Assert.Equal(5L * 1024 * 1024, Access(("WDS_UPLOAD_MAX_MB", "5")).UploadMaxBytes);
    }

    [Fact]
    public void A_ceiling_or_a_size_that_makes_no_sense_falls_back()
    {
        Assert.Equal(25, Access(("WDS_SESSION_MAX_CONNECTIONS", "0")).MaxSessionConnections);
        Assert.Equal(25, Access(("WDS_SESSION_MAX_CONNECTIONS", "lots")).MaxSessionConnections);
        Assert.Equal(100L * 1024 * 1024, Access(("WDS_UPLOAD_MAX_MB", "0")).UploadMaxBytes);
    }

    /// The one combination that cannot mean anything: connections held for one browser, and the same
    /// connections written to the store for everybody.
    [Fact]
    public void Session_scope_and_keeping_url_connections_in_the_store_is_refused()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WDS_CONNECTION_SCOPE"] = "session",
                ["WDS_OPEN_FROM_URL"] = "file",
                ["WDS_OPEN_FROM_URL_KEEP"] = "store",
            })
            .Build();

        var refused = Assert.Throws<InvalidOperationException>(() => StudioAccess.From(config));

        Assert.Contains("WDS_CONNECTION_SCOPE", refused.Message);
        Assert.Contains("WDS_OPEN_FROM_URL_KEEP", refused.Message);
    }

    /// `KEEP=store` on its own is the other branch of the URL feature and stays allowed.
    [Fact]
    public void Keeping_url_connections_in_the_store_is_fine_without_session_scope()
    {
        var access = Access(("WDS_OPEN_FROM_URL", "file"), ("WDS_OPEN_FROM_URL_KEEP", "store"));

        Assert.Equal(ConnectionScope.Stored, access.Scope);
    }
}
