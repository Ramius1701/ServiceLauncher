using System.Diagnostics;

namespace ServiceLauncher;

public static class VolumeManager
{
    // Mount-DiskImage (built into Windows' Storage module) rather than
    // Hyper-V's Get-VHD/Mount-VHD - confirmed live that Get-VHD isn't
    // available on a machine without the Hyper-V role installed, while
    // Mount-DiskImage works regardless (it's what Explorer's own
    // "Mount"/Disk Management "Attach VHD" uses under the hood). A fixed
    // VHDX that's been assigned a drive letter before keeps that letter
    // automatically on remount - Windows remembers it against the disk's
    // signature - so no separate Set-Partition/drive-letter step is
    // needed here.
    public static bool EnsureMounted(VolumeConfig volume, Logger log)
    {
        string root = volume.DriveLetter.TrimEnd(':', '\\') + ":\\";
        if (Directory.Exists(root))
        {
            log.Info($"Volume {volume.Id} ({root}) already mounted.");
            return true;
        }

        log.Info($"Mounting volume {volume.Id}: {volume.VhdxPath} -> {root}");
        RunPowerShell($"Mount-DiskImage -ImagePath '{volume.VhdxPath.Replace("'", "''")}'", log);

        for (int i = 0; i < 30 && !Directory.Exists(root); i++)
            Thread.Sleep(1000);

        bool ok = Directory.Exists(root);
        log.Info(ok ? $"Volume {volume.Id} mounted." : $"Volume {volume.Id} FAILED to mount - check the path in services.json and that the VHDX isn't already attached elsewhere.");
        return ok;
    }

    private static void RunPowerShell(string command, Logger log)
    {
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"" + command.Replace("\"", "\\\"") + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process? p = Process.Start(psi);
        if (p == null)
        {
            log.Info("Failed to start powershell.exe");
            return;
        }

        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);

        if (!string.IsNullOrWhiteSpace(stdout))
            log.Info("[ps] " + stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr))
            log.Info("[ps-err] " + stderr.Trim());
    }
}
