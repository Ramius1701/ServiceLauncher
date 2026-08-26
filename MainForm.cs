using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;

namespace ServiceLauncher;

// Always talks to the local HTTP API over loopback (/api/status,
// /api/launch) rather than ServiceOrchestrator directly - that way this
// form works identically whether it's running inside the primary
// instance (which owns the listener) or as a second, manually-launched
// instance that lost the single-instance mutex to an already-running
// headless copy (see Program.cs). One code path either way.
public class MainForm : Form
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, Label> _statusLabels = new();
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly TextBox _log;
    private readonly Button _launchAllButton;
    private readonly string _tokenQuery;
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _iconGreen;
    private readonly Icon _iconYellow;
    private readonly Icon _iconRed;
    private readonly LauncherConfig _config;
    private readonly string _configPath;
    private bool _busy;
    private bool _reallyExit;

    public MainForm(LauncherConfig config, string token, string configPath)
    {
        _config = config;
        _configPath = configPath;
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{config.ListenPort}/") };

        Text = "ServiceLauncher";
        Width = 520;
        Height = 480;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F12)
                new ConfigEditorForm(_configPath, _config).ShowDialog(this);
        };

        TableLayoutPanel grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            AutoSize = true,
            Padding = new Padding(10),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));

        foreach (ServiceConfig svc in config.Services)
        {
            Label nameLabel = new Label { Text = svc.DisplayName, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };
            Label statusLabel = new Label { Text = "?", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };
            Button launchButton = new Button { Text = "Launch", Width = 80 };
            launchButton.Click += async (_, _) => await LaunchAsync(svc.Id, svc.DisplayName, launchButton);

            grid.Controls.Add(nameLabel);
            grid.Controls.Add(statusLabel);
            grid.Controls.Add(launchButton);
            _statusLabels[svc.Id] = statusLabel;
        }

        _launchAllButton = new Button { Text = "Launch All", Dock = DockStyle.Top, Height = 32, Margin = new Padding(10) };
        _launchAllButton.Click += async (_, _) => await LaunchAsync("all", "All services", _launchAllButton);

        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9),
        };

        Controls.Add(_log);
        Controls.Add(_launchAllButton);
        Controls.Add(grid);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _refreshTimer.Start();

        Load += async (_, _) => await RefreshStatusAsync();

        // Token travels as a query param on every request rather than a
        // header, purely to match the existing /launch and /status
        // convention this project already uses for its bookmarkable URLs.
        _tokenQuery = $"token={Uri.EscapeDataString(token)}";

        _iconGreen = MakeStatusIcon(Color.ForestGreen);
        _iconYellow = MakeStatusIcon(Color.Goldenrod);
        _iconRed = MakeStatusIcon(Color.Firebrick);

        ContextMenuStrip trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Show", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Exit", null, (_, _) => { _reallyExit = true; Close(); });

        _trayIcon = new NotifyIcon
        {
            Icon = _iconYellow,
            Text = "ServiceLauncher",
            Visible = true,
            ContextMenuStrip = trayMenu,
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        Icon = _iconYellow;

        // Closing the window (the X button) minimizes to tray instead of
        // exiting - matches MBBSLauncher's tray behavior. Only the tray
        // menu's "Exit" (or a real process kill) actually ends the
        // process, so the background HTTP listener/CrashMonitor in the
        // primary instance keep running unattended, which is the whole
        // point of this app.
        FormClosing += (_, e) =>
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            _refreshTimer.Dispose();
            _trayIcon.Dispose();
        };

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
                Hide();
        };
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
    }

    private static Icon MakeStatusIcon(Color color)
    {
        using Bitmap bmp = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using Brush brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 13, 13);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private async Task RefreshStatusAsync()
    {
        if (_busy)
            return;

        try
        {
            string json = await _http.GetStringAsync($"api/status?{_tokenQuery}");
            List<ServiceStatusDto>? statuses = JsonSerializer.Deserialize<List<ServiceStatusDto>>(json);
            if (statuses == null)
                return;

            foreach (ServiceStatusDto s in statuses)
            {
                if (!_statusLabels.TryGetValue(s.Id, out Label? label))
                    continue;
                label.Text = s.Alive ? "Running" : "Stopped";
                label.ForeColor = s.Alive ? Color.DarkGreen : Color.DarkRed;
            }

            UpdateTrayIcon(statuses);
        }
        catch (Exception e)
        {
            AppendLog($"Status check failed: {e.Message}");
        }
    }

    // Green = everything configured is up, red = everything's down,
    // yellow = a mix - lets the tray icon alone answer "is the grid up?"
    // without opening the window.
    private void UpdateTrayIcon(List<ServiceStatusDto> statuses)
    {
        int aliveCount = statuses.Count(s => s.Alive);
        _trayIcon.Icon = aliveCount == statuses.Count ? _iconGreen
                        : aliveCount == 0 ? _iconRed
                        : _iconYellow;
        _trayIcon.Text = $"ServiceLauncher - {aliveCount}/{statuses.Count} running";
    }

    private async Task LaunchAsync(string serviceId, string displayName, Button sourceButton)
    {
        _busy = true;
        sourceButton.Enabled = false;
        AppendLog($"--- Launching {displayName} ---");

        try
        {
            // Launch can legitimately take minutes (each dependent service
            // waits up to its own StartupTimeoutSeconds server-side) - this
            // is an async HTTP call so the UI stays responsive while it
            // waits, not a blocking orchestrator call on this thread.
            string json = await _http.GetStringAsync($"api/launch?service={Uri.EscapeDataString(serviceId)}&{_tokenQuery}");
            using JsonDocument doc = JsonDocument.Parse(json);
            AppendLog(doc.RootElement.GetProperty("log").GetString() ?? "(no output)");
        }
        catch (Exception e)
        {
            AppendLog($"Launch failed: {e.Message}");
        }
        finally
        {
            sourceButton.Enabled = true;
            _busy = false;
        }

        await RefreshStatusAsync();
    }

    private void AppendLog(string text)
    {
        _log.AppendText(text.TrimEnd('\n') + Environment.NewLine);
    }
}
