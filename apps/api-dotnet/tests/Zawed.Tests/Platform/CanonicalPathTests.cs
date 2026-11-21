using Zawed.Api.Platform.Web;
using Zawed.Tests.Support;

namespace Zawed.Tests.Platform;

/// <summary>Port of the Java <c>CanonicalPathFilterTests</c> and <c>PathMatchingTests</c>' first layer.</summary>
public sealed class CanonicalPathTests(KestrelApi api) : IClassFixture<KestrelApi>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/error")]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/auth/login/")]
    [InlineData("/api/v1/companies/0193a5b2-7c41-7000-8000-000000000001")]
    [InlineData("/api/v1/a.b_c~d-e")]
    public void ACanonicalPathPasses(string path) => Assert.True(CanonicalPath.IsCanonical(path));

    [Theory]
    [InlineData("")]
    [InlineData("api/v1/auth/login")]
    [InlineData("/api/v1/auth/logi%6E")]
    [InlineData("/api/v1/auth/logi%6e")]
    [InlineData("/api/v1/auth%2Flogin")]
    [InlineData("/api/v1//auth/login")]
    [InlineData("/api/v1/./auth/login")]
    [InlineData("/api/v1/../v1/auth/login")]
    [InlineData("/api/v1/auth/login;jsessionid=x")]
    [InlineData("/api/v1/auth/..")]
    [InlineData("/api/v1/auth/.")]
    [InlineData("/api/v1/auth/login//")]
    [InlineData("/api/v1/ auth")]
    public void AnyOtherSpellingIsRefused(string path) => Assert.False(CanonicalPath.IsCanonical(path));

    [Fact]
    public void AVeryLongPathIsAnsweredWithoutOverflowingOrHanging()
    {
        var path = string.Concat(Enumerable.Repeat("/a", 200_000));
        Assert.True(CanonicalPath.IsCanonical(path));
        Assert.False(CanonicalPath.IsCanonical(path + "/%41"));
    }

    [Theory]
    [InlineData("/api/v1/auth/logi%6E")]
    [InlineData("/api/v1/me%74a")]
    [InlineData("/api/v1//meta")]
    [InlineData("/api/v1/./meta")]
    [InlineData("/api/v1/meta;x=1")]
    public async Task TheServerRefusesANonCanonicalPathWithItsOwnCodeBeforeAnythingElse(string path)
    {
        var (status, response) = await api.RawAsync("GET", path);

        Assert.Equal(400, status);
        Assert.Contains("\"code\":\"PATH_NOT_CANONICAL\"", response);
        Assert.Contains("\"message\":\"The request path is not in its canonical form\"", response);
    }

    [Fact]
    public async Task TheCanonicalSpellingIsServedAndTheQueryStringKeepsItsEncoding()
    {
        Assert.Equal(200, (await api.RawAsync("GET", "/api/v1/meta?x=%41%2F")).Status);
    }
}
