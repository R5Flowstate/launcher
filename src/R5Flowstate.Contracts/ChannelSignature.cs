using System.Security.Cryptography;
using System.Text;

namespace R5Flowstate.Contracts;

/// <summary>
/// Detached signature over the exact CHANNEL bytes.
///
/// Content is otherwise trusted on TLS and a hostname allowlist alone, which
/// means write access to the bucket, a subdomain, or DNS is enough to direct
/// arbitrary writes into every player's game directory. The CHANNEL is the root
/// of that trust: the manifest is pinned by its sha256 in the CHANNEL, and every
/// object is pinned by its hash in the manifest, so signing the CHANNEL covers
/// the whole chain.
///
/// The signature is a sidecar rather than a field inside the document, so there
/// is no canonical-JSON problem to get wrong, and a launcher that does not know
/// about it is unaffected.
/// </summary>
public static class ChannelSignature
{
    public const string SidecarSuffix = ".sig";

    /// <summary>
    /// Base64 SubjectPublicKeyInfo for the release key, or empty while signing
    /// is not yet in use. Empty means unsigned channels are accepted; once a key
    /// is set, a channel that fails to verify is refused.
    /// </summary>
    public const string PublicKeyBase64 = "";

    public static bool IsEnforced => PublicKeyBase64.Length > 0;

    public enum Result
    {
        /// <summary>No key compiled in; signing is not in use yet.</summary>
        NotEnforced = 0,
        Valid = 1,
        Missing = 2,
        Invalid = 3,
    }

    public static Result Verify(ReadOnlySpan<byte> document, byte[]? signature) =>
        Verify(document, signature, PublicKeyBase64);

    public static Result Verify(ReadOnlySpan<byte> document, byte[]? signature, string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64))
            return Result.NotEnforced;
        if (signature is null || signature.Length == 0)
            return Result.Missing;

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return ecdsa.VerifyData(document, signature, HashAlgorithmName.SHA256)
                ? Result.Valid
                : Result.Invalid;
        }
        catch
        {
            // A malformed key or signature is a failed verification, never a pass.
            return Result.Invalid;
        }
    }

    public static string Describe(Result result) => result switch
    {
        Result.Valid => "signature verified",
        Result.Missing => "the channel is not signed and this launcher requires a signature",
        Result.Invalid => "the channel signature does not match the release key",
        _ => "signature not enforced",
    };

    /// <summary>Sidecar URL for a channel URL, whatever the transport.</summary>
    public static string SidecarUrl(string channelUrl) => channelUrl + SidecarSuffix;

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
