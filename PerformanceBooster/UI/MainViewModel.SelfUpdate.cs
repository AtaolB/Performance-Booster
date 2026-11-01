using System.Windows.Input;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

// Updates of the app itself from GitHub releases: checked at startup and every 6 hours with the other updates (in the tray too).
// A newer release is downloaded in the background. It is installed when the user restarts from the banner, at the next start,
// or by itself while the app waits in the tray with nothing running (when automatic updates are on).
public sealed partial class MainViewModel
{
    AppRelease? _release;
    string? _releaseFile;
    bool _checkingSelf, _downloadingSelf, _installingSelf, _autoInstallFailed, _inTray;
    string _selfUpdateStatus = T("Not checked yet.");
    double _selfUpdateProgress;

    // A new version was downloaded and can be installed (App: notification while in the tray).
    public event Action<AppRelease>? SelfUpdateReady;

    // The new exe is in place: the app exits and starts it (minimized: back to the tray).
    public event Action<bool>? SelfUpdateRestartRequested;

    public ICommand CheckSelfUpdateCommand { get; private set; } = null!;
    public ICommand InstallSelfUpdateCommand { get; private set; } = null!;
    public ICommand ReleaseNotesCommand { get; private set; } = null!;
    public ICommand VersionHistoryCommand { get; private set; } = null!;

