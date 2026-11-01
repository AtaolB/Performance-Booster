using System.ComponentModel;
using System.IO.Compression;

namespace PerformanceBooster.Launcher;

// Starts Performance Booster. When the .NET 10 Desktop Runtime is missing it asks, then installs it from Microsoft with
// Windows' own installer window. The app this exe carries then takes the exe's place (a running exe can be renamed), so from
// the next start on the app runs directly; this code runs again only after a new download or an update.
static class Program
{
    [STAThread]
    static int Main()
    {
        try
        {
            if (!Runtime.IsInstalled())
            {
                if (!Dialogs.Ask(Texts.T("Performance Booster needs the .NET 10 Desktop Runtime from Microsoft (about 60 MB).\n\nDownload and install it now? It is installed once, then the app opens."))) return 1;
                if (!Runtime.Install()) return 2;
            }
            Processes.Start(Payload.PutInPlace(), Payload.Arguments());
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or Win32Exception)
        {
            Dialogs.Error(Texts.F("Performance Booster could not be started: {0}", ex.Message));
            return 3;
        }
    }
}

// The app (PerformanceBooster.exe of the main project, compressed into this exe when it is published). Once this exe is renamed,
// .NET can no longer load the parts of it that are not loaded yet (it reads them from the exe's path), so the app is unpacked
// before the rename and after it only File.Move and CreateProcess run (System.Diagnostics.Process would be such a part).
static class Payload
{
    const string AppName = "PerformanceBooster.exe";

    // This exe steps aside as PerformanceBooster.launcher.old.exe (the app deletes it when it starts) and the app takes its name.
    public static string PutInPlace()
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("no exe path");
        var folder = Path.GetDirectoryName(self)!;
        var fresh = Path.Combine(folder, "PerformanceBooster.new.exe");
        var aside = Path.Combine(folder, "PerformanceBooster.launcher.old.exe");
        try
        {
            Unpack(fresh);
            File.Delete(aside);
            File.Move(self, aside);
        }
        catch
        {
            TryDelete(fresh);
            throw;
        }
        try
        {
            File.Move(fresh, self);
            return self;
        }
        catch
        {
            try { File.Move(aside, self); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            TryDelete(fresh);
            throw;
        }
    }

    static void Unpack(string target)
    {
        using var resource = typeof(Payload).Assembly.GetManifestResourceStream("app.bin")
                             ?? throw new InvalidDataException("app.bin is missing: publish this project to embed the app");
        using var app = new DeflateStream(resource, CompressionMode.Decompress);
        using var file = File.Create(target);
        app.CopyTo(file);
    }

    static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // What this exe was given after its own name, passed on as it is (--minimized at sign-in, --restart --updated after an update).
    public static string Arguments()
    {
        var line = Environment.CommandLine;
        int end = line.StartsWith('"') ? line.IndexOf('"', 1) + 1 : line.AsSpan().IndexOfAny(' ', '\t');
        return end <= 0 ? "" : line[end..];
    }
}
