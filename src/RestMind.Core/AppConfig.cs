using System.Text.Json;
using System.Text.Json.Serialization;

namespace RestMind.Core;

/// <summary>
/// Everything the parent configures. Serialized to <c>C:\ProgramData\RestMind\config.json</c>,
/// which is ACL'd so only administrators can write it.
/// </summary>
public sealed class AppConfig
{
    public const int FallbackBreakMinutes = 10;

    /// <summary>Bumped if the file format ever changes, so an installer can migrate it.</summary>
    public int Version { get; set; } = 1;

    public Schedule Schedule { get; set; } = Schedule.CreateDefault();

    /// <summary>How long before a break to warn, so work can be saved. Zero disables the warning.</summary>
    public int PreBreakWarningMinutes { get; set; } = 2;

    /// <summary>PBKDF2 hash of the parent password. Null until the installer sets one.</summary>
    public string? PasswordHash { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static AppConfig FromJson(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();

    /// <summary>True when the supplied password matches the configured parent password.</summary>
    public bool VerifyPassword(string? password) =>
        !string.IsNullOrEmpty(PasswordHash)
        && !string.IsNullOrEmpty(password)
        && PasswordHasher.Verify(password, PasswordHash);
}
