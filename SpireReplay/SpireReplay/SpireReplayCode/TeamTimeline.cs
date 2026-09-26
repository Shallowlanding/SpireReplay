namespace SpireReplay.SpireReplayCode;

public static class TeamTimeline
{
    public static bool SupportsReplay(TeamAttempt source) => source.FormatVersion >= 2 || !source.Steps.Any(s => s.Kind == "choice");
    // A teammate may act while a card waits for a choice. Include the intervening
    // steps in their recorded order, plus choices belonging to all included cards.
    public static int CompleteSelection(TeamAttempt source, int endExclusive)
    {
        int end = Math.Clamp(endExclusive, 0, source.Steps.Count);
        var latestAction = new Dictionary<ulong, int>();
        var latestChoice = new Dictionary<ulong, int>();
        for (int i = 0; i < source.Steps.Count; i++)
        {
            var step = source.Steps[i];
            if (step.Kind == "action") latestAction[step.Player] = i;
            if (step.Kind == "choice") latestChoice[step.Player] = i;
            if (step.Kind == "resume" && step.Payload.StartsWith("hook:"))
            {
                if (latestChoice.TryGetValue(step.Player, out int choice) && choice < end) end = Math.Max(end, i + 1);
                continue;
            }
            if (step.Kind is "choice" or "resume" && latestAction.TryGetValue(step.Player, out int parent) && parent < end)
                end = Math.Max(end, i + 1);
        }
        return end;
    }
}
