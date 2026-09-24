using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SpireReplay.SpireReplayCode;

/// <summary>Replace a complete snapshot atomically, preserving the last good JSON on write failure.</summary>
public sealed class RecordingFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string FilePath { get; }
    public BattleRecording Recording { get; }

    public RecordingFile(string directory, BattleRecording recording)
    {
        Directory.CreateDirectory(directory);
        Recording = recording;
        // The seed is intentionally not used in paths; custom seeds may contain invalid characters.
        FilePath = Path.Combine(directory,
            $"battle-{recording.BattleNumber:D3}-floor-{recording.TotalFloor:D2}-{recording.BattleId}.json");
        Save();
    }

    public void Append(RecordedEvent entry)
    {
        entry.Sequence = Recording.Events.Count + 1;
        Recording.Events.Add(entry);
        Save();
    }

    public void Finish(string status)
    {
        Recording.Status = status;
        Recording.EndedAtUtc = DateTimeOffset.UtcNow;
        Save();
    }

    private void Save() => WriteAtomic(FilePath, Recording);

    internal static void WriteAtomic<T>(string path, T data)
    {
        string json = JsonSerializer.Serialize(data, Options);
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}

public sealed class ActivityJournalFile
{
    public string FilePath { get; }
    public ActivityJournal Journal { get; }

    public ActivityJournalFile(string directory, ActivityJournal journal)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "activities.json");
        Journal = journal;
        RecordingFile.WriteAtomic(FilePath, Journal);
    }

    public void Append(RecordedEvent entry)
    {
        entry.Sequence = Journal.Events.Count + 1;
        Journal.Events.Add(entry);
        RecordingFile.WriteAtomic(FilePath, Journal);
    }
}
