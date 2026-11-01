using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

// Network test, game mode, startup manager, system info and maintenance tools.
public sealed partial class MainViewModel
{
    readonly GameWatcher _watcher = new();
    readonly AppSettings _settings = AppSettings.Current;
    CancellationTokenSource? _testCts;
    SpeedResult? _lastSpeed;
    bool _startupLoaded, _appsLoaded;

    public ObservableCollection<DnsRow> DnsResults { get; } = [];
    public ObservableCollection<StartupRow> StartupItems { get; } = [];
    public ObservableCollection<InfoRow> SystemItems { get; } = [];
    public ICollectionView SystemView { get; private set; } = null!;
    public ObservableCollection<TweakOption> GamePlans { get; } = [];

    public ICommand SpeedTestCommand { get; private set; } = null!;
    public ICommand DnsTestCommand { get; private set; } = null!;
    public ICommand CancelTestCommand { get; private set; } = null!;
    public ICommand UseDnsCommand { get; private set; } = null!;
    public ICommand RefreshStartupCommand { get; private set; } = null!;
    public ICommand ToggleStartupCommand { get; private set; } = null!;
    public ICommand CleanTempCommand { get; private set; } = null!;
    public ICommand ClearShaderCommand { get; private set; } = null!;
    public ICommand FlushDnsCommand { get; private set; } = null!;
    public ICommand RestorePointCommand { get; private set; } = null!;
    public ICommand CopyReportCommand { get; private set; } = null!;

    string _testStatus = T("Test not run yet."), _idle = "–", _down = "–", _downLoaded = "–", _up = "–", _upLoaded = "–",
        _grade = "–", _verdict = "", _dnsStatus = "", _tempSize = "…", _shaderSize = "…", _gameStatus = "";
    bool _testing, _dnsIntercepted, _dnsEncrypted;
    TweakOption? _gamePlan;

    public string TestStatus { get => _testStatus; private set => Set(ref _testStatus, value); }
    public string IdleLatency { get => _idle; private set => Set(ref _idle, value); }
    public string DownSpeed { get => _down; private set => Set(ref _down, value); }
    public string DownLatency { get => _downLoaded; private set => Set(ref _downLoaded, value); }
    public string UpSpeed { get => _up; private set => Set(ref _up, value); }
    public string UpLatency { get => _upLoaded; private set => Set(ref _upLoaded, value); }
    public string Grade { get => _grade; private set => Set(ref _grade, value); }
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public string DnsStatus { get => _dnsStatus; private set => Set(ref _dnsStatus, value); }
    public bool DnsIntercepted
    {
        get => _dnsIntercepted;
        private set { if (Set(ref _dnsIntercepted, value)) Raise(nameof(ShowDnsRedirect)); }
    }

    // Encrypted DNS is in use: the redirection of normal queries no longer matters, so no warning.
    bool DnsEncrypted
    {
        get => _dnsEncrypted;
        set { if (Set(ref _dnsEncrypted, value)) Raise(nameof(ShowDnsRedirect)); }
    }

    public bool ShowDnsRedirect => DnsIntercepted && !DnsEncrypted;

    public string DnsRedirectText =>
        T("Your provider or router answers every normal DNS query itself. That is why the normal times are about the same, and changing the DNS server the normal way has no effect.")
        + " " + (DnsStore.EncryptionSupported
            ? T("Encrypted DNS is not redirected: the encrypted times show each server's real speed, and Use this switches to that server encrypted.")
            : T("Encrypted DNS is not redirected: the encrypted times show each server's real speed. Switching to encrypted DNS needs Windows 11."));
    public string TempSize { get => _tempSize; private set => Set(ref _tempSize, value); }
    public string ShaderSize { get => _shaderSize; private set => Set(ref _shaderSize, value); }
    public string GameStatus { get => _gameStatus; private set => Set(ref _gameStatus, value); }
    public IReadOnlyList<int> SpeedDurations { get; } = SpeedTest.Durations;

