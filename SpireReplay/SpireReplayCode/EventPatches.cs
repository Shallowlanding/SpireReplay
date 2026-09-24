using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;

namespace SpireReplay.SpireReplayCode;

[HarmonyPatch(typeof(EventModel), "SetEventState")]
internal static class EventStatePatch
{
    private static void Postfix(EventModel __instance) => BattleRecorder.EventStateChanged(__instance);
}

[HarmonyPatch(typeof(EventOption), nameof(EventOption.Chosen))]
internal static class EventChoicePatch
{
    private static void Prefix(EventOption __instance, out EventCapture? __state) =>
        __state = BattleRecorder.EventChoiceStarted(__instance);

    private static void Postfix(ref Task __result, EventCapture? __state)
    {
        if (__state is not { } capture) return;
        var original = __result;
        __result = TaskObservation.Observe(original, _ => BattleRecorder.EventChoiceFinished(capture, original));
    }
}
