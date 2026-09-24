using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace SpireReplay.SpireReplayCode;

internal sealed record ActivityToken(Player Player, string Id, int? RoomId, int Floor,
    string? BattleId, object Details, int Act, string? RoomType)
{
    public string StartType { get; init; } = "";
    public string? RoomRevision { get; init; }
}
internal sealed record RewardOptions(List<CardSnapshot> Cards, List<string> Alternatives);

internal static partial class BattleRecorder
{
    private static ActivityJournalFile? _journal;
    private static string? _lastBattleId;
    private static RecordingLocation? _lastBattleLocation;
    private static MegaCrit.Sts2.Core.Rooms.AbstractRoom? _lastBattleRoom;
    private static readonly List<(Reward Reward, ActivityToken Token)> ActiveRewards = [];

    private static void EnsureSession(IRunState run)
    {
        if (ReferenceEquals(_run, run) && _journal != null) return;
        _run = run;
        _sessionId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        _lastBattleId = null;
        _lastBattleLocation = null;
        _lastBattleRoom = null;
        ActiveRewards.Clear();
        ActivePurchases.Clear();
        ActiveRestOptions.Clear();
        ActiveEventChoices.Clear();
        PendingAttempts.Clear();
        EnsureRunArchive(run);
        string directory = Path.Combine(ProjectSettings.GlobalizePath("user://SpireReplay/recordings"), _sessionId);
        _journal = new ActivityJournalFile(directory, new ActivityJournal
        {
            SessionId = _sessionId, Seed = run.Rng.StringSeed,
            CharacterId = run.Players[0].Character.Id.ToString()
        });
    }

    private static bool CanRecord(Player player) => player.RunState.Players.Count == 1 &&
        RunManager.Instance.NetService.Type == NetGameType.Singleplayer &&
        ReferenceEquals(RunManager.Instance.DebugOnlyGetState(), player.RunState);

    private static ActivityToken? Begin(Player player, string type, object details)
    {
        if (!CanRecord(player)) return null;
        EnsureSession(player.RunState);
        var run = player.RunState;
        var location = new RecordingLocation(run.CurrentActIndex + 1, run.TotalFloor, run.CurrentRoom?.Id);
        var token = new ActivityToken(player, Guid.NewGuid().ToString("N"),
            location.RoomId, location.TotalFloor,
            location.MatchesBattle(_lastBattleLocation) && ReferenceEquals(run.CurrentRoom, _lastBattleRoom)
                ? _lastBattleId : null, details, location.Act, run.CurrentRoom?.RoomType.ToString())
        { StartType = type, RoomRevision = WholeRun?.Find(location.Act, location.TotalFloor, location.RoomId ?? 0)?.RevisionId };
        Activity(token, type, details);
        return token;
    }

    private static void Activity(ActivityToken token, string type, object? details)
    {
        if (!CanRecord(token.Player) || !ReferenceEquals(_run, token.Player.RunState) || _journal == null) return;
        var state = ReferenceEquals(token.Player.Creature.CombatState, _combat) ? _combat : null;
        var envelope = new
        {
            operationId = token.Id, roomId = token.RoomId, totalFloor = token.Floor,
            act = token.Act, roomType = token.RoomType, battleId = token.BattleId, details
        };
        var entry = new RecordedEvent
        {
            Type = type, Round = state?.RoundNumber ?? 0,
            PlayerTurn = state == null ? null : token.Player.PlayerCombatState?.TurnNumber,
            Side = state?.CurrentSide.ToString() ?? "OutsideCombat", Data = envelope
        };
        RunReplayDriver.Observe(entry);
        if (PendingAttempts.TryGetValue(token.Player, out var pending)) pending.Entries.Add((token, entry));
        else WriteActivity(token, entry);
    }

    private static void WriteActivity(ActivityToken token, RecordedEvent entry)
    {
        if (_file != null && token.BattleId == _file.Recording.BattleId) _file.Append(entry);
        else
        {
            _journal?.Append(entry);
            if (token != null) WholeRun?.Append(token.Act, token.Floor, token.RoomId ?? 0, token.RoomRevision, entry);
        }
    }

    public static void Choice(Player player, uint choiceId, PlayerChoiceResult result) => Safe(() =>
    {
        if (!CanRecord(player)) return;
        var cards = result.ChoiceType is PlayerChoiceType.CanonicalCard or PlayerChoiceType.CombatCard
            or PlayerChoiceType.DeckCard or PlayerChoiceType.MutableCard
            ? result.AsCards(result.ChoiceType).Select(Card).ToList() : null;
        var reward = ActiveRewards.LastOrDefault(x => ReferenceEquals(x.Reward.Player, player));
        Begin(player, "player_choice", new
        {
            choiceId, choiceType = result.ChoiceType.ToString(), cards,
            sourceCardPlaySequence = PendingPlays.Count == 0 ? (int?)null : PendingPlays.Values.Last(),
            indexes = result.ChoiceType == PlayerChoiceType.Index ? result.AsIndexes() : null,
            playerId = result.ChoiceType == PlayerChoiceType.Player ? result.AsPlayerId() : (ulong?)null,
            action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.ToString(),
            rewardOperationId = reward.Token?.Id,
            eventOperationId = ActiveEventChoices.GetValueOrDefault(player)?.Token.Id,
            // Context only: an index may refer to a nested choice, not necessarily these reward cards.
            activeReward = reward.Reward == null ? null : RewardData(reward.Reward)
        });
    });

