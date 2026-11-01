namespace PerformanceBooster.Core;

public sealed record DriverUpdate(string Id, string Title, string Class, string Manufacturer, DateTime? Date, double SizeMb, bool Optional)
{
    public bool IsDisplay => Class.Equals("Display", StringComparison.OrdinalIgnoreCase);
}

// Windows Update Agent (COM): pending driver updates. Searching needs no admin rights, installing does.
public static class DriverUpdates
{
    const string Criteria = "IsInstalled=0 and Type='Driver' and IsHidden=0";

    public static List<DriverUpdate> Search()
    {
        dynamic session = CreateSession();
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.Online = true; // asks Windows Update every time instead of the last downloaded list
        dynamic result = searcher.Search(Criteria);
        dynamic updates = result.Updates;
        var list = new List<DriverUpdate>();
        int count = updates.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic u = updates.Item(i);
            list.Add(new DriverUpdate(
                (string)u.Identity.UpdateID,
                (string)u.Title,
                Try(() => (string?)u.DriverClass) ?? "",
                Try(() => (string?)u.DriverManufacturer) ?? Try(() => (string?)u.DriverProvider) ?? "",
                Try<DateTime?>(() => (DateTime)u.DriverVerDate),
                Try(() => (double)(decimal)u.MaxDownloadSize) / 1048576.0,
                Try(() => (bool)u.BrowseOnly)));
        }
        return list.OrderBy(d => d.IsDisplay).ThenBy(d => d.Class).ThenBy(d => d.Title).ToList();
    }

    // Called in the elevated copy.
    public static (List<string> Log, bool Reboot, int Failures) Install(ISet<string> ids)
    {
        var log = new List<string>();
        dynamic session = CreateSession();
        dynamic found = session.CreateUpdateSearcher().Search(Criteria).Updates;
        dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", true)!)!;
        var titles = new List<string>();
        int count = found.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic u = found.Item(i);
            if (!ids.Contains((string)u.Identity.UpdateID)) continue;
            if (!(bool)u.EulaAccepted) u.AcceptEula();
            collection.Add(u);
            titles.Add((string)u.Title);
        }
        if (titles.Count == 0)
        {
            log.Add($"{T("SKIPPED")} {T("The selected drivers are no longer among the pending updates.")}");
            return (log, false, 0);
        }

        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = collection;
        downloader.Download();

        dynamic installer = session.CreateUpdateInstaller();
        installer.Updates = collection;
        dynamic result = installer.Install();

        int failures = 0;
        for (int i = 0; i < titles.Count; i++)
        {
            int code = (int)result.GetUpdateResult(i).ResultCode; // 2 succeeded, 3 succeeded with errors, 4 failed, 5 aborted
            bool ok = code is 2 or 3;
            if (!ok) failures++;
            log.Add($"{(ok ? T("OK   ") : T("ERROR"))} {titles[i]}" + (ok ? "" : " " + F("(result code {0})", code)));
        }
        return (log, (bool)result.RebootRequired, failures);
    }

    static dynamic CreateSession()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session", true)!;
        dynamic session = Activator.CreateInstance(type)!;
        session.ClientApplicationID = AppPaths.Name;
        return session;
    }

    static TResult? Try<TResult>(Func<TResult> read)
    {
        try { return read(); }
        catch (Exception) { return default; }
    }
}

// Id/Installed/Available: for winget packages; compared to avoid offering the same update again.
// Hidden: entry removed from the list with "Clear"; kept for the hold rules (Edge, App Installer).
public sealed record HistoryEntry(DateTime At, string Name, string Change, bool Ok, string Message,
    string? Id = null, string? Installed = null, string? Available = null, bool Hidden = false);

// Update history: the last 100 operations.
public static class UpdateHistory
{
    public static List<HistoryEntry> Load() => AppPaths.ReadJson(AppPaths.History, AppJson.Default.History) ?? [];

    public static void Save(List<HistoryEntry> entries)
    {
        try { AppPaths.WriteJson(AppPaths.History, entries.OrderByDescending(e => e.At).Take(100).ToList(), AppJson.Default.History); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
