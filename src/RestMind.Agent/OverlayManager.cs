using System.Runtime.Versioning;
using System.Windows.Forms;
using System.Windows.Threading;
using RestMind.Core;

namespace RestMind.Agent;

/// <summary>
/// Creates and tears down one overlay per monitor, and keeps them on top for as long as the
/// break lasts.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OverlayManager : IDisposable
{
    private static readonly TimeSpan ReassertInterval = TimeSpan.FromMilliseconds(500);

    private readonly PipeClient _client;
    private readonly KeyboardBlocker _keyboard;
    private readonly DispatcherTimer _reassertTimer;
    private readonly List<OverlayWindow> _windows = new();

    private StatusPayload? _latest;

    public OverlayManager(PipeClient client, KeyboardBlocker keyboard)
    {
        _client = client;
        _keyboard = keyboard;

        _reassertTimer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = ReassertInterval,
        };
        _reassertTimer.Tick += (_, _) => ReassertAll();
    }

    public bool IsShowing => _windows.Count > 0;

    public void Apply(StatusPayload status)
    {
        _latest = status;

        if (status.IsBlocking)
        {
            Show();
            foreach (var window in _windows)
            {
                window.Update(status);
            }
        }
        else
        {
            Hide();
        }
    }

    private void Show()
    {
        if (_windows.Count > 0)
        {
            // Monitors can be plugged in mid-break; rebuild if the count no longer matches.
            if (_windows.Count == Screen.AllScreens.Length)
            {
                return;
            }

            Hide();
        }

        var primary = Screen.PrimaryScreen;
        foreach (var screen in Screen.AllScreens)
        {
            var isPrimary = primary is not null && screen.DeviceName == primary.DeviceName;
            var window = new OverlayWindow(_client, isPrimary);

            window.Show();
            window.CoverScreen(screen.Bounds);
            if (_latest is not null)
            {
                window.Update(_latest);
            }

            _windows.Add(window);
        }

        _keyboard.Blocking = true;
        _reassertTimer.Start();
        ReassertAll();
    }

    private void Hide()
    {
        if (_windows.Count == 0)
        {
            return;
        }

        _reassertTimer.Stop();
        _keyboard.Blocking = false;

        foreach (var window in _windows)
        {
            window.ResetUnlockPrompt();
            window.Close();
        }

        _windows.Clear();
    }

    private void ReassertAll()
    {
        foreach (var window in _windows)
        {
            window.ReassertTopmost();
        }
    }

    public void Dispose()
    {
        Hide();
        _reassertTimer.Stop();
    }
}
