using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RestMind.Core;

namespace RestMind.Service;

/// <summary>
/// The enforcement loop. This process owns the clock; the overlay only renders what it is
/// told, which is what makes killing the overlay useless as an escape.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Worker : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StateCheckpointInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxPause = TimeSpan.FromHours(12);

    private readonly ILogger<Worker> _logger;
    private readonly RestMindPaths _paths;
    private readonly ConfigStore _configStore;
    private readonly StateStore _stateStore;
    private readonly PolicyEnforcer _policy;
    private readonly AgentWatchdog _watchdog;
    private readonly BreakScheduler _scheduler;
    private readonly PipeServer _pipe;
    private readonly object _schedulerLock = new();

    // Written by the tick loop, read by pipe threads answering status requests.
    private volatile SchedulerStatus _status = new(EnforcementState.Inactive, TimeSpan.Zero);
    private EnforcementState _lastPersistedState = EnforcementState.Inactive;
    private DateTimeOffset _lastWatchdogCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCheckpoint = DateTimeOffset.MinValue;
    private FileSystemWatcher? _configWatcher;
    private DateTimeOffset _configReloadDueAt = DateTimeOffset.MaxValue;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _paths = new RestMindPaths();
        _paths.EnsureCreated();

        _configStore = new ConfigStore(_paths);
        _stateStore = new StateStore(_paths);
        _policy = new PolicyEnforcer(logger);

        var agentPath = Path.Combine(AppContext.BaseDirectory, "RestMind.Agent.exe");
        _watchdog = new AgentWatchdog(agentPath, logger);

        _scheduler = new BreakScheduler(_configStore.Load(), _stateStore.Load());
        _pipe = new PipeServer(HandleCommand, CurrentStatusMessage, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RestMind service starting; data directory {Root}", _paths.Root);

        // If the service died mid-break last time, Task Manager may still be disabled.
        _policy.Revert();
        _pipe.Start();
        StartConfigWatcher();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await TickAsync().ConfigureAwait(false);
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>
    /// Picks up edits to config.json without the parent needing to run a reload command. This
    /// also removes a chicken-and-egg problem: after changing the password, reloading would
    /// otherwise still require the old one.
    /// </summary>
    private void StartConfigWatcher()
    {
        try
        {
            _configWatcher = new FileSystemWatcher(_paths.Root, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            // Saving a file usually raises several events; settle before re-reading.
            void Schedule(object _, FileSystemEventArgs __) =>
                _configReloadDueAt = DateTimeOffset.UtcNow.AddSeconds(1);

            _configWatcher.Changed += Schedule;
            _configWatcher.Created += Schedule;
            _configWatcher.Renamed += (_, _) => Schedule(this, null!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not watch config.json for changes");
        }
    }

    private void ReloadConfigIfDue(DateTimeOffset now)
    {
        if (now < _configReloadDueAt)
        {
            return;
        }

        _configReloadDueAt = DateTimeOffset.MaxValue;

        lock (_schedulerLock)
        {
            _scheduler.Config = _configStore.Load();
        }

        _logger.LogInformation("Reloaded config.json after an on-disk change");
    }

    private async Task TickAsync()
    {
        var now = DateTimeOffset.UtcNow;
        ReloadConfigIfDue(now);

        SchedulerStatus status;
        SchedulerState snapshot;

        lock (_schedulerLock)
        {
            status = _scheduler.Tick(now);
            snapshot = _scheduler.State.Clone();
            _status = status;
        }

        if (status.State != _lastPersistedState)
        {
            _logger.LogInformation(
                "State {Previous} -> {Current} ({Remaining})",
                _lastPersistedState,
                status.State,
                status.Remaining);

            _lastPersistedState = status.State;
            _lastCheckpoint = now;
            Persist(snapshot);
            _policy.Apply(status.IsBlocking);
        }
        else if (now - _lastCheckpoint >= StateCheckpointInterval)
        {
            _lastCheckpoint = now;
            Persist(snapshot);
        }

        if (now - _lastWatchdogCheck >= WatchdogInterval)
        {
            _lastWatchdogCheck = now;
            _watchdog.EnsureRunning(now);
        }

        // Broadcast every tick so the overlay countdown stays honest.
        await _pipe.BroadcastAsync(CurrentStatusMessage()).ConfigureAwait(false);
    }

    private void Persist(SchedulerState state)
    {
        try
        {
            _stateStore.Save(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist scheduler state");
        }
    }

    private PipeMessage CurrentStatusMessage()
    {
        var status = _status;
        return PipeMessage.ForStatus(status, MessageFor(status.State));
    }

    private static string? MessageFor(EnforcementState state) => state switch
    {
        EnforcementState.OnBreak => "Screen break. Look away from the screen and rest your eyes.",
        EnforcementState.Warning => "Break coming up soon, finish what you are doing.",
        _ => null,
    };

    /// <summary>
    /// Executes a command from the overlay or the parent CLI. Every state-changing command
    /// requires the parent password, checked here rather than at the pipe boundary.
    /// </summary>
    private CommandResult HandleCommand(CommandRequest request)
    {
        if (request.Command == PipeCommand.GetStatus)
        {
            return new CommandResult { Ok = true };
        }

        lock (_schedulerLock)
        {
            if (!_scheduler.Config.VerifyPassword(request.Password))
            {
                _logger.LogWarning("Rejected {Command}: wrong or missing password", request.Command);
                return new CommandResult { Ok = false, Message = "Incorrect password." };
            }

            var now = DateTimeOffset.UtcNow;

            switch (request.Command)
            {
                case PipeCommand.Unlock:
                    _scheduler.EndBreakEarly();
                    return Applied(now, "Break ended.");

                case PipeCommand.Pause:
                    var minutes = Math.Clamp(request.Minutes, 1, (int)MaxPause.TotalMinutes);
                    _scheduler.Pause(now, TimeSpan.FromMinutes(minutes));
                    return Applied(now, $"Enforcement paused for {minutes} minutes.");

                case PipeCommand.Resume:
                    _scheduler.Resume();
                    return Applied(now, "Enforcement resumed.");

                case PipeCommand.StartBreak:
                    _scheduler.StartBreakEarly(now);
                    return Applied(now, "Break started.");

                case PipeCommand.ReloadConfig:
                    _scheduler.Config = _configStore.Load();
                    return Applied(now, "Configuration reloaded.");

                default:
                    return new CommandResult { Ok = false, Message = "Unknown command." };
            }
        }
    }

    /// <summary>
    /// Re-ticks so the caller sees the effect of its own command immediately, and persists the
    /// result rather than waiting for the next checkpoint.
    /// </summary>
    private CommandResult Applied(DateTimeOffset now, string message)
    {
        _status = _scheduler.Tick(now);
        _lastPersistedState = _status.State;
        _lastCheckpoint = now;
        Persist(_scheduler.State.Clone());
        _policy.Apply(_status.IsBlocking);

        _logger.LogInformation("Parent command applied: {Message}", message);
        return new CommandResult { Ok = true, Message = message };
    }

    private void Shutdown()
    {
        _logger.LogInformation("RestMind service stopping");

        _configWatcher?.Dispose();

        // Never leave Task Manager disabled because the service went away.
        _policy.Revert();

        lock (_schedulerLock)
        {
            Persist(_scheduler.State.Clone());
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
