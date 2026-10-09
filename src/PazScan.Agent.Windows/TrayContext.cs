using System.Diagnostics;
using PazScan.Core;
using PazScan.Core.Http;

namespace PazScan.Agent.Windows;

/// <summary>The agent's only UI: an icon by the clock, and its menu.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _startWithWindows;

    public TrayContext(AgentOptions options, string logDirectory, string? startError)
    {
        var status = startError ?? $"Running on port {options.Port}";

        _startWithWindows = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupRegistration.IsEnabled,
            CheckOnClick = true,
        };
        _startWithWindows.CheckedChanged += (_, _) =>
        {
            if (_startWithWindows.Checked) StartupRegistration.Enable();
            else StartupRegistration.Disable();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"Paz Scan Agent {AgentApi.Version}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem(status) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startWithWindows);
        menu.Items.Add("Open logs", null, (_, _) => Open(logDirectory));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = startError is null ? "Paz Scan Agent" : "Paz Scan Agent — not running",
            ContextMenuStrip = menu,
            Visible = true,
        };

        if (startError is not null)
            _icon.ShowBalloonTip(10_000, "Paz Scan Agent could not start", startError, ToolTipIcon.Error);
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("app.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    private static void Open(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
}
