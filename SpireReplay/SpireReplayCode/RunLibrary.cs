using System.Text.Json;

namespace SpireReplay.SpireReplayCode;

public sealed record StoredRun(RunRecording Recording, DateTimeOffset SavedAt, string FilePath);
public sealed record ImportRunResult(StoredRun Entry, bool AlreadyExists, int Removed);

public static class RunLibrary
{
    public const int Capacity = 10;
    public static int Revision { get; private set; }
    public static List<StoredRun> List(string root)
    {
        string directory = Path.Combine(root, "runs");
        if (!Directory.Exists(directory)) return [];
        var entries = new List<StoredRun>();
        foreach (string dir in Directory.GetDirectories(directory))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            string path = Path.Combine(dir, "run.json");
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                var run = JsonSerializer.Deserialize<RunRecording>(File.ReadAllText(path), ReplayJson.Options);
                if (run == null || run.Rooms == null || run.Rooms.Any(r => r == null) || string.IsNullOrWhiteSpace(run.RunId) || Path.GetFileName(dir) != ReplayJson.Hash(run.RunId)) continue;
                entries.Add(new StoredRun(run, run.StoredAtUtc ?? new DateTimeOffset(File.GetCreationTimeUtc(path)), path));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { /* A damaged file must not block other records. */ }
        }
        return entries.OrderBy(e => e.SavedAt).ThenBy(e => e.Recording.RunId, StringComparer.Ordinal).ToList();
    }
    public static ImportRunResult Import(string root, string code, string? activeRunId = null)
    {
        var run = ReplayCode.Import(code); // Validate before touching storage or pruning history.
        var existing = List(root).FirstOrDefault(e => e.Recording.RunId == run.RunId);
        if (existing != null) return new(existing, true, 0);
        run.StoredAtUtc = DateTimeOffset.UtcNow; run.Imported = true;
        string dir = Path.Combine(root, "runs", ReplayJson.Hash(run.RunId));
        CheckTree(root, dir);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "run.json");
        RecordingFile.WriteAtomic(path, run);
        Revision++;
        int removed = Trim(root, activeRunId);
        return new(new StoredRun(run, run.StoredAtUtc.Value, path), false, removed);
    }
    public static int Trim(string root, string? activeRunId = null)
    {
        var entries = List(root);
        int remove = Math.Max(0, entries.Count - Capacity), removed = 0;
        foreach (var entry in entries.Where(e => e.Recording.RunId != activeRunId).Take(remove))
            if (Delete(root, entry.Recording.RunId, activeRunId)) removed++;
        return removed;
    }
    public static int Clear(string root, string? activeRunId = null)
    {
        int count = 0;
        foreach (var entry in List(root).Where(e => e.Recording.RunId != activeRunId))
            if (Delete(root, entry.Recording.RunId, activeRunId)) count++;
        return count;
    }
    public static bool Delete(string root, string runId, string? activeRunId = null)
    {
        if (runId == activeRunId) throw new InvalidOperationException("当前对局正在录制，请结束或退出对局后再删除");
        string dir = Path.Combine(root, "runs", ReplayJson.Hash(runId));
        string battles = Path.Combine(root, "battles", ReplayJson.Hash(runId));
        CheckTree(root, dir); CheckTree(root, battles);
        bool existed = Directory.Exists(dir);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        if (Directory.Exists(battles)) Directory.Delete(battles, true);
        if (existed) Revision++;
        return existed;
    }
    private static void CheckTree(string root, string target)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullTarget = Path.GetFullPath(target);
        if (!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("拒绝操作记录目录之外的路径");
        var cursor = new DirectoryInfo(fullTarget);
        while (cursor != null)
        {
            if (cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("记录路径包含链接，拒绝清理");
            if (cursor.FullName.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
            cursor = cursor.Parent;
        }
        if (!Directory.Exists(fullTarget)) return;
        foreach (string path in Directory.EnumerateFileSystemEntries(fullTarget))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("记录目录包含链接，拒绝清理");
            if (Directory.Exists(path)) CheckTree(root, path);
        }
    }
}
