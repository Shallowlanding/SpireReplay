namespace SpireReplay.SpireReplayCode;

// Plain data only: never serialize game objects or their reference graphs.
public sealed class BattleRecording
{
    public string RunId { get; init; } = "";
    public int AttemptNumber { get; set; } = 1;
    public int SchemaVersion { get; init; } = 2;
    public string ModVersion { get; init; } = "0.11.9";
    public string GameVersion { get; init; } = "";
    public string GameAssemblyId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string BattleId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; set; }
    public string Status { get; set; } = "in_progress";
    public string Seed { get; init; } = "";
    public string CharacterId { get; init; } = "";
    public string GameMode { get; init; } = "";
    public int Ascension { get; init; }
    public int Act { get; init; }
    public int ActFloor { get; init; }
    public int TotalFloor { get; init; }
    public int BattleNumber { get; init; }
    public int? RoomId { get; init; }
    public string EncounterId { get; init; } = "";
    public string RoomType { get; init; } = "";
    public List<RecordedEvent> Events { get; init; } = [];
}

public sealed class RecordedEvent
{
    public int Sequence { get; set; }
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Type { get; init; } = "";
    public int Round { get; init; }
    public int? PlayerTurn { get; init; }
    public string Side { get; init; } = "";
    public object? Data { get; init; }
    public BattleSnapshot? State { get; init; }
}

public sealed record CardSnapshot(string ModelId, string Name, uint? CombatCardId, int UpgradeLevel, int? DeckIndex = null);
public sealed record CreatureSnapshot(uint? CombatId, string ModelId, string Name,
    string Side, int Hp, int MaxHp, int Block);
public sealed record BattleSnapshot(int? Energy, int? Stars, List<CardSnapshot> Hand,
    List<CreatureSnapshot> Creatures);
public sealed record TargetSnapshot(uint? CombatId, string ModelId, string Name);
public sealed record CardPlaySnapshot(CardSnapshot Card, TargetSnapshot? Target,
    bool IsAutoPlay, int PlayIndex, int PlayCount, int EnergySpent, int StarsSpent);

public sealed class ActivityJournal
{
    public int SchemaVersion { get; init; } = 2;
    public string ModVersion { get; init; } = "0.11.9";
    public string SessionId { get; init; } = "";
    public string Seed { get; init; } = "";
    public string CharacterId { get; init; } = "";
    public List<RecordedEvent> Events { get; init; } = [];
}





