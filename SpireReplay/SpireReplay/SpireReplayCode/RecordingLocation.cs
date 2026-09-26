namespace SpireReplay.SpireReplayCode;

// Room IDs are reused across floors; they are never a standalone battle key.
public sealed record RecordingLocation(int Act, int TotalFloor, int? RoomId)
{
    public static bool IsArchitect(string? modelId) => modelId == "EVENT.THE_ARCHITECT";
    public static int ArchiveFloor(int gameFloor, string? modelId) => IsArchitect(modelId) ? gameFloor + 1 : gameFloor;
    public static void NormalizeArchitect(RunRecording run)
    {
        foreach (var room in run.Rooms.Where(r => IsArchitect(r.ModelId) && r.GameFloor == null))
        {
            room.GameFloor = room.Floor;
            room.Floor = ArchiveFloor(room.Floor, room.ModelId);
        }
    }
    public bool MatchesBattle(RecordingLocation? battle) =>
        RoomId.HasValue && TotalFloor > 0 && this == battle;
}
