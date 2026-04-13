using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace ObsidianX.Core.Models;

/// <summary>
/// Crypto wallet-like identity for each brain node.
/// Each brain has a unique address derived from its public key.
/// Format: 0xBRAIN-XXXX-XXXX-XXXX-XXXX
/// </summary>
public class BrainIdentity
{
    public string Address { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    [JsonIgnore] public string PrivateKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string AvatarSeed => Address.Length >= 16 ? Address[..16] : Address;

    public static BrainIdentity Generate(string displayName = "Anonymous Brain")
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateBytes = ecdsa.ExportECPrivateKey();
        var publicBytes = ecdsa.ExportSubjectPublicKeyInfo();

        var hash = SHA256.HashData(publicBytes);
        var addressHex = Convert.ToHexString(hash[..16]).ToLowerInvariant();
        var address = $"0xBRAIN-{addressHex[..4]}-{addressHex[4..8]}-{addressHex[8..12]}-{addressHex[12..16]}";

        return new BrainIdentity
        {
            Address = address,
            PublicKey = Convert.ToBase64String(publicBytes),
            PrivateKey = Convert.ToBase64String(privateBytes),
            DisplayName = displayName,
            CreatedAt = DateTime.UtcNow
        };
    }

    public string Sign(string data)
    {
        if (string.IsNullOrEmpty(PrivateKey))
            throw new InvalidOperationException("Private key not set");
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(Convert.FromBase64String(PrivateKey), out _);
        var dataBytes = Encoding.UTF8.GetBytes(data);
        var signature = ecdsa.SignData(dataBytes, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature);
    }

    public static bool Verify(string data, string signature, string publicKey)
    {
        if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(signature))
            return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            var dataBytes = Encoding.UTF8.GetBytes(data);
            var sigBytes = Convert.FromBase64String(signature);
            return ecdsa.VerifyData(dataBytes, sigBytes, HashAlgorithmName.SHA256);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    public void SaveToFile(string path)
    {
        var json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(path, json);
    }

    public static BrainIdentity LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonConvert.DeserializeObject<BrainIdentity>(json)
            ?? throw new InvalidOperationException("Invalid identity file");
    }
}
