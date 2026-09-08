using GroupSplit.API.Services.Banking;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The start-up check on the origin bank providers are told to call back on.
/// </summary>
/// <remarks>
/// It refuses on start rather than when a link is asked for, so these are the messages a
/// deployment sees instead of every link attempt answering 502 for a reason nobody can read
/// from the outside.
/// </remarks>
public class BankingOptionsValidatorTest
{
    private readonly BankingOptionsValidator _validator = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_origin_is_valid_because_that_is_the_local_posture(string? origin)
    {
        Assert.True(Validate(origin).Succeeded);
    }

    [Theory]
    [InlineData("https://groupsplit.example.com")]
    [InlineData("https://groupsplit.example.com/")]
    [InlineData("https://groupsplit.example.com:8443")]
    public void An_absolute_https_origin_is_valid(string origin)
    {
        Assert.True(Validate(origin).Succeeded);
    }

    /// <summary>
    /// The likeliest mistake: the hostname on its own, as it would be written in a DNS record
    /// or a Caddyfile.
    /// </summary>
    [Fact]
    public void A_hostname_with_no_scheme_is_refused()
    {
        var result = Validate("groupsplit.example.com");

        Assert.False(result.Succeeded);
        Assert.Contains("absolute URL", result.FailureMessage);
    }

    /// <summary>
    /// Plausible on a LAN deployment, and it would start and then refuse every link: the
    /// provider rejects a webhook address that is not HTTPS.
    /// </summary>
    [Fact]
    public void A_plain_http_origin_is_refused()
    {
        var result = Validate("http://groupsplit.lan");

        Assert.False(result.Succeeded);
        Assert.Contains("rather than https", result.FailureMessage);
    }

    [Theory]
    [InlineData("https://groupsplit.example.com/app")]
    [InlineData("https://groupsplit.example.com/?x=1")]
    [InlineData("https://groupsplit.example.com/#frag")]
    public void Anything_past_the_origin_is_refused(string origin)
    {
        var result = Validate(origin);

        Assert.False(result.Succeeded);
        Assert.Contains("path, query or fragment", result.FailureMessage);
    }

    /// <summary>
    /// The setting is named in every message, because the reader is looking at a container
    /// that would not start and has the deployment's variables in front of them.
    /// </summary>
    [Fact]
    public void Every_refusal_names_the_setting()
    {
        foreach (var origin in new[] { "groupsplit.example.com", "http://x.test", "https://x.test/app" })
            Assert.Contains("Banking:PublicOrigin", Validate(origin).FailureMessage);
    }

    private ValidateOptionsResult Validate(string? origin) =>
        _validator.Validate(name: null, new BankingOptions { PublicOrigin = origin });
}
