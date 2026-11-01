using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerformanceBooster.Tests;

// Updating the app itself from GitHub releases: version tags, release parsing, verification, the exe swap,
// and the whole download against a fake GitHub on the loopback address (never the real repository).
static class SelfUpdateTests
{
    static string ReleaseJson(string tag, bool draft, string? digest, long size = 1234, string name = "") => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        name,
        draft,
        prerelease = false,
        body = "Notes",
        html_url = "https://github.com/x/y/releases/tag/" + tag,
        assets = new object[]
        {
            new { id = 1, name = "PerformanceBooster-symbols.zip", size = 10 },
            new { id = 3, name = "PerformanceBooster.Tests.exe", size = 10 },
            new { id = 2, name = "PerformanceBooster.exe", size, digest },
        },
    });

    public static void Run()
    {
        Runner.Test("Version tags and the GitHub release", () =>
        {
            Check.Equal(new Version(2, 1, 0, 0), SelfUpdate.ParseVersion("v2.1.0"), "v2.1.0");
            Check.Equal(new Version(2, 1, 0, 0), SelfUpdate.ParseVersion("2.1"), "2.1");
            Check.Equal(new Version(3, 0, 1, 0), SelfUpdate.ParseVersion("V3.0.1-beta.2"), "pre-release suffix");
            Check.Equal<Version?>(null, SelfUpdate.ParseVersion("latest"), "not a version");
            Check.Equal(new Version(3, 0, 0, 0), SelfUpdate.FindVersion("V 3.0"), "title V 3.0");
            Check.Equal(new Version(3, 1, 0, 0), SelfUpdate.FindVersion("release-3.1"), "tag release-3.1");
            Check.Equal(new Version(3, 0, 2, 0), SelfUpdate.FindVersion("Performance Booster 3.0.2"), "title with the app's name");
            Check.Equal(new Version(3, 0, 0, 0), SelfUpdate.FindVersion("v3"), "major only");
            Check.Equal<Version?>(null, SelfUpdate.FindVersion("performance-boost"), "no number");
            Check.Equal<Version?>(null, SelfUpdate.FindVersion("2026-10-29"), "a date is not a version");
            Check.Equal("2.1.0", SelfUpdate.Format(new Version(2, 1, 0, 0)), "three parts");
            Check.Equal("2.1.0.4", SelfUpdate.Format(new Version(2, 1, 0, 4)), "four parts");

            var sha = new string('a', 64);
            var release = SelfUpdate.ParseRelease(ReleaseJson("v99.1.0", false, "sha256:" + sha))!;
            Check.True(release.Version == new Version(99, 1, 0, 0) && release.Tag == "v99.1.0", "version: " + release.Version);
            Check.True(release.Asset is { Id: 2, Name: "PerformanceBooster.exe", Size: 1234 } && release.Asset.Sha256 == sha, "the app's exe is picked: " + release.Asset);
            Check.True(release.Notes == "Notes" && release.PageUrl.EndsWith("/v99.1.0", StringComparison.Ordinal), "notes and page");
            Check.Equal<AppRelease?>(null, SelfUpdate.ParseRelease(ReleaseJson("v99.1.0", true, null)), "draft");
            Check.Equal<AppRelease?>(null, SelfUpdate.ParseRelease(ReleaseJson("nightly", false, null)), "tag that is not a version");
            var titled = SelfUpdate.ParseRelease(ReleaseJson("performance-boost", false, null, name: "V 3.0"));
            Check.True(titled is { Tag: "performance-boost" } && titled.Version == new Version(3, 0, 0, 0), "version from the title when the tag has none: " + titled?.Version);
            Check.Equal(new Version(3, 2, 0, 0), SelfUpdate.ParseRelease(ReleaseJson("v3.2", false, null, name: "V 3.0"))!.Version, "the tag wins over the title");
            Check.True(SelfUpdate.ParseRelease(ReleaseJson("v99.1.0", false, null))!.Asset!.Sha256 is null, "no digest");
        });

        Runner.Test("Verifying the download and swapping the exe", () =>
        {
            var dir = Path.Combine(Runner.Data, "SelfUpdate");
            Directory.CreateDirectory(dir);
            byte[] exe = [(byte)'M', (byte)'Z', 1, 2, 3];
            var file = Path.Combine(dir, "new.exe");
            File.WriteAllBytes(file, exe);
            var sha = Convert.ToHexString(SHA256.HashData(exe));
            Check.True(SelfUpdate.Verify(file, new(1, "x", exe.Length, sha)) && SelfUpdate.Verify(file, new(1, "x", exe.Length, null)), "verified");
            Check.True(!SelfUpdate.Verify(file, new(1, "x", exe.Length + 1, null)) && !SelfUpdate.Verify(file, new(1, "x", exe.Length, new string('0', 64))), "size or hash mismatch");
            var text = Path.Combine(dir, "text.exe");
            File.WriteAllText(text, "hello");
            Check.True(!SelfUpdate.Verify(text, new(1, "x", 5, null)), "not a program");

            var target = Path.Combine(dir, AppPaths.Id + ".exe");
            var old = Path.Combine(dir, AppPaths.Id + ".old.exe");
            File.WriteAllText(target, "old version");
            SelfUpdate.Replace(file, target);
            Check.True(File.ReadAllBytes(target).SequenceEqual(exe) && File.ReadAllText(old) == "old version" && !File.Exists(file), "the new exe took the name, the old one was kept aside");
            File.WriteAllBytes(file, exe);
            SelfUpdate.Replace(file, target);
            Check.True(File.ReadAllBytes(old).SequenceEqual(exe), "a second update replaces the kept copy");
        });

        Runner.Test("Fake GitHub: token only to GitHub, redirect, download, pending install", async () =>
        {
            var exe = new byte[300_000];
            Random.Shared.NextBytes(exe);
            exe[0] = (byte)'M';
            exe[1] = (byte)'Z';
            var sha = Convert.ToHexString(SHA256.HashData(exe));
            string? apiAuth = null, downloadAuth = null, assetAccept = null;
            int downloads = 0;
            bool missing = false, unnumbered = false;
            using var cdn = new FakeHttp((path, headers) =>
            {
                downloads++;
                downloadAuth = headers.GetValueOrDefault("Authorization");
                return (200, null, exe);
            });
            using var api = new FakeHttp((path, headers) =>
            {
                if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
                {
                    apiAuth = headers.GetValueOrDefault("Authorization");
                    return missing ? (404, null, [])
                        : (200, null, Encoding.UTF8.GetBytes(unnumbered ? ReleaseJson("performance-boost", false, null, name: "Big update") : ReleaseJson("v99.1.0", false, "sha256:" + sha, exe.Length)));
                }
                if (path.EndsWith("/releases/assets/2", StringComparison.Ordinal))
                {
                    assetAccept = headers.GetValueOrDefault("Accept");
                    return (302, $"http://127.0.0.1:{cdn.Port}/file", []);
                }
                return (404, null, []);
            });
            var (apiBase, tokens) = (SelfUpdate.ApiBase, SelfUpdate.TokenSource);
            SelfUpdate.ApiBase = $"http://127.0.0.1:{api.Port}/repos/x/y";
            SelfUpdate.TokenSource = _ => Task.FromResult<string?>("test-token");
            try
            {
                var release = await SelfUpdate.LatestAsync(CancellationToken.None);
                Check.True(release is { Asset.Id: 2 } && release.Version > SelfUpdate.Current, "a newer release");
                Check.Equal("Bearer test-token", apiAuth, "the token goes to GitHub");

                var progress = new ListProgress<double>();
                var file = await SelfUpdate.DownloadAsync(release!, progress, CancellationToken.None);
                Check.True(File.ReadAllBytes(file).SequenceEqual(exe), "downloaded");
                Check.Equal("application/octet-stream", assetAccept, "the asset is asked for as a file");
                Check.Equal<string?>(null, downloadAuth, "no token to the host GitHub redirects to");
                Check.True(progress.Items.Count > 1 && progress.Items[^1] == 100, "progress: " + progress.Items.Count);
                Check.True(SelfUpdate.PendingInstall() is { } pending && pending.File == file && pending.Version == release!.Version, "waits to be installed");

                await SelfUpdate.DownloadAsync(release!, new ListProgress<double>(), CancellationToken.None);
                Check.Equal(1, downloads, "a verified download is reused");

                File.WriteAllBytes(file, [1, 2, 3]);
                Check.True(SelfUpdate.PendingInstall() is null && !Directory.Exists(SelfUpdate.Folder), "a damaged download is not installed and is cleaned up");

                unnumbered = true;
                var noVersion = await ThrowsAsync<InvalidOperationException>(() => SelfUpdate.LatestAsync(CancellationToken.None));
                Check.Equal(L.T("The latest release on GitHub has no version number in its tag or title."), noVersion.Message, "a release without a version is an error, not \"up to date\"");
                unnumbered = false;

                missing = true;
                SelfUpdate.TokenSource = _ => Task.FromResult<string?>(null);
                var error = await ThrowsAsync<InvalidOperationException>(() => SelfUpdate.LatestAsync(CancellationToken.None));
                Check.Equal(L.T("No release found. While the repository is private, a GitHub token is needed."), error.Message, "private repository without a token");
                Check.Equal<string?>(null, apiAuth, "no token header without a token");
            }
            finally
            {
                (SelfUpdate.ApiBase, SelfUpdate.TokenSource) = (apiBase, tokens);
                try { Directory.Delete(SelfUpdate.Folder, true); } catch (DirectoryNotFoundException) { }
            }
        });
    }

    static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException ex)
        {
            return ex;
        }
        throw new TestFailure($"expected {typeof(TException).Name}");
    }
}

// A tiny HTTP server on the loopback address (no firewall prompt, no URL reservation): one connection per request.
sealed class FakeHttp : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    // handler(path, headers) → (status, Location header or null, body)
    public FakeHttp(Func<string, Dictionary<string, string>, (int Status, string? Location, byte[] Body)> handler)
    {
        _listener.Start();
        _ = Task.Run(() => LoopAsync(handler));
    }

    async Task LoopAsync(Func<string, Dictionary<string, string>, (int Status, string? Location, byte[] Body)> handler)
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException) { return; }
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var path = (await reader.ReadLineAsync() ?? "").Split(' ') is [_, var p, ..] ? p : "/";
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    if (line.IndexOf(':') is > 0 and var colon) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                var (status, location, body) = handler(path, headers);
                var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Other")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n"
                           + (location is null ? "" : $"Location: {location}\r\n") + "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(body);
            }
        }
    }

    public void Dispose() => _listener.Stop();
}
