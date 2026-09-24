using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace SpireReplay.SpireReplayCode;

[HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice))]
internal static class PlayerChoicePatch
{
    private static void Postfix(Player player, uint choiceId, PlayerChoiceResult result) =>
        BattleRecorder.Choice(player, choiceId, result);
}

[HarmonyPatch(typeof(CardSelectCmd), "LogChoice")]
internal static class CardsChosenPatch
{
    private static void Postfix(Player player, IEnumerable<CardModel?> cards) => BattleRecorder.CardsChosen(player, cards);
}

[HarmonyPatch(typeof(PotionModel), nameof(PotionModel.OnUseWrapper))]
internal static class PotionUsePatch
{
    private static void Prefix(PotionModel __instance, Creature? target, out ActivityToken? __state) =>
        __state = BattleRecorder.PotionStarted(__instance, target);

    private static void Postfix(ref Task __result, ActivityToken? __state)
    {
        if (__state != null) __result = Observe(__result, __state);
    }

    private static Task Observe(Task original, ActivityToken token) => TaskObservation.Observe(original,
        success => BattleRecorder.Completed(token, "potion_use_finished", new { success }));
}

[HarmonyPatch(typeof(PotionModel), nameof(PotionModel.Discard))]
internal static class PotionDiscardPatch
{
    private static void Prefix(PotionModel __instance, out ActivityToken? __state) =>
        __state = BattleRecorder.PotionStarted(__instance, null, discarded: true);
    private static void Postfix(ActivityToken? __state) => BattleRecorder.Completed(__state,
        __state?.StartType == "potion_event_consumption_started" ? "potion_event_consumed" :
        __state?.StartType == "potion_discard_started" ? "potion_discarded" : "potion_removed");
}

[HarmonyPatch(typeof(Reward), nameof(Reward.SelectUnsynchronized))]
internal static class RewardSelectionPatch
{
    private static void Prefix(Reward __instance, out ActivityToken? __state) =>
        __state = BattleRecorder.RewardStarted(__instance);
    private static void Postfix(Reward __instance, ref Task<bool> __result, ActivityToken? __state)
    {
        if (__state != null) __result = Observe(__result, __instance, __state);
    }
    private static Task<bool> Observe(Task<bool> original, Reward reward, ActivityToken token) =>
        TaskObservation.Observe(original, (success, result) =>
            BattleRecorder.RewardFinished(reward, token, success ? result : null));
}

[HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.BeginRewardsSet))]
internal static class RewardsSetPatch
{
    private static void Postfix(RewardsSet set, ref Task __result)
    {
        var token = BattleRecorder.RewardsOffered(set);
        if (token != null) __result = Observe(__result, set, token);
    }
    private static Task Observe(Task original, RewardsSet set, ActivityToken token) =>
        TaskObservation.Observe(original, completed => BattleRecorder.RewardsFinished(set, token, completed));
}

[HarmonyPatch(typeof(CardReward), nameof(CardReward.Reroll))]
internal static class RewardRerollPatch
{
    private static void Postfix(CardReward __instance) => BattleRecorder.RewardRerolled(__instance);
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.OptionSelected))]
internal static class RewardCardOptionPatch
{
    private static void Prefix(IReadOnlyList<CardCreationResult> ____options,
        IReadOnlyList<CardRewardAlternative> ____extraOptions, out ActivityToken? __state) =>
        __state = BattleRecorder.RewardOptionsShown(____options, ____extraOptions);
    private static void Postfix(ref Task<int?> __result, ActivityToken? __state)
    {
        if (__state != null) __result = Observe(__result, __state);
    }
    private static Task<int?> Observe(Task<int?> original, ActivityToken token) =>
        TaskObservation.Observe(original, (success, index) =>
        {
            if (success) BattleRecorder.RewardOptionChosen(token, index);
            else BattleRecorder.Completed(token, "reward_card_choice_interrupted");
        });
}