    public static void CardsChosen(Player player, IEnumerable<CardModel?> cards) => Safe(() =>
    {
        if (!CanRecord(player)) return;
        var selected = cards.ToList();
        ReplayDriver.ObserveCards(selected);
        Begin(player, "cards_selected", new
        {
            cards = selected.OfType<CardModel>().Select(Card).ToList(),
            eventOperationId = ActiveEventChoices.GetValueOrDefault(player)?.Token.Id,
            sourceCardPlaySequence = PendingPlays.Count == 0 ? (int?)null : PendingPlays.Values.Last(),
            action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.ToString()
        });
    });

    public static ActivityToken? PotionStarted(PotionModel potion, Creature? target, bool discarded = false)
    {
        ActivityToken? token = null;
        Safe(() =>
        {
            if (!CanRecord(potion.Owner)) return;
            var eventChoice = ActiveEventChoices.GetValueOrDefault(potion.Owner);
            string source = RunManager.Instance.ActionExecutor.CurrentlyRunningAction is DiscardPotionGameAction
                ? "player" : eventChoice != null ? "event" : "game_effect";
            string type = !discarded ? "potion_use_started" : source == "event"
                ? "potion_event_consumption_started" : source == "player" ? "potion_discard_started" : "potion_removed_started";
            token = Begin(potion.Owner, type, new
            {
                potionId = potion.Id.ToString(), name = potion.Title.GetFormattedText(),
                slotIndex = potion.Owner.GetPotionSlotIndex(potion),
                source = discarded ? source : null,
                eventOperationId = eventChoice?.Token.Id,
                target = target == null ? null : Creature(target)
            });
        });
        return token;
    }

    public static void Completed(ActivityToken? token, string type, object? result = null) => Safe(() =>
    {
        if (token != null) Activity(token, type, result);
    });

    private static object RewardData(Reward reward) => new
    {
        type = reward.GetType().Name,
        cards = reward is CardReward cards ? cards.Cards.Select(Card).ToList() : null,
        gold = reward is GoldReward gold ? gold.Amount : (int?)null,
        potionId = (reward as PotionReward)?.Potion?.Id.ToString(),
        relicId = (reward as RelicReward)?.Relic?.Id.ToString(),
        selected = reward.SuccessfullySelected
    };

    public static ActivityToken? RewardStarted(Reward reward)
    {
        ActivityToken? token = null;
        Safe(() =>
        {
            if (!CanRecord(reward.Player)) return;
            if (reward is PotionReward) BeginAttempt(reward.Player);
            token = Begin(reward.Player, "reward_selection_started", RewardData(reward));
            if (token != null) ActiveRewards.Add((reward, token));
        });
        return token;
    }

    public static void RewardFinished(Reward reward, ActivityToken? token, bool? success) => Safe(() =>
    {
        if (token == null) return;
        ActiveRewards.RemoveAll(x => x.Token.Id == token.Id);
        Activity(token, "reward_selection_finished", new
        {
            success, remaining = RewardData(reward),
            // A false result means cancelled/not obtained, not necessarily a final skip.
            outcome = success == true ? "obtained" : success == false ? "not_obtained" : "faulted_or_cancelled"
        });
        if (reward is PotionReward)
            FlushAttempt(reward.Player, AttemptFilter.CanOmit(success.HasValue, success == true,
                PendingAttempts.GetValueOrDefault(reward.Player)?.PotionFailure,
                reward.Player.Gold - (PendingAttempts.GetValueOrDefault(reward.Player)?.GoldBefore ?? reward.Player.Gold)));
    });

    public static ActivityToken? RewardsOffered(RewardsSet set)
    {
        ActivityToken? token = null;
        Safe(() =>
        {
            if (!CanRecord(set.Player)) return;
            token = Begin(set.Player, "rewards_offered", new
            {
                setId = set.Id,
                rewards = set.Rewards.Select((reward, index) => new { index, reward = RewardData(reward) }).ToList()
            });
        });
        return token;
    }

    public static void RewardsFinished(RewardsSet set, ActivityToken? token, bool completed) => Safe(() =>
    {
        if (token == null) return;
        Activity(token, "rewards_closed", new
        {
            setId = set.Id, completed,
            // On completion, all remaining unselected items have been skipped by the game.
            rewards = set.Rewards.Select((reward, index) => new
            {
                index, reward = RewardData(reward),
                outcome = reward.SuccessfullySelected ? "obtained" : completed ? "skipped" : "unresolved"
            }).ToList()
        });
    });

    public static void RewardRerolled(CardReward reward) => Safe(() =>
        Begin(reward.Player, "reward_rerolled", RewardData(reward)));

    public static ActivityToken? RewardOptionsShown(IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        ActivityToken? token = null;
        Safe(() =>
        {
            var active = ActiveRewards.LastOrDefault(x => x.Reward is CardReward);
            if (active.Token == null) return;
            token = Begin(active.Reward.Player, "reward_card_options", new RewardOptions(
                options.Select(x => Card(x.Card)).ToList(), alternatives.Select(x => x.GetType().Name).ToList()));
        });
        return token;
    }

    public static void RewardOptionChosen(ActivityToken token, int? index) => Safe(() =>
    {
        var options = (RewardOptions)token.Details;
        CardSnapshot? card = index is >= 0 && index < options.Cards.Count ? options.Cards[index.Value] : null;
        int alternateIndex = (index ?? -1) - options.Cards.Count;
        string? alternative = alternateIndex >= 0 && alternateIndex < options.Alternatives.Count
            ? options.Alternatives[alternateIndex] : null;
        Activity(token, "reward_card_chosen", new { index, skipped = index == null, card, alternative });
    });
}
