using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Cadence.Storage;

/// <summary>
/// Mints token secrets and digests presented ones.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in a storage tier because the format is part of the contract: anything
/// implementing <see cref="IWritableApiTokenStore"/> has to produce digests the authenticate half
/// can resolve, and a second definition of "digest" is a tier that silently rejects every token.
/// </para>
/// <para>
/// SHA-256 with no key derivation, deliberately. A secret from this class carries 256 bits of
/// entropy, so there is nothing to brute-force, and the digest is computed on every authenticated
/// request — a deliberately slow hash would be a deliberately slow API. Passwords would need the
/// opposite, and Cadence never sees one.
/// </para>
/// </remarks>
public static class ApiTokenSecret
{
    /// <summary>How many characters <see cref="Create"/> produces: 32 bytes, Base64Url, unpadded.</summary>
    private const int SecretLength = 43;

    /// <summary>Mints a secret and its digest. The secret is never stored.</summary>
    /// <returns>The value to hand the caller once, and the digest to persist.</returns>
    public static (string Secret, byte[] Digest) Create()
    {
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (secret, Digest(secret));
    }

    /// <summary>Whether a presented value has the shape <see cref="Create"/> produces.</summary>
    /// <remarks>
    /// For refusing a value that was never minted here before a store is asked about it: without it
    /// an unauthenticated caller drives one index seek and one pooled connection per request. Not a
    /// cache — no answer is remembered, and a well-shaped value is still resolved through the store
    /// on every single request.
    /// </remarks>
    /// <param name="secret">The value from an <c>Authorization: Bearer</c> header.</param>
    public static bool HasSecretShape(string? secret)
    {
        if (secret is not { Length: SecretLength })
        {
            return false;
        }

        foreach (var character in secret)
        {
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Digests a presented secret.</summary>
    /// <param name="secret">The value from an <c>Authorization: Bearer</c> header.</param>
    public static byte[] Digest(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    /// <summary>
    /// The short, non-secret label for a digest, shown to operators and used in audit fields.
    /// </summary>
    /// <param name="digest">A digest from <see cref="Digest"/> or <see cref="Create"/>.</param>
    public static string Fingerprint(byte[] digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        return Convert.ToHexStringLower(digest)[..8];
    }
}
