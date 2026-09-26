using Godot;
using MegaCrit.Sts2.Core.Combat;
namespace SpireReplay.SpireReplayCode;

public partial class TeamReplayPanel : CanvasLayer
{
    private static TeamReplayPanel? _instance;
    private readonly PanelPlacement _placement = new();
    private static readonly Color Cyan = new(0.36f, 0.65f, 0.76f), Gold = new(0.86f, 0.69f, 0.31f);
    private PanelContainer _panel = null!;
    private OptionButton _attempt = null!;
    private Button _ready = null!, _start = null!;
    private Label _status = null!, _selection = null!;
    private VBoxContainer _preview = null!;
    private ScrollContainer _scroll = null!;
    private List<TeamAttempt> _sources = [];
    private readonly List<(Button Button, Color Accent, int End)> _targets = [];
    private string? _battle;
    private int _previewSteps = -1;
    private int? _end;
    public static void Install()
    {
        if (_instance != null && GodotObject.IsInstanceValid(_instance)) return;
        _instance = new TeamReplayPanel(); var panel = _instance;
        Callable.From(() => ((SceneTree)Engine.GetMainLoop()).Root.AddChild(panel)).CallDeferred();
    }
    public override void _Ready()
    {
        Layer = 81;
        _panel = new PanelContainer { Position = new Vector2(18, 120), CustomMinimumSize = new Vector2(650, 0) }; AddChild(_panel);
        var style = ReplayPanel.Style(Cyan.Darkened(0.35f));
        style.ContentMarginLeft = style.ContentMarginRight = 14;
        style.ContentMarginTop = style.ContentMarginBottom = 12;
        _panel.AddThemeStyleboxOverride("panel", style);
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 10); _panel.AddChild(box);
        var header = new HBoxContainer(); box.AddChild(header);
        var title = new Label { Text = "SPIRE REPLAY / 多人时间轴 · v0.11.9", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeColorOverride("font_color", Cyan.Lightened(0.3f)); header.AddChild(title); _placement.Attach(title, _panel);
        var collapse = new Button { Text = "收起" }; ReplayPanel.Skin(collapse, Cyan); header.AddChild(collapse);
        _ready = new Button { Text = "准备", ToggleMode = true }; ReplayPanel.Skin(_ready, Cyan); header.AddChild(_ready);
        _ready.Toggled += on => { MultiplayerReplay.Ready = on; if (!on) MultiplayerReplay.Stop("玩家取消准备"); };
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 10); box.AddChild(body);
        collapse.Pressed += () => { body.Visible = !body.Visible; collapse.Text = body.Visible ? "收起" : "展开"; _panel.ResetSize(); };
        _attempt = new OptionButton { CustomMinimumSize = new Vector2(0, 34) }; ReplayPanel.Skin(_attempt, Cyan); body.AddChild(_attempt);
        _attempt.ItemSelected += _ => { _end = null; Preview(); _scroll.ScrollVertical = 0; };
        var help = new Label { Text = "主机点击回合或操作选择终点；选牌随所属操作一起完成" };
        help.AddThemeFontSizeOverride("font_size", 14); body.AddChild(help);
        _scroll = new ScrollContainer { CustomMinimumSize = new Vector2(610, 250), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _preview = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _preview.AddThemeConstantOverride("separation", 9); _scroll.AddChild(_preview); body.AddChild(_scroll);
        _selection = new Label { CustomMinimumSize = new Vector2(610, 24), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _selection.AddThemeColorOverride("font_color", Gold); body.AddChild(_selection);
        var buttons = new HBoxContainer(); body.AddChild(buttons);
        _start = new Button { Text = "主机确认执行到此处", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        ReplayPanel.Skin(_start, Gold); buttons.AddChild(_start);
        _start.Pressed += () => { if (_end.HasValue && Selected is { } source && source != MultiplayerReplay.Current) MultiplayerReplay.StartToIndex(source, _end.Value); };
        var stop = new Button { Text = "全队停止" }; ReplayPanel.Skin(stop, Cyan); buttons.AddChild(stop); stop.Pressed += () => MultiplayerReplay.Stop("玩家停止了重打");
        _status = new Label { CustomMinimumSize = new Vector2(610, 40), AutowrapMode = TextServer.AutowrapMode.WordSmart }; body.AddChild(_status);
        ReplayPanel.ApplyFonts(_panel);
    }
    private TeamAttempt? Selected => _attempt.Selected >= 0 && _attempt.Selected < _sources.Count ? _sources[_attempt.Selected] : null;
    private static Color PlayerColor(TeamStep step) => (step.CharacterId ?? MultiplayerReplay.CharacterFor(step.Player)) switch
    {
        "CHARACTER.SILENT" => new Color("65bd7a"),
        "CHARACTER.REGENT" => new Color("e99a48"),
        "CHARACTER.IRONCLAD" => new Color("db6666"),
        "CHARACTER.NECROBINDER" => new Color("b489df"),
        "CHARACTER.DEFECT" => new Color("699fe5"),
        _ => Cyan
    };
    private void Target(Button button, Color accent, TeamAttempt source, int end, string description)
    {
        end = TeamTimeline.CompleteSelection(source, end);
        ReplayPanel.Skin(button, accent, _end == end); _targets.Add((button, accent, end));
        button.TooltipText = description + "；包含该操作触发的选择，以及等待选择期间先执行的队友操作";
        button.Pressed += () =>
        {
            if (!MultiplayerReplay.Host || MultiplayerReplay.Active || source == MultiplayerReplay.Current) return;
            _end = _end == end ? null : end;
            _selection.Text = _end == null ? "请在时间轴上选择停止位置" : "停止位置：" + description + "（含关联选择）";
            foreach (var item in _targets) ReplayPanel.Skin(item.Button, item.Accent, _end == item.End);
        };
    }
    private void Preview()
    {
        _targets.Clear();
        foreach (Node child in _preview.GetChildren()) { _preview.RemoveChild(child); child.QueueFree(); }
        if (Selected is not { } source) return;
        _previewSteps = source.Steps.Count;
        if (_end == null) _selection.Text = source == MultiplayerReplay.Current ? "当前尝试持续记录；SL 后可选择并重打" : MultiplayerReplay.Host ? "请在时间轴上选择停止位置" : "准备后等待主机选择终点";
        foreach (var group in source.Steps.Select((step, index) => (step, index)).GroupBy(x => x.step.Round))
        {
            var frame = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            frame.AddThemeStyleboxOverride("panel", ReplayPanel.Style(Cyan.Darkened(0.6f)));
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 12); frame.AddChild(row);
            var turn = new Button { Text = $"第 {group.Key}\n回合", CustomMinimumSize = new Vector2(65, 44), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            Target(turn, Cyan, source, MultiplayerReplay.StopIndex(source, group.Key), $"第 {group.Key} 回合所有操作完成，不结束回合"); row.AddChild(turn);
            var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            flow.AddThemeConstantOverride("h_separation", 5); flow.AddThemeConstantOverride("v_separation", 5); row.AddChild(flow);
            foreach (var (step, index) in group)
            {
                string owner = $"{step.PlayerName ?? "玩家"} …{step.Player % 10000:D4}";
                string text = step.Kind == "end" ? "结束回合" : step.Label == "UndoEndPlayerTurnAction" ? "取消结束回合" : step.Label;
                string label = owner + " · " + text + (step.Target == null ? "" : " → " + step.Target);
                if (step.Kind is "choice" or "end" or "resume")
                {
                    var detail = new Label { Text = label, TooltipText = step.Kind == "choice" ? "随所属操作一起执行" : "保留全队操作顺序；目标回合末尾的结束回合不自动执行" };
                    detail.AddThemeFontSizeOverride("font_size", 13); detail.AddThemeColorOverride("font_color", PlayerColor(step)); flow.AddChild(detail); continue;
                }
                var pill = new Button { Text = label, ClipText = true, CustomMinimumSize = new Vector2(Math.Min(450, 24 + label.Length * 14), 30) };
                Color accent = PlayerColor(step);
                Target(pill, accent, source, index + 1, $"第 {group.Key} 回合 · {label}"); flow.AddChild(pill);
            }
            _preview.AddChild(frame); ReplayPanel.ApplyFonts(frame);
        }
        if (source.Steps.Count == 0) _preview.AddChild(new Label { Text = "当前尚无操作；这里会按顺序记录全队操作" });
        ReplayPanel.ApplyFonts(_preview);
    }
    public override void _Process(double delta)
    {
        try
        {
            MultiplayerReplay.Tick(); _placement.Tick(_panel);
            var current = MultiplayerReplay.Current;
            _panel.Visible = current != null && CombatManager.Instance.IsInProgress && !ReplayLibraryPanel.IsOpen;
            if (!_panel.Visible) return;
            float scale = Math.Min(1f, Math.Min((GetViewport().GetVisibleRect().Size.X - 36) / 650, (GetViewport().GetVisibleRect().Size.Y - 150) / 550));
            _panel.Scale = Vector2.One * Math.Max(0.5f, scale);
            if (_battle != current!.Id)
            {
                _battle = current.Id; _sources = MultiplayerReplay.Attempts(); _attempt.Clear(); _end = null;
                for (int i = 0; i < _sources.Count; i++) _attempt.AddItem($"第 {i + 1} 次尝试 · {_sources[i].Steps.Count} 项操作");
                int previous = _sources.Count - 1;
                _sources.Add(current); _attempt.AddItem("当前尝试 · 正在记录全队操作");
                _attempt.Select(previous >= 0 ? previous : _sources.Count - 1); Preview();
            }
            if (Selected is { } selected && _previewSteps != selected.Steps.Count) Preview();
            _ready.SetPressedNoSignal(MultiplayerReplay.Ready); _ready.Text = MultiplayerReplay.Ready ? "已准备" : "准备";
            _attempt.Disabled = MultiplayerReplay.Active;
            foreach (var item in _targets) item.Button.Disabled = MultiplayerReplay.Active || !MultiplayerReplay.Host || Selected == current;
            _start.Disabled = MultiplayerReplay.Active || !MultiplayerReplay.Host || Selected == null || Selected == current || !_end.HasValue;
            _status.Text = MultiplayerReplay.Status;
        }
        catch (Exception e) { MultiplayerReplay.Stop("多人重放异常：" + e.Message); }
    }
}
