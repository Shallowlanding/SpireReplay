using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;

namespace SpireReplay.SpireReplayCode;

internal sealed record EventPage(EventModel Model, string Id, int Number);
internal sealed record EventCapture(ActivityToken Token, EventModel Model, int Hp, int MaxHp, int Gold,
    Dictionary<CardModel, CardSnapshot> Deck, Dictionary<RelicModel, object> Relics);

internal static partial class BattleRecorder
{
    private static readonly ConditionalWeakTable<EventOption, EventPage> EventOptions = new();
    private static readonly ConditionalWeakTable<EventModel, EventPage> EventPages = new();
    private static readonly Dictionary<Player, EventCapture> ActiveEventChoices = [];

    private static object RelicData(RelicModel relic) => new { relicId = relic.Id.ToString(), name = relic.Title.GetFormattedText() };
    private static object OptionData(EventOption option, int index) => new
    {
        index, key = option.TextKey, title = option.Title.GetFormattedText(),
        locked = option.IsLocked, proceed = option.IsProceed,
        relic = option.Relic == null ? null : RelicData(option.Relic)
    };

    public static void EventStateChanged(EventModel model) => Safe(() =>
    {
        if (model.Owner is not { } player || !CanRecord(player)) return;
        EnsureSession(player.RunState);
        EventPages.TryGetValue(model, out var previous);
        var page = new EventPage(model, previous?.Id ?? Guid.NewGuid().ToString("N"), (previous?.Number ?? -1) + 1);
        EventPages.Remove(model);
        EventPages.Add(model, page);
        foreach (var option in model.CurrentOptions)
        {
            EventOptions.Remove(option);
            EventOptions.Add(option, page);
        }
        Begin(player, "event_options", new
        {
            eventInstanceId = page.Id, eventId = model.Id.ToString(), name = model.Title.GetFormattedText(),
            ancient = model is AncientEventModel, page = page.Number, finished = model.IsFinished,
            options = model.CurrentOptions.Select(OptionData).ToList(),
            eventOperationId = ActiveEventChoices.GetValueOrDefault(player)?.Token.Id
        });
    });

    public static EventCapture? EventChoiceStarted(EventOption option)
    {
        EventCapture? capture = null;
        Safe(() =>
        {
            if (!EventOptions.TryGetValue(option, out var page) || page.Model.Owner is not { } player ||
                !CanRecord(player) || option.IsLocked) return;
            var model = page.Model;
            int index = model.CurrentOptions.ToList().IndexOf(option);
            if (index < 0) return;
            // Keep full before-state only in memory; serialize differences after the handler finishes.
            var deck = player.Deck.Cards.ToDictionary(c => c, Card);
            var relics = player.Relics.ToDictionary(r => r, RelicData);
            var token = Begin(player, "event_option_started", new
            {
                eventInstanceId = page.Id, eventId = model.Id.ToString(), ancient = model is AncientEventModel,
                page = page.Number, option = OptionData(option, index), previouslyChosen = option.WasChosen
            });
            if (token == null) return;
            capture = new EventCapture(token, model, player.Creature.CurrentHp, player.Creature.MaxHp, player.Gold, deck, relics);
            ActiveEventChoices[player] = capture;
        });
        return capture;
    }

    public static void EventChoiceFinished(EventCapture capture, Task task) => Safe(() =>
    {
        var player = capture.Token.Player;
        if (ActiveEventChoices.GetValueOrDefault(player) == capture) ActiveEventChoices.Remove(player);
        Activity(capture.Token, "event_option_finished", new
        {
            outcome = task.IsCompletedSuccessfully ? "completed" : task.IsCanceled ? "cancelled" : "faulted",
            errorType = task.Exception?.GetBaseException().GetType().FullName,
            eventFinished = capture.Model.IsFinished,
            hpDelta = player.Creature.CurrentHp - capture.Hp,
            maxHpDelta = player.Creature.MaxHp - capture.MaxHp, goldDelta = player.Gold - capture.Gold,
            cardsAdded = player.Deck.Cards.Where(c => !capture.Deck.ContainsKey(c)).Select(Card).ToList(),
            cardsRemoved = capture.Deck.Where(c => !player.Deck.Cards.Contains(c.Key)).Select(c => c.Value).ToList(),
            cardsUpgraded = player.Deck.Cards.Where(c => capture.Deck.TryGetValue(c, out var before) && before.UpgradeLevel != c.CurrentUpgradeLevel).Select(Card).ToList(),
            relicsAdded = player.Relics.Where(r => !capture.Relics.ContainsKey(r)).Select(RelicData).ToList(),
            relicsRemoved = capture.Relics.Where(r => !player.Relics.Contains(r.Key)).Select(r => r.Value).ToList()
        });
    });
}

