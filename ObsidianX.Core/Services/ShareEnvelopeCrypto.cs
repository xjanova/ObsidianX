using System.Security.Cryptography;
using System.Text;
using ObsidianX.Core.Models;

namespace ObsidianX.Core.Services;

/// <summary>
/// End-to-end encryption for ShareEnvelope payloads. Hub-blind: even a
/// hostile hub with full read access to its own DB cannot recover the
/// shared note content, because the key is derived from a fresh ephemeral
/// pair (generated per request by the requester) combined with the
/// owner's long-term ECDSA key via ECDH.
///
/// Curve: NIST P-256 (matches <see cref="BrainIdentity"/>).
/// AEAD:  AES-256-GCM.
/// KDF:   HKDF-SHA256 with the GCM nonce as salt + a fixed "obsidianx-share-v1" info tag.
/// </summary>
public static class ShareEnvelopeCrypto
{
    private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("obsidianx-share-v1");

    /// <summary>
    /// Generate a fresh ephemeral ECDH keypair on P-256. Use the public half
    /// in ShareRequest.RequesterEphemeralPublicKey; keep the private half in
    /// memory until the matching envelope arrives.
    /// </summary>
    public static (string PublicKeyBase64, string PrivateKeyBase64) GenerateEphemeralPair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo());
        var priv = Convert.ToBase64String(ecdh.ExportECPrivateKey());
        return (pub, priv);
    }

    /// <summary>
    /// Owner-side: encrypt <paramref name="plaintext"/> for the requester
    /// identified by <paramref name="requesterEphemeralPublicKey"/>. Pass
    /// the owner's LONG-TERM private key (from <see cref="BrainIdentity.PrivateKey"/>)
    /// — the same ECDSA-P256 key reused as ECDH; both curves are the same
    /// so .NET's APIs interop via ImportECPrivateKey/ExportECPrivateKey.
    /// </summary>
    public static ShareEnvelope Encrypt(
        BrainIdentity ownerIdentity,
        string requesterEphemeralPublicKey,
        string requesterAddress,
        string nodeId,
        byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(ownerIdentity);
        if (!ownerIdentity.CanSign)
            throw new InvalidOperationException("owner identity has no private key");

        // Import the requester's ephemeral pub + the owner's long-term priv
        // and derive the shared secret. Both must be on the same curve
        // (P-256) — BrainIdentity.Generate enforces this server-side.
        using var ownerEcdh = ECDiffieHellman.Create();
        ownerEcdh.ImportECPrivateKey(Convert.FromBase64String(ownerIdentity.PrivateKey), out _);

        using var requesterPub = ECDiffieHellman.Create();
        requesterPub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(requesterEphemeralPublicKey), out _);
        // TODO(threat-model L4): verify requesterPub is on P-256 — currently
        // ImportSubjectPublicKeyInfo accepts any supported curve, and a
        // curve mismatch surfaces as a generic CryptographicException at
        // DeriveRawSecretAgreement instead of a clear "wrong curve" error.

        var ad = BuildAssociatedData(ownerIdentity.Address, requesterAddress, nodeId, requesterEphemeralPublicKey);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = DeriveKey(ownerEcdh, requesterPub.PublicKey, nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, ad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return new ShareEnvelope
        {
            FromAddress = ownerIdentity.Address,
            ToAddress = requesterAddress,
            NodeId = nodeId,
            RequesterEphemeralPublicKey = requesterEphemeralPublicKey,
            NonceBase64 = Convert.ToBase64String(nonce),
            TagBase64 = Convert.ToBase64String(tag),
            CiphertextBase64 = Convert.ToBase64String(ciphertext),
            AssociatedDataBase64 = Convert.ToBase64String(ad)
        };
    }

    /// <summary>
    /// Requester-side: decrypt an envelope using the ephemeral private key
    /// that was generated for this request plus the OWNER's long-term
    /// public key (looked up from the network roster).
    /// </summary>
    public static byte[] Decrypt(
        string ephemeralPrivateKeyBase64,
        string ownerPublicKeyBase64,
        ShareEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var requesterEcdh = ECDiffieHellman.Create();
        requesterEcdh.ImportECPrivateKey(Convert.FromBase64String(ephemeralPrivateKeyBase64), out _);

        using var ownerPub = ECDiffieHellman.Create();
        ownerPub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ownerPublicKeyBase64), out _);

        var nonce = Convert.FromBase64String(envelope.NonceBase64);
        var tag = Convert.FromBase64String(envelope.TagBase64);
        var ciphertext = Convert.FromBase64String(envelope.CiphertextBase64);
        var ad = Convert.FromBase64String(envelope.AssociatedDataBase64);

        var key = DeriveKey(requesterEcdh, ownerPub.PublicKey, nonce);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: 16);
            // Throws CryptographicException on auth failure — bubble up to
            // caller so they know the envelope was tampered or mis-routed.
            aes.Decrypt(nonce, ciphertext, tag, plaintext, ad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return plaintext;
    }

    private static byte[] DeriveKey(ECDiffieHellman ourEcdh, ECDiffieHellmanPublicKey theirPub, byte[] salt)
    {
        // ECDH agreement → HKDF-SHA256 → 32-byte AES-GCM key.
        // Using the GCM nonce as HKDF salt is the standard cheap-binding
        // trick: same ECDH pair gives a different key per envelope.
        //
        // TODO(threat-model C1): DeriveRawSecretAgreement may throw
        // PlatformNotSupportedException on non-Windows .NET runtimes
        // depending on which provider Create() picked. Tested OK on
        // Windows .NET 9; if we ever ship a Linux server consider
        // DeriveKeyFromHash(SHA256) as a portable fallback.
        var shared = ourEcdh.DeriveRawSecretAgreement(theirPub);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, outputLength: 32, salt: salt, info: HkdfInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    /// <summary>
    /// AssociatedData binds the envelope to the specific request. Any
    /// downstream change to fromAddr / toAddr / nodeId / ephemeral pub will
    /// invalidate the GCM tag and decrypt will throw.
    /// </summary>
    private static byte[] BuildAssociatedData(string fromAddr, string toAddr, string nodeId, string ephPub)
    {
        return Encoding.UTF8.GetBytes(
            "obsidianx-share-v1\n" +
            fromAddr + "\n" +
            toAddr + "\n" +
            nodeId + "\n" +
            ephPub);
    }
}
