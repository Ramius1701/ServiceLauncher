using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ServiceLauncher;

public class UpdateCheckResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string ReleaseTitle { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public bool IsPrerelease { get; set; }
    public bool UpdateAvailable { get; set; }
    public bool RunningNewerThanPublished { get; set; }
}

// Ported from MBBSLauncher's UpdateChecker - manual, on-demand only. This
// class does no work unless CheckForUpdatesAsync() is called; nothing in
// this project wires it to startup or a timer. It only reports what it
// finds - it never downloads or installs anything.
public static class UpdateChecker
{
    public const string AppVersion = "v0.1.0";

    private const string ReleasesApiUrl = "https://api.github.com/repos/Ramius1701/ServiceLauncher/releases";
    private const string UserAgent = "ServiceLauncher-UpdateCheck";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion)
    {
        UpdateCheckResult result = new UpdateCheckResult { CurrentVersion = currentVersion };

        try
        {
            string json = await FetchReleasesJsonAsync().ConfigureAwait(false);

            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                result.Success = false;
                result.ErrorMessage = "No releases were found on GitHub for ServiceLauncher.";
                return result;
            }

            JsonElement? newest = null;
            foreach (JsonElement release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
                    continue;
                newest = release;
                break;
            }

            if (newest == null)
            {
                result.Success = false;
                result.ErrorMessage = "No published releases were found on GitHub for ServiceLauncher.";
                return result;
            }

            JsonElement rel = newest.Value;
            result.LatestVersion = GetString(rel, "tag_name");
            result.ReleaseTitle = GetString(rel, "name");
            result.DownloadUrl = GetString(rel, "html_url");
            result.IsPrerelease = rel.TryGetProperty("prerelease", out JsonElement pre) && pre.ValueKind == JsonValueKind.True;

            if (string.IsNullOrWhiteSpace(result.LatestVersion))
            {
                result.Success = false;
                result.ErrorMessage = "The latest release on GitHub did not include a version tag.";
                return result;
            }

            int comparison = CompareVersions(currentVersion, result.LatestVersion);
            result.UpdateAvailable = comparison < 0;
            result.RunningNewerThanPublished = comparison > 0;
            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage =
                "Could not reach GitHub to check for updates.\n\n" +
                "Please verify this machine has internet access and try again.\n\n" +
                "Details: " + ex.Message;
            return result;
        }
    }

    private static async Task<string> FetchReleasesJsonAsync()
    {
        using HttpClient client = new HttpClient { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using CancellationTokenSource cts = new CancellationTokenSource(RequestTimeout);
        using HttpResponseMessage response = await client.GetAsync(ReleasesApiUrl, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    // System.Version can't parse tags like "v0.1.0-beta2", so this rolls
    // its own comparer: numeric parts first, then a stable release ranks
    // above any pre-release of the same numeric version.
    public static int CompareVersions(string a, string b)
    {
        (int[] partsA, long rankA) = ParseVersion(a);
        (int[] partsB, long rankB) = ParseVersion(b);

        int len = Math.Max(partsA.Length, partsB.Length);
        for (int i = 0; i < len; i++)
        {
            int va = i < partsA.Length ? partsA[i] : 0;
            int vb = i < partsB.Length ? partsB[i] : 0;
            if (va != vb)
                return va < vb ? -1 : 1;
        }

        if (rankA != rankB)
            return rankA < rankB ? -1 : 1;

        return 0;
    }

    private static (int[] parts, long rank) ParseVersion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (Array.Empty<int>(), long.MaxValue);

        string s = raw.Trim();

        Match numMatch = Regex.Match(s, @"\d+(?:\.\d+)*");
        int[] parts = Array.Empty<int>();
        if (numMatch.Success)
        {
            string[] pieces = numMatch.Value.Split('.');
            parts = new int[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
                parts[i] = int.TryParse(pieces[i], out int n) ? n : 0;
        }

        long rank = long.MaxValue;

        Match betaMatch = Regex.Match(s, @"beta[.\-]?(\d+)", RegexOptions.IgnoreCase);
        if (betaMatch.Success)
        {
            rank = long.TryParse(betaMatch.Groups[1].Value, out long betaNum) ? betaNum : 0;
        }
        else if (Regex.IsMatch(s, @"alpha|beta|rc|pre", RegexOptions.IgnoreCase))
        {
            rank = 0;
        }

        return (parts, rank);
    }
}
