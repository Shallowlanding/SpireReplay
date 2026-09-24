using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace SpireReplay.SpireReplayCode;

internal static partial class BattleRecorder
{
    public static ActivityToken? RelicChangeStarted(RelicModel relic, Player player, string type)
    {
        ActivityToken? token = null;
        Safe(() =>
        {
            if (!CanRecord(player)) return;
            token = Begin(player, type, new { relicId = relic.Id.ToString(), name = relic.Title.GetFormattedText() });
        });
        return token;
    }
}

[HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Obtain), new[] { typeof(RelicModel), typeof(Player), typeof(int) })]
internal static class RelicObtainRecordPatch
{
    private static void Prefix(RelicModel relic, Player player, out ActivityToken? __state) =>
        __state = BattleRecorder.RelicChangeStarted(relic, player, "relic_obtain_started");
    private static void Postfix(ref Task<RelicModel> __result, ActivityToken? __state)
    {
        if (__state is { } token)
            __result = TaskObservation.Observe(__result, (completed, _) =>
                BattleRecorder.Completed(token, "relic_obtain_finished", new { completed, relic = token.Details }));
    }
}

[HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Remove))]
internal static class RelicRemoveRecordPatch
{
    private static void Prefix(RelicModel relic, out ActivityToken? __state) =>
        __state = BattleRecorder.RelicChangeStarted(relic, relic.Owner, "relic_remove_started");
    private static void Postfix(ref Task __result, ActivityToken? __state)
    {
        if (__state is { } token)
            __result = TaskObservation.Observe(__result, completed =>
                BattleRecorder.Completed(token, "relic_remove_finished", new { completed, relic = token.Details }));
    }
}
