using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// Trig that regenerates byte-for-byte on every host. A realm is authored on one
/// machine and its scene is re-derived in CI on another (the regenerate-and-diff
/// guard in <c>.github/workflows/realms.yml</c>), so <c>REALM-C-010</c>'s "the
/// design produces exactly what is committed" has to hold ACROSS platforms, not
/// just across runs on one.
///
/// It does not hold for transcendentals. Godot's <c>Mathf.Cos/Sin</c>, its
/// <c>Basis(axis, angle)</c> rotations, and the Euler→Basis conversion behind
/// <c>Node3D.Rotation</c> all call <c>System.MathF</c>, which delegates to the
/// platform's libm — bit-identical on neither Windows nor Linux. One divergent
/// last-bit in a cosine changes a generated vertex, a baked triangle, or a
/// serialised transform, and the diff fails. (Reciprocal square root is safe:
/// <c>MathF.Sqrt</c> is a correctly-rounded hardware instruction, the same
/// everywhere, so the groin vaults were never at risk.)
///
/// Plain IEEE double arithmetic — +, −, ×, ÷ — IS bit-identical across x64
/// (SSE2, no x87, and .NET does not contract to FMA on its own), so a polynomial
/// evaluated in double is stable on every machine. That is all this is: a cosine
/// and sine good to ~1e-6 over the circle — far under a millimetre at realm
/// scale — computed the same way everywhere, and the Euler→Basis built from them
/// with Godot's own matrix layout so a prop keeps the orientation it had.
/// </summary>
internal static class Det
{
    /// <summary>Deterministic cosine and sine of <paramref name="angle"/> (radians).</summary>
    public static (double Cos, double Sin) CosSin(double angle)
    {
        const double HalfPi = 1.5707963267948966;
        var q = System.Math.Round(angle / HalfPi);   // nearest quarter-turn (deterministic)
        var r = angle - q * HalfPi;                   // remainder in [-pi/4, pi/4]
        var r2 = r * r;
        var s = r * (1 - r2 * (1.0 / 6 - r2 * (1.0 / 120 - r2 / 5040)));
        var c = 1 - r2 * (0.5 - r2 * (1.0 / 24 - r2 * (1.0 / 720 - r2 / 40320)));
        return (((long)q % 4 + 4) % 4) switch
        {
            0 => (c, s),
            1 => (-s, c),
            2 => (-c, -s),
            _ => (s, -c),
        };
    }

    /// <summary>
    /// Godot's YXZ Euler→Basis (the default <see cref="Node3D.RotationOrder"/>),
    /// built with <see cref="CosSin"/> instead of libm. Same matrices, same
    /// multiply order (<c>Y·X·Z</c>) as <c>Basis.FromEuler</c>, so a node assigned
    /// this basis keeps the exact orientation the equivalent <c>Rotation</c> gave —
    /// only the last-bit host dependence is gone.
    /// </summary>
    public static Basis EulerBasis(float pitch, float yaw, float roll)
    {
        var (cx, sx) = CosSin(pitch);
        var (cy, sy) = CosSin(yaw);
        var (cz, sz) = CosSin(roll);
        var x = new Basis(1, 0, 0, 0, (float)cx, (float)-sx, 0, (float)sx, (float)cx);
        var y = new Basis((float)cy, 0, (float)sy, 0, 1, 0, (float)-sy, 0, (float)cy);
        var z = new Basis((float)cz, (float)-sz, 0, (float)sz, (float)cz, 0, 0, 0, 1);
        return y * x * z;
    }

    /// <summary>
    /// <see cref="EulerBasis"/> with a per-axis scale folded into the columns —
    /// which is how <see cref="Node3D"/> applies local scale (<c>R·diag(scale)</c>).
    /// Assign the result straight to <c>node.Basis</c> and leave <c>Rotation</c> and
    /// <c>Scale</c> untouched, so the transform serialises from these bytes rather
    /// than from a libm Euler→Basis at save time.
    /// </summary>
    public static Basis EulerScale(float pitch, float yaw, float roll, Vector3 scale)
    {
        var r = EulerBasis(pitch, yaw, roll);
        return new Basis(r.Column0 * scale.X, r.Column1 * scale.Y, r.Column2 * scale.Z);
    }
}
