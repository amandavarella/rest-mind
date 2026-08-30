using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using RestMind.Core;

namespace RestMind.Agent;

/// <summary>
/// The always-there status indicator: how long until the next break, plus a balloon warning a
/// couple of minutes before it starts so work can be saved.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIndicator : IDisposable
{
    private readonly NotifyIcon _icon;
    private bool _warningShown;

    public TrayIndicator()
    {
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Visible = true,
            Text = "Rest Mind",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Rest Mind") { Enabled = false });
        _icon.ContextMenuStrip = menu;
    }

    public void Update(StatusPayload status)
    {
        _icon.Text = Truncate(DescribeFor(status));

        if (status.State == EnforcementState.Warning)
        {
            if (!_warningShown)
            {
                _warningShown = true;
                _icon.ShowBalloonTip(
                    10_000,
                    "Break coming up",
                    $"Screen break in {FormatDuration(status.RemainingSeconds)}. Save what you are doing.",
                    ToolTipIcon.Info);
            }
        }
        else if (status.State != EnforcementState.OnBreak)
        {
            // Re-arm only once the cycle has moved past the break.
            _warningShown = false;
        }
    }

    public void ShowDisconnected() => _icon.Text = "Rest Mind: waiting for the service";

    private static string DescribeFor(StatusPayload status) => status.State switch
    {
        EnforcementState.Working or EnforcementState.Warning =>
            $"Rest Mind: break in {FormatDuration(status.RemainingSeconds)}",
        EnforcementState.OnBreak =>
            $"Rest Mind: break ends in {FormatDuration(status.RemainingSeconds)}",
        EnforcementState.PausedByParent =>
            $"Rest Mind: paused for {FormatDuration(status.RemainingSeconds)}",
        _ => "Rest Mind: off duty",
    };

    private static string FormatDuration(int seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m" : $"{span.Seconds}s";
    }

    /// <summary>NotifyIcon.Text throws above 63 characters.</summary>
    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
