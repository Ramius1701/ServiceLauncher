using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace ServiceLauncher;

// Structured editor for services.json - add/edit/remove services and
// volumes without hand-editing JSON. Deliberately doesn't hot-reload the
// running orchestrator/API/CrashMonitor (they'd all need to pick up a
// new LauncherConfig instance mid-run) - saving just writes the file and
// says to restart, which is simple and impossible to get subtly wrong.
public class ConfigEditorForm : Form
{
    private readonly string _configPath;
    private readonly string _tokenFile;
    private readonly string _logFile;
    private readonly NumericUpDown _portInput;
    private readonly DataGridView _volumesGrid;
    private readonly DataGridView _servicesGrid;
    private readonly BindingList<VolumeRow> _volumeRows;
    private readonly BindingList<ServiceRow> _serviceRows;

    private class VolumeRow
    {
        public string Id { get; set; } = "";
        public string DriveLetter { get; set; } = "";
        public string VhdxPath { get; set; } = "";
    }

    private class ServiceRow
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public string RequiresVolume { get; set; } = "";
        public int StartupTimeoutSeconds { get; set; } = 90;
        public bool UseShellExecute { get; set; } = false;

        public string LivenessType { get; set; } = "process";
        public string LivenessUrl { get; set; } = "";
        public string LivenessProcessPath { get; set; } = "";
        public string LivenessProcessArgsContains { get; set; } = "";
        public string LivenessHeartbeatFile { get; set; } = "";
        public int LivenessMaxHeartbeatAgeSeconds { get; set; } = 30;

        public bool AutoRestartEnabled { get; set; } = false;
        public int AutoRestartAttempts { get; set; } = 3;
        public int AutoRestartCrashConfirmSeconds { get; set; } = 10;
        public int AutoRestartAttemptDelaySeconds { get; set; } = 15;

