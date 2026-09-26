using System.Text.Json;

namespace SpireReplay.SpireReplayCode;

public sealed record ReplayTurnPreview(int Turn, List<string> Actions);

public static class ReplayPreview
{
    public static bool ReadyForAction(bool playerSide, bool playPhase, bool queuePlayPhase, bool busy) =>
        playerSide && playPhase && queuePlayPhase && !busy;

    public static List<ReplayTurnPreview> Build(BattleRecording battle)
    {
        var potions = new Dictionary<string, string>();
        foreach (var e in battle.Events.Where(e => e.Type.StartsWith("potion_")))
        {
            var data = JsonSerializer.SerializeToElement(e.Data);
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("details", out var d) &&
                d.ValueKind == JsonValueKind.Object && d.TryGetProperty("potionId", out var id) && d.TryGetProperty("name", out var name))
                potions[id.GetString()!] = name.GetString()!;
        }
        var creatures = battle.Events.Where(e => e.State != null).SelectMany(e => e.State!.Creatures)
            .Where(c => c.CombatId != null).GroupBy(c => c.CombatId!.Value).ToDictionary(g => g.Key, g => g.First().Name);
        var turns = new List<ReplayTurnPreview>();
        foreach (var e in battle.Events.Where(e => e.Type is "replay_action" or "cards_selected"))
        {
            int turn = e.PlayerTurn ?? 1;
            var row = turns.LastOrDefault();
            if (row == null || row.Turn != turn) { row = new ReplayTurnPreview(turn, []); turns.Add(row); }
            if (e.Type == "cards_selected")
            {
                var cards = ReplayTimeline.Cards(e);
                row.Actions.Add("   ↳ 选牌：" + (cards.Count == 0 ? "跳过" : string.Join("、", cards.Select(c => c.Name))));
                continue;
            }
            var command = ReplayJson.Read<ReplayCommand>(e.Data!);
            string text = command.Kind switch
            {
                "card" => "打出 " + command.Card?.Name,
                "potion" => "使用 " + potions.GetValueOrDefault(command.PotionId ?? "", command.PotionId ?? "药水"),
                "discard" => "丢弃 " + potions.GetValueOrDefault(command.PotionId ?? "", command.PotionId ?? "药水"),
                "end_turn" => "结束回合",
                _ => command.Kind
            };
            if (command.TargetId is uint target) text += " → " + creatures.GetValueOrDefault(target, command.TargetModelId ?? $"目标 {target}");
            row.Actions.Add(text);
        }
        return turns;
    }
}
