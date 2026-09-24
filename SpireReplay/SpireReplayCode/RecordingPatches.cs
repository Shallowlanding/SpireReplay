using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace SpireReplay.SpireReplayCode;

// Observe synchronous history notifications, not async Task creation or input clicks.
// Invalid/cancelled card attempts never reach CardPlayStarted.
[HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.CardPlayStarted))]
internal static class CardPlayStartedPatch
{
    private static void Postfix(ICombatState combatState, CardPlay cardPlay) =>
        BattleRecorder.CardPlay(combatState, cardPlay, finished: false);
}

[HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.CardPlayFinished))]
internal static class CardPlayFinishedPatch
{
    private static void Postfix(ICombatState combatState, CardPlay cardPlay) =>
        BattleRecorder.CardPlay(combatState, cardPlay, finished: true);
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.Reset))]
internal static class CombatResetPatch
{
    private static void Prefix() => BattleRecorder.Reset();
}
