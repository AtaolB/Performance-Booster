using System.ComponentModel;

namespace PerformanceBooster.Launcher;

// The .NET Desktop Runtime the app is built for: found where the app's host looks for it, installed from Microsoft.
static class Runtime
{
    const int Major = 10;
    // Always the newest .NET 10 Windows Desktop Runtime (it forwards to builds.dotnet.microsoft.com).
    const string InstallerUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
    const string ManualPage = "https://dotnet.microsoft.com/download/dotnet/10.0";

    public static bool IsInstalled()
    {
        foreach (var root in Roots())
        {
            var desktop = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
            if (Directory.Exists(desktop) && Directory.EnumerateDirectories(desktop, Major + ".*").Any() && Directory.Exists(Path.Combine(root, "host", "fxr")))
                return true;
        }
        return false;
    }

    static IEnumerable<string> Roots()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } custom) yield return custom;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
    }

    // Downloads the installer (progress window, Cancel), checks that Microsoft signed it and runs it with its own progress
    // window and nothing to click. True when the runtime is there afterwards.
    public static bool Install()
    {
        var file = Path.Combine(Path.GetTempPath(), $"PerformanceBooster-windowsdesktop-runtime-{Major}.exe");
        try
        {
            using (var download = new Download(InstallerUrl, file))
            {
                if (!Dialogs.Progress(download))
                {
                    if (download.Error is { } error) Dialogs.Error(Texts.F("The download failed: {0}", error) + "\n\n" + Manual());
                    return false;
                }
            }
            if (!Signature.IsMicrosoft(file))
            {
                Dialogs.Error(Texts.T("The downloaded file is not signed by Microsoft, so it was not run.") + "\n\n" + Manual());
                return false;
            }
            int code = Processes.Run(file, " /install /passive /norestart");
            if (code == 1602) return false; // cancelled in the installer window
            // 3010 / 1641: done, Windows wants a restart for files that were in use; the runtime works already.
            if (code is not (0 or 3010 or 1641))
            {
                Dialogs.Error(Texts.F("The installation did not finish (code {0}).", code) + "\n\n" + Manual());
                return false;
            }
            if (!IsInstalled())
            {
                Dialogs.Error(Texts.T("The .NET 10 Desktop Runtime is still missing after the installation.") + "\n\n" + Manual());
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(Texts.F("The download failed: {0}", ex.Message) + "\n\n" + Manual());
            return false;
        }
        finally
        {
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static string Manual() => Texts.F("You can install it yourself from {0} and start Performance Booster again.", ManualPage);
}