    public string AppVersionText => F("Version {0}", SelfUpdate.Format(SelfUpdate.Current));
    public string SelfUpdateStatus { get => _selfUpdateStatus; private set => Set(ref _selfUpdateStatus, value); }
    public double SelfUpdateProgress { get => _selfUpdateProgress; private set { if (Set(ref _selfUpdateProgress, value)) Raise(nameof(SelfUpdateProgressText)); } }
    public string SelfUpdateProgressText => Pct(SelfUpdateProgress);
    public bool SelfUpdateDownloading { get => _downloadingSelf; private set { if (Set(ref _downloadingSelf, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool SelfUpdateReadyToInstall => _releaseFile is not null;
    public string SelfUpdateReadyText => _release is null ? "" : F("Performance Booster {0} is ready.", SelfUpdate.Format(_release.Version));
    public string SelfUpdateReadyHint => AutoUpdateApp ? T("Restart now, or it is installed at the next start.") : T("Restart the app to install it.");
    public bool HasReleaseNotes => _release is not null;

    public bool AutoUpdateApp
    {
        get => _settings.AutoUpdateApp;
        set
        {
            if (_settings.AutoUpdateApp == value) return;
            _settings.AutoUpdateApp = value;
            _settings.Save();
            Raise(nameof(AutoUpdateApp));
            Raise(nameof(SelfUpdateReadyHint));
        }
    }

    void InitSelfUpdate()
    {
        CheckSelfUpdateCommand = new RelayCommand(async _ => await CheckSelfUpdateAsync(), _ => !_checkingSelf && !_downloadingSelf && !_installingSelf);
        InstallSelfUpdateCommand = new RelayCommand(async _ => await InstallSelfUpdateAsync(minimized: false),
            _ => SelfUpdateReadyToInstall && !_installingSelf && !Updating && !Busy);
        ReleaseNotesCommand = new RelayCommand(_ =>
        {
            if (_release is not { } r) return;
            var sections = Changelog.FromRelease(r.Notes, L.Code);
            Dialogs.Changes(F("What's new in {0}", SelfUpdate.Format(r.Version)), T("The changes this version brings."),
                sections.Count > 0 ? [new ChangelogEntry(r.Version, null, sections)] : [], r.PageUrl);
        });
        VersionHistoryCommand = new RelayCommand(_ =>
            Dialogs.Changes(T("Version history"), F("You have version {0}.", SelfUpdate.Format(SelfUpdate.Current)),
                Changelog.Between(Changelog.Embedded(), null, SelfUpdate.Current), SelfUpdate.ReleasesPage));
    }

    // Shown once after the app restarted into a new version.
    public void NotifyUpdated() => Message = F("Performance Booster was updated to version {0}.", SelfUpdate.Format(SelfUpdate.Current));

    List<ChangelogEntry>? _whatsNew;

    // At start: if this version is newer than the one that ran last, its notes (and those of skipped versions) are shown once
    // the window is open. A first start shows nothing; an update from a version that did not record itself shows this version.
    public void PrepareWhatsNew()
    {
        var current = SelfUpdate.Format(SelfUpdate.Current);
        if (_settings.LastRunVersion == current) return;
        var entries = Changelog.AfterUpdate(Changelog.Embedded(), _settings.LastRunVersion, _settings.FromFile, SelfUpdate.Current);
        _settings.LastRunVersion = current;
        _settings.Save();
        if (entries.Count > 0) _whatsNew = entries;
    }

    // Called when the window is shown (at start, or later from the tray).
    public void ShowPendingWhatsNew()
    {
        if (_whatsNew is not { } entries) return;
        _whatsNew = null;
        Dialogs.Changes(F("Performance Booster {0}", SelfUpdate.Format(SelfUpdate.Current)),
            entries.Count > 1 ? T("The app was updated. What came with the new versions:") : T("The app was updated. What came with this version:"), entries);
    }

    async Task CheckSelfUpdateAsync()
    {
        if (Demo.On)
        {
            SelfUpdateStatus = F("Last check {0}", DateTime.Now.ToString("t", L.Culture)) + " · " + T("Performance Booster is up to date.");
            return;
        }
        if (_checkingSelf || _downloadingSelf || _installingSelf) return;
        _checkingSelf = true;
        CommandManager.InvalidateRequerySuggested();
        SelfUpdateStatus = T("Looking for a new version…");
        try
        {
            var release = await SelfUpdate.LatestAsync(CancellationToken.None);
            if (release.Version <= SelfUpdate.Current)
            {
                SetRelease(null, null);
                SelfUpdateStatus = F("Last check {0}", DateTime.Now.ToString("t", L.Culture)) + " · " + T("Performance Booster is up to date.");
                return;
            }
            var version = SelfUpdate.Format(release.Version);
            if (release.Asset is null || !SelfUpdate.CanInstall)
            {
                SetRelease(release, null);
                SelfUpdateStatus = F("Version {0} is available.", version) + " "
                                   + (release.Asset is null ? T("The release has no exe to download; get it from the release page.") : T("This is a development build; get the new version from the release page."));
                return;
            }
            if (_releaseFile is not null && _release?.Version == release.Version)
            {
                SelfUpdateStatus = F("Version {0} is downloaded and ready to install.", version);
                return;
            }

            SetRelease(release, null);
            SelfUpdateDownloading = true;
            SelfUpdateProgress = 0;
            SelfUpdateStatus = F("Downloading version {0}…", version);
            var file = await SelfUpdate.DownloadAsync(release, new Progress<double>(p => SelfUpdateProgress = p), CancellationToken.None);
            SetRelease(release, file);
            _autoInstallFailed = false;
            SelfUpdateStatus = F("Version {0} is downloaded and ready to install.", version);
            SelfUpdateReady?.Invoke(release);
        }
        catch (Exception ex)
        {
            SelfUpdateStatus = F("Update check failed: {0}", ex.Message);
        }
        finally
        {
            _checkingSelf = false;
            SelfUpdateDownloading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    void SetRelease(AppRelease? release, string? file)
    {
        _release = release;
        _releaseFile = file;
        foreach (var n in new[] { nameof(SelfUpdateReadyToInstall), nameof(SelfUpdateReadyText), nameof(HasReleaseNotes) }) Raise(n);
        CommandManager.InvalidateRequerySuggested();
    }

    // In the tray with nothing running (no update, change, check or game): the new version installs itself.
    // An installer that would need administrator rights is left to the user, so no permission prompt appears out of nowhere.
    void TryAutoInstallSelfUpdate()
    {
        if (_releaseFile is null || !AutoUpdateApp || !_inTray || _installingSelf || _autoInstallFailed) return;
        if (Busy || Updating || CheckingUpdates || CheckingDrivers || _checkingSelf || _watcher.ActiveGame is not null) return;
        _ = InstallSelfUpdateAsync(minimized: true);
    }

    async Task InstallSelfUpdateAsync(bool minimized)
    {
        if (_releaseFile is not { } file || _installingSelf || Environment.ProcessPath is not { } target) return;
        _installingSelf = true;
        CommandManager.InvalidateRequerySuggested();
        SelfUpdateStatus = T("Installing the update…");
        try
        {
            try
            {
                SelfUpdate.Replace(file, target);
            }
            catch (UnauthorizedAccessException) when (!minimized)
            {
                // The exe is in a folder that needs administrator rights (like Program Files).
                var outcome = await Elevated.RunAsync("--replace-exe", file, target);
                if (!outcome.Started || outcome.ExitCode != 0)
                {
                    SelfUpdateStatus = outcome.Started ? string.Join(" · ", outcome.Log) : T("Cancelled: administrator permission was not granted.");
                    return;
                }
            }
            SelfUpdateRestartRequested?.Invoke(minimized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _autoInstallFailed = minimized;
            SelfUpdateStatus = F("The update could not be installed: {0}", ex.Message);
        }
        finally
        {
            _installingSelf = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
