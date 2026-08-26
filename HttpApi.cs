using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ServiceLauncher;

// Consumed by MainForm - both the primary instance's own GUI and a
// second manually-launched instance acting as a client (see Program.cs)
// talk to this over loopback HTTP rather than the orchestrator directly,
// so both cases share one code path regardless of which process actually
// owns the ServiceOrchestrator.
public record ServiceStatusDto(string Id, string DisplayName, bool Alive);

public class HttpApi
{
    private readonly LauncherConfig _config;
    private readonly ServiceOrchestrator _orchestrator;
    private readonly Logger _log;
    private readonly string _token;

    public HttpApi(LauncherConfig config, ServiceOrchestrator orchestrator, Logger log, string token)
    {
        _config = config;
        _orchestrator = orchestrator;
        _log = log;
        _token = token;
    }

    public void Run()
    {
        HttpListener listener = new HttpListener();
        string wildcardPrefix = $"http://+:{_config.ListenPort}/";
        try
        {
            listener.Prefixes.Add(wildcardPrefix);
            listener.Start();
            _log.Info($"Listening on port {_config.ListenPort} (all interfaces).");
        }
        catch (HttpListenerException e)
        {
            // Binding a wildcard host (+) needs Administrator/URL-ACL
            // rights on Windows - true for the elevated Task Scheduler
            // deployment this was originally built for, but not for a
            // plain interactive launch of the GUI (e.g. testing locally,
            // or the primary instance running before any elevated copy
            // exists). Falling back to loopback-only means the GUI can
            // still talk to itself either way - this used to run
            // synchronously on the main thread, so a bind failure here
            // crashed the whole (console) app visibly; now that it runs
            // on a background thread alongside the GUI, an unhandled
            // exception here would instead kill the entire process
            // silently, with the GUI window just vanishing - so this
            // needs to degrade instead of throwing.
            _log.Info($"Could not bind {wildcardPrefix} ({e.Message}) - falling back to loopback-only. " +
                      "Remote/away-from-machine triggering needs this run elevated (see README); the local GUI still works.");
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{_config.ListenPort}/");
            listener.Start();
            _log.Info($"Listening on port {_config.ListenPort} (loopback only).");
        }

        while (true)
        {
            HttpListenerContext ctx = listener.GetContext();
            ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        string clientIp = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            string suppliedToken = ctx.Request.QueryString["token"] ?? "";

            if (!SecureEquals(suppliedToken, _token))
            {
                _log.Info($"REJECTED bad/missing token from {clientIp} on {path}");
                Respond(ctx, 403, "<h1>Forbidden</h1>");
                return;
            }

            if (path == "/status")
            {
                Respond(ctx, 200, BuildStatusPage());
                return;
            }

            // JSON variants for MainForm - same underlying data/actions as
            // the HTML routes above, just machine-readable for the GUI's
            // HttpClient instead of a browser.
            if (path == "/api/status")
            {
                RespondJson(ctx, 200, BuildStatusList());
                return;
            }

            if (path == "/api/launch")
            {
                string apiServiceId = ctx.Request.QueryString["service"] ?? "all";
                _log.Info($"Launch '{apiServiceId}' requested by {clientIp}");
                string apiResult = apiServiceId == "all" ? _orchestrator.LaunchAll() : _orchestrator.Launch(apiServiceId);
                RespondJson(ctx, 200, new { log = apiResult });
                return;
            }

            if (path != "/launch")
            {
                Respond(ctx, 404, "<h1>Not found</h1><p>Try /launch?service=&lt;id&gt;&amp;token=... or /status?token=...</p>");
                return;
            }

            string serviceId = ctx.Request.QueryString["service"] ?? "all";
            _log.Info($"Launch '{serviceId}' requested by {clientIp}");

            string result = serviceId == "all" ? _orchestrator.LaunchAll() : _orchestrator.Launch(serviceId);
            Respond(ctx, 200, "<h1>Service Launcher</h1><pre>" + WebUtility.HtmlEncode(result) + "</pre>");
        }
        catch (Exception e)
        {
            _log.Info("ERROR handling request: " + e);
            try { Respond(ctx, 500, "<h1>Error</h1>"); } catch { /* connection may already be gone */ }
        }
    }

    private string BuildStatusPage()
    {
        StringBuilder sb = new StringBuilder("<h1>Status</h1><table border=1 cellpadding=6><tr><th>Service</th><th>State</th></tr>");
        foreach (ServiceConfig svc in _config.Services)
        {
            bool alive = LivenessChecker.IsAlive(svc.Liveness);
            sb.Append("<tr><td>").Append(WebUtility.HtmlEncode(svc.DisplayName)).Append("</td><td>")
              .Append(alive ? "Running" : "Stopped").Append("</td></tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    private List<ServiceStatusDto> BuildStatusList()
    {
        return _config.Services
            .Select(svc => new ServiceStatusDto(svc.Id, svc.DisplayName, LivenessChecker.IsAlive(svc.Liveness)))
            .ToList();
    }

    private static bool SecureEquals(string a, string b)
    {
        // Constant-time comparison (CryptographicOperations.FixedTimeEquals)
        // - a naive == would let a timing attack narrow down the token
        // character by character. This endpoint is meant to be reachable
        // from the open internet, so this isn't a hypothetical concern.
        byte[] ba = Encoding.UTF8.GetBytes(a);
        byte[] bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length)
        {
            // Still run a fixed-time comparison against a same-length
            // buffer so a length mismatch doesn't itself leak timing.
            CryptographicOperations.FixedTimeEquals(ba, ba);
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static void Respond(HttpListenerContext ctx, int statusCode, string body)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        byte[] buf = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.OutputStream.Close();
    }

    private static void RespondJson(HttpListenerContext ctx, int statusCode, object body)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        byte[] buf = JsonSerializer.SerializeToUtf8Bytes(body);
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.OutputStream.Close();
    }
}
