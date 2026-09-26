using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace SpireReplay.SpireReplayCode;

[HarmonyPatch]
internal static class ShopPurchasePatch
{
    // Card removal has its own wrapper overload and bypasses the base wrapper.
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper));
        yield return AccessTools.DeclaredMethod(typeof(MerchantCardRemovalEntry), nameof(MerchantCardRemovalEntry.OnTryPurchaseWrapper));
    }
    private static void Prefix(MerchantEntry __instance, Player ____player, bool ignoreCost, out PurchaseCapture? __state) =>
        __state = BattleRecorder.PurchaseStarted(__instance, ____player, ignoreCost);
    private static void Postfix(MerchantEntry __instance, ref Task<bool> __result, PurchaseCapture? __state)
    {
        if (__state is { } capture)
            __result = TaskObservation.Observe(__result, (completed, success) =>
                BattleRecorder.PurchaseFinished(__instance, capture, completed, success));
    }
}

[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseFailed))]
internal static class ShopFailurePatch
{
    private static void Postfix(MerchantEntry __instance, PurchaseStatus status) => BattleRecorder.PurchaseFailed(__instance, status);
}

[HarmonyPatch(typeof(PotionCmd), nameof(PotionCmd.TryToProcure), new[] { typeof(PotionModel), typeof(Player), typeof(int) })]
internal static class PotionProcurePatch
{
    private static void Prefix(PotionModel potion, Player player, int slotIndex, out ActivityToken? __state) =>
        __state = BattleRecorder.PotionProcureStarted(potion, player, slotIndex);
    private static void Postfix(ref Task<PotionProcureResult> __result, ActivityToken? __state)
    {
        if (__state is { } token)
            __result = TaskObservation.Observe(__result, (completed, result) =>
                BattleRecorder.PotionProcureFinished(token, completed, result));
    }
}

[HarmonyPatch(typeof(RestSiteSynchronizer), "ChooseOption")]
internal static class RestOptionPatch
{
    private static void Prefix(RestSiteSynchronizer __instance, Player player, int optionIndex, out RestCapture? __state) =>
        __state = BattleRecorder.RestStarted(__instance, player, optionIndex);
    private static void Postfix(ref Task<bool> __result, RestCapture? __state)
    {
        if (__state is { } capture)
            __result = TaskObservation.Observe(__result, (completed, success) =>
                BattleRecorder.RestFinished(capture, completed, success));
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.UpgradeInternal))]
internal static class CardUpgradePatch
{
    private static void Prefix(CardModel __instance, out UpgradeCapture? __state) =>
        __state = BattleRecorder.UpgradeStarted(__instance);
    private static void Postfix(CardModel __instance, UpgradeCapture? __state) => BattleRecorder.UpgradeFinished(__instance, __state);
}