        // Compact "serviceId:delaySeconds,serviceId2:delaySeconds2" form -
        // keeps AutoLaunch chains editable in one flat grid cell instead
        // of a second nested grid.
        public string AutoLaunch { get; set; } = "";
    }

    public ConfigEditorForm(string configPath, LauncherConfig config)
    {
        _configPath = configPath;
        _tokenFile = config.TokenFile;
        _logFile = config.LogFile;

        Text = "ServiceLauncher - Config Editor (F12)";
        Width = 1000;
        Height = 600;
        StartPosition = FormStartPosition.CenterParent;

        Label portLabel = new Label { Text = "Listen port:", AutoSize = true, Location = new Point(10, 15) };
        _portInput = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = config.ListenPort, Location = new Point(90, 12), Width = 80 };

        Label volumesLabel = new Label { Text = "Volumes", AutoSize = true, Location = new Point(10, 45), Font = new Font(Font, FontStyle.Bold) };
        _volumeRows = new BindingList<VolumeRow>(config.Volumes
            .Select(v => new VolumeRow { Id = v.Id, DriveLetter = v.DriveLetter, VhdxPath = v.VhdxPath })
            .ToList());
        _volumesGrid = new DataGridView
        {
            DataSource = _volumeRows,
            Location = new Point(10, 65),
            Width = 960,
            Height = 120,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        Label servicesLabel = new Label { Text = "Services", AutoSize = true, Location = new Point(10, 195), Font = new Font(Font, FontStyle.Bold) };
        _serviceRows = new BindingList<ServiceRow>(config.Services.Select(ToRow).ToList());
        _servicesGrid = new DataGridView
        {
            DataSource = _serviceRows,
            Location = new Point(10, 215),
            Width = 960,
            Height = 300,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            ScrollBars = ScrollBars.Both,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };

        Label hint = new Label
        {
            Text = "Liveness type: http (needs Url) / process (needs ProcessPath, optional ProcessArgsContains) / heartbeatfile (needs HeartbeatFile). " +
                   "AutoLaunch format: serviceId:delaySeconds, comma-separated. Restart ServiceLauncher after saving for changes to take effect.",
            AutoSize = false,
            Location = new Point(10, 520),
            Width = 960,
            Height = 30,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = Color.DimGray,
        };

        Button saveButton = new Button { Text = "Save", Location = new Point(810, 555), Width = 75, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        Button cancelButton = new Button { Text = "Cancel", Location = new Point(895, 555), Width = 75, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        saveButton.Click += (_, _) => Save();
        cancelButton.Click += (_, _) => Close();

        Controls.Add(portLabel);
        Controls.Add(_portInput);
        Controls.Add(volumesLabel);
        Controls.Add(_volumesGrid);
        Controls.Add(servicesLabel);
        Controls.Add(_servicesGrid);
        Controls.Add(hint);
        Controls.Add(saveButton);
        Controls.Add(cancelButton);
    }

    private static ServiceRow ToRow(ServiceConfig svc) => new ServiceRow
    {
        Id = svc.Id,
        DisplayName = svc.DisplayName,
        ExePath = svc.ExePath,
        Arguments = svc.Arguments,
        WorkingDirectory = svc.WorkingDirectory,
        RequiresVolume = svc.RequiresVolume ?? "",
        StartupTimeoutSeconds = svc.StartupTimeoutSeconds,
        UseShellExecute = svc.UseShellExecute,
        LivenessType = svc.Liveness.Type,
        LivenessUrl = svc.Liveness.Url ?? "",
        LivenessProcessPath = svc.Liveness.ProcessPath ?? "",
        LivenessProcessArgsContains = svc.Liveness.ProcessArgsContains ?? "",
        LivenessHeartbeatFile = svc.Liveness.HeartbeatFile ?? "",
        LivenessMaxHeartbeatAgeSeconds = svc.Liveness.MaxHeartbeatAgeSeconds,
        AutoRestartEnabled = svc.AutoRestart.Enabled,
        AutoRestartAttempts = svc.AutoRestart.RestartAttempts,
        AutoRestartCrashConfirmSeconds = svc.AutoRestart.CrashConfirmSeconds,
        AutoRestartAttemptDelaySeconds = svc.AutoRestart.AttemptDelaySeconds,
        AutoLaunch = string.Join(",", svc.AutoLaunch.Select(a => $"{a.ServiceId}:{a.DelaySeconds}")),
    };

    private static ServiceConfig FromRow(ServiceRow row) => new ServiceConfig
    {
        Id = row.Id.Trim(),
        DisplayName = row.DisplayName.Trim(),
        ExePath = row.ExePath.Trim(),
        Arguments = row.Arguments,
        WorkingDirectory = row.WorkingDirectory.Trim(),
        RequiresVolume = string.IsNullOrWhiteSpace(row.RequiresVolume) ? null : row.RequiresVolume.Trim(),
        StartupTimeoutSeconds = row.StartupTimeoutSeconds,
        UseShellExecute = row.UseShellExecute,
        Liveness = new LivenessConfig
        {
            Type = string.IsNullOrWhiteSpace(row.LivenessType) ? "process" : row.LivenessType.Trim(),
            Url = string.IsNullOrWhiteSpace(row.LivenessUrl) ? null : row.LivenessUrl.Trim(),
            ProcessPath = string.IsNullOrWhiteSpace(row.LivenessProcessPath) ? null : row.LivenessProcessPath.Trim(),
            ProcessArgsContains = string.IsNullOrWhiteSpace(row.LivenessProcessArgsContains) ? null : row.LivenessProcessArgsContains.Trim(),
            HeartbeatFile = string.IsNullOrWhiteSpace(row.LivenessHeartbeatFile) ? null : row.LivenessHeartbeatFile.Trim(),
            MaxHeartbeatAgeSeconds = row.LivenessMaxHeartbeatAgeSeconds,
        },
        AutoRestart = new AutoRestartConfig
        {
            Enabled = row.AutoRestartEnabled,
            RestartAttempts = row.AutoRestartAttempts,
            CrashConfirmSeconds = row.AutoRestartCrashConfirmSeconds,
            AttemptDelaySeconds = row.AutoRestartAttemptDelaySeconds,
        },
        AutoLaunch = ParseAutoLaunch(row.AutoLaunch),
    };

    private static List<AutoLaunchEntry> ParseAutoLaunch(string text)
    {
        List<AutoLaunchEntry> entries = new();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pieces = part.Split(':', 2);
            string id = pieces[0].Trim();
            if (id.Length == 0)
                continue;
            int delay = pieces.Length > 1 && int.TryParse(pieces[1], out int d) ? d : 0;
            entries.Add(new AutoLaunchEntry { ServiceId = id, DelaySeconds = delay });
        }
        return entries;
    }

    private void Save()
    {
        // Force any in-progress cell edit to commit before reading rows.
        _volumesGrid.EndEdit();
        _servicesGrid.EndEdit();

        List<VolumeRow> volumeRows = _volumeRows.Where(v => !string.IsNullOrWhiteSpace(v.Id)).ToList();
        List<ServiceRow> serviceRows = _serviceRows.Where(s => !string.IsNullOrWhiteSpace(s.Id)).ToList();

        if (serviceRows.Select(s => s.Id.Trim()).Distinct().Count() != serviceRows.Count)
        {
            MessageBox.Show("Two or more services have the same Id - Ids must be unique.", "ServiceLauncher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        LauncherConfig config = new LauncherConfig
        {
            ListenPort = (int)_portInput.Value,
            TokenFile = _tokenFile,
            LogFile = _logFile,
            Volumes = volumeRows.Select(v => new VolumeConfig { Id = v.Id.Trim(), DriveLetter = v.DriveLetter.Trim(), VhdxPath = v.VhdxPath.Trim() }).ToList(),
            Services = serviceRows.Select(FromRow).ToList(),
        };

        try
        {
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception e)
        {
            MessageBox.Show($"Could not save {_configPath}:\n{e.Message}", "ServiceLauncher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        MessageBox.Show("Saved. Restart ServiceLauncher for these changes to take effect.", "ServiceLauncher", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    }
}
