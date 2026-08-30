using System.Text.Json;
using System.Text.Json.Serialization;

namespace RestMind.Core;

public enum PipeMessageKind
{
    /// <summary>Client asks to receive status pushes for the life of the connection.</summary>
    Subscribe,

    /// <summary>Client asks the service to do something.</summary>
    Command,

    /// <summary>Service pushes the current status.</summary>
    Status,

    /// <summary>Service answers a command.</summary>
    Result,
}

public enum PipeCommand
{
    None,
    /// <summary>End the current break early. Requires the parent password.</summary>
    Unlock,
    /// <summary>Suspend enforcement for a number of minutes. Requires the parent password.</summary>
    Pause,
    /// <summary>Cancel an active pause. Requires the parent password.</summary>
    Resume,
    /// <summary>Start a break immediately. Requires the parent password.</summary>
    StartBreak,
    /// <summary>Re-read config.json from disk. Requires the parent password.</summary>
    ReloadConfig,
    /// <summary>Ask for the current status. No password needed.</summary>
    GetStatus,
}

public sealed class CommandRequest
{
    public PipeCommand Command { get; set; }
    public string? Password { get; set; }
    public int Minutes { get; set; }
}

public sealed class StatusPayload
{
    public EnforcementState State { get; set; }
    public int RemainingSeconds { get; set; }
    public bool IsBlocking { get; set; }

    /// <summary>Shown on the overlay so the machine explains itself without a parent present.</summary>
    public string? Message { get; set; }
}

public sealed class CommandResult
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// One newline-delimited JSON message on the <c>restmind</c> pipe. A single flat envelope
/// keeps the protocol easy to read in logs and avoids polymorphic serialization.
/// </summary>
public sealed class PipeMessage
{
    public const string PipeName = "restmind";

    public PipeMessageKind Kind { get; set; }
    public CommandRequest? Command { get; set; }
    public StatusPayload? Status { get; set; }
    public CommandResult? Result { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PipeMessage Subscribe() => new() { Kind = PipeMessageKind.Subscribe };

    public static PipeMessage ForCommand(PipeCommand command, string? password = null, int minutes = 0) =>
        new()
        {
            Kind = PipeMessageKind.Command,
            Command = new CommandRequest { Command = command, Password = password, Minutes = minutes },
        };

    public static PipeMessage ForStatus(SchedulerStatus status, string? message = null) =>
        new()
        {
            Kind = PipeMessageKind.Status,
            Status = new StatusPayload
            {
                State = status.State,
                RemainingSeconds = status.RemainingSeconds,
                IsBlocking = status.IsBlocking,
                Message = message,
            },
        };

    public static PipeMessage ForResult(bool ok, string? message = null) =>
        new() { Kind = PipeMessageKind.Result, Result = new CommandResult { Ok = ok, Message = message } };

    /// <summary>Serializes to a single line, newline terminated.</summary>
    public string ToLine() => JsonSerializer.Serialize(this, JsonOptions);

    public static PipeMessage? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PipeMessage>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
