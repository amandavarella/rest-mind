using System.IO;
using System.IO.Pipes;
using System.Text;
using RestMind.Core;

namespace RestMind.Agent;

/// <summary>
/// Keeps a connection to the service, reconnecting for as long as the agent lives. The agent
/// never decides anything itself: if the connection drops it simply shows nothing until the
/// service tells it otherwise, because the service is the one enforcing the break.
/// </summary>
public sealed class PipeClient : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _pendingLock = new();

    private StreamWriter? _writer;
    private TaskCompletionSource<CommandResult>? _pending;
    private Task? _loop;

    /// <summary>Raised on a background thread whenever the service pushes a new status.</summary>
    public event Action<StatusPayload>? StatusReceived;

    /// <summary>Raised when the connection to the service comes up or goes down.</summary>
    public event Action<bool>? ConnectionChanged;

    public void Start() => _loop = Task.Run(() => RunAsync(_shutdown.Token));

    /// <summary>
    /// Sends a parent command and waits for the service's answer. Returns a failure result
    /// rather than throwing, so the overlay can always show something useful.
    /// </summary>
    public async Task<CommandResult> SendAsync(PipeCommand command, string? password, int minutes = 0)
    {
        var completion = new TaskCompletionSource<CommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingLock)
        {
            if (_pending is not null)
            {
                return new CommandResult { Ok = false, Message = "Another request is already in flight." };
            }

            _pending = completion;
        }

        try
        {
            var writer = _writer;
            if (writer is null)
            {
                return new CommandResult { Ok = false, Message = "Not connected to the RestMind service." };
            }

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(PipeMessage.ForCommand(command, password, minutes).ToLine())
                    .ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            var finished = await Task.WhenAny(completion.Task, Task.Delay(CommandTimeout)).ConfigureAwait(false);
            return finished == completion.Task
                ? await completion.Task.ConfigureAwait(false)
                : new CommandResult { Ok = false, Message = "The service did not respond." };
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return new CommandResult { Ok = false, Message = "Lost the connection to the RestMind service." };
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending = null;
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndPumpAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                // The service may be restarting; fall through to the reconnect delay.
            }

            _writer = null;
            ConnectionChanged?.Invoke(false);

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectAndPumpAsync(CancellationToken cancellationToken)
    {
        await using var stream = new NamedPipeClientStream(
            ".",
            PipeMessage.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await stream.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        _writer = writer;
        ConnectionChanged?.Invoke(true);

        await writer.WriteLineAsync(PipeMessage.Subscribe().ToLine()).ConfigureAwait(false);

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return; // service closed the connection
            }

            Dispatch(PipeMessage.TryParse(line));
        }
    }

    private void Dispatch(PipeMessage? message)
    {
        if (message is null)
        {
            return;
        }

        switch (message.Kind)
        {
            case PipeMessageKind.Status when message.Status is not null:
                StatusReceived?.Invoke(message.Status);
                break;

            case PipeMessageKind.Result when message.Result is not null:
                lock (_pendingLock)
                {
                    _pending?.TrySetResult(message.Result);
                }

                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected while shutting down.
            }
        }

        _shutdown.Dispose();
        _writeLock.Dispose();
    }
}
