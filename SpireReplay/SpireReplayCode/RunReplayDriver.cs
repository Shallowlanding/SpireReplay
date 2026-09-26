using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.TestSupport;
using System.Text.Json;
using static SpireReplay.SpireReplayCode.RunReplayPlan;
using ICardSelector = MegaCrit.Sts2.Core.TestSupport.ICardSelector;
using Environment = System.Environment;

namespace SpireReplay.SpireReplayCode;

internal static class RunReplayDriver
{
    public static bool Active { get; private set; }
    public static string Status { get; private set; } = "选择历史版本后开始整局重打";
    public static readonly ICardSelector Selector = new WorldSelector();
    private static RunRecording? _source;
    private static RunFloorTarget? _target;
    private static string? _runId;
    private static RunRoomRecord? _room;
    private static List<RecordedEvent> _steps = [];
    private static int _cursor;
    private static Task? _pending;
    private static Task? _rewardPending;
    private static bool _awaitingInput;
    private static string? _startedBattle;
    private static long _progress;
    private static long _nextTick;
    private static RecordedEvent? Next => _cursor < _steps.Count ? _steps[_cursor] : null;

    public static void Start(RunRecording? source, RunFloorTarget? target = null)
    {
        Stop("正在校验整局记录");
        var current = BattleRecorder.WholeRun?.Data;
        if (source == null || current == null)
        { Status = "请先进入对局并选择历史版本"; return; }
        CancelledSelectionFilter.Clean(source);
        RecordingLocation.NormalizeArchitect(source);
        if (Mismatch(source, current) is { } reason) { Status = reason; return; }
        var state = RunManager.Instance.DebugOnlyGetState();
        if (target != null && (!source.Rooms.Any(r => r.Act == target.Act && r.Floor == target.Floor) ||
            state != null && target.IsPast(state.CurrentActIndex + 1, RecordingLocation.ArchiveFloor(state.TotalFloor, state.CurrentRoom?.ModelId?.ToString()))))
        { Status = "目标小层不存在或已经经过，请重新选择或 SL"; return; }
        _target = target;
        _source = source; _runId = current.RunId; _room = null; _startedBattle = null;
        _progress = Environment.TickCount64; _nextTick = 0; Active = true;
        Status = "整局重打进行中，可随时停止";
    }
    public static void Stop(string message)
    {
        if (Active) ReplayDriver.Stop(message);
        Active = false; _pending = null; _rewardPending = null; _awaitingInput = false; _room = null;
        _steps = []; _cursor = 0;
        Status = message;
    }
    public static void Observe(RecordedEvent entry)
    {
        try
        {
        if (!Active || BattleRecorder.CurrentRecording != null) return;
        if (Failed(entry)) { Stop("游戏操作取消或失败，已停止整局重打"); return; }
        if (!IsInput(entry)) return;
        if (Next == null || Signature(Next) != Signature(entry))
        { Stop("当前操作与历史记录不同，整局重打已停止"); return; }
        _cursor++; _awaitingInput = false; _progress = Environment.TickCount64;
        }
        catch (Exception e) { Stop("操作记录无法校验，已停止：" + e.Message); }
    }
    private static T? Field<T>(object node, string field) => (T?)AccessTools.Field(node.GetType(), field)?.GetValue(node);
    private static object? Call(object node, string method, params object?[] args) => AccessTools.Method(node.GetType(), method).Invoke(node, args);
    private static IEnumerable<T> Nodes<T>(Node? root) where T : Node
    {
        if (root == null) yield break;
        if (root is T match) yield return match;
        foreach (Node child in root.GetChildren()) foreach (var item in Nodes<T>(child)) yield return item;
    }
    private static bool Click(NClickableControl? button)
    {
        if (button == null || !button.IsInsideTree() || !button.IsVisibleInTree() || !button.IsEnabled) return false;
        button.ForceClick(); return true;
    }
    private static void Wait()
    {
        if (Environment.TickCount64 - _progress > 30000)
            Stop("等待操作或页面超时，可能遇到尚不支持的选择；已交还手动操作");
    }
    public static void Tick()
    {
        if (!Active) return;
        try
        {
            var manager = RunManager.Instance;
            if (!manager.IsInProgress || manager.IsCleaningUp || BattleRecorder.WholeRun?.Data.RunId != _runId)
            { Stop("已离开对局，整局重打停止"); return; }
            if (Environment.TickCount64 < _nextTick) return;
            _nextTick = Environment.TickCount64 + 250;
            var run = manager.DebugOnlyGetState();
            if (run == null || run.Players.Count != 1) { Stop("只支持单人对局"); return; }
            if (Nodes<Control>(((SceneTree)Engine.GetMainLoop()).Root).Any(n => n.GetType().Name == "NPauseMenu" && n.IsVisibleInTree()))
            { Stop("游戏已暂停，整局重打停止"); return; }
            var room = run.CurrentRoom;
            if (room == null) { Wait(); return; }
            int archiveFloor = RecordingLocation.ArchiveFloor(run.TotalFloor, room.ModelId?.ToString());
            var recorded = _source!.Rooms.FirstOrDefault(r => r.Act == run.CurrentActIndex + 1 && r.Floor == archiveFloor && r.RoomId == room.Id);
            if (recorded != null && _room != recorded)
            {
                if (recorded.Type != room.RoomType.ToString() || recorded.ModelId != (room.ModelId?.ToString() ?? ""))
                { Stop("当前房间与历史记录不同"); return; }
                _room = recorded; _steps = recorded.Events.Where(IsInput).ToList();
                var live = BattleRecorder.WholeRun!.Find(recorded.Act, recorded.Floor, recorded.RoomId);
                _cursor = MatchingPrefix(_steps, live?.Events ?? []);
                if (_cursor < 0) { Stop("本房间已进行的操作与记录不同，请重开或 SL"); return; }
                _pending = null; _rewardPending = null; _awaitingInput = false; _startedBattle = null; _progress = Environment.TickCount64;
            }
            Status = $"整局重打 · 第 {archiveFloor} 层 · {RunSummary.RoomName(room.RoomType.ToString())}";
            if (BattleRecorder.CurrentRecording is { } battle)
            {
                if (recorded?.Battle == null) { Stop("历史记录没有这场战斗的完整操作"); return; }
                if (_startedBattle != battle.BattleId)
                {
                    _startedBattle = battle.BattleId;
                    ReplayDriver.Start(recorded.Battle, new ReplayDestination(int.MaxValue, "本场战斗记录结束"), true);
                    if (!ReplayDriver.Active) { Stop(ReplayDriver.Status); return; }
                }
                else if (!ReplayDriver.Active && !ReplayDriver.Status.StartsWith("已到达"))
                    Stop("战斗重打停止：" + ReplayDriver.Status);
                else if (!ReplayDriver.Active) Wait();
                else _progress = Environment.TickCount64;
                if (Active) Status += " · " + ReplayDriver.Status;
                return;
            }
            if (Next?.Type == "reward_rerolled") { Stop("记录包含奖励刷新，请手动完成刷新后再继续"); return; }
            // A card reward screen may need input while its parent reward/event task is awaiting it.
            if (Next?.Type == "reward_card_chosen" && NOverlayStack.Instance?.Peek() is NCardRewardSelectionScreen cards)
            { if (!_awaitingInput) ChooseRewardCard(cards, Next); else Wait(); return; }
            if (_rewardPending != null)
            {
                if (!_rewardPending.IsCompleted) { Wait(); return; }
                if (_rewardPending.IsFaulted || _rewardPending.IsCanceled) { Stop("奖励操作失败，整局重打停止"); return; }
                _rewardPending = null; _progress = Environment.TickCount64;
            }
            if (_pending != null)
            {
                if (!_pending.IsCompleted)
                {
                    if (!_awaitingInput && Next?.Type == "reward_selection_started") Dispatch(Next);
                    else if (CanCloseNestedRewards(_awaitingInput, Next) && NOverlayStack.Instance?.Peek() is NRewardsScreen nestedRewards)
                    {
                        // OfferCustom completes only after this page is closed, including when
                        // an explicitly skipped card reward remains in the reward list.
                        if (Click(Field<NProceedButton>(nestedRewards, "_proceedButton")))
                            _nextTick = Environment.TickCount64 + 1000;
                        else Wait();
                    }
                    else Wait();
                    return;
                }
                if (_pending.IsFaulted || _pending.IsCanceled) { Stop("游戏操作未成功完成，整局重打已停止"); return; }
                if (_pending is Task<bool> result && !result.Result) { Stop("购买或休息操作未成功，已停止"); return; }
                _pending = null; _progress = Environment.TickCount64;
            }
            if (_awaitingInput) { Wait(); return; }
            if (_target != null && _target.IsComplete(_source!, BattleRecorder.WholeRun!.Data, run.CurrentActIndex + 1, archiveFloor))
            { Stop($"已完成第 {_target.Floor} 层全部已记录操作，交还手动操作"); return; }
            if (_target?.IsPast(run.CurrentActIndex + 1, archiveFloor) == true)
            { Stop("已到达所选小层之后，重打已停止"); return; }
            if (NMapScreen.Instance is { IsOpen: true, IsTravelEnabled: true, IsTraveling: false } map)
            {
                if (Next != null) { map.Close(); Wait(); }
                else Travel(map, run.CurrentActIndex + 1, archiveFloor);
                return;
            }
            if (recorded == null) { Stop("已到达历史记录未覆盖的房间"); return; }
            if (manager.ActionExecutor.IsRunning || manager.ActionExecutor.IsPaused) { Wait(); return; }
            if (Next is { } next)
            {
                if (room.RoomType.ToString() == "Treasure" && NOverlayStack.Instance?.Peek() == null)
                {
                    var chestRoom = Nodes<NTreasureRoom>(NRun.Instance).FirstOrDefault();
                    if (chestRoom != null && Click(Field<NClickableControl>(chestRoom, "_chestButton"))) return;
                }
                Dispatch(next); return;
            }
            Proceed(room);
        }
        catch (Exception e) { MainFile.Logger.Warn(e.ToString()); Stop("整局重打异常，已停止：" + e.GetBaseException().Message); }
    }
    internal static bool CanCloseNestedRewards(bool awaitingInput, RecordedEvent? next) =>
        !awaitingInput && (next == null || next.Type == "event_option_started");
    private static void Travel(NMapScreen map, int act, int floor)
    {
        if (_target != null && (act == _target.Act && floor == _target.Floor || _target.IsPast(act, floor)))
        { Stop($"已到达第 {_target.Floor} 层终点，不再进入下一层"); return; }
        var next = _source!.Rooms.Where(r => r.Act > act || r.Act == act && r.Floor > floor)
            .OrderBy(r => r.Act).ThenBy(r => r.Floor).ThenBy(r => r.RoomId).FirstOrDefault();
        if (next == null) { Stop("已执行到历史记录末尾"); return; }
        if (next.Act != act || next.MapRow == null || next.MapCol == null)
        { Stop("下一层地图坐标尚未就绪或记录缺失"); return; }
        var point = Nodes<NMapPoint>(map).SingleOrDefault(p => p.Point.coord.row == next.MapRow && p.Point.coord.col == next.MapCol);
        if (point == null || !point.IsEnabled || !(bool)AccessTools.Property(typeof(NMapPoint), "IsTravelable").GetValue(point)!)
        { Stop("历史路线的下一节点当前不可到达"); return; }
        map.OnMapPointSelectedLocally(point);
        _nextTick = Environment.TickCount64 + 1500;
        Wait();
    }
    private static void Dispatch(RecordedEvent entry)
    {
        var manager = RunManager.Instance;
        var d = Details(entry);
        switch (entry.Type)
        {
            case "event_option_started":
                var model = manager.EventSynchronizer.GetLocalEvent();
                var option = Get(d, "option"); int index = Number(option, "index");
                if (model.Id.ToString() != Text(d, "eventId") || index < 0 || index >= model.CurrentOptions.Count ||
                    model.CurrentOptions[index].TextKey != Text(option, "key") || model.CurrentOptions[index].IsLocked)
                { Stop("事件选项与记录不同"); return; }
                if (NEventRoom.Instance == null) { Wait(); return; }
                _awaitingInput = true;
                NEventRoom.Instance.OptionButtonClicked(model.CurrentOptions[index], index);
                _pending = manager.EventSynchronizer.AwaitPendingOptionTasks();
                break;
            case "rest_option_started":
                var options = manager.RestSiteSynchronizer.GetLocalOptions(); int rest = Number(d, "optionIndex");
                if (rest < 0 || rest >= options.Count || options[rest].OptionId != Text(d, "optionId"))
                { Stop("休息选项与记录不同"); return; }
                var restButton = NRestSiteRoom.Instance?.GetButtonForOption(options[rest]);
                _awaitingInput = true;
                if (!Click(restButton)) { _awaitingInput = false; Wait(); }
                break;
            case "shop_purchase_started":
                var shop = NMerchantRoom.Instance;
                if (shop == null) { Wait(); return; }
                if (!shop.Inventory.IsOpen) { shop.OpenInventory(); return; }
                var inventory = shop.Inventory.Inventory;
                if (inventory == null) { Wait(); return; }
                string id = Text(Get(d, "item"), "modelId");
                var items = inventory.AllEntries.Where(e => e.GetType().Name == Text(d, "entryType") && e.IsStocked &&
                    (e switch { MerchantCardEntry c => c.CreationResult?.Card.Id.ToString(), MerchantRelicEntry r => r.Model?.Id.ToString(),
                        MerchantPotionEntry p => p.Model?.Id.ToString(), _ => "" }) == id).ToList();
                if (items.Count != 1 || items[0].Cost != Number(d, "price") || !items[0].EnoughGold)
                { Stop("商店物品、价格或金币与记录不符"); return; }
                _awaitingInput = true;
                _pending = items[0] is MerchantCardRemovalEntry removal ? removal.OnTryPurchaseWrapper(inventory, false, true) : items[0].OnTryPurchaseWrapper(inventory);
                break;
            case "reward_selection_started":
                if (NOverlayStack.Instance?.Peek() is not NRewardsScreen screen) { Wait(); return; }
                var rewards = Nodes<NRewardButton>(screen).Where(b => b.Reward is { } r && !r.SuccessfullySelected &&
                    RewardKey(JsonSerializer.SerializeToElement(new
                    {
                        type = r.GetType().Name,
                        cards = r is CardReward cardReward ? cardReward.Cards.Select(BattleRecorder.Card).ToList() : null,
                        potionId = (r as PotionReward)?.Potion?.Id.ToString(),
                        relicId = (r as RelicReward)?.Relic?.Id.ToString(),
                        gold = r is GoldReward goldReward ? goldReward.Amount : (int?)null
                    })) == RewardKey(d)).ToList();
                if (rewards.Count != 1)
                { Stop(rewards.Count == 0 ? "找不到与记录内容相同的奖励" : "存在多份内容完全相同的奖励，暂需手动选择"); return; }
                if (!rewards[0].IsEnabled || !rewards[0].IsVisibleInTree()) { Wait(); return; }
                _awaitingInput = true;
                _rewardPending = (Task)Call(rewards[0], "GetReward")!;
                break;
            case "potion_use_started":
                Stop("战外使用药水暂需手动完成，完成后可重新开始"); break;
            case "potion_discard_started":
                var player = manager.DebugOnlyGetState()!.Players[0];
                int slot = Number(d, "slotIndex");
                var potion = slot >= 0 && slot < player.MaxPotionCount ? player.GetPotionAtSlotIndex(slot) : null;
                if (potion?.Id.ToString() != Text(d, "potionId")) { Stop("待丢弃药水与记录不同"); return; }
                _awaitingInput = true;
                manager.ActionQueueSynchronizer.RequestEnqueue(new MegaCrit.Sts2.Core.GameActions.DiscardPotionGameAction(player, (uint)slot, false));
                break;
            case "cards_selected": Wait(); break;
            default: Stop("此操作暂需手动完成：" + entry.Type); break;
        }
    }
    private static void ChooseRewardCard(NCardRewardSelectionScreen screen, RecordedEvent entry)
    {
        var gate = Field<TaskCompletionSource<int?>>(screen, "_completionSource");
        if (gate == null || gate.Task.IsCompleted) { Wait(); return; }
        var options = Field<IReadOnlyList<CardCreationResult>>(screen, "_options")!;
        var alternatives = Field<IReadOnlyList<CardRewardAlternative>>(screen, "_extraOptions")!;
        var d = Details(entry); var expected = Get(d, "card");
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var matches = options.Where(o => o.Card.Id.ToString() == Text(expected, "modelId") && o.Card.CurrentUpgradeLevel == Number(expected, "upgradeLevel", 0)).ToList();
            if (matches.Count != 1) { Stop("奖励卡牌与记录不匹配"); return; }
            _awaitingInput = true; Call(screen, "SelectCard", screen.GetCardHolder(matches[0].Card));
        }
        else
        {
            int index = Number(d, "index") - options.Count;
            if (index < 0 || index >= alternatives.Count || alternatives[index].GetType().Name != Text(d, "alternative"))
            { Stop("奖励跳过/替代选项与记录不同，需手动选择"); return; }
            _awaitingInput = true; Call(screen, "OnAlternateRewardSelected", index);
        }
    }
    private static void Proceed(AbstractRoom room)
    {
        if (NOverlayStack.Instance?.Peek() is NRewardsScreen rewards)
        {
            if (!Click(Field<NProceedButton>(rewards, "_proceedButton"))) Wait();
            else _nextTick = Environment.TickCount64 + 1000;
            return;
        }
        if (NOverlayStack.Instance?.ScreenCount > 0) { Wait(); return; }
        if (room is EventRoom)
        {
            if (RunManager.Instance.EventSynchronizer.GetLocalEvent().IsFinished) _pending = NEventRoom.Proceed();
            else Stop("事件还有未记录或暂不支持的选择，已停止");
        }
        else if (NMerchantRoom.Instance is { } shop && room is MerchantRoom)
        {
            if (shop.Inventory.IsOpen) Call(shop.Inventory, "Close");
            else if (!Click(shop.ProceedButton)) Wait();
        }
        else if (NRestSiteRoom.Instance is { } rest && room.RoomType.ToString() == "RestSite")
        { if (!Click(rest.ProceedButton)) Wait(); }
        else if (room.RoomType.ToString() == "Treasure")
        { Treasure(); }
        else if (_source!.Rooms.LastOrDefault() == _room) Stop("已执行到历史记录末尾");
        else Wait();
    }
    private static void Treasure()
    {
        var treasure = Nodes<NTreasureRoom>(NRun.Instance).FirstOrDefault();
        if (treasure == null) { Wait(); return; }
        var wanted = _room!.Events.Where(e => e.Type == "relic_obtain_started").Select(e => Text(Details(e), "relicId")).ToList();
        if (wanted.Count == 0) { Stop("此宝箱未记录明确的遗物领取，需手动处理以避免改变开箱结果"); return; }
        var live = BattleRecorder.WholeRun!.Find(_room.Act, _room.Floor, _room.RoomId)!;
        var obtained = live.Events.Where(e => e.Type == "relic_obtain_started").Select(e => Text(Details(e), "relicId")).ToList();
        if (!obtained.SequenceEqual(wanted.Take(obtained.Count))) { Stop("宝箱遗物与记录不同"); return; }
        if (obtained.Count >= wanted.Count)
        { if (!Click(treasure.ProceedButton)) Wait(); return; }
        var collection = Field<NTreasureRoomRelicCollection>(treasure, "_relicCollection");
        if (collection?.IsVisibleInTree() == true && collection.SingleplayerRelicHolder.IsVisibleInTree())
        {
            var holder = collection.SingleplayerRelicHolder;
            if (holder.Relic.Model.Id.ToString() != wanted[obtained.Count]) { Stop("宝箱待选遗物与记录不同"); return; }
            if (!Click(holder)) Wait(); else _nextTick = Environment.TickCount64 + 1000;
        }
        else if (!Click(Field<NClickableControl>(treasure, "_chestButton"))) Wait();
    }
    private sealed class WorldSelector : ICardSelector
    {
        public async Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var candidates = options.ToList(); var chosen = new List<CardModel>();
            if (Active && Next?.Type == "cards_selected")
            {
                var expected = ReplayTimeline.Cards(Next);
                foreach (var card in expected)
                {
                    var matches = candidates.Where(c => !chosen.Contains(c) && ReplayTimeline.SameCard(card, BattleRecorder.Card(c))).ToList();
                    if (matches.Count != 1) { chosen.Clear(); break; }
                    chosen.Add(matches[0]);
                }
                if (chosen.Count == expected.Count && chosen.Count >= minSelect && chosen.Count <= maxSelect) return chosen;
            }
            Stop("选牌与记录不同，已交还手动选择");
            var prefs = new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, minSelect, maxSelect) { RequireManualConfirmation = true };
            var screen = NSimpleCardSelectScreen.Create(candidates, prefs); NOverlayStack.Instance!.Push(screen);
            return await screen.CardsSelected();
        }
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
            => throw new NotSupportedException("整局奖励使用游戏原生奖励界面");
    }
}
