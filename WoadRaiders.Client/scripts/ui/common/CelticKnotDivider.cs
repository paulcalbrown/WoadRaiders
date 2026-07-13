using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// A horizontal two-strand Celtic plait, drawn procedurally: a woad strand and
/// a verdigris strand weave over and under each other, alternating at every
/// crossing. The illusion is the classic trick — draw both strands, then
/// redraw the "over" half-waves with a dark casing so they appear to cut the
/// strand beneath. Ends get small medallion dots; the centre crossing carries
/// a green jewel.
/// </summary>
public partial class CelticKnotDivider : Control
{
    private const float Margin = 24f;      // room for the end medallions
    private const float TargetHalfWave = 36f;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public override void _Draw()
    {
        var strandA = new Color(0.50f, 0.68f, 0.95f, 0.90f); // woad
        var strandB = new Color(UiTheme.Verdigris, 0.85f);
        float cy = Size.Y * 0.5f;
        float amp = Size.Y * 0.30f;
        float span = Size.X - 2f * Margin;
        if (span < TargetHalfWave * 2f)
            return;

        // A whole number of half-waves so both strands land on the centre line
        // at both ends; crossings sit at every half-wave boundary.
        int halves = Mathf.Max(2, Mathf.RoundToInt(span / TargetHalfWave));
        float half = span / halves;

        Vector2 PointA(float x) => new(x, cy + amp * Mathf.Sin(Mathf.Pi * (x - Margin) / half));
        Vector2 PointB(float x) => new(x, cy - amp * Mathf.Sin(Mathf.Pi * (x - Margin) / half));

        // Base pass: both strands in full — these read as the "under" parts.
        DrawStrand(PointA, Margin, Margin + span, strandA, casing: false);
        DrawStrand(PointB, Margin, Margin + span, strandB, casing: false);

        // Weave pass: around each crossing, redraw the winning strand with a
        // dark casing so it visibly passes over the other.
        for (int k = 0; k <= halves; k++)
        {
            float xk = Margin + k * half;
            float from = Mathf.Max(Margin, xk - half * 0.5f);
            float to = Mathf.Min(Margin + span, xk + half * 0.5f);
            if (k % 2 == 0)
                DrawStrand(PointA, from, to, strandA, casing: true);
            else
                DrawStrand(PointB, from, to, strandB, casing: true);
        }

        // End medallions and the centre jewel.
        foreach (var x in new[] { Margin, Margin + span })
        {
            DrawCircle(new Vector2(x, cy), 3f, UiTheme.OozeGreen);
            DrawArc(new Vector2(x, cy), 7f, 0, Mathf.Tau, 32, new Color(UiTheme.WoadDim, 0.8f), 1.3f, true);
        }
        var c = new Vector2(Margin + span * 0.5f, cy);
        DrawColoredPolygon(
            [c + new Vector2(-6, 0), c + new Vector2(0, -6), c + new Vector2(6, 0), c + new Vector2(0, 6)],
            new Color(UiTheme.OozeGreen, 0.95f));
        DrawArc(c, 9f, 0, Mathf.Tau, 32, new Color(UiTheme.WoadDim, 0.6f), 1.2f, true);
    }

    private void DrawStrand(Func<float, Vector2> point, float from, float to, Color color, bool casing)
    {
        int steps = Mathf.Max(2, Mathf.CeilToInt((to - from) / 3f));
        var points = new Vector2[steps + 1];
        var highlight = new Vector2[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            points[i] = point(from + (to - from) * i / steps);
            highlight[i] = points[i] + new Vector2(0, -1f);
        }
        if (casing)
            DrawPolyline(points, UiTheme.Night, 9f);
        DrawPolyline(points, color, 3.6f, true);
        DrawPolyline(highlight, new Color(color.Lightened(0.35f), 0.55f), 1.2f, true);
    }
}
