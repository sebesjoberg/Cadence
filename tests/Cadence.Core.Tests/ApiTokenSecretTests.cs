using Cadence.Storage;
using Xunit;

namespace Cadence.Core.Tests;

public sealed class ApiTokenSecretTests
{
    [Fact]
    public void CreateReturnsABase64UrlSecretAndItsDigest()
    {
        var (secret, digest) = ApiTokenSecret.Create();

        Assert.Equal(43, secret.Length);
        Assert.DoesNotContain('+', secret);
        Assert.DoesNotContain('/', secret);
        Assert.DoesNotContain('=', secret);
        Assert.Equal(32, digest.Length);
    }

    [Fact]
    public void EverySecretIsDifferent()
    {
        var secrets = Enumerable.Range(0, 64).Select(_ => ApiTokenSecret.Create().Secret).ToList();

        Assert.Equal(secrets.Count, secrets.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DigestingAMintedSecretReproducesItsDigest()
    {
        var (secret, digest) = ApiTokenSecret.Create();

        Assert.Equal(digest, ApiTokenSecret.Digest(secret));
    }

    [Fact]
    public void AMintedSecretHasTheShapeResolutionLooksFor()
    {
        var (secret, _) = ApiTokenSecret.Create();

        Assert.True(ApiTokenSecret.HasSecretShape(secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("s3cret-token-value-32-chars-long")]
    [InlineData("MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDEy")]
    [InlineData("MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE+")]
    [InlineData("MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE/")]
    [InlineData("MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=")]
    public void AnythingElseHasNot(string? presented)
        => Assert.False(ApiTokenSecret.HasSecretShape(presented));

    [Fact]
    public void FingerprintIsTheFirstEightHexOfTheDigest()
    {
        var (_, digest) = ApiTokenSecret.Create();

        Assert.Equal(Convert.ToHexStringLower(digest)[..8], ApiTokenSecret.Fingerprint(digest));
    }
}
