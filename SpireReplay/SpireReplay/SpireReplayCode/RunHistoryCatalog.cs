using System.Text.Json;

namespace SpireReplay.SpireReplayCode;

public sealed record RunHistoryEntry(RunRecording Recording, DateTimeOffset StartedAt)
{
    public string Label(int version) => $"版本 {version} · {StartedAt.ToLocalTime():MM-dd HH:mm} · 录至 {Recording.Rooms.Max(r => r.Floor)} 层 · {RunHistoryCatalog.Status(Recording.Status)}";
}

public static class RunHistoryCatalog
{
    public static string Status(string status) => status switch
    { "won" => "已通关", "lost" => "已结束", _ => "未完成" };

    // Read-only snapshots: selecting history never redirects the active recorder.
    public static List<RunHistoryEntry> Find(string root, RunRecording current, Action<string>? warning = null)
    {
        var result = new List<RunHistoryEntry>();
        string directory = Path.Combine(root, "runs");
        if (!Directory.Exists(directory)) return result;
        foreach (string folder in Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
        {
            string path = Path.Combine(folder, "run.json");
            if (!File.Exists(path)) continue;
            try
            {
                var run = JsonSerializer.Deserialize<RunRecording>(File.ReadAllText(path), ReplayJson.Options);
                if (run == null || run.FormatVersion != 1 || string.IsNullOrEmpty(run.RunId) || run.RunId == current.RunId ||
                    !string.Equals(run.Seed, current.Seed, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(run.CharacterId, current.CharacterId, StringComparison.OrdinalIgnoreCase) ||
                    run.Rooms == null || run.Rooms.Count == 0 || run.Areas == null || result.Any(r => r.Recording.RunId == run.RunId)) continue;
                CancelledSelectionFilter.Clean(run);
                RecordingLocation.NormalizeArchitect(run);
                result.Add(new RunHistoryEntry(run, run.StoredAtUtc ?? run.StartedAt ?? new DateTimeOffset(File.GetCreationTimeUtc(path))));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            { warning?.Invoke($"跳过无法读取的历史记录 {path}: {e.Message}"); }
        }
        return result.OrderBy(r => r.StartedAt).ThenBy(r => r.Recording.RunId, StringComparer.Ordinal).ToList();
    }
}
