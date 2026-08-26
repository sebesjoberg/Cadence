using System.Security.Cryptography;
using System.Text;

namespace Cadence.Api.Internal;

/// <summary>
/// The configured tokens, as digests. The compare never needs the plaintext, so this keeps none:
/// <see cref="CadenceApiOptions.Tokens"/> stays the only place a token value lives.
/// </summary>
internal sealed class TokenSet
{
    private readonly byte[][] _digests;
    private readonly string[] _fingerprints;

    public TokenSet(IEnumerable<string> tokens)
    {
        var digests = new List<byte[]>();
        var fingerprints = new List<string>();

        foreach (var token in tokens)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            digests.Add(digest);
            fingerprints.Add(Convert.ToHexStringLower(digest)[..8]);
        }

        _digests = [.. digests];
        _fingerprints = [.. fingerprints];
    }

    /// <summary>
    /// The fingerprint of the token matching <paramref name="presented"/>, or null when none does.
    /// </summary>
    /// <remarks>
    /// Both sides are hashed first, so the compare is fixed-length and the presented token's length
    /// does not leak. Every configured token is compared even after a match, so the time taken does
    /// not reveal how far down the list the caller's token sits.
    /// </remarks>
    public string? Match(string presented)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        string? matched = null;

        for (var i = 0; i < _digests.Length; i++)
        {
            if (CryptographicOperations.FixedTimeEquals(digest, _digests[i]))
            {
                matched = _fingerprints[i];
            }
        }

        return matched;
    }
}
