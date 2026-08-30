using RestMind.Core;
using Xunit;

namespace RestMind.Core.Tests;

public class SerializationTests
{
    [Fact]
    public void Config_RoundTripsThroughJson()
    {
        var config = new AppConfig
        {
            PreBreakWarningMinutes = 3,
            PasswordHash = "1000.c2FsdA==.aGFzaA==",
            Schedule = Schedule.CreateDefault(45, 15, new TimeOnly(8, 30), new TimeOnly(19, 45)),
        };
        config.Schedule.ForDay(DayOfWeek.Sunday)!.Enabled = false;

        var restored = AppConfig.FromJson(config.ToJson());

        Assert.Equal(3, restored.PreBreakWarningMinutes);
        Assert.Equal(config.PasswordHash, restored.PasswordHash);

        var monday = restored.Schedule.ForDay(DayOfWeek.Monday)!;
        Assert.Equal(45, monday.WorkMinutes);
        Assert.Equal(15, monday.BreakMinutes);
        Assert.Equal(new TimeOnly(8, 30), monday.ActiveStart);
        Assert.Equal(new TimeOnly(19, 45), monday.ActiveEnd);
        Assert.False(restored.Schedule.ForDay(DayOfWeek.Sunday)!.Enabled);
    }

    [Fact]
    public void Config_WritesDayNamesNotNumbers_SoTheFileStaysEditable()
    {
        var json = new AppConfig().ToJson();

        Assert.Contains("\"Monday\"", json);
        Assert.Contains("07:00", json);
    }

    [Fact]
    public void PipeStatusMessage_RoundTrips()
    {
        var status = new SchedulerStatus(EnforcementState.OnBreak, TimeSpan.FromMinutes(7));
        var message = PipeMessage.ForStatus(status, "Time to rest your eyes.");

        var restored = PipeMessage.TryParse(message.ToLine());

        Assert.NotNull(restored);
        Assert.Equal(PipeMessageKind.Status, restored!.Kind);
        Assert.Equal(EnforcementState.OnBreak, restored.Status!.State);
        Assert.Equal(420, restored.Status.RemainingSeconds);
        Assert.True(restored.Status.IsBlocking);
        Assert.Equal("Time to rest your eyes.", restored.Status.Message);
    }

    [Fact]
    public void PipeCommandMessage_RoundTrips()
    {
        var message = PipeMessage.ForCommand(PipeCommand.Pause, "hunter2", 30);

        var restored = PipeMessage.TryParse(message.ToLine());

        Assert.NotNull(restored);
        Assert.Equal(PipeMessageKind.Command, restored!.Kind);
        Assert.Equal(PipeCommand.Pause, restored.Command!.Command);
        Assert.Equal("hunter2", restored.Command.Password);
        Assert.Equal(30, restored.Command.Minutes);
    }

    [Fact]
    public void PipeMessage_SerializesToASingleLine()
    {
        var line = PipeMessage.ForCommand(PipeCommand.Unlock, "pw").ToLine();

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{unclosed")]
    public void MalformedPipeMessage_ParsesToNullInsteadOfThrowing(string line)
    {
        Assert.Null(PipeMessage.TryParse(line));
    }
}
