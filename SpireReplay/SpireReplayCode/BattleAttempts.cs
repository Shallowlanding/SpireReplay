using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpireReplay.SpireReplayCode;

public sealed record ReplayCommand(string Kind, string Fingerprint, CardSnapshot? Card = null,
    uint? TargetId = null, string? TargetModelId = null, int? PotionSlot = null, string? PotionId = null);

public static class ReplayJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public static T Read<T>(object data) => data is T value ? value :
        (data is JsonElement element ? element.Deserialize<T>(Options) :
            JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(data), Options))!;
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Only owns battle files inside this run/battle directory; never deletes session journals.</summary>
public sealed class BattleAttempts
{
    public string DirectoryPath { get; }
    public BattleAttempts(string root, string runId, BattleRecording battle)
    {
        string key = ReplayJson.Hash($"{battle.Act}|{battle.TotalFloor}|{battle.RoomId}|{battle.EncounterId}|{battle.BattleNumber}");
        DirectoryPath = Path.Combine(root, "battles", ReplayJson.Hash(runId), key);
        Directory.CreateDirectory(DirectoryPath);
    }

    public List<BattleRecording> ReadAll() => Directory.EnumerateFiles(DirectoryPath, "battle-*.json")
        .Select(path => JsonSerializer.Deserialize<BattleRecording>(File.ReadAllText(path), ReplayJson.Options)
            ?? throw new InvalidDataException($"无法读取战斗记录：{path}"))
        .OrderBy(b => b.AttemptNumber).ThenBy(b => b.StartedAtUtc).ToList();

    public void KeepFinal(RecordingFile final)
    {
        string root = Path.GetFullPath(DirectoryPath) + Path.DirectorySeparatorChar;
        string keep = Path.GetFullPath(final.FilePath);
        if (!keep.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("记录不属于当前战斗");
        // Final file is already durably saved before earlier attempts are removed.
        foreach (string path in Directory.EnumerateFiles(DirectoryPath, "battle-*.json"))
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("路径越界");
            if (!string.Equals(full, keep, StringComparison.OrdinalIgnoreCase)) File.Delete(full);
        }
    }
}

public sealed class ReplayTimeline
{
    public List<RecordedEvent> Steps { get; }
    public int Position { get; private set; }
    public RecordedEvent? Next => Position < Steps.Count ? Steps[Position] : null;
    public ReplayTimeline(BattleRecording recording) => Steps = recording.Events
        .Where(e => e.Type is "replay_action" or "cards_selected").ToList();
    public void Advance() => Position++;
    public static List<CardSnapshot> Cards(RecordedEvent entry)
    {
        var json = JsonSerializer.SerializeToElement(entry.Data);
        var data = json.TryGetProperty("details", out var details) ? details : json.GetProperty("Details");
        var cards = data.TryGetProperty("cards", out var c) ? c : data.GetProperty("Cards");
        return cards.Deserialize<List<CardSnapshot>>(ReplayJson.Options)!;
    }
    public static bool SameCard(CardSnapshot a, CardSnapshot b) =>
        a.ModelId == b.ModelId && a.UpgradeLevel == b.UpgradeLevel &&
        (a.CombatCardId == null || a.CombatCardId == b.CombatCardId) &&
        (a.DeckIndex == null || a.DeckIndex == b.DeckIndex);
    public static bool Equivalent(RecordedEvent a, RecordedEvent b) => a.Type == b.Type &&
        a.PlayerTurn == b.PlayerTurn && (a.Type == "replay_action"
            ? ReplayJson.Read<ReplayCommand>(a.Data!) == ReplayJson.Read<ReplayCommand>(b.Data!)
            : Cards(a).SequenceEqual(Cards(b)));
    public bool AcceptPrefix(IEnumerable<RecordedEvent> recorded)
    {
        foreach (var entry in recorded.Where(e => e.Type is "replay_action" or "cards_selected"))
        {
            if (Next == null || !Equivalent(Next, entry)) return false;
            Advance();
        }
        return true;
    }
}
