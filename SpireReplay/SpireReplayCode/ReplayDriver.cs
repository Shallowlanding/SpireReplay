using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using ICardSelector = MegaCrit.Sts2.Core.TestSupport.ICardSelector;
using Environment = System.Environment;

namespace SpireReplay.SpireReplayCode;

internal static class ReplayDriver
{
    public static bool Active { get; private set; }
    public static string Status { get; private set; } = "SL 后选择之前的尝试";
    public static readonly ICardSelector Selector = new ReplaySelector();
    public static bool CanSelectAutomatically
    {
        get
        {
            if (!Active) return false;
            if (_timeline != null && _destination?.Reached(_timeline) == true) return false;
            return true;
        }
    }
    private static ReplayTimeline? _timeline;
    private static string? _battleId;
    private static ReplayDestination? _destination;
    private static bool _submitting;
    private static GameAction? _pending;
    private static long _lastProgress;

    public static void Stop(string message)
    {
        if (Active) MainFile.Logger.Info($"Replay stopped: {message}; destination={_destination}; nextSequence={_timeline?.Next?.Sequence}");
        Active = false;
        Status = message;
        _timeline = null;
        _pending = null;
    }

    public static void Start(BattleRecording source, ReplayDestination destination, bool wholeRun = false)
    {
        Stop("正在校验记录…");
        var current = BattleRecorder.CurrentRecording;
        if (current == null || current.BattleId == source.BattleId || (!wholeRun && current.RunId != source.RunId) ||
            current.Seed != source.Seed || current.CharacterId != source.CharacterId || current.Ascension != source.Ascension ||
            current.GameAssemblyId != source.GameAssemblyId || current.EncounterId != source.EncounterId ||
            current.TotalFloor != source.TotalFloor || current.Act != source.Act || current.RoomId != source.RoomId)
        { Status = "记录与当前战斗或游戏版本不匹配"; return; }
        var timeline = new ReplayTimeline(source);
        if (source.Events.Any(e => e.Type == "undo_end_turn"))
        { Status = "这次尝试含撤销结束回合，第一版暂不支持重放"; return; }
        if (!timeline.Steps.Any(e => e.Type == "replay_action"))
        { Status = "这次尝试没有可重放操作（旧版记录不支持）"; return; }
        if (!timeline.AcceptPrefix(current.Events))
        { Status = "当前操作与所选尝试不同，请 SL 后再开始"; return; }
        _timeline = timeline;
        _battleId = current.BattleId;
        _destination = destination;
        if (destination.Reached(timeline))
        { Status = "当前位置已到达或超过所选位置；若需倒回，请先 SL"; return; }
        _lastProgress = Environment.TickCount64;
        Active = true;
        Status = $"重放第 {source.AttemptNumber} 次尝试 → {destination.Description}";
    }

    public static string Fingerprint(CombatState state)
    {
        var player = state.RunState.Players[0];
        var pcs = player.PlayerCombatState!;
        // Compact guard, not a dump of the hand after each action. No RNG is advanced.
        return ReplayJson.Hash(JsonSerializer.Serialize(new
        {
            state.RoundNumber, pcs.TurnNumber, pcs.Energy, pcs.Stars,
            piles = pcs.AllPiles.Select(p => new { p.Type, cards = p.Cards.Select(c => new
            { id = c.Id.ToString(), c.CurrentUpgradeLevel, combatId = BattleRecorder.Card(c).CombatCardId }) }),
            creatures = state.Creatures.Select(c => new { c.CombatId, c.CurrentHp, c.MaxHp, c.Block,
                id = c.Monster?.Id.ToString() ?? c.Player?.Character.Id.ToString(),
                powers = c.Powers.Select(p => new { id = p.Id.ToString(), p.Amount }) }),
            potions = player.PotionSlots.Select(p => p?.Id.ToString()),
            rng = state.RunState.Rng.ToSerializable().Rngs.OrderBy(x => x.Key).ToArray()
        }));
    }

