using System.Text.Json;

namespace SpireReplay.SpireReplayCode;

public sealed record RunArea(int Act, string Id, string Name);
public sealed class RunRoomRecord
{
    public int Act { get; init; }
    public int Floor { get; init; }
    public int RoomId { get; init; }
    public string Type { get; init; } = "";
    public string ModelId { get; init; } = "";
    public int? MapRow { get; init; }
    public int? MapCol { get; init; }
    public string RevisionId { get; init; } = Guid.NewGuid().ToString("N");
    public List<RecordedEvent> Events { get; init; } = [];
    public BattleRecording? Battle { get; set; }
}
public sealed class RunRecording
{
    public int FormatVersion { get; init; } = 1;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoredAtUtc { get; set; }
    public bool Imported { get; set; }
    public string RunId { get; init; } = "";
    public string Seed { get; init; } = "";
    public string CharacterId { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string GameAssemblyId { get; init; } = "";
    public int Ascension { get; init; }
    public string GameMode { get; init; } = "";
    public List<string>? ModifierIds { get; set; }
    public bool RecordedFromStart { get; init; }
    public string Status { get; set; } = "in_progress";
    public List<RunArea> Areas { get; set; } = [];
    public List<RunRoomRecord> Rooms { get; init; } = [];
}

public sealed class RunArchive
{
    public RunRecording Data { get; }
    public string FilePath { get; }
    public int Revision { get; private set; }
    public RunArchive(string root, RunRecording initial)
    {
        string directory = Path.Combine(root, "runs", ReplayJson.Hash(initial.RunId));
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "run.json");
        Data = File.Exists(FilePath) ? JsonSerializer.Deserialize<RunRecording>(File.ReadAllText(FilePath), ReplayJson.Options)
            ?? throw new InvalidDataException("整局记录无效") : initial;
        if (Data.RunId != initial.RunId) throw new InvalidDataException("整局身份不匹配");
        Data.StoredAtUtc ??= File.Exists(FilePath) ? new DateTimeOffset(File.GetCreationTimeUtc(FilePath)) : DateTimeOffset.UtcNow;
        Data.Areas = initial.Areas;
        Data.ModifierIds = initial.ModifierIds;
        Save();
        RunLibrary.Trim(root, Data.RunId);
    }
    public RunRoomRecord? Find(int act, int floor, int roomId) => Data.Rooms.LastOrDefault(r => r.Act == act && r.Floor == floor && r.RoomId == roomId);
    public void Enter(RunRoomRecord room, bool preserveCompleted, bool baseRoom)
    {
        var old = Find(room.Act, room.Floor, room.RoomId);
        Data.Rooms.RemoveAll(r => r.Act > room.Act || r.Act == room.Act && r.Floor > room.Floor);
        if (preserveCompleted && old != null) { Save(); return; }
        if (!preserveCompleted)
        {
            Data.Rooms.RemoveAll(r => r.Act > room.Act || r.Act == room.Act &&
                (r.Floor > room.Floor || r.Floor == room.Floor && (baseRoom || r.RoomId >= room.RoomId)));
            Data.Status = "in_progress";
        }
        Data.Rooms.Add(room);
        Save();
    }
    public void Append(int act, int floor, int roomId, string? revisionId, RecordedEvent entry)
    {
        var room = Find(act, floor, roomId);
        if (room == null || revisionId != null && room.RevisionId != revisionId) return;
        // Do not share event sequence numbers with the diagnostic session journal.
        var copy = JsonSerializer.Deserialize<RecordedEvent>(JsonSerializer.Serialize(entry, ReplayJson.Options), ReplayJson.Options)!;
        copy.Sequence = room.Events.Count + 1;
        room.Events.Add(copy);
        Save();
    }
    public void SetBattle(BattleRecording battle)
    {
        var room = Find(battle.Act, battle.TotalFloor, battle.RoomId ?? 0);
        if (room == null) return;
        room.Battle = JsonSerializer.Deserialize<BattleRecording>(JsonSerializer.Serialize(battle, ReplayJson.Options), ReplayJson.Options);
        Save();
    }
    public void End(bool victory) { Data.Status = victory ? "won" : "lost"; Save(); }
    private void Save() { RecordingFile.WriteAtomic(FilePath, Data); Revision++; }
}
