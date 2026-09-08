using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>
/// Where the key ring that protects the sign-in, antiforgery and OIDC cookies is kept.
/// </summary>
/// <remarks>
/// It was kept nowhere in particular, which is to say in the container's writable layer,
/// and every deploy threw it away: the next container could not decrypt a single cookie the
/// last one had issued. What makes that hard to notice is that it looks like a working
/// deployment right up until the second deploy, so the rule under test is that the fallback
/// is refused rather than taken quietly.
/// </remarks>
public class KeyRingTest
{
    private static WebApplicationBuilder BuilderFor(string environment, string? keyRingPath)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment
        });

        if (keyRingPath is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KeyRingExtensions.KeyRingPathConfigurationKey] = keyRingPath
            });
        }

        return builder;
    }

    [Fact]
    public void A_deployment_that_says_nothing_about_the_key_ring_is_refused()
    {
        var builder = BuilderFor(Environments.Production, keyRingPath: null);

        var refusal = Assert.Throws<InvalidOperationException>(() => builder.AddWebKeyRing());

        Assert.Contains(KeyRingExtensions.KeyRingPathConfigurationKey, refusal.Message);
    }

    /// <summary>
    /// Locally the framework default lands in the user profile, which persists across runs
    /// on its own and wants no volume, so there is nothing here worth failing a run over.
    /// </summary>
    [Fact]
    public void Development_is_left_to_the_default_location()
    {
        var builder = BuilderFor(Environments.Development, keyRingPath: null);

        builder.AddWebKeyRing();
    }

    [Fact]
    public void A_configured_directory_is_created_if_it_is_not_there_yet()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groupsplit-keyring-{Guid.NewGuid():N}");

        try
        {
            BuilderFor(Environments.Production, path).AddWebKeyRing();

            Assert.True(Directory.Exists(path),
                "the ring has to have somewhere to be written before the first cookie is issued");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
