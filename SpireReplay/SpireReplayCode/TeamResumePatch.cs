using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace SpireReplay.SpireReplayCode;

[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice))]
internal static class TeamResumePatch
{
    private static bool Prefix(GameAction action) => MultiplayerReplay.AllowResume(action);
}
