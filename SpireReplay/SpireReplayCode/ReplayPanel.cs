using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace SpireReplay.SpireReplayCode;

public partial class ReplayPanel : CanvasLayer
{
    private static ReplayPanel? _instance;
    public static void EnsureInstalled()
    {
        if (_instance != null && GodotObject.IsInstanceValid(_instance)) return;
        _instance = new ReplayPanel();
        var panel = _instance;
        Callable.From(() => ((SceneTree)Engine.GetMainLoop()).Root.AddChild(panel)).CallDeferred();
    }
    private PanelContainer _panel = null!;
    private OptionButton _attempt = null!;
    private OptionButton _mode = null!;
    private OptionButton _version = null!;
    private string? _catalogRun;
    private int _libraryRevision = -1;
    private RunRecording? ViewedRun => _version.Selected > 0 && _version.Selected <= BattleRecorder.RunHistory.Count
        ? BattleRecorder.RunHistory[_version.Selected - 1].Recording : BattleRecorder.WholeRun?.Data;
    private readonly PanelPlacement _placement = new();
    private bool _wholeMode;
    private int _runRevision = -1;
    private Label _help = null!;
    private Label _status = null!;
    private Label _selection = null!;
    private Button _start = null!;
    private Button _stop = null!;
    private VBoxContainer _preview = null!;
    private ScrollContainer _scroll = null!;
    private readonly List<(Button Button, Color Accent, ReplayDestination Destination)> _targets = [];
    private List<BattleRecording> _sources = [];
    private ReplayDestination? _destination;
    private RunFloorTarget? _floorTarget;
    private readonly List<(PanelContainer Frame, RunFloorTarget Target)> _floorButtons = [];
    private string? _battle;
    private double _refresh;
    private const float PanelWidth = 650;
    private static readonly Color Cyan = new(0.36f, 0.65f, 0.76f);
    private static readonly Color Gold = new(0.86f, 0.69f, 0.31f);

