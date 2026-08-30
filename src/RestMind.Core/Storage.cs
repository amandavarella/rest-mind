using System.Text.Json;

namespace RestMind.Core;

/// <summary>
/// Where config, state, and logs live. On Windows this resolves to
/// <c>C:\ProgramData\RestMind</c>, which the installer ACLs to administrators only.
/// </summary>
public sealed class RestMindPaths
{
    public RestMindPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RestMind");
    }

    public string Root { get; }
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string StateFile => Path.Combine(Root, "state.json");
    public string LogDirectory => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}

/// <summary>Reads and writes the parent-owned configuration file.</summary>
public sealed class ConfigStore
{
    private readonly RestMindPaths _paths;

    public ConfigStore(RestMindPaths paths) => _paths = paths;

    /// <summary>
    /// Loads config.json, falling back to defaults when it is missing or unreadable. Enforcement
    /// must never fail open just because a file got corrupted.
    /// </summary>
    public AppConfig Load()
    {
        try
        {
            if (File.Exists(_paths.ConfigFile))
            {
                return AppConfig.FromJson(File.ReadAllText(_paths.ConfigFile));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to defaults.
        }

        return new AppConfig();
    }

    public void Save(AppConfig config)
    {
        _paths.EnsureCreated();
        AtomicWrite.Text(_paths.ConfigFile, config.ToJson());
    }
}

/// <summary>Persists the scheduler state so a reboot cannot clear an in-progress break.</summary>
public sealed class StateStore
{
    private readonly RestMindPaths _paths;

    public StateStore(RestMindPaths paths) => _paths = paths;

    public SchedulerState Load()
    {
        try
        {
            if (File.Exists(_paths.StateFile))
            {
                var json = File.ReadAllText(_paths.StateFile);
                var state = JsonSerializer.Deserialize<SchedulerState>(json, AppConfig.JsonOptions);
                if (state is not null)
                {
                    // The clock only advances while the service runs, so a stale tick timestamp
                    // must not be credited as work after a reboot.
                    state.LastTickUtc = null;
                    state.AccruingWork = false;
                    return state;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to a fresh state.
        }

        return new SchedulerState();
    }

    public void Save(SchedulerState state)
    {
        _paths.EnsureCreated();
        AtomicWrite.Text(_paths.StateFile, JsonSerializer.Serialize(state, AppConfig.JsonOptions));
    }
}

internal static class AtomicWrite
{
    /// <summary>
    /// Writes via a temp file and replaces the target, so a crash mid-write cannot leave a
    /// truncated config or state file behind.
    /// </summary>
    public static void Text(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);

        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}
