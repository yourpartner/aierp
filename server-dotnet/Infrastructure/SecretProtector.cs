using System.Security.Cryptography;
using System.Text;

namespace Server.Infrastructure;

public static class SecretProtector
{
    public static string? ProtectToBase64(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        if (OperatingSystem.IsWindows())
        {
            var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        else
        {
            // Fallback for non-windows: use plain base64 (not secure)
            return Convert.ToBase64String(bytes);
        }
    }

    public static string? UnprotectFromBase64(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;

        if (OperatingSystem.IsWindows())
        {
            var protectedBytes = Convert.FromBase64String(protectedBase64);
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        else
        {
            // Fallback for non-windows: assume it's plain base64
            var bytes = Convert.FromBase64String(protectedBase64);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}


