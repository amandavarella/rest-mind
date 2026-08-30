using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using RestMind.Core;

namespace RestMind.Service;

/// <summary>
/// The service's end of the <c>restmind</c> named pipe. Any signed-in user may connect, which
/// they must, since the overlay runs as the child. Authority comes from the parent password
/// checked inside the command handler, never from who is allowed to open the pipe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeServer : IAsyncDisposable
{
    private const int MaxInstances = 8;

    private readonly Func<CommandRequest, CommandResult> _commandHandler;
    private readonly Func<PipeMessage> _statusProvider;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;

    public PipeServer(
        Func<CommandRequest, CommandResult> commandHandler,
        Func<PipeMessage> statusProvider,
        ILogger logger)
    {
        _commandHandler = commandHandler;
        _statusProvider = statusProvider;
        _logger = logger;
    }

    public void Start() => _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));

    /// <summary>Pushes a message to every connected client, dropping any that have gone away.</summary>
    public async Task BroadcastAsync(PipeMessage message)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        var line = message.ToLine();
        foreach (var (id, client) in _clients)
        {
            if (!await client.TryWriteAsync(line).ConfigureAwait(false))
            {
                Remove(id);
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? stream = null;
            try
            {
                stream = CreatePipe();
                await stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var accepted = stream;
                stream = null; // ownership moves to the client handler
                _ = Task.Run(() => HandleClientAsync(accepted, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                stream?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                stream?.Dispose();
                _logger.LogWarning(ex, "Pipe accept failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeMessage.PipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var client = new Client(stream);
        _clients[id] = client;

        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break; // client disconnected
                }

                var message = PipeMessage.TryParse(line);
                if (message is null)
                {
                    continue;
                }

                await DispatchAsync(message, client).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Normal disconnect, including the agent being killed.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pipe client failed");
        }
        finally
        {
            Remove(id);
        }
    }

    private async Task DispatchAsync(PipeMessage message, Client client)
    {
        switch (message.Kind)
        {
            case PipeMessageKind.Subscribe:
                await client.TryWriteAsync(_statusProvider().ToLine()).ConfigureAwait(false);
                break;

            case PipeMessageKind.Command:
                var request = message.Command ?? new CommandRequest();
                var result = _commandHandler(request);
                await client.TryWriteAsync(PipeMessage.ForResult(result.Ok, result.Message).ToLine())
                    .ConfigureAwait(false);
                // Everything the command changed should be visible everywhere immediately.
                await BroadcastAsync(_statusProvider()).ConfigureAwait(false);
                break;
        }
    }

    private void Remove(Guid id)
    {
        if (_clients.TryRemove(id, out var client))
        {
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        foreach (var id in _clients.Keys)
        {
            Remove(id);
        }

        _shutdown.Dispose();
    }

    /// <summary>One connected client, with a lock so concurrent broadcasts cannot interleave.</summary>
    private sealed class Client : IDisposable
    {
        private readonly NamedPipeServerStream _stream;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public Client(NamedPipeServerStream stream)
        {
            _stream = stream;
            _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        }

        public async Task<bool> TryWriteAsync(string line)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_stream.IsConnected)
                {
                    return false;
                }

                await _writer.WriteLineAsync(line).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            try
            {
                _writer.Dispose();
                _stream.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Nothing useful to do while tearing down a dead connection.
            }

            _writeLock.Dispose();
        }
    }
}
