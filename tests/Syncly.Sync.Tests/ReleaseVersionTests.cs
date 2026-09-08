using Syncly.App;

namespace Syncly.Sync.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v2.0.10", "2.0.9", true)]
    [InlineData("2.0.10", "2.0.10", false)]
    [InlineData("v2.0.1", "2.0.10", false)]
    [InlineData("v2.0.11+build", "2.0.10", true)]
    public void Compares_release_tags_to_the_running_app(string latest, string current, bool newer)
    {
        Assert.Equal(newer, ReleaseVersion.IsNewer(latest, current));
    }

    [Fact]
    public void Current_version_is_semver()
    {
        Assert.True(ReleaseVersion.TryParse(AppRelease.CurrentVersion, out _));
    }
}
