using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PerformanceBooster.Core;

public sealed record ReleaseAsset(long Id, string Name, long Size, string? Sha256);

public sealed record AppRelease(Version Version, string Tag, string Notes, string PageUrl, ReleaseAsset? Asset);

// The app updates itself from the GitHub releases of its repository: the latest release's version (from its tag, v1.2.0, or else
// its title, "V 1.2") is compared with the running version, its PerformanceBooster.exe is downloaded next to the settings and
// verified (size, the SHA-256 GitHub reports), then the running exe is swapped for it and the app restarts.
// While the repository is private, the GitHub API needs a token: PERFORMANCEBOOSTER_GITHUB_TOKEN, GH_TOKEN or GITHUB_TOKEN,
// or the GitHub CLI's login (gh auth token). A public repository needs none.
public static partial class SelfUpdate
{
    public const string Repository = "AtaolB/Performance-Booster";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    public static Version Current { get; } = Normalize(typeof(SelfUpdate).Assembly.GetName().Version ?? new Version(0, 0));

    static readonly string[] TokenVariables = ["PERFORMANCEBOOSTER_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];
    static readonly Lazy<HttpClient> _http = new(() =>
    {
        // Redirects are followed by hand so the token only ever goes to GitHub, never to the download host it redirects to.
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppPaths.Id, Format(Current)));
        return client;
    });

    // Tests point these at a local fake server and token.
    internal static string ApiBase { get; set; } = "https://api.github.com/repos/" + Repository;
    internal static Func<CancellationToken, Task<string?>> TokenSource { get; set; } = FindTokenAsync;

    // Only the released exe (one file) replaces itself; a development build (Debug, or a build folder where the app's DLL
    // sits next to the exe) only checks.
    public static bool CanInstall =>
#if DEBUG
        false;
#else
        Environment.ProcessPath is { } exe && !File.Exists(Path.ChangeExtension(exe, ".dll"));
