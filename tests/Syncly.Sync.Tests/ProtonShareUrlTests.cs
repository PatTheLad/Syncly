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
}
