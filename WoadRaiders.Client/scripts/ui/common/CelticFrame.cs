using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// A decorative frame around a full-screen menu: a doubled border line
/// inset from the edges with a triskele — the Celtic triple spiral — set in a
/// ring medallion at each corner. Purely ornamental; ignores the mouse and
/// redraws only on resize.
/// </summary>
public partial class CelticFrame : Control
{
    private const float OuterInset = 26f;
    private const float InnerInset = 36f;
    private const float CornerInset = 60f;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        // Anchors AND offsets: a plain anchors preset on an in-tree node keeps
        // its current (zero) rect, leaving the frame collapsed in a corner.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Resized += QueueRedraw;
    }

    public override void _Draw()
    {
        var line = new Color(UiTheme.WoadDim, 0.35f);
        var faint = new Color(UiTheme.WoadDim, 0.18f);
        DrawRect(new Rect2(OuterInset, OuterInset, Size.X - 2 * OuterInset, Size.Y - 2 * OuterInset),
            line, filled: false, width: 2f);
        DrawRect(new Rect2(InnerInset, InnerInset, Size.X - 2 * InnerInset, Size.Y - 2 * InnerInset),
            faint, filled: false, width: 1f);

        foreach (var corner in new Vector2[]
                 {
                     new(CornerInset, CornerInset),
                     new(Size.X - CornerInset, CornerInset),
                     new(CornerInset, Size.Y - CornerInset),
                     new(Size.X - CornerInset, Size.Y - CornerInset),
                 })
            DrawMedallion(corner);
    }

    private void DrawMedallion(Vector2 center)
    {
        // Clear the frame lines beneath so the medallion sits "on" the frame.
        DrawCircle(center, 32f, UiTheme.Night);
        DrawArc(center, 26f, 0, Mathf.Tau, 64, new Color(UiTheme.WoadDim, 0.55f), 1.5f, true);
        DrawArc(center, 30f, 0, Mathf.Tau, 64, new Color(UiTheme.WoadDim, 0.25f), 1f, true);
        DrawTriskele(center, 20f, new Color(UiTheme.WoadDim, 0.85f));
        DrawCircle(center, 2.2f, new Color(UiTheme.OozeGreen, 0.9f));
    }

    private void DrawTriskele(Vector2 center, float radius, Color color)
    {
        for (int arm = 0; arm < 3; arm++)
        {
            float rot = arm * Mathf.Tau / 3f;
            var points = new Vector2[22];
            for (int i = 0; i < points.Length; i++)
            {
                float t = i / (float)(points.Length - 1);
                float angle = rot + t * 4.4f;
                float r = 2f + t * (radius - 4f);
                points[i] = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r;
            }
            DrawPolyline(points, color, 2f, true);
            DrawCircle(points[^1], 2.2f, color);
        }
    }
}