    public static ReplayCommand? Command(GameAction action, CombatState state)
    {
        var player = state.RunState.Players[0];
        if (action.OwnerId != player.NetId) return null;
        string hash = Fingerprint(state);
        if (action is PlayCardAction play)
        {
            var card = play.NetCombatCard.ToCardModelOrNull();
            if (card == null) return null;
            return new("card", hash, BattleRecorder.Card(card), play.TargetId, TargetId(play.Target));
        }
        if (action is UsePotionAction potion)
            return new("potion", hash, TargetId: potion.TargetId,
                TargetModelId: TargetId(state.GetCreature(potion.TargetId)), PotionSlot: (int)potion.PotionIndex,
                PotionId: player.GetPotionAtSlotIndex((int)potion.PotionIndex)?.Id.ToString());
        if (action is DiscardPotionGameAction)
        {
            int slot = (int)(uint)AccessTools.Field(typeof(DiscardPotionGameAction), "_potionSlotIndex").GetValue(action)!;
            return new("discard", hash, PotionSlot: slot, PotionId: player.GetPotionAtSlotIndex(slot)?.Id.ToString());
        }
        return action is EndPlayerTurnAction ? new("end_turn", hash) : null;
    }

    private static string? TargetId(Creature? creature) => creature?.Monster?.Id.ToString() ?? creature?.Player?.Character.Id.ToString();

    public static void Queued(GameAction action)
    {
        if (!Active || action is not (PlayCardAction or UsePotionAction or DiscardPotionGameAction or EndPlayerTurnAction)) return;
        if (_submitting) _pending = action;
        else Stop("检测到手动操作，重放已停止");
    }

