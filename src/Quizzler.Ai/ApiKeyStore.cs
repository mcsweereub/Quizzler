using System.Security.Cryptography;
using System.Text;

namespace Quizzler.Ai;

/// <summary>
/// Where the Anthropic API key comes from: the ANTHROPIC_API_KEY environment variable, or a key saved in Settings.
/// A saved key is encrypted with Windows DPAPI for the current user before it goes in the settings table, so copying
/// quizzler.db to another PC (or sharing it) doesn't carry a usable key with it.
/// </summary>
public sealed class ApiKeyStore(Func<string, string?> getSetting, Action<string, string?> setSetting)
{
    public const string SettingKey = "anthropic_api_key";
    public const string EnvironmentVariable = "ANTHROPIC_API_KEY";

    public static string? FromEnvironment =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } key ? key : null;

    public bool HasSavedKey => LoadSaved() is not null;

    public bool IsConfigured => FromEnvironment is not null || HasSavedKey;

    /// <summary>"environment variable", "saved key", or null.</summary>
    public string? Source => FromEnvironment is not null ? "the ANTHROPIC_API_KEY environment variable" : HasSavedKey ? "the key saved in Settings" : null;

    public string? GetKey() => FromEnvironment ?? LoadSaved();

    public void Save(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            setSetting(SettingKey, null);
            return;
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saving a key needs Windows (DPAPI).");
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
        setSetting(SettingKey, Convert.ToBase64String(blob));
    }

    private string? LoadSaved()
    {
        if (getSetting(SettingKey) is not { Length: > 0 } stored || !OperatingSystem.IsWindows()) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // Saved by another Windows user or PC.
        }
    }

    private static readonly byte[] Entropy = "Quizzler.AnthropicApiKey"u8.ToArray();
}
