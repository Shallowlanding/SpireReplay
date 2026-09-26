using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireReplay.SpireReplayCode;

public partial class ReplayLibraryPanel : Control
{
    public static bool IsOpen { get; private set; }
    private static string Root => ProjectSettings.GlobalizePath("user://SpireReplay/recordings");
    private static string? ActiveId => RunManager.Instance.IsInProgress && !RunManager.Instance.IsCleaningUp && RunManager.Instance.NetService.Type == MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Singleplayer ? BattleRecorder.WholeRun?.Data.RunId : null;
    private readonly Color _cyan = new(0.36f, 0.65f, 0.76f);
    private VBoxContainer _list = null!;
    private TextEdit _code = null!;
    private Control _dialog = null!;
    private Label _dialogTitle = null!, _dialogStatus = null!;
    private Button _dialogConfirm = null!;
    private bool _importing;
    private Label _status = null!;
    private Label _count = null!;
    private Button _export = null!, _delete = null!, _clear = null!, _copySeed = null!;
    private string? _selected;
    private ConfirmationDialog _deleteDialog = null!;
    private Action? _confirmedDelete;
    private List<StoredRun> _entries = [];

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.8f) }; AddChild(shade);
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer(); AddChild(center); center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(870, 0) };
        frame.AddThemeStyleboxOverride("panel", ReplayPanel.Style(_cyan)); center.AddChild(frame);
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 10); frame.AddChild(box);
        var header = new HBoxContainer(); box.AddChild(header);
        header.AddChild(new Label { Text = "SPIRE REPLAY  /  复盘管理", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        AddButton(header, "返回设置", Close);
        _count = new Label(); box.AddChild(_count);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(840, 280), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll); _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(_list);
        _list.AddThemeConstantOverride("separation", 8);
        var actions = new HBoxContainer(); box.AddChild(actions);
        _copySeed = AddButton(actions, "复制种子", CopySelectedSeed);
        _export = AddButton(actions, "导出选中对局", Export);
        _delete = AddButton(actions, "删除选中对局", Delete);
        _clear = AddButton(actions, "清空历史", Clear);
        AddButton(actions, "导入复盘码", () => ShowCodeDialog(true, ""));
        _status = new Label { CustomMinimumSize = new Vector2(840, 24), AutowrapMode = TextServer.AutowrapMode.WordSmart }; box.AddChild(_status);
        _dialog = new Control { Visible = false }; AddChild(_dialog); _dialog.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.85f) }; _dialog.AddChild(dim); dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var dialogCenter = new CenterContainer(); _dialog.AddChild(dialogCenter); dialogCenter.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var dialogFrame = new PanelContainer { CustomMinimumSize = new Vector2(760, 0) };
        dialogFrame.AddThemeStyleboxOverride("panel", ReplayPanel.Style(_cyan)); dialogCenter.AddChild(dialogFrame);
        var dialogBox = new VBoxContainer(); dialogBox.AddThemeConstantOverride("separation", 12); dialogFrame.AddChild(dialogBox);
        _dialogTitle = new Label(); dialogBox.AddChild(_dialogTitle);
        _code = new TextEdit { CustomMinimumSize = new Vector2(730, 240), WrapMode = TextEdit.LineWrappingMode.Boundary, PlaceholderText = "粘贴复盘码" }; dialogBox.AddChild(_code);
        var dialogActions = new HBoxContainer(); dialogBox.AddChild(dialogActions);
        _dialogConfirm = AddButton(dialogActions, "导入", () => { if (_importing) Import(); else { DisplayServer.ClipboardSet(_code.Text); _dialogStatus.Text = "已复制"; } });
        AddButton(dialogActions, "返回", () => _dialog.Hide());
        _dialogStatus = new Label { CustomMinimumSize = new Vector2(730, 24), AutowrapMode = TextServer.AutowrapMode.WordSmart }; dialogBox.AddChild(_dialogStatus);
        ReplayPanel.ApplyFonts(dialogFrame);
        ReplayPanel.ApplyFonts(frame);
        // Scale the logical layout to fit small windows without clipping controls.
        void Fit() { var size = GetViewportRect().Size; float scale = Math.Min(1f, Math.Min(size.X / 930f, size.Y / 710f)); center.Scale = Vector2.One * scale; center.Size = size / scale; dialogCenter.Scale = Vector2.One * scale; dialogCenter.Size = size / scale; }
        Resized += Fit; Fit();
        _deleteDialog = new ConfirmationDialog { Title = "确认删除", OkButtonText = "删除", CancelButtonText = "返回", Exclusive = true, Transient = true };
        AddChild(_deleteDialog);
        ReplayPanel.ApplyFonts(_deleteDialog);
        _deleteDialog.Confirmed += () => Guard(() => { var action = _confirmedDelete; _confirmedDelete = null; action?.Invoke(); });
        _deleteDialog.Canceled += () => _confirmedDelete = null;
        Hide();
    }
    private Button AddButton(Node parent, string text, Action action)
    {
        var button = new Button { Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 36) };
        ReplayPanel.Skin(button, _cyan); parent.AddChild(button); button.Pressed += () => Guard(action); return button;
    }
    public void Open()
    {
        MultiplayerReplay.Stop("打开复盘管理，已停止重打");
        RunReplayDriver.Stop("打开复盘管理，已停止重打"); ReplayDriver.Stop("打开复盘管理，已停止重打");
        IsOpen = true; Show(); _code.Text = ""; _status.Text = ""; _dialog.Hide();
        Guard(Refresh);
    }
    public void Close() { IsOpen = false; Hide(); _dialog.Hide(); _deleteDialog.Hide(); _confirmedDelete = null; }
    public override void _ExitTree() { if (Visible) IsOpen = false; }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception e) { if (_dialog.Visible) _dialogStatus.Text = e.Message; else _status.Text = "操作未完成：" + e.Message; MainFile.Logger.Warn("Replay library: " + e); }
    }
    private void Refresh()
    {
        _entries = RunLibrary.List(Root); _confirmedDelete = null;
        if (!_entries.Any(e => e.Recording.RunId == _selected)) _selected = null;
        foreach (Node child in _list.GetChildren()) { _list.RemoveChild(child); child.QueueFree(); }
        _count.Text = $"本地记录{_entries.Count}/{RunLibrary.Capacity}，超过上限自动按顺序清理";
        foreach (var entry in _entries.AsEnumerable().Reverse())
        {
            var run = entry.Recording;
            string state = run.Status switch { "won" => "胜利", "lost" => "已结束", _ => "未完成" };
            string character = run.CharacterId;
            character = character.Split('.').Last() switch { "IRONCLAD" => "铁甲战士", "SILENT" => "静默猎手", "DEFECT" => "故障机器人", "REGENT" => "储君", "NECROBINDER" => "亡灵契约师", _ => character };
            string label = $"{character} · 进阶 {run.Ascension} · {state} · 录至 {run.Rooms.Select(r => r.Floor).DefaultIfEmpty().Max()} 层\n种子 {run.Seed} · {(run.Imported ? "导入" : "本地")} {entry.SavedAt.ToLocalTime():MM-dd HH:mm}";
            if (run.RunId == ActiveId) label += " · 当前局（保留）";
            var card = new Button { Text = label, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 66), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            ReplayPanel.Skin(card, _cyan.Darkened(0.3f), run.RunId == _selected);
            card.Pressed += () => Guard(() => { _selected = _selected == run.RunId ? null : run.RunId; Refresh(); });
            _list.AddChild(card);
        }
        ReplayPanel.ApplyFonts(_list);
        _copySeed.Disabled = _selected == null;
        _export.Disabled = _selected == null; _delete.Disabled = _selected == null || _selected == ActiveId;
        _clear.Disabled = !_entries.Any(e => e.Recording.RunId != ActiveId);
        _delete.Text = "删除选中对局"; _clear.Text = "清空历史";
    }
    private void CopySelectedSeed()
    {
        var entry = _entries.FirstOrDefault(e => e.Recording.RunId == _selected);
        if (entry == null) { Refresh(); return; }
        DisplayServer.ClipboardSet(entry.Recording.Seed);
        _status.Text = "种子已复制";
    }
    private void Export()
    {
        var entry = RunLibrary.List(Root).FirstOrDefault(e => e.Recording.RunId == _selected);
        if (entry == null) { Refresh(); return; }
        ShowCodeDialog(false, ReplayCode.Export(entry.Recording));
    }
    private void ShowCodeDialog(bool importing, string code)
    {
        _importing = importing; _code.Editable = importing; _code.Text = code;
        _dialogTitle.Text = importing ? "导入复盘码" : "导出复盘码";
        _dialogConfirm.Text = importing ? "导入" : "复制";
        _dialogStatus.Text = ""; _dialog.Show(); _code.GrabFocus();
    }
    private void Import()
    {
        var result = RunLibrary.Import(Root, _code.Text, ActiveId); _selected = result.Entry.Recording.RunId; Refresh();
        _dialog.Hide();
        _status.Text = result.AlreadyExists ? "此对局已存在" : "导入成功";
    }
    private void Delete()
    {
        string? selected = _selected;
        if (selected == null) return;
        _deleteDialog.DialogText = "删除选中的对局及其本地战斗记录？";
        _confirmedDelete = () => { RunLibrary.Delete(Root, selected, ActiveId); _selected = null; Refresh(); _status.Text = "已删除本地记录"; };
        _deleteDialog.PopupCentered(new Vector2I(480, 180));
    }
    private void Clear()
    {
        _deleteDialog.DialogText = "清空全部本地历史？正在录制的当前局会保留。";
        _confirmedDelete = () => { int count = RunLibrary.Clear(Root, ActiveId); Refresh(); _status.Text = $"已清理 {count} 条历史记录"; };
        _deleteDialog.PopupCentered(new Vector2I(480, 180));
    }

}

