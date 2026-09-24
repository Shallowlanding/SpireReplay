using MegaCrit.Sts2.Core.Entities.Players;

namespace SpireReplay.SpireReplayCode;

// Only known no-op outcomes are filtered. Failed random potion generation remains recorded.
public static class AttemptFilter
{
    public static bool CanOmit(bool completed, bool success, string? reason, int goldDelta) =>
        completed && !success && goldDelta == 0 && reason is "FailureGold" or "FailureSpace" or "FailureOutOfStock" or "TooFull";
}

internal sealed class PendingAttempt
{
    public int GoldBefore { get; init; }
    public List<(ActivityToken Token, RecordedEvent Entry)> Entries { get; } = [];
    public string? PotionFailure { get; set; }
}

internal static partial class BattleRecorder
{
    private static readonly Dictionary<Player, PendingAttempt> PendingAttempts = [];

    private static void BeginAttempt(Player player)
    {
        EnsureSession(player.RunState);
        FlushAttempt(player, false);
        PendingAttempts[player] = new PendingAttempt { GoldBefore = player.Gold };
    }

    private static void FlushAttempt(Player player, bool omit)
    {
        if (!PendingAttempts.Remove(player, out var pending)) return;
        // Any unexpected side effect makes this attempt worth keeping.
        var harmlessTypes = new HashSet<string>
        {
            "shop_purchase_started", "shop_purchase_finished", "reward_selection_started",
            "reward_selection_finished", "potion_obtain_started", "potion_obtain_finished"
        };
        if (omit && pending.Entries.All(x => harmlessTypes.Contains(x.Entry.Type))) return;
        foreach (var (token, entry) in pending.Entries) WriteActivity(token, entry);
    }
}
