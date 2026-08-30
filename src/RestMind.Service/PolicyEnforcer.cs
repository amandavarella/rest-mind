using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace RestMind.Service;

/// <summary>
/// Turns Task Manager off for the duration of a break and, more importantly, reliably turns it
/// back on. Leaving a machine with Task Manager permanently disabled would be a far worse
/// outcome than a break being escapable, so every path here errs towards reverting.
/// </summary>
/// <remarks>
/// This sets the machine-wide policy under HKLM rather than per-user under the child's hive.
/// It is one value to set and one to remove, which makes the revert simple enough to trust.
/// The trade-off is that Task Manager is also unavailable to anyone else signed in during a
/// break; it returns as soon as the break ends.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PolicyEnforcer
{
    private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ValueName = "DisableTaskMgr";

    private readonly ILogger _logger;
    private bool _applied;

    public PolicyEnforcer(ILogger logger) => _logger = logger;

    public void Apply(bool blocking)
    {
        if (blocking == _applied)
        {
            return;
        }

        if (blocking)
        {
            Set();
        }
        else
        {
            Revert();
        }
    }

    /// <summary>
    /// Clears the policy unconditionally. Called at startup and shutdown so a service crash
    /// during a break cannot leave Task Manager disabled forever.
    /// </summary>
    public void Revert()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey, writable: true);
            if (key?.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.LogInformation("Task Manager re-enabled");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to re-enable Task Manager");
        }
        finally
        {
            _applied = false;
        }
    }

    private void Set()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(PolicyKey, writable: true);
            key.SetValue(ValueName, 1, RegistryValueKind.DWord);
            _applied = true;
            _logger.LogInformation("Task Manager disabled for the break");
        }
        catch (Exception ex)
        {
            // A break that is slightly more escapable is acceptable; a crash here is not.
            _logger.LogError(ex, "Failed to disable Task Manager");
        }
    }
}
