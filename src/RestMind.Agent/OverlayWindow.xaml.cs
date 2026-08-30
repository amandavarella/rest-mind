using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using RestMind.Core;

// Disambiguate from the Windows Forms types pulled in for the tray icon.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace RestMind.Agent;

/// <summary>
/// One full-screen blocker, one per monitor. It renders whatever the service reports and can
/// send an unlock attempt; it has no authority of its own.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class OverlayWindow : Window
{
    private readonly PipeClient _client;
    private readonly bool _isPrimary;

    public OverlayWindow(PipeClient client, bool isPrimary)
    {
        InitializeComponent();

        _client = client;
        _isPrimary = isPrimary;
        UnlockPanel.Visibility = isPrimary ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Places the window over one monitor, in physical pixels.</summary>
    public void CoverScreen(System.Drawing.Rectangle bounds)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTopmost,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpShowWindow);
    }

    public void Update(StatusPayload status)
    {
        CountdownText.Text = FormatCountdown(status.RemainingSeconds);

        if (!string.IsNullOrWhiteSpace(status.Message))
        {
            MessageText.Text = status.Message;
        }
    }

    /// <summary>
    /// Re-asserts topmost, so an app that steals the foreground is covered again within a tick
    /// rather than leaving a usable hole in the block.
    /// </summary>
    public void ReassertTopmost()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);

        if (_isPrimary && NativeMethods.GetForegroundWindow() != handle)
        {
            NativeMethods.SetForegroundWindow(handle);
        }
    }

    public void ResetUnlockPrompt()
    {
        PasswordInput.Clear();
        PasswordPanel.Visibility = Visibility.Collapsed;
        RevealButton.Visibility = Visibility.Visible;
        UnlockStatusText.Visibility = Visibility.Collapsed;
    }

    private static string FormatCountdown(int totalSeconds)
    {
        var remaining = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        RevealButton.Visibility = Visibility.Collapsed;
        PasswordPanel.Visibility = Visibility.Visible;
        PasswordInput.Focus();
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = TryUnlockAsync();
        }
    }

    private void OnUnlockClick(object sender, RoutedEventArgs e) => _ = TryUnlockAsync();

    private async Task TryUnlockAsync()
    {
        var password = PasswordInput.Password;
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        UnlockButton.IsEnabled = false;
        ShowUnlockStatus("Checking...");

        var result = await _client.SendAsync(PipeCommand.Unlock, password).ConfigureAwait(true);

        UnlockButton.IsEnabled = true;
        PasswordInput.Clear();

        if (!result.Ok)
        {
            ShowUnlockStatus(result.Message ?? "Incorrect password.");
            PasswordInput.Focus();
        }

        // On success the service broadcasts a new status and the overlay is hidden for us.
    }

    private void ShowUnlockStatus(string text)
    {
        UnlockStatusText.Text = text;
        UnlockStatusText.Visibility = Visibility.Visible;
    }
}
