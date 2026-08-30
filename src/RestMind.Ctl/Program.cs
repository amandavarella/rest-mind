using System.IO.Pipes;
using System.Text;
using RestMind.Core;

namespace RestMind.Ctl;

/// <summary>
/// The parent's command line. Password-protected commands go over the pipe to the service;
/// set-password and status-of-config work directly on the ACL'd files and so need an
/// administrator prompt.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "status" => await StatusAsync().ConfigureAwait(false),
                "unlock" => await SendAsync(PipeCommand.Unlock).ConfigureAwait(false),
                "break" => await SendAsync(PipeCommand.StartBreak).ConfigureAwait(false),
                "resume" => await SendAsync(PipeCommand.Resume).ConfigureAwait(false),
                "reload" => await SendAsync(PipeCommand.ReloadConfig).ConfigureAwait(false),
                "pause" => await PauseAsync(args).ConfigureAwait(false),
                "set-password" => SetPassword(),
                "show-config" => ShowConfig(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed: {ex.Message}");
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        Rest Mind parent controls.

          RestMind.Ctl status                 Show what the enforcer is doing right now
          RestMind.Ctl unlock                 End the current break immediately
          RestMind.Ctl break                  Start a break immediately
          RestMind.Ctl pause --minutes 30     Suspend enforcement for a while
          RestMind.Ctl resume                 Cancel a pause
          RestMind.Ctl reload                 Re-read config.json after editing it
          RestMind.Ctl set-password           Set the parent password (run as administrator)
          RestMind.Ctl show-config            Print the current configuration

        Every command except status and show-config asks for the parent password.
        """);

    private static async Task<int> StatusAsync()
    {
        var status = await PipeConversation.RequestStatusAsync().ConfigureAwait(false);
        if (status is null)
        {
            Console.Error.WriteLine("Could not reach the RestMind service. Is it running?");
            return 1;
        }

        Console.WriteLine($"State:     {status.State}");
        Console.WriteLine($"Remaining: {TimeSpan.FromSeconds(status.RemainingSeconds):hh\\:mm\\:ss}");
        Console.WriteLine($"Blocking:  {(status.IsBlocking ? "yes" : "no")}");
        return 0;
    }

    private static async Task<int> PauseAsync(string[] args)
    {
        var minutes = 30;
        var index = Array.FindIndex(args, a => a is "--minutes" or "-m");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var parsed))
        {
            minutes = parsed;
        }

        if (minutes < 1)
        {
            Console.Error.WriteLine("--minutes must be at least 1.");
            return 1;
        }

        return await SendAsync(PipeCommand.Pause, minutes).ConfigureAwait(false);
    }

    private static async Task<int> SendAsync(PipeCommand command, int minutes = 0)
    {
        var password = ReadPassword("Parent password: ");
        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("No password entered.");
            return 1;
        }

        var result = await PipeConversation.SendCommandAsync(command, password, minutes).ConfigureAwait(false);
        if (result is null)
        {
            Console.Error.WriteLine("Could not reach the RestMind service. Is it running?");
            return 1;
        }

        Console.WriteLine(result.Message ?? (result.Ok ? "Done." : "Refused."));
        return result.Ok ? 0 : 1;
    }

    private static int SetPassword()
    {
        var paths = new RestMindPaths();
        var store = new ConfigStore(paths);

        var first = ReadPassword("New parent password: ");
        if (string.IsNullOrWhiteSpace(first))
        {
            Console.Error.WriteLine("Password cannot be empty.");
            return 1;
        }

        if (ReadPassword("Confirm password: ") != first)
        {
            Console.Error.WriteLine("Passwords did not match.");
            return 1;
        }

        var config = store.Load();
        config.PasswordHash = PasswordHasher.Hash(first);

        try
        {
            store.Save(config);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Cannot write {paths.ConfigFile}. Run this command from an administrator prompt.");
            return 1;
        }

        Console.WriteLine("Password set. Run 'RestMind.Ctl reload' so the service picks it up.");
        return 0;
    }

    private static int ShowConfig()
    {
        var paths = new RestMindPaths();
        var config = new ConfigStore(paths).Load();

        Console.WriteLine($"Config file: {paths.ConfigFile}");
        Console.WriteLine($"Password set: {(string.IsNullOrEmpty(config.PasswordHash) ? "no" : "yes")}");
        Console.WriteLine($"Pre-break warning: {config.PreBreakWarningMinutes} min");
        Console.WriteLine();
        Console.WriteLine("Day        Active           Work  Break");

        foreach (var day in config.Schedule.Days.OrderBy(d => (int)d.Day))
        {
            var window = day.Enabled ? $"{day.ActiveStart:HH\\:mm}-{day.ActiveEnd:HH\\:mm}" : "off";
            Console.WriteLine($"{day.Day,-10} {window,-16} {day.WorkMinutes,4}  {day.BreakMinutes,5}");
        }

        return 0;
    }

    /// <summary>Reads a password without echoing it, so it does not end up on screen.</summary>
    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var builder = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }
}

/// <summary>A single short-lived request/response exchange with the service.</summary>
internal static class PipeConversation
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static async Task<StatusPayload?> RequestStatusAsync()
    {
        var reply = await ExchangeAsync(PipeMessage.Subscribe(), PipeMessageKind.Status).ConfigureAwait(false);
        return reply?.Status;
    }

    public static async Task<CommandResult?> SendCommandAsync(PipeCommand command, string password, int minutes)
    {
        var reply = await ExchangeAsync(
            PipeMessage.ForCommand(command, password, minutes),
            PipeMessageKind.Result).ConfigureAwait(false);

        return reply?.Result;
    }

    private static async Task<PipeMessage?> ExchangeAsync(PipeMessage request, PipeMessageKind expected)
    {
        try
        {
            await using var stream = new NamedPipeClientStream(
                ".",
                PipeMessage.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var timeout = new CancellationTokenSource(ConnectTimeout);
            await stream.ConnectAsync(timeout.Token).ConfigureAwait(false);

            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            await writer.WriteLineAsync(request.ToLine()).ConfigureAwait(false);

            using var readTimeout = new CancellationTokenSource(ConnectTimeout);
            while (await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false) is { } line)
            {
                var message = PipeMessage.TryParse(line);
                if (message?.Kind == expected)
                {
                    return message;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
                                       or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
