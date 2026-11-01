using System.Runtime.InteropServices;

namespace PerformanceBooster.Core;

// Which cores the game needs and which ones the slowed-down background apps can have while it runs:
// - processors with performance and efficiency cores: the background gets the efficiency cores;
// - processors whose core groups have different L3 caches (two dies, one with extra cache): the background gets
//   the group with the smaller cache, the game keeps the large one.
// On other processors all cores are alike, so there is nothing to split (Current is null).
public static class CpuTopology
{
    public enum Kind { Hybrid, Cache }

    public sealed record Layout(Kind Kind, IReadOnlyList<uint> BackgroundSets, int GameCores, int BackgroundCores, long GameCacheMb, long BackgroundCacheMb);

    // One logical processor: its CPU set ID, core, efficiency class (higher = faster cores) and the size of its L3 cache.
    internal sealed record Cpu(uint Id, int Group, int Logical, int Core, int EfficiencyClass, long L3Bytes);

    static readonly Lazy<Layout?> _current = new(() => Plan(Read()));

    public static Layout? Current => _current.Value;

    internal static Layout? Plan(IReadOnlyList<Cpu> cpus)
    {
        if (cpus.Count < 4) return null;
        static int Cores(IEnumerable<Cpu> c) => c.Select(x => (x.Group, x.Core)).Distinct().Count();

        if (cpus.Select(c => c.EfficiencyClass).Distinct().Count() > 1)
        {
            int slowest = cpus.Min(c => c.EfficiencyClass);
            var background = cpus.Where(c => c.EfficiencyClass == slowest).ToList();
            var game = cpus.Where(c => c.EfficiencyClass != slowest).ToList();
            return new Layout(Kind.Hybrid, background.Select(c => c.Id).ToList(), Cores(game), Cores(background), 0, 0);
        }

        var sizes = cpus.Select(c => c.L3Bytes).Where(s => s > 0).Distinct().ToList();
        if (sizes.Count > 1 && cpus.All(c => c.L3Bytes > 0))
        {
            long large = sizes.Max();
            var background = cpus.Where(c => c.L3Bytes != large).ToList();
            var game = cpus.Where(c => c.L3Bytes == large).ToList();
            return new Layout(Kind.Cache, background.Select(c => c.Id).ToList(), Cores(game), Cores(background), large >> 20, background.Max(c => c.L3Bytes) >> 20);
        }
        return null;
    }

    // This computer's logical processors (tests use their CPU set IDs).
    internal static List<Cpu> Read()
    {
        var caches = L3Caches();
        var list = new List<Cpu>();
        Native.GetSystemCpuSetInformation(IntPtr.Zero, 0, out var length, IntPtr.Zero, 0);
        if (length == 0) return list;
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!Native.GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0)) return list;
            // SYSTEM_CPU_SET_INFORMATION: Size, Type, Id (8), Group (12), LogicalProcessorIndex (14), CoreIndex (15),
            // LastLevelCacheIndex (16), NumaNodeIndex (17), EfficiencyClass (18).
            for (int offset = 0; offset < length;)
            {
                int size = Marshal.ReadInt32(buffer, offset);
                if (size <= 0) break;
                uint id = (uint)Marshal.ReadInt32(buffer, offset + 8);
                int group = Marshal.ReadInt16(buffer, offset + 12), logical = Marshal.ReadByte(buffer, offset + 14);
                long l3 = caches.FirstOrDefault(c => c.Group == group && (c.Mask & (1UL << logical)) != 0).Size;
                list.Add(new Cpu(id, group, logical, Marshal.ReadByte(buffer, offset + 15), Marshal.ReadByte(buffer, offset + 18), l3));
                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    // The L3 caches with the processors that share each one.
    static List<(int Group, ulong Mask, long Size)> L3Caches()
    {
        var list = new List<(int, ulong, long)>();
        uint length = 0;
        Native.GetLogicalProcessorInformationEx(Native.RelationCache, IntPtr.Zero, ref length);
        if (length == 0) return list;
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!Native.GetLogicalProcessorInformationEx(Native.RelationCache, buffer, ref length)) return list;
            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship, Size, then CACHE_RELATIONSHIP: Level (8), CacheSize (12),
            // GroupMask.Mask (40), GroupMask.Group (48).
            for (int offset = 0; offset < length;)
            {
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0) break;
                if (Marshal.ReadByte(buffer, offset + 8) == 3)
                    list.Add((Marshal.ReadInt16(buffer, offset + 48), (ulong)Marshal.ReadInt64(buffer, offset + 40), (uint)Marshal.ReadInt32(buffer, offset + 12)));
                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }
}
