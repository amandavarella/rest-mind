using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace RestMind.Service;

/// <summary>
/// Keeps the overlay agent alive in the interactive session. The agent is deliberately
/// expendable: killing it never stops the break clock, which lives in the service, but the
/// overlay should come back within a few seconds so the block is not usefully escapable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentWatchdog
{
    private const string AgentProcessName = "RestMind.Agent";
    private static readonly TimeSpan RelaunchCooldown = TimeSpan.FromSeconds(5);

    private readonly string _agentPath;
    private readonly ILogger _logger;
    private DateTimeOffset _lastLaunchAttempt = DateTimeOffset.MinValue;

    public AgentWatchdog(string agentPath, ILogger logger)
    {
        _agentPath = agentPath;
        _logger = logger;
    }

    /// <summary>
    /// Relaunches the agent if nobody is running it in the active console session. Called on a
    /// timer, so it is written to be cheap and to never throw.
    /// </summary>
    public void EnsureRunning(DateTimeOffset nowUtc)
    {
        try
        {
            var sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
            {
                return; // No interactive session attached, e.g. nobody logged in.
            }

            if (IsAgentRunningInSession(sessionId))
            {
                return;
            }

            if (nowUtc - _lastLaunchAttempt < RelaunchCooldown)
            {
                return; // Do not spin if the agent is crash-looping.
            }

            _lastLaunchAttempt = nowUtc;
            Launch(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent watchdog check failed");
        }
    }

    private static bool IsAgentRunningInSession(uint sessionId)
    {
        foreach (var process in Process.GetProcessesByName(AgentProcessName))
        {
            using (process)
            {
                if (process.SessionId == sessionId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void Launch(uint sessionId)
    {
        if (!File.Exists(_agentPath))
        {
            _logger.LogError("Agent executable not found at {Path}", _agentPath);
            return;
        }

        if (!NativeMethods.WTSQueryUserToken(sessionId, out var userToken))
        {
            _logger.LogWarning(
                "Could not obtain a user token for session {Session} (error {Error})",
                sessionId,
                Marshal.GetLastWin32Error());
            return;
        }

        var duplicateToken = IntPtr.Zero;
        var environment = IntPtr.Zero;

        try
        {
            var security = new NativeMethods.SecurityAttributes();
            security.nLength = Marshal.SizeOf(security);

            if (!NativeMethods.DuplicateTokenEx(
                    userToken,
                    NativeMethods.MaximumAllowed,
                    ref security,
                    NativeMethods.SecurityImpersonation,
                    NativeMethods.TokenPrimary,
                    out duplicateToken))
            {
                _logger.LogWarning("DuplicateTokenEx failed with {Error}", Marshal.GetLastWin32Error());
                return;
            }

            NativeMethods.CreateEnvironmentBlock(out environment, duplicateToken, false);

            var startup = new NativeMethods.StartupInfo();
            startup.cb = Marshal.SizeOf(startup);
            // "winsta0\default" is the interactive desktop; without it the window is invisible.
            startup.lpDesktop = Marshal.StringToHGlobalUni("winsta0\\default");

            var processSecurity = new NativeMethods.SecurityAttributes();
            processSecurity.nLength = Marshal.SizeOf(processSecurity);
            var threadSecurity = new NativeMethods.SecurityAttributes();
            threadSecurity.nLength = Marshal.SizeOf(threadSecurity);

            try
            {
                var created = NativeMethods.CreateProcessAsUser(
                    duplicateToken,
                    _agentPath,
                    null,
                    ref processSecurity,
                    ref threadSecurity,
                    false,
                    NativeMethods.CreateUnicodeEnvironment,
                    environment,
                    Path.GetDirectoryName(_agentPath),
                    ref startup,
                    out var processInfo);

                if (created)
                {
                    _logger.LogInformation(
                        "Relaunched agent as pid {Pid} in session {Session}",
                        processInfo.dwProcessId,
                        sessionId);
                    NativeMethods.CloseHandle(processInfo.hProcess);
                    NativeMethods.CloseHandle(processInfo.hThread);
                }
                else
                {
                    _logger.LogWarning(
                        "CreateProcessAsUser failed with {Error}",
                        Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                if (startup.lpDesktop != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(startup.lpDesktop);
                }
            }
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                NativeMethods.DestroyEnvironmentBlock(environment);
            }

            if (duplicateToken != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(duplicateToken);
            }

            NativeMethods.CloseHandle(userToken);
        }
    }
}
