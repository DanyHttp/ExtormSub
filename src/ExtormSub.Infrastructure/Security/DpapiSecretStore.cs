using System.Security.Cryptography;
using System.Text;
using ExtormSub.Core.Settings;

namespace ExtormSub.Infrastructure.Security;

/// <summary>
/// Secrets encrypted with Windows DPAPI for the current user: only this Windows account on this
/// machine can decrypt them. One file per name; plaintext never touches disk or logs.
/// </summary>
public sealed class DpapiSecretStore(string directory) : ISecretStore
{
    private static readonly byte[] Entropy = "ExtormSub.secret.v1"u8.ToArray();

    public string? Get(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null; // copied from another user/machine: treat as missing
        }
    }

    public void Set(string name, string? value)
    {
        var path = PathFor(name);
        if (string.IsNullOrEmpty(value))
        {
            File.Delete(path);
            return;
        }
        Directory.CreateDirectory(directory);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, cipher);
    }

    private string PathFor(string name)
    {
        var safe = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        return Path.Combine(directory, safe + ".bin");
    }

    public static string ApiKeyName(string provider) => "apikey-" + provider.ToLowerInvariant();
}
