namespace SpireReplay.SpireReplayCode;

public sealed record RunFloorTarget(int Act, int Floor)
{
    public bool IsPast(int act, int floor) => act > Act || act == Act && floor > Floor;
    public bool IsComplete(RunRecording source, RunRecording current, int act, int floor)
    {
        if (act != Act || floor != Floor) return false;
        var rooms = source.Rooms.Where(r => r.Act == Act && r.Floor == Floor).ToList();
        if (rooms.Count == 0) return false;
        foreach (var expected in rooms)
        {
            var actual = current.Rooms.FirstOrDefault(r => r.Act == Act && r.Floor == Floor && r.RoomId == expected.RoomId);
            if (actual == null) return false;
            var expectedEvents = CancelledSelectionFilter.Effective(expected.Events);
            var actualEvents = CancelledSelectionFilter.Effective(actual.Events);
            var inputs = expectedEvents.Where(RunReplayPlan.IsInput).ToList();
            if (RunReplayPlan.MatchingPrefix(inputs, actual.Events) != inputs.Count) return false;
            if (expected.Battle != null && (actual.Battle == null || actual.Battle.Status != expected.Battle.Status)) return false;
            if (expected.Battle == null && expected.Type is "Monster" or "Elite" or "Boss") return false;
            // Starts are recorded before async effects settle. Do not stop merely because the last input was sent.
            foreach (var group in expectedEvents.Where(e => e.Type.EndsWith("_finished") && e.Type != "card_play_finished").GroupBy(e => e.Type))
                if (actualEvents.Count(e => e.Type == group.Key) < group.Count()) return false;
        }
        return true;
    }
}
