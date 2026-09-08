using Microsoft.AspNetCore.DataProtection;

namespace GroupSplit.App.Web;

/// <summary>
/// Where the web app's Data Protection key ring lives.
/// </summary>
/// <remarks>
/// This ring protects the sign-in cookie, the antiforgery token, and the OIDC correlation,
/// nonce and state cookies. Nothing here had configured it at all, so the ring fell back to
/// the framework default: a directory under the container's home, which is part of the
/// container's writable layer and goes with it. Every deploy therefore minted a brand new
/// ring, and every cookie the previous container had issued became undecryptable --
/// "The key {...} was not found in the key ring" on the antiforgery token, and "Unable to
/// unprotect the message.State" for anyone who happened to be mid sign-in.
/// <para>
/// So the path is configured rather than defaulted, and the deployment mounts a volume at
/// it. Absent outside development it throws rather than falling back, for the same reason
/// <c>Keycloak:Authority</c> does: the fallback is what made this invisible in the first
/// place, and a deployment that has forgotten the volume looks exactly like one that has it
/// until the second deploy logs everybody out.
/// </para>
/// <para>
/// Unencrypted at rest, unlike the API's ring, which is wrapped with a certificate the
/// database never sees. The threat differs: that ring shares a database with the ciphertext
/// it opens, whereas this one sits in a volume on the host, and anything that can read the
/// volume can already read the container it belongs to. What it protects is a session, not
/// a bank token, and it expires on its own in ninety days.
/// </para>
/// </remarks>
public static class KeyRingExtensions
{
    /// <summary>The directory the ring is kept in. Set by the AppHost, which owns the mount.</summary>
    public const string KeyRingPathConfigurationKey = "DataProtection:KeyRingPath";

    extension(WebApplicationBuilder builder)
    {
        public IHostApplicationBuilder AddWebKeyRing()
        {
            var protection = builder.Services.AddDataProtection()
                // Pinned, because it is part of key derivation: left to default to the
                // content root path, moving the app would orphan every cookie in flight.
                // It is not the API's name -- these are two rings protecting two different
                // things, and neither reads what the other wrote.
                .SetApplicationName("GroupSplit.Web");

            var keyRingPath = builder.Configuration[KeyRingPathConfigurationKey];

            if (string.IsNullOrWhiteSpace(keyRingPath))
            {
                if (!builder.Environment.IsDevelopment())
                {
                    throw new InvalidOperationException(
                        $"{KeyRingPathConfigurationKey} must be configured outside of development, "
                        + "or the key ring is lost every time the container is replaced and every "
                        + "signed-in browser is logged out.");
                }

                // Locally the default lands in the user profile, which persists across runs
                // perfectly well and needs no volume.
                return builder;
            }

            protection.PersistKeysToFileSystem(Directory.CreateDirectory(keyRingPath));

            return builder;
        }
    }
}
