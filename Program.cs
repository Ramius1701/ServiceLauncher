using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using ServiceLauncher;

string baseDir = AppContext.BaseDirectory;
string configPath = Path.Combine(baseDir, "services.json");

if (!File.Exists(configPath))
{
    Console.WriteLine($"No config found at {configPath}.");
    Console.WriteLine("Copy services.example.json to services.json next to the exe, edit it for your setup, then restart.");
    return 1;
}

// Single instance - matches MBBSLauncher's own guard. Only the first
// instance may own the port and the ServiceOrchestrator; a second copy
// (e.g. someone launching the GUI manually while the Task Scheduler
// copy is already running headless) doesn't fail out here anymore - see
// below, it becomes a GUI client of the instance that's already running
// instead of a whole competing process.
using Mutex singleInstance = new Mutex(true, "Global\\ServiceLauncher_SingleInstance", out bool createdNew);
if (!createdNew && !Environment.UserInteractive)
{
    Console.WriteLine("ServiceLauncher is already running.");
    return 1;
}

LauncherConfig config = JsonSerializer.Deserialize<LauncherConfig>(
        File.ReadAllText(configPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new Exception("services.json is empty or invalid.");

string tokenPath = Path.IsPathRooted(config.TokenFile) ? config.TokenFile : Path.Combine(baseDir, config.TokenFile);

if (!createdNew)
{
    // Someone at the machine manually launched a second copy while the
    // Task Scheduler instance already owns the port - don't compete for
    // it, just become a GUI client of whichever instance is already
    // running (same token file, since it's already running headless and
    // that instance is the only thing that would ever create it).
    if (!File.Exists(tokenPath))
    {
        MessageBox.Show(
            $"ServiceLauncher is already running, but its token file wasn't found at {tokenPath}.",
            "ServiceLauncher");
        return 1;
    }

    ApplicationConfiguration.Initialize();
    Application.Run(new MainForm(config, File.ReadAllText(tokenPath).Trim(), configPath));
    return 0;
}

string logPath = Path.IsPathRooted(config.LogFile) ? config.LogFile : Path.Combine(baseDir, config.LogFile);
Logger log = new Logger(logPath);

string token;
if (File.Exists(tokenPath))
{
    token = File.ReadAllText(tokenPath).Trim();
}
else
{
    // Generated once, on first run - never hardcoded, never checked into
    // any repo. Printed to the log exactly once so it can be copied into
    // a bookmark; after that it only ever lives in this file.
    token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("/", "_").Replace("+", "-");
    File.WriteAllText(tokenPath, token);
    log.Info($"Generated a new access token, saved to {tokenPath}.");
    log.Info("Keep that file private - anyone with this token can trigger launches on this machine.");
}

log.Info("ServiceLauncher starting.");
log.Info($"Your launch URL: http://<this-machine's-address>:{config.ListenPort}/launch?service=all&token={token}");

ServiceOrchestrator orchestrator = new ServiceOrchestrator(config, log);
HttpApi api = new HttpApi(config, orchestrator, log, token);

// The HTTP listener always runs so remote/away-from-machine triggering
// (the original reason this app exists) keeps working no matter what -
// the GUI below is an additional local control surface, not a
// replacement for it.
Thread apiThread = new Thread(api.Run) { IsBackground = true };
apiThread.Start();

// Runs headless too, same as the API - crash recovery matters most
// exactly when nobody's watching the GUI.
new CrashMonitor(config, orchestrator, log).Start();

if (Environment.UserInteractive)
{
    ApplicationConfiguration.Initialize();
    Application.Run(new MainForm(config, token, configPath));
}
else
{
    // Headless (Task Scheduler, "run whether user is logged on or not"
    // = no interactive desktop) - no GUI to show, just keep the process
    // alive for the background HTTP listener like before this existed.
    Thread.Sleep(Timeout.Infinite);
}

return 0;
