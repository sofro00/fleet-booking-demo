using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using FleetBooking.Shared;

namespace FleetBooking.Client;

public static class SessionStore
{
    private static readonly byte[] SessionEntropy = Encoding.UTF8.GetBytes("FleetBookingDemo-Vozila-v1");
    private static readonly byte[] CredentialEntropy = Encoding.UTF8.GetBytes("FleetBookingDemo-Vozila-credentials-v1");
    private static readonly string DirectoryPath = Path.Combine(AppContext.BaseDirectory, ".local", "client");
    private static readonly string SessionPath = Path.Combine(DirectoryPath, "session.dat");
    private static readonly string CredentialPath = Path.Combine(DirectoryPath, "credentials.dat");

    public static void Save(LoginResponse session)
    {
        Directory.CreateDirectory(DirectoryPath);
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        var encrypted = ProtectedData.Protect(plain, SessionEntropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SessionPath, encrypted);
        CryptographicOperations.ZeroMemory(plain);
    }

    public static LoginResponse? Load()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            var encrypted = File.ReadAllBytes(SessionPath);
            var plain = ProtectedData.Unprotect(encrypted, SessionEntropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<LoginResponse>(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch { ClearSession(); return null; }
    }

    public static void SaveCredentials(string email, string password)
    {
        Directory.CreateDirectory(DirectoryPath);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new SavedCredentials(email.Trim(), password));
        var encrypted = ProtectedData.Protect(plain, CredentialEntropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(CredentialPath, encrypted);
        CryptographicOperations.ZeroMemory(plain);
    }

    public static SavedCredentials? LoadCredentials()
    {
        try
        {
            if (!File.Exists(CredentialPath)) return null;
            var encrypted = File.ReadAllBytes(CredentialPath);
            var plain = ProtectedData.Unprotect(encrypted, CredentialEntropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<SavedCredentials>(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch { ClearCredentials(); return null; }
    }

    public static void ClearSession()
    {
        try { if (File.Exists(SessionPath)) File.Delete(SessionPath); } catch { }
    }

    public static void ClearCredentials()
    {
        try { if (File.Exists(CredentialPath)) File.Delete(CredentialPath); } catch { }
    }

    public static void ClearAll()
    {
        ClearSession();
        ClearCredentials();
    }
}

public sealed record SavedCredentials(string Email, string Password);
