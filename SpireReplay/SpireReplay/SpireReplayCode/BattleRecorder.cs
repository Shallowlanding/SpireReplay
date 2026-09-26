using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireReplay.SpireReplayCode;

internal static partial class BattleRecorder
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static IRunState? _run;
    private static CombatState? _combat;
    private static RecordingFile? _file;
    private static string _sessionId = "";
    private static int _lastPlayerTurn = 1;
    private static readonly Dictionary<CardPlay, int> PendingPlays = [];
    internal static CombatState? CurrentCombat => _combat;
    internal static BattleRecording? CurrentRecording => _file?.Recording;
    internal static BattleAttempts? Attempts { get; private set; }

    public static void Initialize()
    {
        if (_initialized) return;
        var manager = CombatManager.Instance;
        manager.CombatSetUp += state => Safe(() => Start(state));
        manager.CombatBegan += state => Safe(() => Record(state, "combat_started"));
        manager.TurnStarted += state => Safe(() => Record(state, "turn_started"));
        manager.TurnEnded += state => Safe(() => Record(state, "turn_ended"));
        manager.PlayerEndedTurn += (player, canBackOut) => Safe(() =>
            Record(player.Creature.CombatState, "end_turn", new
            {
                source = RunManager.Instance.ActionExecutor.CurrentlyRunningAction is EndPlayerTurnAction
                    ? "player_action" : "game_effect",
                canBackOut
            }));
        manager.PlayerUnendedTurn += player => Safe(() =>
            Record(player.Creature.CombatState, "undo_end_turn"));
        manager.CombatWon += room => Safe(() => Finish(room, "won"));
        manager.CombatEnded += room => Safe(() => Finish(room, "lost"));
        manager.CombatWon += _ => Safe(() => MultiplayerReplay.Finish(true));
        manager.CombatEnded += _ => Safe(() => MultiplayerReplay.Finish(false));
        _initialized = true;
    }

    private static void Start(CombatState state)
    {
        CloseInterrupted();
        MultiplayerReplay.Setup(state);
        if (state.RunState.Players.Count != 1 ||
            RunManager.Instance.NetService.Type != NetGameType.Singleplayer) return;

        IRunState run = state.RunState;
        EnsureSession(run);
        _combat = state;
        _lastPlayerTurn = 1;
        int battleNumber = run.MapPointHistory.SelectMany(act => act)
            .SelectMany(point => point.Rooms)
            .Count(room => room.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss);
        var recording = new BattleRecording
        {
            RunId = RunIdentity(run),
            GameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown",
            GameAssemblyId = typeof(CardModel).Module.ModuleVersionId.ToString(),
            SessionId = _sessionId,
            Seed = run.Rng.StringSeed,
            CharacterId = run.Players[0].Character.Id.ToString(),
            GameMode = run.GameMode.ToString(),
            Ascension = run.AscensionLevel,
            Act = run.CurrentActIndex + 1,
            ActFloor = run.ActFloor,
            TotalFloor = run.TotalFloor,
            BattleNumber = Math.Max(1, battleNumber),
            RoomId = run.CurrentRoom?.Id,
            EncounterId = state.Encounter?.Id.ToString() ?? "unknown",
            RoomType = run.CurrentRoom?.RoomType.ToString() ?? "unknown"
        };
        string root = ProjectSettings.GlobalizePath("user://SpireReplay/recordings");
        Attempts = new BattleAttempts(root, recording.RunId, recording);
        recording.AttemptNumber = Attempts.ReadAll().Select(b => b.AttemptNumber).DefaultIfEmpty(0).Max() + 1;
        _file = new RecordingFile(Attempts.DirectoryPath, recording);
        _lastBattleId = recording.BattleId;
        _lastBattleLocation = new RecordingLocation(recording.Act, recording.TotalFloor, recording.RoomId);
        _lastBattleRoom = run.CurrentRoom;
        Record(state, "combat_setup");
        ReplayDriver.Stop(recording.AttemptNumber == 1 ? "先手动操作；SL 后可选择这次记录重放" :
            $"当前第 {recording.AttemptNumber} 次尝试，可选择之前的记录");
        ReplayPanel.EnsureInstalled();
        MainFile.Logger.Info($"Recording battle to {_file.FilePath}");
    }

    public static void CardPlay(ICombatState state, CardPlay play, bool finished) => Safe(() =>
    {
        if (!ReferenceEquals(state, _combat) || _file == null) return;
        if (finished)
        {
            if (PendingPlays.Remove(play, out int startSequence))
                Record(state, "card_play_finished", new { startedSequence = startSequence });
            return;
        }
        PendingPlays[play] = _file.Recording.Events.Count + 1;
        Record(state, "card_play_started",
            new CardPlaySnapshot(Card(play.Card), play.Target == null ? null : new TargetSnapshot(
                play.Target.CombatId, play.Target.Monster?.Id.ToString() ?? play.Target.Player?.Character.Id.ToString() ?? "unknown",
                play.Target.Name),
                play.IsAutoPlay, play.PlayIndex, play.PlayCount,
                play.Resources.EnergySpent, play.Resources.StarsSpent));
    });

    public static void Reset() => Safe(CloseInterrupted);

    private static void Finish(CombatRoom room, string outcome)
    {
        if (!ReferenceEquals(room.CombatState, _combat) || _file == null) return;
        Record(_combat, "combat_ended", new { outcome });
        _file.Finish(outcome);
        WholeRun?.SetBattle(_file.Recording);
        ReplayDriver.Stop("战斗结束，已保留最后一次尝试");
        Attempts?.KeepFinal(_file);
        MainFile.Logger.Info($"Battle recording saved ({outcome}): {_file.FilePath}");
        _file = null;
        _combat = null;
        PendingPlays.Clear();
    }

    private static void CloseInterrupted()
    {
        ReplayDriver.Stop("战斗已离开；SL 后可选择之前的尝试");
        if (_file != null)
        {
            // No snapshot here: the game may already have disposed the combat piles.
            _file.Append(new RecordedEvent { Type = "recording_interrupted", PlayerTurn = _lastPlayerTurn });
            _file.Finish("interrupted");
        }
        _file = null;
        _combat = null;
        PendingPlays.Clear();
    }

    private static void Record(ICombatState? state, string type, object? data = null)
    {
        if (state == null || !ReferenceEquals(state, _combat) || _file == null) return;
        var player = state.RunState.Players[0];
        var pcs = player.PlayerCombatState;
        _lastPlayerTurn = pcs?.TurnNumber ?? _lastPlayerTurn;
        _file.Append(new RecordedEvent
        {
            Type = type,
            Round = state.RoundNumber,
            PlayerTurn = _lastPlayerTurn,
            Side = state.CurrentSide.ToString(),
            Data = data,
            State = type == "combat_setup" || (type == "turn_started" && state.CurrentSide == CombatSide.Player)
                ? new BattleSnapshot(pcs?.Energy, pcs?.Stars,
                    pcs?.Hand.Cards.Select(Card).ToList() ?? [], state.Creatures.Select(Creature).ToList())
                : null
        });
    }

    internal static CardSnapshot Card(CardModel card) => new(card.Id.ToString(), card.Title,
        card.IsMutable && card.Pile?.IsCombatPile == true &&
        NetCombatCardDb.Instance.TryGetCardId(card, out uint id) ? id : null, card.CurrentUpgradeLevel,
        card.Pile?.Type == PileType.Deck ? card.Owner.Deck.Cards.ToList().IndexOf(card) : null);

    private static CreatureSnapshot Creature(Creature creature) => new(creature.CombatId,
        creature.Monster?.Id.ToString() ?? creature.Player?.Character.Id.ToString() ?? "unknown",
        creature.Name, creature.Side.ToString(), creature.CurrentHp, creature.MaxHp, creature.Block);

    // Exceptions in a recorder must never escape into game callbacks or Harmony patches.
    private static void Safe(Action action)
    {
        lock (Gate)
        {
            try { action(); }
            catch (Exception error)
            {
                _file = null;
                _combat = null;
                try { MainFile.Logger.Error($"Recording stopped for this battle: {error}"); }
                catch { /* Logging failures must not break the game either. */ }
            }
        }
    }
}
