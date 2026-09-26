using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.TestSupport;
using ICardSelector = MegaCrit.Sts2.Core.TestSupport.ICardSelector;

namespace SpireReplay.SpireReplayCode;

public sealed record TeamStep(ulong Player, int Round, string Kind, string Payload, string Fingerprint, string Label)
{
    public string? PlayerName { get; init; }
    public string? CharacterId { get; init; }
    public string? ActionKind { get; init; }
    public string? Target { get; init; }
}
public sealed class TeamAttempt
{
    public int FormatVersion { get; set; } = 1;
    public string Key { get; set; } = "";
    public string Version { get; set; } = "";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Started { get; set; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "in_progress";
    public List<TeamStep> Steps { get; set; } = [];
}
// Registered by the game's mod message discovery. Never broadcast client commands as host instructions.
public struct TeamReplayMessage : INetMessage
{
    public string Json;
    public bool ShouldBroadcast => false;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter writer) => writer.WriteString(Json);
    public void Deserialize(PacketReader reader)
    {
        int length = reader.ReadInt();
        if (length < 0 || length > 65536) throw new InvalidDataException("Replay packet too large");
        var bytes = new byte[length]; reader.ReadBytes(bytes, length);
        Json = System.Text.Encoding.UTF8.GetString(bytes);
    }
}
public sealed record TeamEnvelope(string Kind, string Key, string Session, int Index = 0, string Text = "", TeamStep? Step = null, int Protocol = 3);

