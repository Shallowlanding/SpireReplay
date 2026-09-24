using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace SpireReplay.SpireReplayCode;

internal sealed record PurchaseCapture(ActivityToken Token, int GoldBefore)
{
    public string? FailureReason { get; set; }
    public Dictionary<CardModel, CardSnapshot> DeckBefore { get; init; } = [];
}
internal sealed record RestCapture(ActivityToken Token, int HpBefore);
internal sealed record UpgradeCapture(ActivityToken Token, int LevelBefore);

internal static partial class BattleRecorder
{
    private static readonly Dictionary<MerchantEntry, PurchaseCapture> ActivePurchases = [];
    private static readonly Dictionary<Player, RestCapture> ActiveRestOptions = [];

    public static PurchaseCapture? PurchaseStarted(MerchantEntry entry, Player player, bool ignoreCost)
    {
        PurchaseCapture? capture = null;
        Safe(() =>
        {
            if (!CanRecord(player)) return;
            object? item = entry switch
            {
                MerchantRelicEntry relic when relic.Model != null => new
                { modelId = relic.Model.Id.ToString(), name = relic.Model.Title.GetFormattedText() },
                MerchantPotionEntry potion when potion.Model != null => new
                { modelId = potion.Model.Id.ToString(), name = potion.Model.Title.GetFormattedText() },
                MerchantCardEntry card when card.CreationResult != null => Card(card.CreationResult.Card),
                _ => null
            };
            BeginAttempt(player);
            var token = Begin(player, "shop_purchase_started", new
            {
                entryType = entry.GetType().Name, item, price = entry.Cost, ignoreCost,
                goldBefore = player.Gold, stocked = entry.IsStocked,
                potionCount = player.Potions.Count(), potionCapacity = player.MaxPotionCount
            });
            if (token == null) return;
            capture = new PurchaseCapture(token, player.Gold)
            { DeckBefore = entry is MerchantCardRemovalEntry ? player.Deck.Cards.ToDictionary(c => c, Card) : [] };
            ActivePurchases[entry] = capture;
        });
        return capture;
    }

    public static void PurchaseFailed(MerchantEntry entry, PurchaseStatus status) => Safe(() =>
    {
        if (ActivePurchases.TryGetValue(entry, out var capture)) capture.FailureReason = status.ToString();
    });

    public static void PurchaseFinished(MerchantEntry entry, PurchaseCapture capture, bool completed, bool success) => Safe(() =>
    {
        ActivePurchases.Remove(entry);
        var player = capture.Token.Player;
        Activity(capture.Token, "shop_purchase_finished", new
        {
            completed, success = completed && success,
            failureReason = !completed ? "faulted_or_cancelled" : success ? null : capture.FailureReason ?? "cancelled_or_not_obtained",
            goldAfter = player.Gold, goldDelta = player.Gold - capture.GoldBefore,
            cardsRemoved = capture.DeckBefore.Where(c => !player.Deck.Cards.Contains(c.Key)).Select(c => c.Value).ToList()
        });
        FlushAttempt(player, AttemptFilter.CanOmit(completed, success, capture.FailureReason, player.Gold - capture.GoldBefore));
    });

    public static ActivityToken? PotionProcureStarted(PotionModel potion, Player player, int slotIndex)
    {
        ActivityToken? token = null;
        Safe(() =>
        {
            if (!CanRecord(player)) return;
            token = Begin(player, "potion_obtain_started", new
            {
                potionId = potion.Id.ToString(), name = potion.Title.GetFormattedText(), requestedSlot = slotIndex,
                potionCount = player.Potions.Count(), potionCapacity = player.MaxPotionCount,
                sourceCardPlaySequence = PendingPlays.Count == 0 ? (int?)null : PendingPlays.Values.Last(),
                shopOperationId = ActivePurchases.Values.LastOrDefault(x => ReferenceEquals(x.Token.Player, player))?.Token.Id
            });
        });
        return token;
    }

    public static void PotionProcureFinished(ActivityToken token, bool completed, PotionProcureResult? result) => Safe(() =>
    {
        if (completed && result?.success == false && PendingAttempts.TryGetValue(token.Player, out var pending))
            pending.PotionFailure = result.failureReason.ToString();
        Activity(token, "potion_obtain_finished", new
        {
            completed, success = completed && result?.success == true,
            failureReason = !completed ? "faulted_or_cancelled" : result?.success == true ? null : result?.failureReason.ToString(),
            slotIndex = result?.success == true ? token.Player.GetPotionSlotIndex(result.potion) : (int?)null,
            potionCount = token.Player.Potions.Count(), potionCapacity = token.Player.MaxPotionCount
        });
    });

    public static RestCapture? RestStarted(RestSiteSynchronizer synchronizer, Player player, int optionIndex)
    {
        RestCapture? capture = null;
        Safe(() =>
        {
            if (!CanRecord(player)) return;
            var options = synchronizer.GetOptionsForPlayer(player);
            if (optionIndex < 0 || optionIndex >= options.Count) return;
            var option = options[optionIndex];
            var token = Begin(player, "rest_option_started", new
            {
                optionIndex, optionId = option.OptionId, name = option.Title.GetFormattedText(),
                hpBefore = player.Creature.CurrentHp, maxHpBefore = player.Creature.MaxHp
            });
            if (token == null) return;
            capture = new RestCapture(token, player.Creature.CurrentHp);
            ActiveRestOptions[player] = capture;
        });
        return capture;
    }

    public static void RestFinished(RestCapture capture, bool completed, bool success) => Safe(() =>
    {
        var player = capture.Token.Player;
        ActiveRestOptions.Remove(player);
        Activity(capture.Token, "rest_option_finished", new
        {
            completed, success = completed && success, hpAfter = player.Creature.CurrentHp,
            maxHpAfter = player.Creature.MaxHp, hpDelta = player.Creature.CurrentHp - capture.HpBefore
        });
    });

    public static UpgradeCapture? UpgradeStarted(CardModel card)
    {
        UpgradeCapture? capture = null;
        Safe(() =>
        {
            // Preview clones have no pile; loading cards are not in an active run yet.
            // Only observe real deck/combat cards, never upgrade previews in UI.
            if (!card.IsMutable || card.Pile == null || !CanRecord(card.Owner)) return;
            var token = Begin(card.Owner, "card_upgrade_started", new
            {
                card = Card(card),
                restOperationId = ActiveRestOptions.GetValueOrDefault(card.Owner)?.Token.Id,
                sourceCardPlaySequence = PendingPlays.Count == 0 ? (int?)null : PendingPlays.Values.Last()
            });
            if (token != null) capture = new UpgradeCapture(token, card.CurrentUpgradeLevel);
        });
        return capture;
    }

    public static void UpgradeFinished(CardModel card, UpgradeCapture? capture) => Safe(() =>
    {
        if (capture == null) return;
        Activity(capture.Token, "card_upgrade_finished", new
        {
            success = card.CurrentUpgradeLevel > capture.LevelBefore,
            levelBefore = capture.LevelBefore, levelAfter = card.CurrentUpgradeLevel, card = Card(card)
        });
    });
}
