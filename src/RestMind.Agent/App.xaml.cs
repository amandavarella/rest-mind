using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using RestMind.Core;

// Windows Forms is referenced for the tray icon, which makes several of these names ambiguous.
using Application = System.Windows.Application;

namespace RestMind.Agent;

[SupportedOSPlatform("windows")]
public partial class App : Application
{
    // Session-scoped, so each signed-in user gets exactly one agent.
    private const string SingleInstanceMutex = @"Local\RestMind.Agent";

    private Mutex? _instanceLock;
    private PipeClient? _client;
    private OverlayManager? _overlays;
    private KeyboardBlocker? _keyboard;
    private TrayIndicator? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceLock = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var isOnlyInstance);
        if (!isOnlyInstance)
        {
            // The watchdog can race with the logon auto-start; second one just steps aside.
            Shutdown();
            return;
        }

        _keyboard = new KeyboardBlocker();
        _keyboard.Install();

        _client = new PipeClient();
        _overlays = new OverlayManager(_client, _keyboard);
        _tray = new TrayIndicator();

        _client.StatusReceived += OnStatusReceived;
        _client.ConnectionChanged += OnConnectionChanged;
        _client.Start();
    }

    /// <summary>Arrives on a background thread; all UI work has to hop to the dispatcher.</summary>
    private void OnStatusReceived(StatusPayload status) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            _overlays?.Apply(status);
            _tray?.Update(status);
        });

    private void OnConnectionChanged(bool connected) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            if (connected)
            {
                return;
            }

            // Without the service there is no authority to block, so clear the overlay rather
            // than stranding the machine behind a countdown nothing is driving.
            _overlays?.Apply(new StatusPayload
            {
                State = EnforcementState.Inactive,
                IsBlocking = false,
                RemainingSeconds = 0,
            });
            _tray?.ShowDisconnected();
        });

    protected override void OnExit(ExitEventArgs e)
    {
        _overlays?.Dispose();
        _keyboard?.Dispose();
        _tray?.Dispose();
        _client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _instanceLock?.Dispose();

        base.OnExit(e);
    }
}
