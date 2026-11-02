using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using PerformanceBooster.Core;
using PerformanceBooster.UI;

namespace PerformanceBooster;

public partial class App : Application
{
    static string InstanceName => AppPaths.LocalName("Instance");
    static string ShowSignalName => AppPaths.LocalName("Show");

    Mutex? _instance;
    bool _ownsInstance;
    EventWaitHandle? _showSignal;
    RegisteredWaitHandle? _showWait;
    Tray? _tray;
    MainWindow? _window;

    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // UI tests use a separate folder so they never touch the user's settings and backups.
        if (Environment.GetEnvironmentVariable("PERFORMANCEBOOSTER_DATA") is { Length: > 0 } testData) AppPaths.FolderOverride = testData;
        var settings = AppSettings.Current;
        L.Init(settings.Language);
        // Screenshots: a made-up computer, only together with a separate data folder. After the language: its settings
        // are built in it.
        if (AppPaths.FolderOverride is not null && Environment.GetEnvironmentVariable("PERFORMANCEBOOSTER_DEMO") == "1") Demo.Start();

        // Timer resolution helper: holds the finer timer for the app (see TimerResolution), opens no window, exits with the app.
        if (e.Args.Length > 0 && e.Args[0] == "--timer")
        {
            Shutdown(TimerResolution.RunHelper(e.Args));
            return;
        }

        // Network monitor helper (elevated): opens no window, exits when the main app exits.
        if (e.Args.Length > 0 && e.Args[0] == "--netmon")
        {
            Shutdown(NetMonitor.RunHelper(e.Args));
            return;
        }

        // RAM cleaner (elevated, started by its scheduled task while a game runs): opens no window, exits with the game or the app.
        if (e.Args.Length > 0 && e.Args[0] == "--memory-cleaner")
        {
            Shutdown(StandbyCleaner.RunHelper());
            return;
        }

        // Elevated copy: applies the requested change without opening a window, then exits.
        if (e.Args.Length > 0 && Elevated.Commands.Contains(e.Args[0]))
        {
            Shutdown(Elevated.RunCli(e.Args));
            return;
        }

        // Single instance: a second launch brings the running window to the front.
        if (!AcquireInstance(waitForPrevious: e.Args.Contains("--restart")))
        {
            try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(0);
            return;
        }
        // A new version downloaded earlier is installed before the window opens.
        if (settings.AutoUpdateApp && SelfUpdate.CanInstall && SelfUpdate.PendingInstall() is { } pending && InstallPending(pending.File, e.Args.Contains("--minimized")))
            return;

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, -1, false);

        L.ApplyToWpf();
        DispatcherUnhandledException += OnUnhandled;
        RemoveReplacedExe();
        AppPaths.DeleteRequests();
