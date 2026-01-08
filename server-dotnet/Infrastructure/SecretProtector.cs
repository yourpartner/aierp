using System.Security.Cryptography;
using System.Text;

namespace Server.Infrastructure;

public static class SecretProtector
{
    public static string? ProtectToBase64(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;

        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("SecretProtector: DPAPI is only available on Windows. Configure an alternative protector for non-Windows environments.");

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string? UnprotectFromBase64(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;

        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("SecretProtector: DPAPI is only available on Windows. Configure an alternative protector for non-Windows environments.");

        var protectedBytes = Convert.FromBase64String(protectedBase64);
        var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}