    public static void Tick()
    {
        if (!Active) return;
        try
        {
            var state = BattleRecorder.CurrentCombat;
            if (state == null || BattleRecorder.CurrentRecording?.BattleId != _battleId)
            { Stop("战斗已切换"); return; }
            if (_pending != null)
            {
                if (!_pending.CompletionTask.IsCompleted) { CheckTimeout(); return; }
                if (_pending.Exception != null) { Stop("游戏执行操作时发生异常，已停止"); return; }
                _pending = null;
                _lastProgress = Environment.TickCount64;
            }
            if (_destination!.Reached(_timeline!))
            { Stop($"已到达：{_destination.Description}，交还手动操作"); return; }
            var player = state.RunState.Players[0];
            var manager = CombatManager.Instance;
            if (!ReplayPreview.ReadyForAction(state.CurrentSide == CombatSide.Player,
                player.PlayerCombatState?.Phase == PlayerTurnPhase.Play,
                RunManager.Instance.ActionQueueSynchronizer.CombatState == ActionSynchronizerCombatState.PlayPhase,
                RunManager.Instance.ActionExecutor.IsRunning || RunManager.Instance.ActionExecutor.IsPaused ||
                !manager.IsInProgress || manager.IsPaused || manager.PlayerActionsDisabled || manager.IsPlayerReadyToEndTurn(player)))
            { CheckTimeout(); return; }
            int turn = player.PlayerCombatState!.TurnNumber;
            var next = _timeline!.Next;
            if (next == null) { Stop("所选尝试已执行完毕，交还手动操作"); return; }
            if (next.Type != "replay_action") { CheckTimeout(); return; }
            if (next.PlayerTurn != turn) { Stop("回合与记录不一致，已停止"); return; }
            var command = ReplayJson.Read<ReplayCommand>(next.Data!);
            if (command.Fingerprint != Fingerprint(state)) { Stop("手牌、资源或战场状态与记录不一致，已停止"); return; }
            var target = state.GetCreature(command.TargetId);
            if (command.TargetId != null && (target == null || TargetId(target) != command.TargetModelId))
            { Stop("目标与记录不一致，已停止"); return; }
            _timeline.Advance();
            _lastProgress = Environment.TickCount64;
            _submitting = true;
            try
            {
                switch (command.Kind)
                {
                    case "card":
                        var matches = player.PlayerCombatState.Hand.Cards.Where(c => ReplayTimeline.SameCard(command.Card!, BattleRecorder.Card(c))).ToList();
                        if (matches.Count != 1 || !matches[0].CanPlayTargeting(target) || !matches[0].TryManualPlay(target))
                            Stop("卡牌无法唯一定位或无法打出，已停止");
                        break;
                    case "potion":
                    case "discard":
                        int slot = command.PotionSlot ?? -1;
                        if (slot < 0 || slot >= player.PotionSlots.Count || player.PotionSlots[slot]?.Id.ToString() != command.PotionId)
                        { Stop("药水栏位与记录不一致，已停止"); break; }
                        if (command.Kind == "potion") player.PotionSlots[slot]!.EnqueueManualUse(target);
                        else RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new DiscardPotionGameAction(player, (uint)slot, true));
                        break;
                    case "end_turn":
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
                        break;
                    default: Stop("不支持的操作，已停止"); break;
                }
                if (Active && _pending == null) Stop("操作未进入游戏队列，已停止");
            }
            finally { _submitting = false; }
        }
        catch (Exception error)
        {
            Stop("重放异常，已停止：" + error.Message);
            MainFile.Logger.Error($"Replay stopped: {error}");
        }
    }

    private static void CheckTimeout()
    {
        if (Environment.TickCount64 - _lastProgress > 30000)
            Stop("等待超过 30 秒；可能遇到未支持的选择，请手动操作");
    }

    public static void ObserveCards(IEnumerable<CardModel?> cards)
    {
        if (!Active) return;
        var actual = cards.OfType<CardModel>().Select(BattleRecorder.Card).ToList();
        var next = _timeline?.Next;
        if (next?.Type != "cards_selected" || !ReplayTimeline.Cards(next).SequenceEqual(actual))
        { Stop("选牌结果与记录不一致，已停止"); return; }
        _timeline!.Advance();
        _lastProgress = Environment.TickCount64;
    }

    private sealed class ReplaySelector : ICardSelector
    {
        public async Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var candidates = options.ToList();
            var selected = new List<CardModel>();
            var next = _timeline?.Next;
            if (Active && next?.Type == "cards_selected")
            {
                foreach (var expected in ReplayTimeline.Cards(next))
                {
                    var matches = candidates.Where(c => !selected.Contains(c) && ReplayTimeline.SameCard(expected, BattleRecorder.Card(c))).ToList();
                    if (matches.Count != 1) { selected.Clear(); break; }
                    selected.Add(matches[0]);
                }
                if (selected.Count == ReplayTimeline.Cards(next).Count && selected.Count >= minSelect && selected.Count <= maxSelect)
                    return selected;
            }
            Stop("选牌与记录不匹配，请在弹窗中手动选择");
            var prefs = new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, minSelect, maxSelect)
            { RequireManualConfirmation = true };
            var screen = NSimpleCardSelectScreen.Create(candidates, prefs);
            NOverlayStack.Instance!.Push(screen);
            return await screen.CardsSelected();
        }
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
            => throw new NotSupportedException("战后奖励不属于战斗重放");
    }
}

[HarmonyPatch(typeof(GameAction), nameof(GameAction.Execute))]
internal static class ReplayActionRecordPatch
{
    private static void Prefix(GameAction __instance)
    {
        if (__instance.State.ToString() == "WaitingForExecution") BattleRecorder.RecordReplayAction(__instance);
    }
}

[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue))]
internal static class ReplayQueuePatch
{
    private static void Prefix(GameAction action) => ReplayDriver.Queued(action);
}

[HarmonyPatch(typeof(CardSelectCmd), "get_LocalSelector")]
internal static class ReplaySelectorPatch
{
    private static void Postfix(ref ICardSelector? __result)
    {
        if (ReplayDriver.CanSelectAutomatically && __result == null) __result = ReplayDriver.Selector;
        else if (RunReplayDriver.Active && __result == null) __result = RunReplayDriver.Selector;
    }
}

internal static partial class BattleRecorder
{
    public static void RecordReplayAction(GameAction action) => Safe(() =>
    {
        if (_combat == null || _file == null || _combat.RunState.Players[0].PlayerCombatState == null ||
            action is not (PlayCardAction or UsePotionAction or DiscardPotionGameAction or EndPlayerTurnAction)) return;
        var command = ReplayDriver.Command(action, _combat);
        if (command != null) Record(_combat, "replay_action", command);
    });
}
