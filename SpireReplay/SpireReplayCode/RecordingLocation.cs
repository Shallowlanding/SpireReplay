namespace SpireReplay.SpireReplayCode;

// Room IDs are reused across floors; they are never a standalone battle key.
public sealed record RecordingLocation(int Act, int TotalFloor, int? RoomId)
{
    public bool MatchesBattle(RecordingLocation? battle) =>
        RoomId.HasValue && TotalFloor > 0 && this == battle;
}
