using System.Text.Json.Serialization;

namespace PerformanceBooster.Core;

// Every JSON the app reads or writes: its own files, the language files and what PowerShell returns. The serializers are
// generated at build time, so nothing is built by reflection when the app starts.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(L.Language))]
[JsonSerializable(typeof(Backups.Entry), TypeInfoPropertyName = "Backup")]
[JsonSerializable(typeof(Dictionary<string, Backups.Entry>), TypeInfoPropertyName = "Backups")]
[JsonSerializable(typeof(BackgroundLimiter.Entry), TypeInfoPropertyName = "LimitedApp")]
[JsonSerializable(typeof(List<BackgroundLimiter.Entry>), TypeInfoPropertyName = "LimitedApps")]
[JsonSerializable(typeof(List<HistoryEntry>), TypeInfoPropertyName = "History")]
[JsonSerializable(typeof(Defender.Change))]
[JsonSerializable(typeof(Elevated.LeftoverRequest))]
[JsonSerializable(typeof(GameSession))]
[JsonSerializable(typeof(LatencyResult))]
[JsonSerializable(typeof(CleanerStatus))]
[JsonSerializable(typeof(SelfUpdate.PendingUpdate))]
[JsonSerializable(typeof(Programs.StorePackage))]
[JsonSerializable(typeof(List<Programs.StorePackage>), TypeInfoPropertyName = "StorePackages")]
[JsonSerializable(typeof(Dictionary<string, DateTime>), TypeInfoPropertyName = "LastSeen")]
[JsonSerializable(typeof(string))]
internal sealed partial class AppJson : JsonSerializerContext;
