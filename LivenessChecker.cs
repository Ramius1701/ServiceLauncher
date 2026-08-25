using System.Diagnostics;
using System.Net.Sockets;

namespace ServiceLauncher;

public static class LivenessChecker
{
    public static bool IsAlive(LivenessConfig cfg)
    {
        return cfg.Type.ToLowerInvariant() switch
        {
            "http" => CheckHttp(cfg.Url),
            "process" => CheckProcess(cfg),
            "heartbeatfile" => CheckHeartbeat(cfg.HeartbeatFile, cfg.MaxHeartbeatAgeSeconds),
            _ => false
        };
    }

    // Raw TCP connect, not a full HTTP request - the listener either has
    // its port bound or it doesn't, which is exactly the signal wanted
    // here. Any successful connect counts as alive (a 404 still proves
    // something real is listening) - same convention as this project's
    // own Casperia-Dev IsHostAlive helper.
    private static bool CheckHttp(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        try
        {
            Uri uri = new Uri(url);
            using TcpClient client = new TcpClient();
            Task connectTask = client.ConnectAsync(uri.Host, uri.Port);
            if (Task.WhenAny(connectTask, Task.Delay(3000)).Result != connectTask)
                return false;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    // Matches by exact exe path first - several services can share the
    // same exe name (every OpenSim.exe region instance does), so name
    // alone would give a false "alive" for the wrong instance. When
    // ProcessArgsContains is set, also requires the process's own command
    // line to contain that substring (e.g. a specific -inifile= value) -
    // the only way to actually disambiguate multiple instances of the
    // identical exe.
    private static bool CheckProcess(LivenessConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.ProcessPath))
            return false;

        string targetName = Path.GetFileNameWithoutExtension(cfg.ProcessPath);
        foreach (Process proc in Process.GetProcessesByName(targetName))
        {
            try
            {
                string? mainModulePath = proc.MainModule?.FileName;
                if (mainModulePath == null || !string.Equals(mainModulePath, cfg.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrEmpty(cfg.ProcessArgsContains))
                    return true;

                string commandLine = GetCommandLine(proc.Id);
                if (commandLine.Contains(cfg.ProcessArgsContains, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // MainModule throws for processes we don't have access to
                // (a different user, a protected system process) - just
                // not a match, not a fatal error.
            }
        }
        return false;
    }

    private static string GetCommandLine(int processId)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={processId}').CommandLine\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using Process? p = Process.Start(psi);
            if (p == null)
                return string.Empty;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool CheckHeartbeat(string? path, int maxAgeSeconds)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;
        return (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds <= maxAgeSeconds;
    }
}