[HarmonyPatch(typeof(NSettingsScreen), nameof(NSettingsScreen._Ready))]
internal static class ReplaySettingsPatch
{
    private static void Postfix(NSettingsScreen __instance)
    {
        try
        {
            if (__instance.HasNode("SpireReplayLibrary")) return;
            var panel = new ReplayLibraryPanel { Name = "SpireReplayLibrary" }; __instance.AddChild(panel);
            var general = __instance.GetNode<NSettingsPanel>("%GeneralSettings");
            var content = general.Content;
            var source = content.GetNode<Control>("SendFeedback");
            // Copy the native setting row and visuals, but not its feedback signal handlers.
            var row = (Control)source.Duplicate((int)Node.DuplicateFlags.Scripts);
            row.Name = "SpireReplaySettings";
            void ClearUniqueNames(Node node) { node.UniqueNameInOwner = false; foreach (Node child in node.GetChildren()) ClearUniqueNames(child); }
            ClearUniqueNames(row);
            var divider = (Control)__instance.GetNode<Control>("%ModdingDivider").Duplicate((int)Node.DuplicateFlags.Scripts);
            divider.Name = "SpireReplayDivider"; ClearUniqueNames(divider); divider.Show();
            content.AddChild(divider); content.MoveChild(divider, source.GetIndex() + 1);
            content.AddChild(row); content.MoveChild(row, source.GetIndex() + 2);
            row.GetNode<MegaRichTextLabel>("Label").Text = "复盘管理（SpireReplay）";
            var button = row.GetNode<NOpenFeedbackScreenButton>(source.GetPathTo(__instance.GetNode<NOpenFeedbackScreenButton>("%FeedbackButton")));
            button.GetNode<MegaLabel>("Label").SetTextAutoSize("打开");
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => panel.Open()));
            Callable.From(() => { if (GodotObject.IsInstanceValid(general)) AccessTools.Method(typeof(NSettingsPanel), "RefreshSize").Invoke(general, null); }).CallDeferred();
            __instance.SettingsClosed += panel.Close;
        }
        catch (Exception e) { MainFile.Logger.Warn("Unable to add replay settings: " + e); }
    }
}