internal static class MultiplayerReplay
{
    public static TeamAttempt? Current { get; private set; }
    public static bool Active { get; private set; }
    public static bool Ready { get; set; }
    public static string Status { get; private set; } = "请所有玩家点击准备，再由主机开始";
    public static bool Host => _net?.Type == NetGameType.Host;
    public static readonly ICardSelector Selector = new TeamSelector();
    private static CombatState? _combat;
    internal static string? CharacterFor(ulong player) => _combat?.RunState.Players.FirstOrDefault(p => p.NetId == player)?.Character.Id.ToString();
    private static INetGameService? _net;
    private static PlayerChoiceSynchronizer? _choices;
    private static string _directory = "", _path = "", _session = "";
    private static TeamAttempt? _source;
    private static int _index, _limit, _lastCommand = -1;
    private static bool _sent, _submitting;
    private static long _progress, _pulse;
    private static bool _waitingReady;
    private static long _readyPoll;
    private static string _startFingerprint = "";
    private static string? _pendingReadySession;
    private static readonly HashSet<ulong> Acks = [];
    private static readonly List<GameAction> PendingActions = [];
    private static readonly Dictionary<GameAction, string> ActionPayloads = [];
    private static readonly List<GameAction> HeldResumes = [];
    private static bool _releasingResume;
    private static bool _orderedResumes;
    private static bool _orderedHooks;
    private static TaskCompletionSource<TeamStep?>? _choiceWait;
    private static TeamStep? _choice, _localStep;
    public static List<TeamAttempt> Attempts() => Directory.Exists(_directory) ? Directory.EnumerateFiles(_directory, "*.json")
        .Select(p => { try { return JsonSerializer.Deserialize<TeamAttempt>(File.ReadAllText(p), ReplayJson.Options); } catch { return null; } })
        .OfType<TeamAttempt>().Where(a => a.Id != Current?.Id && a.Key == Current?.Key).OrderBy(a => a.Started).ToList() : [];
    public static void Setup(CombatState state)
    {
        Reset();
        if (RunManager.Instance.NetService.Type is not (NetGameType.Host or NetGameType.Client)) return;
        _combat = state; _net = RunManager.Instance.NetService;
        string key = ReplayJson.Hash($"{AccessTools.Field(typeof(RunManager), "_startTime").GetValue(RunManager.Instance)}|{state.RunState.Rng.StringSeed}|{state.RunState.CurrentActIndex}|{state.RunState.TotalFloor}|{state.RunState.CurrentRoom?.Id}|{state.Encounter?.Id}|{string.Join(',', state.RunState.Players.Select(p => p.NetId))}");
        ActionPayloads.Clear();
        Current = new TeamAttempt { FormatVersion = 3, Key = key, Version = typeof(CardModel).Module.ModuleVersionId.ToString() };
        _directory = Path.Combine(ProjectSettings.GlobalizePath("user://SpireReplay/recordings"), "multiplayer", key);
        Directory.CreateDirectory(_directory); _path = Path.Combine(_directory, Current.Id + ".json"); Save();
        _net.RegisterMessageHandler<TeamReplayMessage>(Receive);
        _choices = RunManager.Instance.PlayerChoiceSynchronizer;
        _choices.PlayerChoiceReceived += ChoiceReceived;
        TeamReplayPanel.Install();
        Status = Host ? "先手动打一遍；SL 后全员准备，由主机选择记录" : "正在记录全队操作；SL 后点击准备等待主机";
    }
    private static void Save() { if (Current != null) RecordingFile.WriteAtomic(_path, Current); }
    public static void Finish(bool won)
    {
        if (Current == null || Current.Status == "won") return;
        Current.Status = won ? "won" : "lost"; Save(); Stop("战斗结束");
        if (won) foreach (string path in Directory.EnumerateFiles(_directory, "*.json")) if (path != _path) File.Delete(path);
    }
    public static void Reset()
    {
        Stop("战斗已切换", false);
        if (_net != null) _net.UnregisterMessageHandler<TeamReplayMessage>(Receive);
        if (_choices != null) _choices.PlayerChoiceReceived -= ChoiceReceived;
        Current = null; _combat = null; _net = null; _choices = null; Ready = false;
    }
    public static string Fingerprint() => _combat == null ? "" : ReplayJson.Hash(JsonSerializer.Serialize(new
    {
        _combat.RoundNumber,
        players = _combat.RunState.Players.Select(p => new { p.NetId, turn = p.PlayerCombatState?.TurnNumber,
            energy = p.PlayerCombatState?.Energy, stars = p.PlayerCombatState?.Stars,
            piles = p.PlayerCombatState?.AllPiles.Select(x => new { x.Type, cards = x.Cards.Select(c => new { id = c.Id.ToString(), c.CurrentUpgradeLevel, combatId = BattleRecorder.Card(c).CombatCardId }) }),
            potions = p.PotionSlots.Select(x => x?.Id.ToString()) }),
        creatures = _combat.Creatures.Select(c => new { c.CombatId, c.CurrentHp, c.MaxHp, c.Block, powers = c.Powers.Select(p => new { id = p.Id.ToString(), p.Amount }) }),
        rng = _combat.RunState.Rng.ToSerializable().Rngs.OrderBy(x => x.Key).ToArray()
    }));
    private static string Encode(IPacketSerializable value) { var writer = new PacketWriter(); value.Serialize(writer); return Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition); }
    private static T Decode<T>(string data) where T : IPacketSerializable, new() { var reader = new PacketReader(); reader.Reset(Convert.FromBase64String(data)); return reader.Read<T>(); }
    private static string EncodeAction(GameAction action)
    {
        var writer = new PacketWriter(); var netAction = action.ToNetAction(); writer.WriteByte((byte)netAction.ToId()); netAction.Serialize(writer);
        return Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition);
    }
    private static INetAction DecodeAction(string payload)
    {
        var reader = new PacketReader(); reader.Reset(Convert.FromBase64String(payload));
        if (!ActionTypes.TryGetActionType(reader.ReadByte(), out var type) || type == null) throw new InvalidDataException("未知动作");
        var action = (INetAction)Activator.CreateInstance(type)!; action.Deserialize(reader); return action;
    }
    public static void RecordAction(GameAction action)
    {
        try { RecordActionCore(action); }
        catch (Exception e) { Stop("多人记录异常：" + e.Message); MainFile.Logger.Warn(e.ToString()); }
    }
    private static void RecordActionCore(GameAction action)
    {
        if (_combat != null && Current != null && action is GenericHookGameAction hook)
            ActionPayloads[action] = "hook:" + hook.HookId;
        if (_combat == null || Current == null || action is not (PlayCardAction or UsePotionAction or DiscardPotionGameAction or EndPlayerTurnAction or UndoEndPlayerTurnAction)) return;
        ActionPayloads[action] = EncodeAction(action);
        if (Active && Host) PendingActions.Add(action);
        string label = action is PlayCardAction play ? play.NetCombatCard.ToCardModelOrNull()?.Title ?? "卡牌" : action switch
        { EndPlayerTurnAction => "结束回合", UndoEndPlayerTurnAction => "取消结束回合", UsePotionAction => "使用药水", DiscardPotionGameAction => "丢弃药水", _ => action.GetType().Name };
        Observe(new TeamStep(action.OwnerId, _combat.RoundNumber, action is EndPlayerTurnAction ? "end" : "action", EncodeAction(action), Fingerprint(), label)
        {
            PlayerName = _combat.RunState.Players.FirstOrDefault(p => p.NetId == action.OwnerId)?.Creature.Name,
            CharacterId = CharacterFor(action.OwnerId),
            ActionKind = action is UsePotionAction or DiscardPotionGameAction ? "potion" : "card",
            Target = action is PlayCardAction targeted ? targeted.Target?.Name : null
        });
    }
    private static void ChoiceReceived(Player player, uint id, NetPlayerChoiceResult result)
    {
        try { ChoiceReceivedCore(player, id, result); }
        catch (Exception e) { Stop("多人选牌记录异常：" + e.Message); MainFile.Logger.Warn(e.ToString()); }
    }
    private static void ChoiceReceivedCore(Player player, uint id, NetPlayerChoiceResult result)
    {
        if (_combat == null || Current == null || !CombatManager.Instance.IsInProgress) return;
        string label = "选择";
        try
        {
            var choice = PlayerChoiceResult.FromNetData(player, _combat.RunState, result);
            if (choice.ChoiceType != PlayerChoiceType.Index) label = "选择 " + string.Join("、", choice.AsCards(choice.ChoiceType).Select(c => c.Title));
        }
        catch { /* Some native choices contain indexes rather than cards. */ }
        Observe(new TeamStep(player.NetId, _combat.RoundNumber, "choice", Encode(result), "", label) { PlayerName = player.Creature.Name, CharacterId = CharacterFor(player.NetId) });
    }
    private static bool Same(TeamStep a, TeamStep b) => a.Player == b.Player && a.Kind == b.Kind && a.Round == b.Round && a.Payload == b.Payload && a.Fingerprint == b.Fingerprint;
    public static void RecordResume(GameAction action)
    {
        try
        {
            if (Current == null || _combat == null || Active && (!_orderedResumes || action is GenericHookGameAction && !_orderedHooks) || !ActionPayloads.TryGetValue(action, out var payload)) return;
            if (Active && Host && !PendingActions.Contains(action)) PendingActions.Add(action);
            Observe(new TeamStep(action.OwnerId, _combat.RoundNumber, "resume", payload, Fingerprint(), "选牌后继续结算")
            { PlayerName = _combat.RunState.Players.FirstOrDefault(p => p.NetId == action.OwnerId)?.Creature.Name, CharacterId = CharacterFor(action.OwnerId) });
        }
        catch (Exception e) { Stop("多人恢复结算记录异常：" + e.Message); MainFile.Logger.Warn(e.ToString()); }
    }
    public static bool AllowResume(GameAction action)
    {
        if (!Active || !_orderedResumes || action is GenericHookGameAction && !_orderedHooks || _releasingResume || !ActionPayloads.ContainsKey(action)) return true;
        if (!HeldResumes.Contains(action)) HeldResumes.Add(action);
        return false;
    }
    private static void ReleaseResume(GameAction action)
    {
        HeldResumes.Remove(action); _releasingResume = true;
        try { RunManager.Instance.ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice(action); }
        finally { _releasingResume = false; }
    }
    private static void Observe(TeamStep step)
    {
        Current!.Steps.Add(step); Save();
        if (!Active || !Host || _source == null) return;
        if (_index >= _limit || !Same(_source.Steps[_index], step)) { Stop("执行顺序或选择与记录不一致"); return; }
        _index++; _sent = false; _progress = System.Environment.TickCount64;
    }
    public static int StopIndex(TeamAttempt source, int round)
    {
        // Keep earlier players' end-turn inputs if other players act afterwards,
        // but leave the final trailing end-turn actions to the players.
        return source.Steps.FindLastIndex(s => s.Round <= round && s.Kind != "end") + 1;
    }
    public static void Start(TeamAttempt source, int round)
        => StartToIndex(source, StopIndex(source, round));
    public static void StartToIndex(TeamAttempt source, int endExclusive)
    {
        if (!Host || Current == null || _combat == null) return;
        Stop("正在等待全员准备");
        if (!TeamTimeline.SupportsReplay(source)) { Status = "此旧记录缺少选牌后结算顺序，请用新版重新录制一次再 SL"; return; }
        if (source.Key != Current.Key || source.Version != Current.Version) { Status = "队伍、战斗或版本不匹配"; return; }
        if (Current.Steps.Count > source.Steps.Count || !Current.Steps.Zip(source.Steps).All(p => Same(p.First, p.Second))) { Status = "当前操作不同，请全队 SL 后再试"; return; }
        _limit = TeamTimeline.CompleteSelection(source, endExclusive); _index = Current.Steps.Count;
        if (_index >= _limit) { Status = "已经到达所选位置"; return; }
        _source = source; _session = Guid.NewGuid().ToString("N"); Acks.Clear(); Acks.Add(_net!.NetId);
        Active = true; Ready = true; _sent = false; _progress = System.Environment.TickCount64;
        _orderedResumes = source.FormatVersion >= 2;
        _orderedHooks = source.FormatVersion >= 3;
        Current.FormatVersion = Math.Min(Current.FormatVersion, source.FormatVersion);
        _waitingReady = true; _readyPoll = System.Environment.TickCount64;
        _startFingerprint = Fingerprint();
        Send(new TeamEnvelope("begin", Current.Key, _session, Index: source.FormatVersion, Text: _startFingerprint));
    }
    private static void Send(TeamEnvelope message, ulong? peer = null)
    {
        if (_net?.IsConnected != true) return;
        var packet = new TeamReplayMessage { Json = JsonSerializer.Serialize(message) };
        if (peer.HasValue) _net.SendMessage(packet, peer.Value); else _net.SendMessage(packet);
    }
    public static void Stop(string reason, bool notify = true)
    {
        bool wasActive = Active || _pendingReadySession != null;
        string notifySession = Active ? _session : _pendingReadySession ?? _session;
        _pendingReadySession = null;
        Active = false; _source = null; _choice = null; _localStep = null; PendingActions.Clear(); _choiceWait?.TrySetResult(null); _choiceWait = null;
        _waitingReady = false;
        Status = reason;
        if (wasActive && notify && Current != null) Send(new TeamEnvelope("stop", Current.Key, notifySession, Text: reason));
        foreach (var action in HeldResumes.ToArray())
        {
            try
            {
                if (RunManager.Instance.IsInProgress && !RunManager.Instance.IsCleaningUp && action.State.ToString() == "GatheringPlayerChoice") ReleaseResume(action);
            }
            catch (Exception e) { MainFile.Logger.Warn("交还选牌结算失败：" + e.Message); }
        }
        HeldResumes.Clear();
    }
    private static void Receive(TeamReplayMessage packet, ulong sender)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<TeamEnvelope>(packet.Json);
            if (msg == null || msg.Protocol != 3 || Current == null || msg.Key != Current.Key || !_combat!.RunState.Players.Any(p => p.NetId == sender)) return;
            bool authority = _net is NetClientGameService client && sender == client.HostNetId;
            if (msg.Kind == "begin" && authority)
            {
                if (!Ready)
                {
                    _pendingReadySession = msg.Session;
                    Status = "主机已选择终点；点击准备后将自动开始";
                    Send(new TeamEnvelope("waiting", Current.Key, msg.Session)); return;
                }
                if (msg.Text != Fingerprint())
                {
                    string reason = $"玩家 …{_net!.NetId % 10000:D4} 战斗状态与主机不同，请全队 SL 后重试";
                    Status = reason; Send(new TeamEnvelope("reject", Current.Key, msg.Session, Text: reason)); return;
                }
                Stop("正在同步", false); _session = msg.Session; _lastCommand = -1; Active = true; _progress = System.Environment.TickCount64;
                _orderedResumes = msg.Index >= 2;
                _orderedHooks = msg.Index >= 3;
                Current.FormatVersion = Math.Min(Current.FormatVersion, msg.Index);
                Send(new TeamEnvelope("ack", Current.Key, _session)); return;
            }
            if (authority && msg.Kind == "stop" && msg.Session == _pendingReadySession) { Stop(msg.Text, false); return; }
            if (msg.Session != _session || !Active) return;
            if (authority && msg.Kind == "pulse") { _progress = System.Environment.TickCount64; return; }
            if (Host && msg.Kind == "ack") { Acks.Add(sender); return; }
            if (Host && msg.Kind == "waiting") return;
            if (msg.Kind == "stop" || Host && msg.Kind == "reject") { Stop(msg.Text, Host); return; }
            if (!authority || msg.Kind != "step" || msg.Step == null || msg.Step.Player != _net!.NetId || msg.Index <= _lastCommand) return;
            _lastCommand = msg.Index; _progress = System.Environment.TickCount64; Execute(msg.Step);
        }
        catch (Exception e) { Stop("多人重放消息异常：" + e.Message); MainFile.Logger.Warn(e.ToString()); }
    }
    public static void Tick()
    {
        if (Current != null && (!RunManager.Instance.IsInProgress || RunManager.Instance.IsCleaningUp)) { Reset(); return; }
        if (!Active) return;
        if (_choice is { } waitingChoice && _choiceWait == null && OpenHandSelection.IsOpen) Execute(waitingChoice);
        if (!Active) return;
        if (_net?.IsConnected != true) { Stop("连接中断"); return; }
        if (!_waitingReady && System.Environment.TickCount64 - _progress > 30000) { Stop("等待超过 30 秒，请检查掉线或未支持的选择"); return; }
        if (_localStep is { } local && Fingerprint() == local.Fingerprint && !RunManager.Instance.ActionExecutor.IsRunning) { _localStep = null; Execute(local); }
        if (!Host || _source == null) return;
        if (PendingActions.Any(a => a.Exception != null)) { Stop("动作执行异常，已停止"); return; }
        PendingActions.RemoveAll(a => a.CompletionTask.IsCompleted);
        if (System.Environment.TickCount64 - _pulse > 5000) { _pulse = System.Environment.TickCount64; Send(new TeamEnvelope("pulse", Current!.Key, _session)); }
        if (Acks.Count != _combat!.RunState.Players.Count)
        {
            var missing = _combat.RunState.Players.Where(p => !Acks.Contains(p.NetId)).ToList();
            Status = "等待准备：" + string.Join("、", missing.Select(p => p.Creature.Name)) + "；全员准备后自动执行";
            if (System.Environment.TickCount64 - _readyPoll >= 1000)
            {
                _readyPoll = System.Environment.TickCount64;
                foreach (var player in missing)
                    Send(new TeamEnvelope("begin", Current!.Key, _session, Index: _source.FormatVersion, Text: _startFingerprint), player.NetId);
            }
            return;
        }
        if (_waitingReady)
        {
            if (Fingerprint() != _startFingerprint) { Stop("等待准备期间战斗状态已变化，请重新选择执行"); return; }
            _waitingReady = false; _progress = System.Environment.TickCount64;
        }
        Status = $"同步重打 {_index}/{_limit}";
        if (_index >= _limit) { if (PendingActions.Count == 0 && !RunManager.Instance.ActionExecutor.IsRunning) Stop("已完成所选操作及其选择，交还手动操作"); return; }
        if (_sent) { Status += " · 等待队友执行已发送操作"; return; }
        var step = _source.Steps[_index];
        if (step.Round != _combat.RoundNumber) { Status += $" · 等待进入第 {step.Round} 回合"; return; }
        if (step.Kind != "choice")
        {
            if (RunManager.Instance.ActionExecutor.IsRunning || RunManager.Instance.ActionExecutor.IsPaused ||
                step.Kind != "resume" && RunManager.Instance.ActionQueueSynchronizer.CombatState != ActionSynchronizerCombatState.PlayPhase) { Status += " · 等待游戏结算"; return; }
            if (Fingerprint() != step.Fingerprint) { Status += $" · 等待状态匹配：{step.Label}"; return; }
        }
        _sent = true;
        if (step.Player == _net!.NetId) Execute(step);
        else Send(new TeamEnvelope("step", Current!.Key, _session, _index, Step: step), step.Player);
    }
    private static void Execute(TeamStep step)
    {
        if (step.Kind == "choice")
        {
            if (_choiceWait == null && OpenHandSelection.IsOpen)
            {
                var owner = _combat!.RunState.Players.Single(p => p.NetId == _net!.NetId);
                var result = PlayerChoiceResult.FromNetData(owner, _combat.RunState, Decode<NetPlayerChoiceResult>(step.Payload));
                if (result.ChoiceType != PlayerChoiceType.CombatCard) { Stop("已打开的手牌选择类型与记录不符"); return; }
                _choice = null;
                if (OpenHandSelection.Complete(result.AsCombatCards().ToList())) return;
            }
            _choice = step; _choiceWait?.TrySetResult(step); return;
        }
        if (Fingerprint() != step.Fingerprint || RunManager.Instance.ActionExecutor.IsRunning) { _localStep = step; return; }
        if (step.Kind == "resume")
        {
            var pending = HeldResumes.FirstOrDefault(a => a.OwnerId == step.Player && ActionPayloads.GetValueOrDefault(a) == step.Payload);
            if (pending == null) { _localStep = step; return; }
            ReleaseResume(pending); return;
        }
        var player = _combat!.RunState.Players.Single(p => p.NetId == step.Player);
        var action = DecodeAction(step.Payload).ToGameAction(player);
        if (action is not (PlayCardAction or UsePotionAction or DiscardPotionGameAction or EndPlayerTurnAction or UndoEndPlayerTurnAction)) { Stop("不支持此重打动作"); return; }
        if (action is PlayCardAction play && (play.NetCombatCard.ToCardModelOrNull() is not { } card || card.Owner != player || !card.CanPlayTargeting(play.Target))) { Stop("卡牌无法打出"); return; }
        _submitting = true;
        try { RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action); }
        finally { _submitting = false; }
    }
    public static void Queued(GameAction action)
    {
        if (Active && !_submitting && action is PlayCardAction or UsePotionAction or DiscardPotionGameAction or EndPlayerTurnAction or UndoEndPlayerTurnAction) Stop("检测到手动操作，全队重打已停止");
    }
    private sealed class TeamSelector : ICardSelector
    {
        public async Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var candidates = options.ToList();
            var player = _combat!.RunState.Players.Single(p => p.NetId == _net!.NetId);
            var synchronizer = _choices!;
            int slot = _combat.RunState.Players.ToList().FindIndex(p => p.NetId == player.NetId);
            // CardSelectCmd reserves this before calling LocalSelector, but its LocalSelector
            // branch omits SyncLocalChoice. Capture the ID before awaiting another player's action.
            uint choiceId = checked(synchronizer.ChoiceIds[slot] - 1);
            PlayerChoiceType type = candidates.All(c => c.Pile?.IsCombatPile == true) ? PlayerChoiceType.CombatCard :
                candidates.All(c => c.Pile?.Type == PileType.Deck) ? PlayerChoiceType.DeckCard : PlayerChoiceType.Index;
            _choiceWait = new TaskCompletionSource<TeamStep?>();
            var step = _choice ?? await _choiceWait.Task;
            _choice = null; _choiceWait = null;
            if (step != null && Active)
            {
                try
                {
                    var choice = PlayerChoiceResult.FromNetData(player, _combat.RunState, Decode<NetPlayerChoiceResult>(step.Payload));
                    type = choice.ChoiceType;
                    var selected = choice.ChoiceType == PlayerChoiceType.Index ? choice.AsIndexes().Where(i => i >= 0).Select(i => candidates[i]).ToList() : choice.AsCards(choice.ChoiceType).ToList();
                    if (selected.Count >= minSelect && selected.Count <= maxSelect && selected.Distinct().Count() == selected.Count && selected.All(candidates.Contains))
                    {
                        synchronizer.SyncLocalChoice(player, choiceId, choice);
                        return selected;
                    }
                }
                catch (Exception e) { MainFile.Logger.Warn("多人选牌：" + e.Message); }
                Stop("选牌与记录不符，请手动选择");
            }
            var screen = NSimpleCardSelectScreen.Create(candidates, new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, minSelect, maxSelect) { RequireManualConfirmation = true });
            NOverlayStack.Instance!.Push(screen);
            var manual = (await screen.CardsSelected()).ToList();
            var result = type == PlayerChoiceType.Index ? PlayerChoiceResult.FromIndexes(manual.Select(c => candidates.IndexOf(c)).ToList()) : PlayerChoiceResult.FromCards(manual, type);
            synchronizer.SyncLocalChoice(player, choiceId, result);
            return manual;
        }
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives) => throw new NotSupportedException();
    }
}
