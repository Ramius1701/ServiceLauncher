using System.Security.Cryptography;
using System.Text.Json;
using ServiceLauncher;

string baseDir = AppContext.BaseDirectory;
string configPath = Path.Combine(baseDir, "services.json");

if (!File.Exists(configPath))
{
    Console.WriteLine($"No config found at {configPath}.");
    Console.WriteLine("Copy services.example.json to services.json next to the exe, edit it for your setup, then restart.");
    return 1;
}

// Single instance - matches MBBSLauncher's own guard. A second copy
// started by accident (e.g. Task Scheduler firing while one's already
// running from a manual test) would otherwise both try to bind the same
// port and one would just fail anyway - this fails fast with a clear
// message instead.
using Mutex singleInstance = new Mutex(true, "Global\\ServiceLauncher_SingleInstance", out bool createdNew);
if (!createdNew)
{
    Console.WriteLine("ServiceLauncher is already running.");
    return 1;
}

LauncherConfig config = JsonSerializer.Deserialize<LauncherConfig>(
        File.ReadAllText(configPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new Exception("services.json is empty or invalid.");

string logPath = Path.IsPathRooted(config.LogFile) ? config.LogFile : Path.Combine(baseDir, config.LogFile);
Logger log = new Logger(logPath);

string tokenPath = Path.IsPathRooted(config.TokenFile) ? config.TokenFile : Path.Combine(baseDir, config.TokenFile);
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
api.Run();

return 0;
