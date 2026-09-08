using Syncly.Backend.ProtonDrive;

namespace Syncly.Sync.Tests;

public class ProtonShareUrlTests
{
    [Fact]
    public void Parses_a_drive_editor_link()
    {
        var url = ProtonShareUrl.Parse("https://drive.proton.me/urls/AbCdEfGh1234#s3cretPass");
        Assert.Equal("AbCdEfGh1234", url.Token);
        Assert.Equal("s3cretPass", url.Password);
    }

    [Fact]
    public void Parses_proton_me_drive_urls()
    {
        var url = ProtonShareUrl.Parse("https://proton.me/drive/urls/Token_99#abc");
        Assert.Equal("Token_99", url.Token);
        Assert.Equal("abc", url.Password);
    }

    [Fact]
    public void Rejects_a_link_without_the_password_fragment()
    {
        Assert.Throws<FormatException>(() =>
            ProtonShareUrl.Parse("https://drive.proton.me/urls/AbCdEfGh1234"));
    }

    [Fact]
    public void Ignores_junk_after_a_second_hash()
    {
        var url = ProtonShareUrl.Parse(
            "https://drive.proton.me/urls/AbCdEfGh1234#s3cretPass#not-part-of-the-link");
        Assert.Equal("AbCdEfGh1234", url.Token);
        Assert.Equal("s3cretPass", url.Password);
    }

    [Fact]
    public void Rejects_a_non_proton_host()
    {
        Assert.Throws<FormatException>(() =>
            ProtonShareUrl.Parse("https://example.com/urls/AbCdEfGh1234#pw"));
    }

    [Theory]
    [InlineData("v2/volumes/vol/folders/id/children", "unauth/v2/volumes/vol/folders/id/children")]
    [InlineData("urls/TOKEN/info", "urls/TOKEN/info")]
    [InlineData("v2/urls/TOKEN/info", "v2/urls/TOKEN/info")]
    [InlineData("unauth/v2/already", "unauth/v2/already")]
    public void Public_link_data_uses_the_unauth_prefix(string input, string expected)
    {
        Assert.Equal(expected, ProtonDriveBackend.DataPath(input));
    }

    [Fact]
    public void Api_json_keeps_proton_pascal_case_field_names()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { LinkIDs = new[] { "abc" } }, ProtonDriveBackend.ApiJson);
        Assert.Contains("\"LinkIDs\"", json);
        Assert.DoesNotContain("\"linkIDs\"", json);
    }
}
