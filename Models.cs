namespace ServiceLauncher;

public class LauncherConfig
{
    public int ListenPort { get; set; } = 8090;
    public string TokenFile { get; set; } = "token.txt";
    public string LogFile { get; set; } = "launcher.log";
    public List<VolumeConfig> Volumes { get; set; } = new();
    public List<ServiceConfig> Services { get; set; } = new();
}

// A drive that has to be mounted before anything living on it can start -
// e.g. a VHDX kept unmounted by design so it's safe to back up. Not every
// setup needs one; services without a RequiresVolume skip this entirely.
public class VolumeConfig
{
    public string Id { get; set; } = "";
    public string DriveLetter { get; set; } = "";
    public string VhdxPath { get; set; } = "";
}

public class ServiceConfig
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? RequiresVolume { get; set; }
    public string ExePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";

    // Most services here (OpenSim.exe, Robust.exe) should stay false -
    // launched with no window, no console. Only set true for something
    // that genuinely needs a real window to run correctly.
    public bool UseShellExecute { get; set; } = false;

    public LivenessConfig Liveness { get; set; } = new();
    public int StartupTimeoutSeconds { get; set; } = 90;

    // Services to launch after this one comes up, each with its own
    // delay - mirrors MBBSLauncher's "auto-launch up to 20 programs after
    // the BBS starts, each with an independent delay timer."
    public List<AutoLaunchEntry> AutoLaunch { get; set; } = new();
}

public class LivenessConfig
{
    // "http" - TCP-connects to Url; any successful connect counts as
    //   alive, matching this project's own IsHostAlive/Util.IsHostAlive
    //   convention (a 404 still proves the listener is up). Use this for
    //   anything with a distinguishable port - it's the only reliable way
    //   to tell apart several processes that share the same exe (every
    //   OpenSim.exe region instance looks identical by name/path alone).
    // "process" - matches by exact exe path (Process.MainModule.FileName),
    //   optionally narrowed by ProcessArgsContains if the exe path alone
    //   is ambiguous (multiple instances of the same exe). Use this for a
    //   genuinely singleton program with no HTTP endpoint to probe.
    // "heartbeatfile" - a file that's touched/rewritten while the service
    //   is healthy; alive if its last-write time is within
    //   MaxHeartbeatAgeSeconds. Use this for something that can hang
    //   without actually exiting, where neither of the above would catch
    //   the failure.
    public string Type { get; set; } = "process";

    public string? Url { get; set; }
    public string? ProcessPath { get; set; }
    public string? ProcessArgsContains { get; set; }
    public string? HeartbeatFile { get; set; }
    public int MaxHeartbeatAgeSeconds { get; set; } = 30;
}

public class AutoLaunchEntry
{
    public string ServiceId { get; set; } = "";
    public int DelaySeconds { get; set; } = 0;
}
