using RestMind.Core;
using Xunit;

namespace RestMind.Core.Tests;

public class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "restmind-tests-" + Guid.NewGuid().ToString("N"));

    private RestMindPaths Paths => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Config_SavesAndLoads()
    {
        var store = new ConfigStore(Paths);
        var config = new AppConfig { PreBreakWarningMinutes = 5, PasswordHash = "x" };

        store.Save(config);
        var loaded = store.Load();

        Assert.Equal(5, loaded.PreBreakWarningMinutes);
        Assert.Equal("x", loaded.PasswordHash);
    }

    [Fact]
    public void MissingConfig_LoadsDefaults()
    {
        var loaded = new ConfigStore(Paths).Load();

        Assert.Equal(50, loaded.Schedule.ForDay(DayOfWeek.Monday)!.WorkMinutes);
    }

    [Fact]
    public void CorruptConfig_FallsBackToDefaultsRatherThanFailingOpen()
    {
        var paths = Paths;
        paths.EnsureCreated();
        File.WriteAllText(paths.ConfigFile, "{ this is not json");

        var loaded = new ConfigStore(paths).Load();

        Assert.Equal(50, loaded.Schedule.ForDay(DayOfWeek.Monday)!.WorkMinutes);
    }

    [Fact]
    public void State_SavesAndLoads_KeepingTheBreakEndTime()
    {
        var store = new StateStore(Paths);
        var breakEnds = new DateTimeOffset(2026, 1, 5, 9, 10, 0, TimeSpan.Zero);

        store.Save(new SchedulerState
        {
            Phase = CyclePhase.OnBreak,
            BreakEndsAtUtc = breakEnds,
            WorkAccruedSeconds = 123,
        });

        var loaded = store.Load();

        Assert.Equal(CyclePhase.OnBreak, loaded.Phase);
        Assert.Equal(breakEnds, loaded.BreakEndsAtUtc);
        Assert.Equal(123, loaded.WorkAccruedSeconds);
    }

    [Fact]
    public void LoadedState_ClearsTheLastTick_SoDowntimeIsNotCreditedAsWork()
    {
        var store = new StateStore(Paths);
        store.Save(new SchedulerState { LastTickUtc = DateTimeOffset.UtcNow.AddHours(-5) });

        Assert.Null(store.Load().LastTickUtc);
    }

    [Fact]
    public void SavingTwice_ReplacesTheFileCleanly()
    {
        var store = new StateStore(Paths);

        store.Save(new SchedulerState { WorkAccruedSeconds = 1 });
        store.Save(new SchedulerState { WorkAccruedSeconds = 2 });

        Assert.Equal(2, store.Load().WorkAccruedSeconds);
        Assert.False(File.Exists(Paths.StateFile + ".tmp"));
    }
}
