namespace SpireReplay.SpireReplayCode;

public sealed record ReplayDestination(int BoundarySequence, string Description)
{
    // Boundary is exclusive: complete this action (including nested choices), stop before the next input.
    public static ReplayDestination AfterAction(BattleRecording battle, int sequence)
    {
        var actions = battle.Events.Where(e => e.Type == "replay_action").ToList();
        int index = actions.FindIndex(e => e.Sequence == sequence);
        if (index < 0 || ReplayJson.Read<ReplayCommand>(actions[index].Data!).Kind == "end_turn")
            throw new ArgumentException("请选择卡牌、药水或回合标题");
        int boundary = index + 1 < actions.Count ? actions[index + 1].Sequence : int.MaxValue;
        var command = ReplayJson.Read<ReplayCommand>(actions[index].Data!);
        return new(boundary, $"第 {actions[index].PlayerTurn} 回合 · {command.Card?.Name ?? (command.Kind == "discard" ? "丢弃药水" : "使用药水")}之后");
    }

    public static ReplayDestination EndOfTurn(BattleRecording battle, int turn)
    {
        var commands = battle.Events.Where(e => e.Type == "replay_action" && e.PlayerTurn == turn).ToList();
        var end = commands.FirstOrDefault(e => ReplayJson.Read<ReplayCommand>(e.Data!).Kind == "end_turn");
        int boundary = end?.Sequence ?? battle.Events.FirstOrDefault(e =>
            (e.Type is "replay_action" or "cards_selected") && e.PlayerTurn > turn)?.Sequence ?? int.MaxValue;
        return new(boundary, $"第 {turn} 回合操作完成（不结束回合）");
    }

    public bool Reached(ReplayTimeline timeline) => timeline.Next == null || timeline.Next.Sequence >= BoundarySequence;
}