    public int SelectedSpeedDuration
    {
        get => SpeedTest.Durations.Contains(_settings.SpeedTestSeconds) ? _settings.SpeedTestSeconds : SpeedTest.Durations[0];
        set
        {
            if (value == 0 || value == _settings.SpeedTestSeconds) return;
            _settings.SpeedTestSeconds = value;
            _settings.Save();
            Raise(nameof(SelectedSpeedDuration));
        }
    }
    public string PrimaryGpuName { get; private set; } = "GPU";

    public bool Testing
    {
        get => _testing;
        private set
        {
            if (!Set(ref _testing, value)) return;
            Raise(nameof(IsIdleForTest));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIdleForTest => !Testing;

    public bool GameBoost
    {
        get => _settings.GameBoost;
        set
        {
            if (_settings.GameBoost == value) return;
            _settings.GameBoost = value;
            _settings.Save();
            Raise(nameof(GameBoost));
            TickGameWatcher();
        }
    }

    public bool RaisePriority
    {
        get => _settings.RaisePriority;
        set
        {
            if (_settings.RaisePriority == value) return;
            _settings.RaisePriority = value;
            _settings.Save();
            Raise(nameof(RaisePriority));
        }
    }

    public bool AutoStartEnabled
    {
        get => AutoStart.Enabled;
        set
        {
            try { AutoStart.Enabled = value; }
            catch (Exception ex) { Message = F("Could not change the startup setting: {0}", ex.Message); }
            Raise(nameof(AutoStartEnabled));
        }
    }

    public TweakOption? GamePlan
    {
        get => _gamePlan;
        set
        {
            // The drop-down sends an empty selection while its list changes; the choice is kept and shown again.
            if (value is null)
            {
                RaiseLater(nameof(GamePlan));
                return;
            }
            if (!Set(ref _gamePlan, value)) return;
            _settings.GamePlan = value.Key == "auto" ? null : value.Value;
            _settings.Save();
        }
    }

    // Game mode plan: only existing plans (no create options here). Kept in step with the Settings tab, so a plan created
    // or deleted there shows up here too; the chosen plan stays selected.
    void SyncGamePlans(IReadOnlyList<TweakOption> planOptions)
    {
        var plans = planOptions.Where(o => o.Value is not null && !o.Value.StartsWith(PowerPlanStore.CreatePrefix, StringComparison.Ordinal))
            .Select(o => new TweakOption(o.Key, o.Label, o.Value)).ToList();
        if (plans.Count == 0) return; // the plans could not be read this time; keep the list
        if (_settings.GamePlan is { } chosen && !plans.Any(o => string.Equals(o.Value, chosen, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.GamePlan = null; // the plan was deleted: the recommended plan is used
            _settings.Save();
        }
        plans.Insert(0, new TweakOption("auto", T("Recommended plan"), null));
        var selected = plans.FirstOrDefault(o => string.Equals(o.Value, _settings.GamePlan, StringComparison.OrdinalIgnoreCase)) ?? plans[0];
        if (!plans.Select(o => (o.Key, o.Label)).SequenceEqual(GamePlans.Select(o => (o.Key, o.Label))))
        {
            _gamePlan = null;
            GamePlans.Clear();
            foreach (var o in plans) GamePlans.Add(o);
        }
        _gamePlan = GamePlans.First(o => o.Equals(selected));
        Raise(nameof(GamePlan));
    }

    internal AppSettings Settings => _settings;

    void InitTools()
    {
        SystemView = CollectionViewSource.GetDefaultView(SystemItems);
        SystemView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(InfoRow.Group)));

        GamePlans.Add(new TweakOption("auto", T("Recommended plan"), null));
        _gamePlan = GamePlans[0];
        SyncGamePlans(Catalog.Find("power.plan")!.Options());

        SpeedTestCommand = new RelayCommand(async _ => await RunSpeedTestAsync(), _ => !Testing);
        DnsTestCommand = new RelayCommand(async _ => await RunDnsTestAsync(), _ => !Testing);
        CancelTestCommand = new RelayCommand(_ => _testCts?.Cancel(), _ => Testing);
        // One click: the adapter uses the server (the elevated copy also clears the DNS cache, see DnsStore).
        UseDnsCommand = new RelayCommand(async p =>
        {
            if (p is not DnsRow { OptionKey: { } key } dns || Tweaks.FirstOrDefault(r => r.Id == "net.dns") is not { } row || row.Options.FirstOrDefault(o => o.Key == key) is not { } option)
                return;
            await ApplyAsync([(row, option)]);
            if (row.CurrentKey != key) return;
            DnsEncrypted = key.EndsWith("-doh", StringComparison.Ordinal);
            for (int i = 0; i < DnsResults.Count; i++)
            {
                bool inUse = DnsResults[i].Server == dns.Server;
                DnsResults[i] = DnsResults[i] with { InUse = inUse, OptionKey = inUse ? null : DnsOptionKey(DnsResults[i].Server) };
            }
            Message = key.EndsWith("-doh", StringComparison.Ordinal)
                ? F("DNS: {0} ({1}) is in use; the DNS cache was cleared.", dns.Name, T("encrypted"))
                : F("DNS server changed to {0} ({1}); the DNS cache was cleared.", dns.Name, dns.Server);
        }, _ => !Busy);
        RefreshStartupCommand = new RelayCommand(async _ => await RefreshStartupAsync());
        ToggleStartupCommand = new RelayCommand(async p => { if (p is StartupRow row) await ToggleStartupAsync(row); }, _ => !Busy);
        CleanTempCommand = new RelayCommand(async _ => await CleanAsync(temp: true));
        ClearShaderCommand = new RelayCommand(async _ => await CleanAsync(temp: false));
        FlushDnsCommand = new RelayCommand(_ => Message = Maintenance.FlushDns() ? T("DNS cache cleared.") : T("Could not clear the DNS cache."));
        RestorePointCommand = new RelayCommand(async _ => await RunElevatedAsync(["--restorepoint"], T("Restore point")), _ => !Busy);
        CopyReportCommand = new RelayCommand(_ => CopyReport());
    }

    // Data a tab needs is loaded the first time it opens; not everything at startup.
    void OnTabOpened(int tab)
    {
        if (tab == TabProcesses) _ = RefreshProcessManagerAsync();
        if (tab == TabPrograms && !_programsLoaded) _ = LoadProgramsAsync();
        if (tab == TabStartup && !_startupLoaded) _ = RefreshStartupAsync();
        if (tab == TabGame && !_appsLoaded) _ = RefreshRunningAppsAsync();
        if (tab == TabGame) RefreshWindowsGames();
        if (tab == TabGame) RefreshGameBoost();
        if (tab == TabSystem && TempSize == "…") _ = RefreshSizesAsync();
        if (tab == TabLatency) _ = RefreshInterruptsAsync();
    }

    // --- Network test ---

    async Task RunSpeedTestAsync()
    {
        Testing = true;
        _testCts = new CancellationTokenSource();
        IdleLatency = DownSpeed = DownLatency = UpSpeed = UpLatency = Grade = "…";
        Verdict = "";
        try
        {
            var r = Demo.On ? await Task.Delay(1500).ContinueWith(_ => Demo.Speed())
                : await SpeedTest.RunAsync(new Progress<string>(s => TestStatus = s), SelectedSpeedDuration, _testCts.Token);
            _lastSpeed = r;
            IdleLatency = $"{r.IdleMs:0} ms";
            DownSpeed = $"{r.DownMbps:0} Mbps";
            DownLatency = double.IsNaN(r.DownLoadedMs) ? "–" : $"{r.DownLoadedMs:0} ms (+{Math.Max(0, r.DownLoadedMs - r.IdleMs):0})";
            UpSpeed = $"{r.UpMbps:0} Mbps";
            UpLatency = double.IsNaN(r.UpLoadedMs) ? "–" : $"{r.UpLoadedMs:0} ms (+{Math.Max(0, r.UpLoadedMs - r.IdleMs):0})";
            Grade = r.Grade;
            Verdict = r.Verdict;
            TestStatus = F("Test finished ({0}).", DateTime.Now.ToString("t", L.Culture));
        }
        catch (OperationCanceledException)
        {
            TestStatus = T("Test cancelled.");
            IdleLatency = DownSpeed = DownLatency = UpSpeed = UpLatency = Grade = "–";
        }
        catch (Exception ex)
        {
            TestStatus = F("Test failed: {0}", ex.Message);
            IdleLatency = DownSpeed = DownLatency = UpSpeed = UpLatency = Grade = "–";
        }
        finally
        {
            Testing = false;
        }
    }

    // The DNS setting's option for a tested server, encrypted when Windows supports it (null for a server the setting does not offer).
    static string? DnsOptionKey(string server) =>
        Catalog.Find("net.dns")!.Options().FirstOrDefault(o => o.Value is { } v && v.StartsWith(server + ",", StringComparison.Ordinal)
                                                              && v.EndsWith(DnsStore.EncryptedSuffix, StringComparison.Ordinal) == DnsStore.EncryptionSupported)?.Key;

    static string DnsMs(double ms) => F("{0} ms", ms.ToString(ms < 10 ? "0.0" : "0", L.Culture));

    async Task RunDnsTestAsync()
    {
        Testing = true;
        _testCts = new CancellationTokenSource();
        DnsResults.Clear();
        DnsIntercepted = false;
        try
        {
            var intercepted = Demo.On ? Task.FromResult(false) : DnsBenchmark.InterceptedAsync(_testCts.Token);
            var results = Demo.On ? await Task.Delay(1200).ContinueWith(_ => Demo.Dns())
                : await DnsBenchmark.RunAsync(new Progress<string>(s => DnsStatus = s), _testCts.Token);
            DnsIntercepted = await intercepted;
            DnsEncrypted = await Task.Run(() => Catalog.Find("net.dns")?.Store().Read()?.EndsWith(DnsStore.EncryptedSuffix, StringComparison.Ordinal) == true);
            bool first = true;
            var current = Demo.On ? Demo.DnsServer : DnsBenchmark.Current();
            var currentKey = Tweaks.FirstOrDefault(r => r.Id == "net.dns")?.CurrentKey;
            // With redirected normal DNS only encrypted DNS changes anything: the encrypted times count, and Use turns encryption on.
            bool usable = !DnsIntercepted || DnsStore.EncryptionSupported;
            foreach (var r in results)
            {
                var key = DnsOptionKey(r.Server);
                bool inUse = key is not null ? key == currentKey : r.Server == current;
                if (inUse || !usable) key = null;
                DnsResults.Add(new DnsRow(r.Name, r.Server, double.IsNaN(r.MedianMs) ? T("no response") : DnsMs(r.MedianMs),
                    double.IsNaN(r.FirstMs) ? "" : F("first query {0}", DnsMs(r.FirstMs)),
                    double.IsNaN(r.EncryptedMs) ? "–" : DnsMs(r.EncryptedMs),
                    r.Failures == 0 ? "" : F("{0}/{1} unanswered", r.Failures, r.Queries),
                    first && usable && !double.IsNaN(r.EffectiveMs), key, inUse));
                first = false;
            }
            DnsStatus = DnsIntercepted
                ? T("Normal times are close because the same server answers every normal query; the encrypted times count.")
                : T("Response time of repeated queries (the server's cache); lower is better. The first query of a name can take longer while the server looks it up. Use this switches to the server and clears the DNS cache.");
        }
        catch (OperationCanceledException)
        {
            DnsStatus = T("Test cancelled.");
        }
        finally
        {
            Testing = false;
        }
    }

    // --- Game mode ---

    void TickGameWatcher()
    {
        if (Demo.On)
        {
            GameStatus = T("Waiting for a game");
            return;
        }
        try { _watcher.Tick(_settings); }
        catch (Exception ex) { GameStatus = F("Game mode error: {0}", ex.Message); return; }
        GameStatus = _watcher.Status;
        if (_watcher.ActiveEntry is { } entry) RememberGamePath(entry, _watcher.ActivePath);
        TickGameHealth();
    }


    // --- Startup ---

    async Task RefreshStartupAsync()
    {
        if (Demo.On) return;
        _startupLoaded = true;
        try
        {
            var items = await Task.Run(StartupManager.List);
            StartupItems.Clear();
            foreach (var item in items) StartupItems.Add(new StartupRow(item));
        }
        catch (Exception ex)
        {
            Message = F("Could not read the startup list: {0}", ex.Message);
        }
    }

    async Task ToggleStartupAsync(StartupRow row)
    {
        bool enable = !row.Enabled;
        if (row.Item.NeedsAdmin)
        {
            var name = Convert.ToBase64String(Encoding.UTF8.GetBytes(row.Name));
            await RunElevatedAsync(["--startup", row.Item.Location.ToString(), name, enable ? "1" : "0"], T("Startup"));
        }
        else
        {
            try
            {
                StartupManager.SetEnabled(row.Item.Location, row.Name, enable);
                Message = (enable ? F("{0} enabled at startup", row.Name) : F("{0} disabled at startup", row.Name)) + ". " + T("Takes effect at the next sign-in.");
            }
            catch (Exception ex)
            {
                Message = F("Could not change: {0}", ex.Message);
            }
        }
        await RefreshStartupAsync();
    }

    // --- System info and maintenance ---

    async Task RefreshSystemAsync()
    {
        var games = KnownGamePaths();
        var items = Demo.On ? Demo.Hardware() : await Task.Run(() => HardwareInfo.Collect(games));
        SystemItems.Clear();
        foreach (var item in items) SystemItems.Add(new InfoRow(item));
        var gpus = items.Where(i => i.Group == HardwareInfo.GroupGpu && i.Label != HardwareInfo.LabelDisplay && i.Label != HardwareInfo.LabelShader).Select(i => i.Label).ToList();
        PrimaryGpuName = gpus.FirstOrDefault(n => !n.Contains("Intel", StringComparison.OrdinalIgnoreCase)) ?? gpus.FirstOrDefault() ?? "GPU";
    }

    async Task RefreshSizesAsync()
    {
        var (temp, shader) = Demo.On ? (840L << 20, 1210L << 20) : await Task.Run(() => (Maintenance.TempSize(), Maintenance.ShaderCacheSize()));
        TempSize = Maintenance.Size(temp);
        ShaderSize = Maintenance.Size(shader);
    }

    async Task CleanAsync(bool temp)
    {
        var title = temp ? T("Clean temporary files") : T("Clear shader cache");
        var text = temp
            ? F("Files older than 1 day in the temporary files folder will be deleted (about {0}). Files in use are skipped.", TempSize)
            : F("The DirectX, NVIDIA, AMD and Intel shader caches will be deleted ({0}). Games recompile shaders on first launch, with short hitches meanwhile. Use this only if a corrupt cache causes stutter.", ShaderSize);
        if (!Dialogs.Confirm(title, text, T("Clean"), danger: true)) return;
        var r = await Task.Run(() => temp ? Maintenance.CleanTemp() : Maintenance.ClearShaderCaches());
        Message = F("{0} freed ({1} file|{0} freed ({1} files", Maintenance.Size(r.FreedBytes), r.Files) + (r.Skipped > 0 ? ", " + F("{0} in use, skipped", r.Skipped) : "") + ")";
        await RefreshSizesAsync();
    }

    void CopyReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine(F("{0} report {1}", AppPaths.Name, DateTime.Now.ToString("g", L.Culture)));
        foreach (var g in SystemItems.GroupBy(i => i.Group))
        {
            sb.AppendLine($"\n[{g.Key}]");
            foreach (var i in g) sb.AppendLine($"{i.Label}: {i.Value}");
        }
        sb.AppendLine();
        sb.Append(Elevated.Report());
        try { Clipboard.SetText(sb.ToString()); Message = T("Report copied to the clipboard."); }
        catch (Exception ex) { Message = F("Could not copy to the clipboard: {0}", ex.Message); }
    }
}