#if DEBUG
        // Binding errors (wrong property name etc.) are written to a file; the UI test checks this file.
        Directory.CreateDirectory(AppPaths.Folder);
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(Path.Combine(AppPaths.Folder, "bindings.log")));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        Trace.AutoFlush = true;
#endif
        if (AppPaths.FolderOverride is null)
            _ = Task.Run(() =>
            {
                try { AutoStart.Refresh(); } catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { }
            });

        _window = new MainWindow();
        MainWindow = _window;
        var vm = _window.ViewModel;
        vm.LanguageChangeRequested += language => Restart(language);
        vm.SelfUpdateRestartRequested += StartNewVersion;

        _tray = new Tray(vm.GameBoost);
        _tray.OpenRequested += ShowWindow;
        _tray.CheckUpdatesRequested += () => _ = vm.CheckAllUpdatesAsync();
        _tray.GameModeToggled += on => vm.GameBoost = on;
        _tray.AutoStartToggled += on => vm.AutoStartEnabled = on;
        vm.TemperatureWarning += (title, text) => _tray.Notify(title, text, () => { ShowWindow(); vm.SelectedTab = MainViewModel.TabSensors; });
        _tray.ExitRequested += ExitApp;
        vm.PropertyChanged += (_, p) =>
        {
            if (p.PropertyName == nameof(MainViewModel.GameBoost)) _tray.SetGameMode(vm.GameBoost);
            if (p.PropertyName == nameof(MainViewModel.StatusText)) _tray.SetTooltip($"{AppPaths.Name} · {vm.StatusText}");
        };
        vm.UpdatesFound += count =>
        {
            if (_window.IsVisible) _window.FlashIfInactive();
            else _tray.Notify(AppPaths.Name, F("{0} update is ready. Click to see it.|{0} updates are ready. Click to see them.", count), () => { ShowWindow(); vm.SelectedTab = MainViewModel.TabUpdates; });
        };
        vm.SelfUpdateReady += release =>
        {
            if (_window.IsVisible) _window.FlashIfInactive();
            else if (!settings.AutoUpdateApp) // otherwise it installs itself while in the tray
                _tray.Notify(AppPaths.Name, F("Performance Booster {0} is ready. Click to update.", SelfUpdate.Format(release.Version)),
                    () => { ShowWindow(); vm.SelectedTab = MainViewModel.TabUpdates; });
        };
        vm.GameSessionEnded += (session, findings) =>
        {
            if (_window.IsActive) return;
            var text = F("{0}: {1}", session.Game, findings[0].Title) + (findings.Count > 1 ? " " + F("(and {0} more)", findings.Count - 1) : "");
            _tray.Notify(T("What held the game back"), text, () => { ShowWindow(); vm.SelectedTab = MainViewModel.TabGame; });
        };
        _window.HiddenToTray += () =>
        {
            if (settings.TrayHintShown) return;
            settings.TrayHintShown = true;
            settings.Save();
            _tray.Notify(AppPaths.Name, T("Still running in the tray; game mode and update checks continue. Right-click the icon to exit."));
        };

        if (e.Args.Contains("--updated"))
        {
            vm.NotifyUpdated();
            if (e.Args.Contains("--minimized")) _tray.Notify(AppPaths.Name, vm.Message);
        }
        vm.PrepareWhatsNew();
        if (e.Args.Contains("--minimized")) vm.SetVisible(false, inTray: true);
        else
        {
            _window.Show();
            Dispatcher.BeginInvoke(vm.ShowPendingWhatsNew, DispatcherPriority.ApplicationIdle);
        }
    }

    bool InstallPending(string file, bool minimized)
    {
        try
        {
            SelfUpdate.Replace(file, Environment.ProcessPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // for example a folder that needs administrator rights: the Updates tab installs it with permission
        }
        StartNewVersion(minimized);
        return true;
    }

    // The new exe is in place: the new version starts (it waits until this copy has released the instance), then this copy
    // exits. Starting first: this exe has been renamed, and .NET can no longer load the parts of it not loaded yet (like
    // System.Management), so a failure while exiting cannot keep the new version from starting.
    void StartNewVersion(bool minimized)
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart --updated" + (minimized ? " --minimized" : "")) { UseShellExecute = false })?.Dispose();
        ExitApp();
    }

    // If a new version is dropped in while the app runs, the running exe is renamed "PerformanceBooster.old.exe"; the launcher
    // that put this exe in place stepped aside as "PerformanceBooster.launcher.old.exe" (after unpacking it as
    // "PerformanceBooster.new.exe"). They are deleted at the next start.
    static void RemoveReplacedExe()
    {
        if (Path.GetDirectoryName(Environment.ProcessPath) is not { } dir) return;
        foreach (var name in (ReadOnlySpan<string>)[AppPaths.Id + ".old.exe", AppPaths.Id + ".launcher.old.exe", AppPaths.Id + ".new.exe"])
        {
            try
            {
                var old = Path.Combine(dir, name);
                if (File.Exists(old)) File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    bool AcquireInstance(bool waitForPrevious)
    {
        _instance = new Mutex(false, InstanceName);
        try
        {
            _ownsInstance = _instance.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsInstance = true;
        }
        return _ownsInstance;
    }

    void ShowWindow()
    {
        if (_window is null || IsExiting) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.ViewModel.SetVisible(true);
        Dispatcher.BeginInvoke(_window.ViewModel.ShowPendingWhatsNew, DispatcherPriority.ApplicationIdle);
    }

    void Restart(string language)
    {
        var settings = AppSettings.Current;
        settings.Language = language;
        settings.Save();
        ExitApp();
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = false });
    }

    void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        _window?.ViewModel.Dispose();
        _tray?.Dispose();
        _window?.Close();
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose();
        _ownsInstance = false;
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Dialogs.Info(T("Unexpected error"), e.Exception.Message, warning: true);
        e.Handled = true;
    }
}
