using System.Text;

namespace PerformanceBooster.Tests;

sealed class TestFailure(string message) : Exception(message);

sealed class TestSkipped(string message) : Exception(message);

static class Check
{
    public static void True(bool condition, string what)
    {
        if (!condition) throw new TestFailure(what);
    }

    public static void Equal<TValue>(TValue expected, TValue actual, string what)
    {
        if (!EqualityComparer<TValue>.Default.Equals(expected, actual))
            throw new TestFailure($"{what}: expected «{expected}», got «{actual}»");
    }

    public static void Empty(IEnumerable<string> problems, string what)
    {
        var list = problems.ToList();
        if (list.Count > 0) throw new TestFailure($"{what} ({list.Count}):\n        " + string.Join("\n        ", list.Take(300)));
    }

    public static TException Throws<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new TestFailure($"{what}: expected {typeof(TException).Name}, got {ex.GetType().Name} ({ex.Message})");
        }
        throw new TestFailure($"{what}: expected an exception");
    }

    public static void Skip(string why) => throw new TestSkipped(why);
}

// Tests that do not change system settings: fake stores, temporary registry keys, temporary folders and fake processes.
static class Runner
{
    static int _passed, _failed, _skipped;
    static readonly List<string> Failures = [];
    static StreamWriter? _log;

    public static string Root { get; } = FindRoot();
    public static string Data { get; private set; } = "";
    public static string Out { get; private set; } = "";
    public static bool Network { get; private set; } = true;
    public static string? Exe { get; private set; }

    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Network = !args.Contains("--no-net");
        var only = Arg(args, "--only");
        Out = Arg(args, "--out") ?? Path.Combine(Path.GetTempPath(), "PerformanceBoosterTests-out");
        Exe = Arg(args, "--exe") ?? Path.Combine(Root, @"PerformanceBooster\bin\Debug\net10.0-windows\win-x64\PerformanceBooster.exe");
        Directory.CreateDirectory(Out);
        _log = new StreamWriter(Path.Combine(Out, "test-log.txt"), false, new UTF8Encoding(false)) { AutoFlush = true };

        Data = Path.Combine(Path.GetTempPath(), $"PerformanceBoosterTests-{Environment.ProcessId}");
        Directory.CreateDirectory(Data);
        AppPaths.FolderOverride = Data;
        WindowsGames.Override = () => []; // the games of this computer never take part (a test sets its own list)
        L.Init("en");

        var clock = Stopwatch.StartNew();
        (string Name, Action Body)[] sections =
        [
            ("Localization", LocalizationTests.Run),
            ("Engine", EngineTests.Run),
            ("Updates", UpdateTests.Run),
            ("AppUpdate", SelfUpdateTests.Run),
            ("Changelog", ChangelogTests.Run),
            ("System", SystemTests.Run),
            ("GameBoost", GameBoostTests.Run),
            ("Latency", LatencyTests.Run),
            ("Network", NetworkTests.Run),
            ("Programs", ProgramTests.Run),
            ("Details", DetailTests.Run),
            ("App", AppTests.Run),
        ];
        try
        {
            foreach (var (name, body) in sections)
            {
                if (only is not null && !name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                Write("");
                Write($"== {name} ==");
                body();
            }
        }
        finally
        {
            Fixtures.Cleanup();
            try { Directory.Delete(Data, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        Write("");
        Write($"Result: {_passed} passed, {_failed} failed, {_skipped} skipped · {clock.Elapsed.TotalSeconds:0} s");
        foreach (var f in Failures) Write("  FAILED " + f);
        _log.Dispose();
        return _failed == 0 ? 0 : 1;
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static readonly List<string> _notes = [];

    public static void Test(string name, Action body)
    {
        var clock = Stopwatch.StartNew();
        _notes.Clear();
        try
        {
            body();
            _passed++;
            Write($"  OK    {name} ({clock.ElapsedMilliseconds} ms)");
        }
        catch (TestSkipped s)
        {
            _skipped++;
            Write($"  SKIP  {name}: {s.Message}");
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add(name);
            var detail = ex is TestFailure ? ex.Message : ex.ToString();
            Write($"  FAIL  {name}: {detail}");
        }
        foreach (var note in _notes) Write($"        · {note}");
    }

    public static void Test(string name, Func<Task> body) => Test(name, () => body().GetAwaiter().GetResult());

    // Info line written under the test result.
    public static void Note(string text) => _notes.Add(text);

    public static void Write(string line)
    {
        Console.WriteLine(line);
        _log?.WriteLine(line);
    }

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PerformanceBooster", "PerformanceBooster.csproj"))) return dir.FullName;
        throw new InvalidOperationException("Source folder not found");
    }
}

// Temporary registry keys and processes created by tests; cleaned up even if a test stops halfway.
static class Fixtures
{
    public const string TestKey = @"Software\PerformanceBoosterTests";
    static readonly List<Action> Undo = [];

    public static void OnCleanup(Action undo) => Undo.Add(undo);

    public static void Cleanup()
    {
        for (int i = Undo.Count - 1; i >= 0; i--)
        {
            try { Undo[i](); } catch (Exception) { }
        }
        Undo.Clear();
    }

    // A copy of ping.exe: a harmless fake process that runs for a while without opening a window.
    public static Process StartFake(string folder, string exeName, int seconds = 60)
    {
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, exeName);
        if (!File.Exists(exe)) File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        var p = Process.Start(new ProcessStartInfo(exe, $"-n {seconds} 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        // A child takes the priority of a parent that runs low (a build agent, a background shell); a real app starts normal.
        p.PriorityClass = ProcessPriorityClass.Normal;
        OnCleanup(() => { if (!p.HasExited) p.Kill(); p.Dispose(); });
        return p;
    }
}

sealed class FakeStore(string? value, bool available = true) : ISettingStore
{
    public string? Value { get; private set; } = value;
    public int Writes { get; private set; }
    public bool Available => available;
    public string? Read() => Value;

    public void Write(string? value)
    {
        Value = value;
        Writes++;
    }
}

sealed class ThrowingStore : ISettingStore
{
    public bool Available => true;
    public string? Read() => throw new InvalidOperationException("read error");
    public void Write(string? value) => throw new InvalidOperationException("write error");
}
