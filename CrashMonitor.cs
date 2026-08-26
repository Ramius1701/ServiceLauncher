namespace ServiceLauncher;

// Adapted from MBBSLauncher's RestartManager - Stage 1 only (retry with
// debounce + a cooldown between attempts), per service, running headless
// so it works the same whether the GUI is open or not. See AutoRestart
// on ServiceConfig for why Stage 2 (auto-reboot the machine) isn't here.
public class CrashMonitor
{
    private const int PollIntervalSeconds = 5;

    private readonly LauncherConfig _config;
    private readonly ServiceOrchestrator _orchestrator;
    private readonly Logger _log;
    private readonly Dictionary<string, Watch> _watches = new();

    private class Watch
    {
        public bool Armed;                // seen alive at least once - ignore stale "down" at startup
        public bool SuppressUntilHealthy; // attempts exhausted; wait for a healthy sighting before re-arming
        public int ConfirmSecondsElapsed;
        public int Attempt;
        public bool InCooldown;
        public int CooldownSecondsElapsed;
    }

    public CrashMonitor(LauncherConfig config, ServiceOrchestrator orchestrator, Logger log)
    {
        _config = config;
        _orchestrator = orchestrator;
        _log = log;
        foreach (ServiceConfig svc in config.Services)
            _watches[svc.Id] = new Watch();
    }

    public void Start()
    {
        Thread thread = new Thread(RunLoop) { IsBackground = true };
        thread.Start();
    }

    private void RunLoop()
    {
        while (true)
        {
            Thread.Sleep(PollIntervalSeconds * 1000);
            foreach (ServiceConfig svc in _config.Services)
            {
                if (svc.AutoRestart.Enabled)
                    Tick(svc, _watches[svc.Id]);
            }
        }
    }

    private void Tick(ServiceConfig svc, Watch watch)
    {
        bool alive = LivenessChecker.IsAlive(svc.Liveness);

        if (watch.InCooldown)
        {
            if (alive)
            {
                watch.InCooldown = false;
                watch.Attempt = 0;
                watch.ConfirmSecondsElapsed = 0;
                return;
            }

            watch.CooldownSecondsElapsed += PollIntervalSeconds;
            if (watch.CooldownSecondsElapsed >= svc.AutoRestart.AttemptDelaySeconds)
            {
                watch.InCooldown = false;
                watch.CooldownSecondsElapsed = 0;
                Restart(svc, watch);
            }
            return;
        }

        if (alive)
        {
            watch.Armed = true;
            watch.SuppressUntilHealthy = false;
            watch.ConfirmSecondsElapsed = 0;
            watch.Attempt = 0;
            return;
        }

        // Never seen it up yet, or already gave up until it's healthy again -
        // don't chase a service that was never actually running under us.
        if (!watch.Armed || watch.SuppressUntilHealthy)
            return;

        watch.ConfirmSecondsElapsed += PollIntervalSeconds;
        if (watch.ConfirmSecondsElapsed < svc.AutoRestart.CrashConfirmSeconds)
            return;

        watch.ConfirmSecondsElapsed = 0;
        watch.Attempt = 1;
        _log.Info($"{svc.DisplayName}: crash detected (down for {svc.AutoRestart.CrashConfirmSeconds}s+) - restart attempt {watch.Attempt} of {svc.AutoRestart.RestartAttempts}.");
        Restart(svc, watch);
    }

    // orchestrator.Launch already blocks until the service comes up or its
    // own StartupTimeoutSeconds elapses, so by the time this returns we
    // already know whether the attempt actually worked.
    private void Restart(ServiceConfig svc, Watch watch)
    {
        try
        {
            _orchestrator.Launch(svc.Id);
        }
        catch (Exception e)
        {
            _log.Info($"{svc.DisplayName}: restart attempt {watch.Attempt} threw - {e.Message}");
        }

        if (LivenessChecker.IsAlive(svc.Liveness))
        {
            watch.Attempt = 0;
            return;
        }

        if (watch.Attempt >= svc.AutoRestart.RestartAttempts)
        {
            _log.Info($"{svc.DisplayName}: crash-restart exhausted after {svc.AutoRestart.RestartAttempts} attempt(s) - giving up until it's seen healthy again.");
            watch.SuppressUntilHealthy = true;
            watch.Attempt = 0;
            return;
        }

        watch.Attempt++;
        watch.InCooldown = true;
        watch.CooldownSecondsElapsed = 0;
        _log.Info($"{svc.DisplayName}: still down - next restart attempt ({watch.Attempt} of {svc.AutoRestart.RestartAttempts}) in {svc.AutoRestart.AttemptDelaySeconds}s.");
    }
}