    internal static StyleBoxFlat Style(Color border, bool selected = false)
    {
        return new StyleBoxFlat
        {
            BgColor = selected ? new Color(0.20f, 0.18f, 0.10f, 0.98f) : new Color(0.10f, 0.12f, 0.16f, 0.97f),
            BorderColor = selected ? Gold : border,
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 9, ContentMarginRight = 9, ContentMarginTop = 5, ContentMarginBottom = 5
        };
    }
    internal static void Skin(Button button, Color accent, bool selected = false)
    {
        button.FocusMode = Control.FocusModeEnum.None;
        button.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        button.AddThemeStyleboxOverride("normal", Style(accent, selected));
        button.AddThemeStyleboxOverride("hover", Style(accent.Lightened(0.25f), selected));
        button.AddThemeStyleboxOverride("pressed", Style(Gold, true));
        button.AddThemeStyleboxOverride("disabled", Style(accent.Darkened(0.55f)));
        button.AddThemeColorOverride("font_color", selected ? new Color(1, 0.88f, 0.57f) : new Color(0.88f, 0.92f, 0.96f));
        button.AddThemeFontSizeOverride("font_size", 16);
    }
    public override void _Ready()
    {
        Layer = 80;
        _panel = new PanelContainer { Position = new Vector2(18, 120), CustomMinimumSize = new Vector2(PanelWidth, 0) };
        var frame = Style(Cyan.Darkened(0.35f));
        frame.ContentMarginLeft = frame.ContentMarginRight = 14;
        frame.ContentMarginTop = frame.ContentMarginBottom = 12;
        _panel.AddThemeStyleboxOverride("panel", frame);
        AddChild(_panel);
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 10); _panel.AddChild(box);
        var header = new HBoxContainer();
        var title = new Label { Text = "SPIRE REPLAY  /  操作时间轴", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeColorOverride("font_color", Cyan.Lightened(0.3f));
        _placement.Attach(title, _panel);
        header.AddChild(title);
        var collapse = new Button { Text = "收起" }; Skin(collapse, Cyan); header.AddChild(collapse); box.AddChild(header);
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 10); box.AddChild(body);
        collapse.Pressed += () => { body.Visible = !body.Visible; collapse.Text = body.Visible ? "收起" : "展开"; _panel.ResetSize(); };
        _mode = new OptionButton();
        _mode.AddItem("战斗重放"); _mode.AddItem("整局浏览"); Skin(_mode, Cyan); body.AddChild(_mode);
        _mode.ItemSelected += index => { _wholeMode = index == 1; _battle = null; _destination = null; };
        _version = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        Skin(_version, Cyan); body.AddChild(_version);
        _version.ItemSelected += _ => { _floorTarget = null; _battle = null; _runRevision = -1; _scroll.ScrollVertical = 0; };
        _attempt = new OptionButton { CustomMinimumSize = new Vector2(0, 34), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        Skin(_attempt, Cyan); body.AddChild(_attempt);
        _help = new Label { Text = "点击回合或操作，再确认执行；标题栏可拖动" };
        _help.AddThemeFontSizeOverride("font_size", 14); body.AddChild(_help);
        _scroll = new ScrollContainer { CustomMinimumSize = new Vector2(610, 250), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _preview = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _preview.AddThemeConstantOverride("separation", 9); _scroll.AddChild(_preview); body.AddChild(_scroll);
        _selection = new Label { Text = "请在时间轴上选择停止位置", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(610, 24) };
        _selection.AddThemeColorOverride("font_color", Gold); body.AddChild(_selection);
        var buttons = new HBoxContainer();
        _start = new Button { Text = "确认执行到此处", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _stop = new Button { Text = "停止", CustomMinimumSize = new Vector2(90, 0) };
        Skin(_start, Gold); Skin(_stop, Cyan); buttons.AddChild(_start); buttons.AddChild(_stop); body.AddChild(buttons);
        _status = new Label { CustomMinimumSize = new Vector2(610, 32), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeFontSizeOverride("font_size", 15); body.AddChild(_status);
        _attempt.ItemSelected += _ => { if (_wholeMode) RefreshWholeRun(); else RefreshPreview(); };
        _start.Pressed += () =>
        {
            if (_wholeMode) { RunReplayDriver.Start(ViewedRun, _floorTarget); return; }
            if (_destination != null && _attempt.Selected >= 0 && _attempt.Selected < _sources.Count)
                ReplayDriver.Start(_sources[_attempt.Selected], _destination);
        };
        _stop.Pressed += () => { RunReplayDriver.Stop("已停止；当前已开始的动作会正常结算"); ReplayDriver.Stop("已停止；当前已开始的动作会正常结算"); };
        ApplyFonts(_panel);
    }

    private void AddTarget(Button button, Color accent, ReplayDestination target)
    {
        Skin(button, accent);
        button.TooltipText = target.Description + "；点击选择，再确认执行";
        _targets.Add((button, accent, target));
        button.Pressed += () =>
        {
            if (ReplayDriver.Active) return;
            _destination = target;
            _selection.Text = "停止位置：" + target.Description;
            foreach (var item in _targets) Skin(item.Button, item.Accent, item.Destination == target);
        };
    }
    private void RefreshPreview()
    {
        _destination = null; _targets.Clear(); _selection.Text = "请在时间轴上选择停止位置";
        foreach (Node child in _preview.GetChildren()) { _preview.RemoveChild(child); child.QueueFree(); }
        if (_attempt.Selected < 0 || _attempt.Selected >= _sources.Count) return;
        var source = _sources[_attempt.Selected];
        var steps = new ReplayTimeline(source).Steps;
        var labels = ReplayPreview.Build(source).SelectMany(t => t.Actions).ToList();
        foreach (var group in steps.Select((e, i) => (Event: e, Label: labels[i])).GroupBy(x => x.Event.PlayerTurn ?? 1))
        {
            var frame = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            frame.AddThemeStyleboxOverride("panel", Style(Cyan.Darkened(0.6f)));
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 12); frame.AddChild(row);
            var turn = new Button { Text = $"第 {group.Key}\n回合", CustomMinimumSize = new Vector2(65, 44), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            AddTarget(turn, Cyan, ReplayDestination.EndOfTurn(source, group.Key)); row.AddChild(turn);
            var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            flow.AddThemeConstantOverride("h_separation", 5); flow.AddThemeConstantOverride("v_separation", 5); row.AddChild(flow);
            foreach (var item in group)
            {
                if (item.Event.Type == "cards_selected")
                {
                    var choice = new Label { Text = item.Label.Trim(), TooltipText = "该选择随所属操作一起执行" };
                    choice.AddThemeFontSizeOverride("font_size", 13); choice.AddThemeColorOverride("font_color", Cyan); flow.AddChild(choice); continue;
                }
                var command = ReplayJson.Read<ReplayCommand>(item.Event.Data!);
                if (command.Kind == "end_turn") continue;
                string label = item.Label.StartsWith("打出 ") ? item.Label[3..] : item.Label;
                var pill = new Button { Text = label, ClipText = true, CustomMinimumSize = new Vector2(Math.Min(420, 30 + label.Length * 15), 30) };
                Color accent = command.Kind is "potion" or "discard" ? Gold : command.TargetId != null ? new Color(0.65f, 0.32f, 0.37f) : Cyan;
                AddTarget(pill, accent, ReplayDestination.AfterAction(source, item.Event.Sequence)); flow.AddChild(pill);
            }
            _preview.AddChild(frame); ApplyFonts(frame);
        }
        _scroll.ScrollVertical = 0;
    }
    private void RefreshWholeRun()
    {
        _floorButtons.Clear();
        _destination = null; _targets.Clear();
        foreach (Node child in _preview.GetChildren()) { _preview.RemoveChild(child); child.QueueFree(); }
        var archive = BattleRecorder.WholeRun;
        var view = ViewedRun;
        if (archive == null || view == null || _attempt.Selected < 0 || _attempt.Selected >= view.Areas.Count) return;
        int act = view.Areas[_attempt.Selected].Act;
        var rooms = view.Rooms.Where(r => r.Act == act).OrderBy(r => r.Floor).ThenBy(r => r.RoomId).ToList();
        _selection.Text = (view.RecordedFromStart ? "从开局记录" : "中途开始记录") +
            $" · 进阶 {view.Ascension} · {RunHistoryCatalog.Status(view.Status)}" +
            (_version.Selected == 0 ? " · 当前记录持续更新" : " · 历史版本只读");
        if (rooms.Count == 0) _preview.AddChild(new Label { Text = "此大层暂无记录" });
        foreach (var room in rooms)
        {
            var frame = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            frame.AddThemeStyleboxOverride("panel", Style(Cyan.Darkened(0.6f)));
            var row = new HBoxContainer(); frame.AddChild(row);
            var target = new RunFloorTarget(room.Act, room.Floor);
            var title = new Label { Text = $"第 {room.Floor} 层\n{RunSummary.RoomName(room.Type)}", CustomMinimumSize = new Vector2(85, 0) };
            title.AddThemeColorOverride("font_color", Cyan); row.AddChild(title);
            frame.MouseFilter = Control.MouseFilterEnum.Stop;
            frame.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
            frame.TooltipText = "点击整张小层记录选择终点；再次点击取消";
            frame.AddThemeStyleboxOverride("panel", Style(Cyan.Darkened(0.6f), _floorTarget == target));
            _floorButtons.Add((frame, target));
            Vector2? pressedAt = null;
            frame.GuiInput += input =>
            {
                if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mouse) return;
                if (mouse.Pressed) { pressedAt = frame.GetGlobalMousePosition(); return; }
                var start = pressedAt; pressedAt = null;
                if (start == null || start.Value.DistanceTo(frame.GetGlobalMousePosition()) > 8 ||
                    RunReplayDriver.Active || ReplayDriver.Active) return;
                _floorTarget = _floorTarget == target ? null : target;
                foreach (var item in _floorButtons)
                    item.Frame.AddThemeStyleboxOverride("panel", Style(Cyan.Darkened(0.6f), item.Target == _floorTarget));
                frame.AcceptEvent();
            };
            var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; row.AddChild(content);
            var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            content.AddChild(flow);
            foreach (string line in RunSummary.Describe(room))
            {
                var chip = new Label { Text = line, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(230, 0) };
                chip.AddThemeFontSizeOverride("font_size", 15); flow.AddChild(chip);
            }
            if (room.Battle is { } battle)
            {
                var expand = new Button { Text = $"展开战斗 · 第 {battle.AttemptNumber} 次最终尝试" }; Skin(expand, Cyan); content.AddChild(expand);
                var details = new VBoxContainer { Visible = false }; content.AddChild(details);
                BuildReadOnlyTimeline(details, battle);
                expand.Pressed += () => { details.Visible = !details.Visible; expand.Text = details.Visible ? "收起战斗操作" : $"展开战斗 · 第 {battle.AttemptNumber} 次最终尝试"; };
            }
            else if (room.Type is "Monster" or "Elite" or "Boss") content.AddChild(new Label { Text = "战斗尚未完成" });
            PassFloorCardInput(row);
            _preview.AddChild(frame); ApplyFonts(frame);
        }
        _runRevision = archive.Revision;
    }
    // Static content lets the outer card receive clicks. Real buttons keep their own input.
    private static void PassFloorCardInput(Node node)
    {
        if (node is Button button) { button.MouseFilter = Control.MouseFilterEnum.Stop; return; }
        if (node is Control control) control.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (Node child in node.GetChildren()) PassFloorCardInput(child);
    }
    // Labels in styled containers deliberately have no replay input handlers.
    private static void BuildReadOnlyTimeline(VBoxContainer container, BattleRecording battle)
    {
        var steps = new ReplayTimeline(battle).Steps;
        var labels = ReplayPreview.Build(battle).SelectMany(t => t.Actions).ToList();
        container.AddThemeConstantOverride("separation", 9);
        foreach (var group in steps.Select((e, i) => (Event: e, Label: labels[i])).GroupBy(x => x.Event.PlayerTurn ?? 1))
        {
            var frame = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            frame.AddThemeStyleboxOverride("panel", Style(Cyan.Darkened(0.6f)));
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8); frame.AddChild(row);
            var turn = new Label { Text = $"第 {group.Key}\n回合", CustomMinimumSize = new Vector2(52, 44),
                VerticalAlignment = VerticalAlignment.Center, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            turn.AddThemeColorOverride("font_color", Cyan); row.AddChild(turn);
            var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            flow.AddThemeConstantOverride("h_separation", 5); flow.AddThemeConstantOverride("v_separation", 5); row.AddChild(flow);
            foreach (var item in group)
            {
                if (item.Event.Type == "cards_selected")
                {
                    var choice = new Label { Text = item.Label.Trim(), AutowrapMode = TextServer.AutowrapMode.WordSmart,
                        CustomMinimumSize = new Vector2(260, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
                    choice.AddThemeFontSizeOverride("font_size", 13); choice.AddThemeColorOverride("font_color", Cyan);
                    flow.AddChild(choice); continue;
                }
                var command = ReplayJson.Read<ReplayCommand>(item.Event.Data!);
                var accent = command.Kind is "potion" or "discard" ? Gold : command.TargetId != null ? new Color(0.65f, 0.32f, 0.37f) : Cyan;
                var pill = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                pill.AddThemeStyleboxOverride("panel", Style(accent));
                string text = item.Label.StartsWith("打出 ") ? item.Label[3..] : item.Label;
                var label = new Label { Text = text, TooltipText = text, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    CustomMinimumSize = new Vector2(Math.Clamp(text.Length * 15, 65, 285), 0), MouseFilter = Control.MouseFilterEnum.Ignore };
                label.AddThemeFontSizeOverride("font_size", 16); pill.AddChild(label); flow.AddChild(pill);
            }
            container.AddChild(frame);
        }
    }
    internal static void ApplyFonts(Node node)
    {
        if (node is Control control) control.ApplyLocaleFontSubstitution(FontType.Regular, "font");
        foreach (Node child in node.GetChildren()) ApplyFonts(child);
    }
    private void RefreshRunVersions(RunArchive archive)
    {
        if (_catalogRun == archive.Data.RunId && _libraryRevision == RunLibrary.Revision) return;
        _libraryRevision = RunLibrary.Revision;
        BattleRecorder.RefreshRunHistory();
        _catalogRun = archive.Data.RunId;
        _floorTarget = null;
        _version.Clear();
        _version.AddItem("当前记录");
        for (int i = 0; i < BattleRecorder.RunHistory.Count; i++)
        {
            string label = BattleRecorder.RunHistory[i].Label(i + 1);
            _version.AddItem(label);
        }
        _version.Select(0); _battle = null;
    }

    public override void _Process(double delta)
    {
        var manager = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
        if (!manager.IsInProgress || manager.IsCleaningUp ||
            manager.NetService.Type != MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Singleplayer)
        {
            _panel.Visible = false;
            if (RunReplayDriver.Active) RunReplayDriver.Stop("已离开单人对局");
            if (ReplayDriver.Active) ReplayDriver.Stop("已离开单人对局");
            return;
        }
        _placement.Tick(_panel);
        ReplayDriver.Tick(); RunReplayDriver.Tick(); _refresh += delta;
        if (_refresh < 0.15) return; _refresh = 0;
        var current = BattleRecorder.CurrentRecording;
        var archive = BattleRecorder.WholeRun;
        _panel.Visible = MultiplayerReplay.Current == null && !ReplayLibraryPanel.IsOpen && MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress &&
            !MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsCleaningUp && (current != null || archive != null);
        if (!_panel.Visible) return;
        if (archive != null) RefreshRunVersions(archive);
        var view = ViewedRun;
        _version.Visible = _wholeMode;
        if (current == null && !_wholeMode) { _wholeMode = true; _mode.Select(1); _battle = null; }
        _start.Visible = true; _stop.Visible = true;
        _start.Text = _wholeMode ? (_floorTarget == null ? "开始整局重打" : $"重打至第 {_floorTarget.Floor} 层操作结束") : "确认执行到此处";
        _mode.Disabled = ReplayDriver.Active || RunReplayDriver.Active;
        _version.Disabled = ReplayDriver.Active || RunReplayDriver.Active;
        _help.Text = _wholeMode ? "点击小层选择终点，再点取消；不选则重打至记录末尾" : "点击回合或操作，再确认执行；标题栏可拖动";
        // Fit smaller windows without changing the timeline's logical layout.
        float scale = Math.Min(1f, Math.Min((GetViewport().GetVisibleRect().Size.X - 36) / PanelWidth,
            (GetViewport().GetVisibleRect().Size.Y - 150) / 490));
        _panel.Scale = Vector2.One * Math.Max(0.5f, scale);
        string key = _wholeMode ? "run:" + view?.RunId : current?.BattleId ?? "";
        if (_battle != key)
        {
            _battle = key; _attempt.Clear();
            try
            {
                if (_wholeMode)
                {
                    foreach (var area in view?.Areas ?? []) _attempt.AddItem($"第 {area.Act} 大层 · {area.Name}");
                    if (_attempt.ItemCount > 0)
                        _attempt.Select(Math.Clamp((view?.Rooms.LastOrDefault()?.Act ?? 1) - 1, 0, _attempt.ItemCount - 1));
                    RefreshWholeRun();
                }
                else
                {
                _sources = BattleRecorder.Attempts!.ReadAll().Where(b => b.BattleId != current!.BattleId).ToList();
                foreach (var source in _sources)
                {
                    int turns = source.Events.Select(e => e.PlayerTurn ?? 0).DefaultIfEmpty().Max();
                    int actions = source.Events.Count(e => e.Type == "replay_action");
                    _attempt.AddItem($"第 {source.AttemptNumber} 次尝试  ·  录至 {turns} 回合  ·  {actions} 次操作");
                }
                if (_sources.Count > 0) _attempt.Select(_sources.FindIndex(b => b.Events.Count == _sources.Max(x => x.Events.Count)));
                else _attempt.AddItem("暂无历史尝试，SL 后可重放");
                RefreshPreview();
                }
            }
            catch (Exception error) { _sources = []; ReplayDriver.Stop("读取尝试失败：" + error.Message); }
        }
        if (_wholeMode && _version.Selected == 0 && archive?.Revision != _runRevision) RefreshWholeRun();
        _start.Disabled = ReplayDriver.Active || RunReplayDriver.Active || (_wholeMode ? _version.Selected <= 0 : _destination == null || _sources.Count == 0);
        _stop.Disabled = !ReplayDriver.Active && !RunReplayDriver.Active; _attempt.Disabled = ReplayDriver.Active || RunReplayDriver.Active;
        foreach (var item in _targets) item.Button.Disabled = ReplayDriver.Active;
        foreach (var item in _floorButtons) item.Frame.MouseDefaultCursorShape = ReplayDriver.Active || RunReplayDriver.Active
            ? Control.CursorShape.Arrow : Control.CursorShape.PointingHand;
        _status.Text = _wholeMode ? $"种子 {view?.Seed} · {view?.Rooms.Count} 个房间 · {RunHistoryCatalog.Status(view?.Status ?? "")}\n{RunReplayDriver.Status}" : ReplayDriver.Status;
    }
}

