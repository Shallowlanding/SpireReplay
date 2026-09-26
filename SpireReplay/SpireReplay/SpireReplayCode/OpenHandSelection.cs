using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace SpireReplay.SpireReplayCode;

internal static class OpenHandSelection
{
    public static bool IsOpen => NPlayerHand.Instance?.IsInCardSelection == true;

    public static bool Valid(IReadOnlyCollection<CardModel> selected, IReadOnlyCollection<CardModel> candidates, int min, int max)
        => selected.Count >= min && selected.Count <= max && selected.Distinct().Count() == selected.Count && selected.All(candidates.Contains);

    public static bool Complete(IReadOnlyList<CardModel> selected)
    {
        var hand = NPlayerHand.Instance;
        if (hand?.IsInCardSelection != true) return false;
        var completion = AccessTools.Field(typeof(NPlayerHand), "_selectionCompletionSource").GetValue(hand) as TaskCompletionSource<IEnumerable<CardModel>>;
        if (completion == null || completion.Task.IsCompleted) return false;
        var prefs = (CardSelectorPrefs)AccessTools.Field(typeof(NPlayerHand), "_prefs").GetValue(hand)!;
        var filter = (Func<CardModel, bool>?)AccessTools.Field(typeof(NPlayerHand), "_currentSelectionFilter").GetValue(hand);
        var combat = AccessTools.Field(typeof(NPlayerHand), "_combatState").GetValue(hand) as MegaCrit.Sts2.Core.Combat.CombatState;
        if (combat == null) return false;
        var player = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(combat);
        if (player?.PlayerCombatState == null) return false;
        var candidates = player.PlayerCombatState.Hand.Cards.Where(c => filter?.Invoke(c) ?? true).ToList();
        if (!Valid(selected, candidates, prefs.MinSelect, prefs.MaxSelect)) throw new InvalidDataException("当前手牌选择与记录不匹配");
        // Resume the original SelectCards task: it closes the UI, sends the native
        // choice once, logs the selection and continues the original relic/card effect.
        return completion.TrySetResult(selected.ToList());
    }
}
