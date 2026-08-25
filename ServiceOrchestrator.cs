using System.Diagnostics;
using System.Text;

namespace ServiceLauncher;

public class ServiceOrchestrator
{
    private readonly LauncherConfig _config;
    private readonly Logger _log;

    public ServiceOrchestrator(LauncherConfig config, Logger log)
    {
        _config = config;
        _log = log;
    }

    public string Launch(string serviceId)
    {
        StringBuilder sb = new StringBuilder();
        LaunchRecursive(serviceId, sb, new HashSet<string>());
        return sb.ToString();
    }

    public string LaunchAll()
    {
        StringBuilder sb = new StringBuilder();
        HashSet<string> visited = new HashSet<string>();
        foreach (ServiceConfig svc in _config.Services)
            LaunchRecursive(svc.Id, sb, visited);
        return sb.ToString();
    }

    // Idempotent by design - matches MBBSLauncher's "Already-Running
    // Detection." Hitting /launch again (a stale bookmark, an impatient
    // double-tap on a phone with a slow connection) never double-starts
    // anything; it just confirms what's already up.
    private void LaunchRecursive(string serviceId, StringBuilder sb, HashSet<string> visited)
    {
        if (!visited.Add(serviceId))
            return; // already handled this pass - a service reachable via
                     // more than one auto-launch chain only runs once

        ServiceConfig? svc = _config.Services.FirstOrDefault(s => s.Id == serviceId);
        if (svc == null)
        {
            Report(sb, $"Unknown service '{serviceId}' - check services.json.");
            return;
        }

        if (svc.RequiresVolume != null)
        {
            VolumeConfig? vol = _config.Volumes.FirstOrDefault(v => v.Id == svc.RequiresVolume);
            if (vol == null)
            {
                Report(sb, $"{svc.DisplayName}: unknown volume '{svc.RequiresVolume}' - check services.json.");
                return;
            }
            if (!VolumeManager.EnsureMounted(vol, _log))
            {
                Report(sb, $"{svc.DisplayName}: FAILED - volume '{vol.Id}' did not mount.");
                return;
            }
        }

        bool up;
        if (LivenessChecker.IsAlive(svc.Liveness))
        {
            Report(sb, $"{svc.DisplayName}: already running.");
            up = true;
        }
        else
        {
            Report(sb, $"{svc.DisplayName}: starting...");

            // Process.Start throws (Win32Exception) for a bad exe path
            // instead of returning a failure result - found live testing
            // a deliberately-fake placeholder entry: an unhandled
            // exception here didn't just fail that one service, it
            // crashed the whole HTTP response, silently discarding the
            // status of every service already reported above it (even
            // though those had genuinely already started fine). One
            // misconfigured entry should never take down the report for
            // everything else in the same launch.
            try
            {
                StartProcess(svc);
            }
            catch (Exception e)
            {
                Report(sb, $"{svc.DisplayName}: FAILED to start - {e.Message}");
                return;
            }

            up = false;
            int waited = 0;
            while (waited < svc.StartupTimeoutSeconds)
            {
                Thread.Sleep(2000);
                waited += 2;
                if (LivenessChecker.IsAlive(svc.Liveness))
                {
                    up = true;
                    break;
                }
            }

            Report(sb, up
                    ? $"{svc.DisplayName}: up after {waited}s."
                    : $"{svc.DisplayName}: did NOT come up within {svc.StartupTimeoutSeconds}s - check its own log on the machine.");
        }

        // Don't chain-launch dependents of something that never actually
        // came up - a Store-order-style false success (looked alive
        // briefly, wasn't really) shouldn't cascade into starting things
        // that depend on it.
        if (!up)
            return;

        foreach (AutoLaunchEntry entry in svc.AutoLaunch)
        {
            if (entry.DelaySeconds > 0)
            {
                Report(sb, $"Waiting {entry.DelaySeconds}s before {entry.ServiceId}...");
                Thread.Sleep(entry.DelaySeconds * 1000);
            }
            LaunchRecursive(entry.ServiceId, sb, visited);
        }
    }

    private static void StartProcess(ServiceConfig svc)
    {
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = svc.ExePath,
            Arguments = svc.Arguments,
            WorkingDirectory = svc.WorkingDirectory,
            UseShellExecute = svc.UseShellExecute,
            CreateNoWindow = !svc.UseShellExecute
        };
        Process.Start(psi);
    }

    private void Report(StringBuilder sb, string message)
    {
        _log.Info(message);
        sb.Append(message).Append('\n');
    }
}
