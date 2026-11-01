using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PerformanceBooster.Core;

public sealed record WingetPackage(string Name, string Id, string Installed, string Available, string Source, bool RequireExplicit)
{
    public bool UnknownVersion => Installed.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || Installed.StartsWith('<');
    public bool IsStore => Source.Equals("msstore", StringComparison.OrdinalIgnoreCase);
    public bool IsWinget => Id.Equals("Microsoft.AppInstaller", StringComparison.OrdinalIgnoreCase);
    // Available is an older regular version than winget's newest (a test version): the upgrade asks for it by number.
    public bool ExactVersion { get; init; }
}

public enum UpgradeStage { Preparing, Downloading, Verifying, Installing }

public sealed record UpgradeProgress(UpgradeStage Stage, double Percent, string Text);

public sealed record UpgradeResult(bool Success, bool NotApplicable, bool RebootRequired, int ExitCode, string Message);

// Through the winget command line: list, upgrade, pin. Tables are read by the column positions in the header,
// so it works even when the column headers are in another language.
public static partial class Winget
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly Lazy<string?> _path = new(Find);
    const string Common = " --accept-source-agreements --disable-interactivity";

    // Fake winget in tests: (exe, arguments prepended).
    internal static (string Exe, string Prefix)? Override { get; set; }

    public static string? ExePath => Override?.Exe ?? _path.Value;
    public static bool Available => ExePath is not null;

    static string? Find()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    // One winget process at a time: running in parallel can lock the source database.
    static async Task<(int Code, List<string> Lines)> RunAsync(string args, Action<string>? onLine, CancellationToken ct)
    {
        if (ExePath is null) throw new InvalidOperationException(T("winget not found"));
        await Gate.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo(ExePath, (Override?.Prefix ?? "") + args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = new Process { StartInfo = psi };
            var lines = new List<string>();
            void Receive(object _, DataReceivedEventArgs e)
            {
                if (e.Data is null) return;
                lock (lines) lines.Add(e.Data);
                onLine?.Invoke(e.Data);
            }
            p.OutputDataReceived += Receive;
            p.ErrorDataReceived += Receive;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // An installer started with administrator rights cannot be closed from here; winget itself is.
                try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception) { }
                // The next winget command waits until this one has really exited (it may hold the source database).
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await p.WaitForExitAsync(wait.Token); } catch (OperationCanceledException) { }
                throw;
            }
            p.WaitForExit(); // waits until the redirected output is read to the end
            lock (lines) return (p.ExitCode, lines.ToList());
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<List<WingetPackage>> ListUpgradesAsync(bool includeUnknown, CancellationToken ct)
    {
        var (code, lines) = await RunAsync("upgrade --include-pinned" + (includeUnknown ? " --include-unknown" : "") + Common, null, ct);
        var result = ParseUpgrades(lines);
        if (result.Count == 0 && code != 0 && Tables(lines).Count == 0)
            throw new InvalidOperationException(LastMeaningful(lines) ?? F("winget exit code 0x{0:X8}", code));
        return await SkipPrereleasesAsync(result, VersionsAsync, ct);
    }

    // Alpha, beta and other test versions are not offered. Such a package is offered at its newest regular version
    // above the installed one, or not at all. Packages of a test channel (Preview, Beta, Insiders… in the ID or name) keep theirs.
    internal static async Task<List<WingetPackage>> SkipPrereleasesAsync(List<WingetPackage> packages,
        Func<WingetPackage, CancellationToken, Task<List<string>>> versions, CancellationToken ct)
    {
        var result = new List<WingetPackage>();
        foreach (var p in packages)
        {
            if (!IsPrerelease(p.Available) || IsTestChannel(p))
            {
                result.Add(p);
                continue;
            }
            string? best = null;
            foreach (var v in await versions(p, ct))
                if (!IsPrerelease(v) && (p.UnknownVersion || CompareVersions(v, p.Installed) > 0) && (best is null || CompareVersions(v, best) > 0)) best = v;
            if (best is not null) result.Add(p with { Available = best, ExactVersion = true });
        }
        return result;
    }

    static async Task<List<string>> VersionsAsync(WingetPackage p, CancellationToken ct)
    {
        var (_, lines) = await RunAsync($"show --id \"{p.Id}\" --exact --versions" + SourceArg(p) + Common, null, ct);
        return ParseVersions(lines);
    }

    // "winget show --versions": a one-column table whose rule is only as wide as its header.
    internal static List<string> ParseVersions(IReadOnlyList<string> raw)
    {
        var lines = raw.Select(Clean).ToList();
        int rule = lines.FindIndex(l => l.Length >= 3 && l.All(c => c == '-'));
        return rule < 0 ? [] : lines.Skip(rule + 1).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Contains(' ')).ToList();
    }

    [GeneratedRegex(@"(?<![a-z])(alpha|beta|preview|pre|prerelease|rc|dev|nightly|canary|insiders?|snapshot|eap|experimental|unstable)(?![a-z])|(?<=\d)[ab](?=\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Prerelease();

    [GeneratedRegex(@"(?<![a-z])(alpha|beta|preview|prerelease|dev|developer|nightly|canary|insiders?|eap|experimental)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex TestChannel();

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelCase();

    // 1.0.0-beta.2, 2.1-rc1, 3.13.0b1, 7.5.0-preview.3, 1.0.0.dev20…
    internal static bool IsPrerelease(string version) => Prerelease().IsMatch(version);

    // Mozilla.Firefox.DeveloperEdition, Microsoft.PowerShell.Preview, Google.Chrome.Beta…
    internal static bool IsTestChannel(WingetPackage p) => TestChannel().IsMatch(CamelCase().Replace(p.Id + " " + p.Name, " "));

    // Part by part as winget does: the number at the start of each part, then the rest as text ("1.10" > "1.9", "2.0" = "2.0.0").
    internal static int CompareVersions(string a, string b)
    {
        var x = a.Trim().TrimStart('v', 'V').Split('.');
        var y = b.Trim().TrimStart('v', 'V').Split('.');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var (n1, r1) = VersionPart(i < x.Length ? x[i] : "0");
            var (n2, r2) = VersionPart(i < y.Length ? y[i] : "0");
            int c = n1.CompareTo(n2);
            if (c == 0) c = string.Compare(r1, r2, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return 0;
    }

    static (long Number, string Suffix) VersionPart(string part)
    {
        int k = 0;
        while (k < part.Length && char.IsDigit(part[k])) k++;
        return (long.TryParse(part[..k], out var n) ? n : 0, part[k..]);
    }

    internal static List<WingetPackage> ParseUpgrades(IReadOnlyList<string> lines)
    {
        var tables = Tables(lines);
        var result = new List<WingetPackage>();
        for (int t = 0; t < tables.Count; t++)
        {
            var (starts, rows) = tables[t];
            if (starts.Count < 4) continue;
            foreach (var row in rows)
            {
                var name = Field(row, starts, 0);
                var id = Field(row, starts, 1);
                var installed = Field(row, starts, 2);
                var available = Field(row, starts, 3);
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id) || id.Contains(' ') || string.IsNullOrEmpty(available)) continue;
                var source = starts.Count > 4 ? Field(row, starts, 4) ?? "" : "";
                // Tables after the first: packages that "require explicit targeting".
                result.Add(new WingetPackage(name, id, installed ?? "", available, source, t > 0));
            }
        }
        return result.DistinctBy(p => (p.Id.ToLowerInvariant(), p.Source)).ToList();
    }

    public static async Task<HashSet<string>> PinnedIdsAsync(CancellationToken ct)
    {
        var (_, lines) = await RunAsync("pin list" + Common, null, ct);
        return ParsePins(lines);
    }

    internal static HashSet<string> ParsePins(IReadOnlyList<string> lines)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (starts, rows) in Tables(lines))
            foreach (var row in rows)
                if (starts.Count > 2 && Field(row, starts, 1) is { Length: > 0 } id && !id.Contains(' ')) ids.Add(id);
        return ids;
    }

    public static async Task<(bool Ok, string Message)> PinAsync(WingetPackage p, bool add, CancellationToken ct)
    {
        var verb = add ? "pin add" : "pin remove";
        var (code, lines) = await RunAsync($"{verb} --id \"{p.Id}\" --exact" + SourceArg(p) + Common, null, ct);
        return (code == 0, LastMeaningful(lines) ?? F("exit code 0x{0:X8}", code));
    }

    public static async Task<string> ShowAsync(WingetPackage p, CancellationToken ct)
    {
        var (_, lines) = await RunAsync($"show --id \"{p.Id}\" --exact" + SourceArg(p) + Common, null, ct);
        return string.Join(Environment.NewLine, lines.Select(Clean).Where(l => !IsNoise(l)));
    }

    public static async Task<UpgradeResult> UpgradeAsync(WingetPackage p, bool includeUnknown, bool silent, IProgress<UpgradeProgress> progress, CancellationToken ct)
    {
        var args = $"upgrade --id \"{p.Id}\" --exact" + SourceArg(p) + (p.ExactVersion ? $" --version \"{p.Available}\"" : "") + (silent ? " --silent" : " --interactive")
                   + (includeUnknown || p.UnknownVersion ? " --include-unknown" : "") + " --accept-package-agreements" + Common;
        var state = new UpgradeOutput();
        progress.Report(new(UpgradeStage.Preparing, -1, T("Preparing")));
        var (code, _) = await RunAsync(args, line => { if (state.Read(line) is { } p2) progress.Report(p2); }, ct);
        return state.Result(code);
    }

    // For packages with no match in the Programs list: winget's own uninstall command.
    public static async Task<UninstallResult> UninstallAsync(WingetPackage p, bool silent, CancellationToken ct)
    {
        var args = $"uninstall --id \"{p.Id}\" --exact" + SourceArg(p) + (silent ? " --silent" : " --interactive") + Common;
        var (code, lines) = await RunAsync(args, null, ct);
        if (code == 0) return new(true, false, false, 0, T("Uninstalled"));
        var last = LastMeaningful(lines);
        return new(false, false, false, code, $"{Explain(code, last) ?? last ?? T("Unknown error")} ({F("code 0x{0:X8}", code)})");
    }

    static string SourceArg(WingetPackage p) => string.IsNullOrEmpty(p.Source) ? "" : $" --source {p.Source}";

    // Line-by-line reading of winget upgrade output: stage, download/install percentage, result.
    internal sealed class UpgradeOutput
    {
        bool _installed, _notApplicable, _reboot;
        string? _error, _last;
        UpgradeStage _stage = UpgradeStage.Preparing;

        public UpgradeProgress? Read(string raw)
        {
            var line = Clean(raw).Trim();
            if (IsNoise(line)) return null;
            // The bar after the install starts is the install's own (MSIX and Store packages report it); before that it is the download.
            if (DownloadPercent(line) is double pct)
                return _stage == UpgradeStage.Installing
                    ? new(UpgradeStage.Installing, pct, T("Installing"))
                    : new(_stage = UpgradeStage.Downloading, pct, SizeProgress().Match(line) is { Success: true } size ? F("Downloading · {0}", size.Value) : T("Downloading"));
            _last = line;
            UpgradeProgress? result = null;
            if (Has(line, "Downloading")) result = new(UpgradeStage.Downloading, 0, T("Downloading"));
            else if (Has(line, "verified installer hash")) result = new(UpgradeStage.Verifying, -1, T("Verified"));
            else if (Has(line, "Starting package install") || Has(line, "Installing")) result = new(UpgradeStage.Installing, -1, T("Installing"));
            if (result is not null) _stage = result.Stage;
            if (Has(line, "Successfully installed")) _installed = true;
            if (Has(line, "No available upgrade") || Has(line, "No applicable upgrade") || Has(line, "No newer package")) _notApplicable = true;
            if (Has(line, "restart") || Has(line, "reboot")) _reboot = true;
            if (Has(line, "fail") || Has(line, "error") || Has(line, "cannot")) _error = line;
            return result;
        }

        public UpgradeResult Result(int code)
        {
            bool ok = (code == 0 || _installed) && !_notApplicable;
            string message = ok ? (_reboot ? T("Updated · restart required") : T("Updated"))
                : _notApplicable ? T("No applicable update found")
                : $"{Explain(code, _error) ?? _error ?? _last ?? T("Unknown error")} ({F("code 0x{0:X8}", code)})";
            return new(ok, _notApplicable, _reboot, code, message);
        }
    }

    // Most installers report no percentage to winget; the bar then advances with time, slowing down as it goes
    // (half way after 20 s), and never reaches the end before winget reports the result.
    public static double EstimatedInstallPercent(TimeSpan elapsed) => 95 * (1 - Math.Pow(0.5, Math.Max(0, elapsed.TotalSeconds) / 20));

    // The installed version was installed with another installer (apps that update themselves).
    public const int InstallTechnologyMismatch = unchecked((int)0x8A15008E);

    // Plain explanation for common winget errors; for others, winget's own message is shown.
    internal static string? Explain(int code, string? line) => unchecked((uint)code) switch
    {
        0x8A15008E => T("Installed with a different installer; winget cannot update it. If the app updates itself (browsers, for example), use its own updater."),
        0x8A150011 => T("Could not verify the installer (hash mismatch). The publisher may have just replaced the file; try again in a few hours."),
        _ when line?.Contains("1618", StringComparison.Ordinal) == true => T("Another installation is in progress. Try again when it finishes."),
        _ => null,
    };

    // --- Table reading ---

    // Each table: the header line above the "-----" rule and the lines below it. Column starts come from the header words.
    internal static List<(List<int> Starts, List<string> Rows)> Tables(IReadOnlyList<string> raw)
    {
        var lines = raw.Select(Clean).ToList();
        var tables = new List<(List<int>, List<string>)>();
        for (int i = 1; i < lines.Count; i++)
        {
            if (!IsRule(lines[i])) continue;
            var header = lines[i - 1];
            var starts = new List<int>();
            for (int k = 0; k < header.Length; k++)
                if (header[k] != ' ' && (k == 0 || header[k - 1] == ' ')) starts.Add(k);
            var rows = new List<string>();
            int j = i + 1;
            while (j < lines.Count && !IsRule(lines[j])) rows.Add(lines[j++]);
            if (j < lines.Count && rows.Count > 0) rows.RemoveAt(rows.Count - 1); // header of the next table
            tables.Add((starts, rows));
            i = j - 1;
        }
        return tables;
    }

    static string? Field(string row, List<int> starts, int index)
    {
        if (index >= starts.Count || row.Length <= starts[index]) return null;
        if (index > 0 && row[starts[index] - 1] != ' ') return null; // line that does not fit the columns (a sentence)
        int end = index + 1 < starts.Count ? Math.Min(starts[index + 1], row.Length) : row.Length;
        return row[starts[index]..end].Trim();
    }

    // The progress bar rewrites the same line with \r; the last part is the valid one.
    static string Clean(string line)
    {
        int k = line.LastIndexOf('\r');
        return (k >= 0 ? line[(k + 1)..] : line).TrimEnd();
    }

    static bool IsRule(string line) => line.Length >= 10 && line.All(c => c == '-');

    // Empty line, spinner (- \ | /) or only a progress bar.
    static bool IsNoise(string line)
    {
        var t = line.Trim();
        return t.Length == 0 || t is "-" or "\\" or "|" or "/" || t.All(c => c is '█' or '▒' or ' ');
    }

    static bool Has(string line, string text) => line.Contains(text, StringComparison.OrdinalIgnoreCase);

    static string? LastMeaningful(IEnumerable<string> lines) =>
        lines.Select(l => Clean(l).Trim()).LastOrDefault(l => !IsNoise(l) && DownloadPercent(l) is null);

    [GeneratedRegex(@"([\d.,]+)\s*(B|KB|MB|GB)\s*/\s*([\d.,]+)\s*(B|KB|MB|GB)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeProgress();

    [GeneratedRegex(@"(\d{1,3})\s*%")]
    private static partial Regex PercentProgress();

    internal static double? DownloadPercent(string line)
    {
        var m = SizeProgress().Match(line);
        if (m.Success)
        {
            double done = Bytes(m.Groups[1].Value, m.Groups[2].Value), total = Bytes(m.Groups[3].Value, m.Groups[4].Value);
            if (total > 0) return Math.Clamp(done / total * 100, 0, 100);
        }
        m = PercentProgress().Match(line);
        if (m.Success && (line.Contains('█') || line.Contains('▒'))) return Math.Clamp(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100);
        return null;
    }

    static double Bytes(string number, string unit)
    {
        if (!double.TryParse(number.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return 0;
        return unit.ToUpperInvariant() switch { "KB" => v * 1024, "MB" => v * 1048576, "GB" => v * 1073741824, _ => v };
    }
}
