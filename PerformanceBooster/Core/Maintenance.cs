namespace PerformanceBooster.Core;

public sealed record CleanResult(long FreedBytes, int Files, int Skipped);

public static class Maintenance
{
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static long TempSize() => FolderSize(Path.GetTempPath(), TimeSpan.FromDays(1));

    // Files older than 1 day in the user's TEMP folder. Files in use are skipped.
    public static CleanResult CleanTemp() => Clean(Path.GetTempPath(), TimeSpan.FromDays(1));

    public static IReadOnlyList<string> ShaderCacheFolders() =>
        new[]
        {
            Path.Combine(Local, "D3DSCache"),
            Path.Combine(Local, "NVIDIA", "DXCache"),
            Path.Combine(Local, "NVIDIA", "GLCache"),
            Path.Combine(Local, "AMD", "DxCache"),
            Path.Combine(Local, "AMD", "DxcCache"),
            Path.Combine(Local, "AMD", "VkCache"),
            Path.Combine(Local, "Intel", "ShaderCache"),
        }.Where(Directory.Exists).ToList();

    public static long ShaderCacheSize() => ShaderCacheFolders().Sum(f => FolderSize(f, TimeSpan.Zero));

    // If a corrupt cache causes stutter: games recompile their shaders on first launch.
    public static CleanResult ClearShaderCaches()
    {
        long freed = 0;
        int files = 0, skipped = 0;
        foreach (var folder in ShaderCacheFolders())
        {
            var r = Clean(folder, TimeSpan.Zero);
            freed += r.FreedBytes;
            files += r.Files;
            skipped += r.Skipped;
        }
        return new(freed, files, skipped);
    }

    public static bool FlushDns() => Native.DnsFlushResolverCache();

    internal static CleanResult Clean(string folder, TimeSpan minAge)
    {
        long freed = 0;
        int files = 0, skipped = 0;
        var cutoff = DateTime.Now - minAge;
        foreach (var file in SafeFiles(folder))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime > cutoff) continue;
                long size = info.Length;
                info.Delete();
                freed += size;
                files++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }
        foreach (var dir in SafeDirectories(folder).OrderByDescending(d => d.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new(freed, files, skipped);
    }

    internal static long FolderSize(string folder, TimeSpan minAge)
    {
        var cutoff = DateTime.Now - minAge;
        long total = 0;
        foreach (var file in SafeFiles(folder))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime <= cutoff) total += info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    static IEnumerable<string> SafeFiles(string folder) => Walk(folder, files: true);

    static IEnumerable<string> SafeDirectories(string folder) => Walk(folder, files: false);

    // Everything below the folder; folders that cannot be read and links (reparse points) are skipped.
    static IEnumerable<string> Walk(string folder, bool files)
    {
        if (!Directory.Exists(folder)) yield break;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo dir)
                {
                    pending.Push(dir.FullName);
                    if (!files) yield return dir.FullName;
                }
                else if (files) yield return entry.FullName;
            }
        }
    }

    public static string Size(long bytes) => bytes >= 1073741824
        ? (bytes / 1073741824.0).ToString("0.0", L.Culture) + " GB"
        : (bytes / 1048576.0).ToString("0", L.Culture) + " MB";
}
