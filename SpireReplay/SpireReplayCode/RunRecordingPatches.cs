using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireReplay.SpireReplayCode;

internal static partial class BattleRecorder
{
    internal static RunArchive? WholeRun { get; private set; }
    internal static List<RunHistoryEntry> RunHistory { get; private set; } = [];
    private static string RunIdentity(IRunState run) =>
        $"{AccessTools.Field(typeof(RunManager), "_startTime").GetValue(RunManager.Instance)}|{run.Rng.StringSeed}|{run.Players[0].Character.Id}|{run.AscensionLevel}";

    private static void EnsureRunArchive(IRunState run)
    {
        string id = RunIdentity(run);
        if (WholeRun?.Data.RunId == id && File.Exists(WholeRun.FilePath)) return;
        WholeRun = new RunArchive(ProjectSettings.GlobalizePath("user://SpireReplay/recordings"), new RunRecording
        {
            StartedAt = DateTimeOffset.UtcNow, RunId = id, Seed = run.Rng.StringSeed, CharacterId = run.Players[0].Character.Id.ToString(),
            GameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown",
            GameAssemblyId = typeof(CardModel).Module.ModuleVersionId.ToString(), Ascension = run.AscensionLevel,
            GameMode = run.GameMode.ToString(), ModifierIds = run.Modifiers.Select(m => m.Id.ToString()).ToList(), RecordedFromStart = run.TotalFloor <= 1,
            Areas = run.Acts.Select((act, i) => new RunArea(i + 1, act.Id.ToString(), act.Title.GetFormattedText())).ToList()
        });
        RefreshRunHistory();
    }
    internal static void RefreshRunHistory()
    {
        if (WholeRun == null) { RunHistory = []; return; }
        RunHistory = RunHistoryCatalog.Find(ProjectSettings.GlobalizePath("user://SpireReplay/recordings"), WholeRun.Data,
            message => MainFile.Logger.Warn(message));
    }
    public static void EnterArchiveRoom(AbstractRoom room, bool restoring) => Safe(() =>
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        if (run == null || run.Players.Count != 1 || !CanRecord(run.Players[0])) return;
        EnsureSession(run);
        ReplayPanel.EnsureInstalled();
        if (room is MapRoom) return;
        var coord = run.CurrentMapCoord;
        WholeRun!.Enter(new RunRoomRecord
        {
            Act = run.CurrentActIndex + 1, Floor = run.TotalFloor, RoomId = room.Id ?? run.NextRoomId,
            Type = room.RoomType.ToString(), ModelId = room.ModelId?.ToString() ?? "",
            MapRow = coord?.row, MapCol = coord?.col
        }, restoring || room.IsPreFinished, run.CurrentRoomCount == 0);
    });
    public static void EndArchive(bool victory) => Safe(() => WholeRun?.End(victory));
}

[HarmonyPatch(typeof(RunManager), "EnterRoomInternal")]
internal static class RunRoomRecordPatch
{
    private static void Prefix(AbstractRoom room, bool isRestoringRoomStackBase) => BattleRecorder.EnterArchiveRoom(room, isRestoringRoomStackBase);
}
[HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
internal static class RunEndRecordPatch
{
    private static void Postfix(bool isVictory) => BattleRecorder.EndArchive(isVictory);
}
