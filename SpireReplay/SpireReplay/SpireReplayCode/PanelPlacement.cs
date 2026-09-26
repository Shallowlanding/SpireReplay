using Godot;
using System.Text.Json;

namespace SpireReplay.SpireReplayCode;

public sealed record PanelPosition(float X, float Y);
internal sealed class PanelPlacement
{
    private bool _dragging;
    private Vector2 _offset;
    private readonly string _path = ProjectSettings.GlobalizePath("user://SpireReplay/panel-position.json");
    public void Attach(Control handle, Control panel)
    {
        try
        {
            if (File.Exists(_path))
            {
                var saved = JsonSerializer.Deserialize<PanelPosition>(File.ReadAllText(_path), ReplayJson.Options);
                if (saved != null && float.IsFinite(saved.X) && float.IsFinite(saved.Y)) panel.Position = new Vector2(saved.X, saved.Y);
            }
        }
        catch (Exception error) { MainFile.Logger.Warn("面板位置读取失败：" + error.Message); }
        handle.MouseFilter = Control.MouseFilterEnum.Stop;
        handle.MouseDefaultCursorShape = Control.CursorShape.Move;
        handle.TooltipText = "按住拖动；松开后保存位置";
        handle.GuiInput += input =>
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            { _dragging = true; _offset = panel.GetGlobalMousePosition() - panel.Position; handle.AcceptEvent(); }
        };
    }
    public void Tick(Control panel)
    {
        var size = panel.GetViewportRect().Size;
        if (_dragging) panel.Position = panel.GetGlobalMousePosition() - _offset;
        panel.Position = new Vector2(Math.Clamp(panel.Position.X, 0, Math.Max(0, size.X - panel.Size.X * panel.Scale.X)),
            Math.Clamp(panel.Position.Y, 0, Math.Max(0, size.Y - panel.Size.Y * panel.Scale.Y)));
        if (_dragging && !Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _dragging = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                RecordingFile.WriteAtomic(_path, new PanelPosition(panel.Position.X, panel.Position.Y));
            }
            catch (Exception error) { MainFile.Logger.Warn("面板位置保存失败：" + error.Message); }
        }
    }
}