#endif

    public static string Folder => Path.Combine(AppPaths.Folder, "update");
    static string Pending => Path.Combine(Folder, "pending.json");

    public static string Format(Version v) => v.Revision > 0 ? v.ToString(4) : v.ToString(3);

    internal static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    // "v1.2.0", "1.2", "v1.2.0-beta" → 1.2.0.0
    public static Version? ParseVersion(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        int suffix = text.IndexOfAny(['-', '+']);
        if (suffix >= 0) text = text[..suffix];
        return Version.TryParse(text, out var v) ? Normalize(v) : null;
    }

    // A version anywhere in a tag or release title: "V 1.2", "release-1.2", "Performance Booster 1.2.1", "v2" → 2.0.
    public static Version? FindVersion(string text)
    {
        if (ParseVersion(text) is { } exact) return exact;
        var m = VersionInText().Match(text);
        if (m.Success) return ParseVersion(m.Groups[1].Value);
        m = MajorOnly().Match(text);
        return m.Success ? ParseVersion(m.Groups[1].Value + ".0") : null;
    }

    // Same patterns as the release workflow (.github/workflows/release.yml).
    [GeneratedRegex(@"(?<![\w.])[vV]?\s*(\d+(?:\.\d+){1,3})(?![\w.])")]
    private static partial Regex VersionInText();

    [GeneratedRegex(@"^\s*[vV]\s*(\d+)\s*$")]
    private static partial Regex MajorOnly();

    // The latest published release (GitHub leaves drafts and pre-releases out).
    public static async Task<AppRelease> LatestAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = await TokenSource(timeout.Token);
        using var response = await SendAsync(ApiBase + "/releases/latest", "application/vnd.github+json", token, HttpCompletionOption.ResponseContentRead, timeout.Token);
        // A private repository answers 404 without a token, like a repository without releases.
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(token is null ? T("No release found. While the repository is private, a GitHub token is needed.") : T("No release found on GitHub."));
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(F("GitHub answered {0}.", (int)response.StatusCode));
        // Not "up to date": a release without a version number cannot be compared.
        return ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token))
               ?? throw new InvalidOperationException(T("The latest release on GitHub has no version number in its tag or title."));
    }

    internal static AppRelease? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        var tag = Text(root, "tag_name") ?? "";
        if ((FindVersion(tag) ?? FindVersion(Text(root, "name") ?? "")) is not { } version) return null;

        ReleaseAsset? asset = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var name = Text(a, "name") ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !name.StartsWith(AppPaths.Id, StringComparison.OrdinalIgnoreCase)) continue;
                var digest = Text(a, "digest");
                var candidate = new ReleaseAsset(a.GetProperty("id").GetInt64(), name, a.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                    digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null);
                // PerformanceBooster.exe itself before other exes with the same prefix.
                if (asset is null || name.Equals(AppPaths.Id + ".exe", StringComparison.OrdinalIgnoreCase)) asset = candidate;
            }
        return new AppRelease(version, tag, Text(root, "body") ?? "", Text(root, "html_url") ?? ReleasesPage, asset);

        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }

    // Downloads the release's exe and returns its path. A verified file from an earlier run is reused.
    public static async Task<string> DownloadAsync(AppRelease release, IProgress<double> progress, CancellationToken ct)
    {
        var asset = release.Asset ?? throw new InvalidOperationException(T("The release has no exe to download."));
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, $"{AppPaths.Id}-{Format(release.Version)}.exe");
        if (!Verify(file, asset))
        {
            foreach (var old in Directory.EnumerateFiles(Folder))
                try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var part = file + ".part";
            var token = await TokenSource(ct);
            using (var response = await SendAsync($"{ApiBase}/releases/assets/{asset.Id}", "application/octet-stream", token, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException(F("GitHub answered {0}.", (int)response.StatusCode));
                long total = response.Content.Headers.ContentLength ?? asset.Size;
                using var input = await response.Content.ReadAsStreamAsync(ct);
                using var output = File.Create(part);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress.Report(Math.Min(100, done * 100.0 / total));
                }
            }
            if (!Verify(part, asset))
            {
                File.Delete(part);
                throw new InvalidDataException(T("The downloaded file could not be verified (size or SHA-256 does not match)."));
            }
            File.Move(part, file, overwrite: true);
        }
        progress.Report(100);
        AppPaths.WriteJson(Pending, new PendingUpdate(Format(release.Version), file, asset.Size, asset.Sha256), AppJson.Default.PendingUpdate);
        return file;
    }

    static async Task<HttpResponseMessage> SendAsync(string url, string accept, string? token, HttpCompletionOption completion, CancellationToken ct)
    {
        var uri = new Uri(url);
        for (int hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(accept);
            if (token is not null && IsGitHub(uri)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await _http.Value.SendAsync(request, completion, ct);
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } location || hop == 5) return response;
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            response.Dispose();
        }
    }

    static bool IsGitHub(Uri uri) => uri.Authority.Equals(new Uri(ApiBase).Authority, StringComparison.OrdinalIgnoreCase);

    internal static bool Verify(string file, ReleaseAsset asset)
    {
        var info = new FileInfo(file);
        if (!info.Exists || (asset.Size > 0 && info.Length != asset.Size)) return false;
        using var stream = File.OpenRead(file);
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') return false; // a Windows program
        if (asset.Sha256 is null) return true;
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record PendingUpdate(string Version, string File, long Size, string? Sha256);

    // A downloaded, verified exe newer than this one, waiting to be installed. Once this version is current or newer,
    // the leftovers of the download are deleted.
    public static (Version Version, string File)? PendingInstall()
    {
        var pending = AppPaths.ReadJson(Pending, AppJson.Default.PendingUpdate);
        if (pending is null || ParseVersion(pending.Version) is not { } version) return null;
        if (version > Current && Verify(pending.File, new ReleaseAsset(0, "", pending.Size, pending.Sha256))) return (version, pending.File);
        try { Directory.Delete(Folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    // Puts the downloaded exe in place of the running one. Windows lets a running exe be renamed: the current exe becomes
    // PerformanceBooster.old.exe (the new version deletes it when it starts) and the new one takes its name.
    public static void Replace(string downloaded, string target)
    {
        var old = Path.Combine(Path.GetDirectoryName(target)!, AppPaths.Id + ".old.exe");
        if (File.Exists(old)) File.Delete(old);
        File.Move(target, old);
        try
        {
            File.Copy(downloaded, target);
        }
        catch
        {
            File.Move(old, target);
            throw;
        }
        try { File.Delete(downloaded); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static async Task<string?> FindTokenAsync(CancellationToken ct)
    {
        foreach (var name in TokenVariables)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) return value.Trim();
        // The GitHub CLI's login, if it is installed and signed in.
        try
        {
            using var p = Process.Start(new ProcessStartInfo("gh", "auth token")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync(ct);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await p.WaitForExitAsync(wait.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            var token = (await output).Trim();
            return p.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch (Win32Exception)
        {
            return null; // no GitHub CLI
        }
    }
}
