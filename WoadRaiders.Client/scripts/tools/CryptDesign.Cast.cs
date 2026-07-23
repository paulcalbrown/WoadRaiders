using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// The Crypt's garrison, its lord, and the way out.
///
/// ONE HUNDRED camps (Minion 53, Rogue 27, Mage 20). It was two hundred, and
/// two hundred is not a difficulty setting — it is a wall. A single raider
/// cannot cross the Ossuary alive, which makes the second half of the realm
/// unreachable and every beat past it unauthored in practice. Halved until
/// the realm can be walked end to end solo; the wire never bounded this
/// number and does not bound it now, playability does.
///
/// Camps are laid as FRACTIONS of the space that holds them, never as
/// coordinates, so reshaping a chamber spreads its garrison through the new
/// floor instead of leaving it huddled where the old walls used to be. Each
/// states the LEVEL it belongs to as well, which is what keeps a sentry on the
/// span's deck and the drowned garrison on the pit floor 480 below it —
/// because the camp says so, not because its fraction happened to miss the
/// bridge's footprint.
/// </summary>
public sealed partial class CryptDesign
{
    private void Cast()
    {
        // The raiders arrive in the porch, at the one place daylight still
        // reaches — so that "inside" has something to mean afterwards.
        var porch = Named("B1");
        _scene.SetPlayerSpawn(_scene.OnFloor(new Vector3(porch.X0 + 200f, porch.FloorY, porch.MidZ)));

        // Beat 2 — the nave. Two types: minions in the vessel, rogues working
        // the aisles where the arcade breaks the sight lines.
        Camps("B2", EnemyType.Minion, 10, 0.10f, 0.30f, 0.90f, 0.70f, salt: 101);
        // Rogues in the AISLES, one band each side — and both bands stop short
        // of the arcade lines, because a pier is 92 across and a camp placed in
        // one is not a flanker, it is a validator failure.
        Camps("B2", EnemyType.Rogue, 3, 0.15f, 0.02f, 0.85f, 0.20f, salt: 103);
        Camps("B2", EnemyType.Rogue, 2, 0.15f, 0.80f, 0.85f, 0.98f, salt: 105);

        // Beat 4 — the bone gallery, the realm's biggest horde and its most
        // oppressive room. Three types: the crescendo of the upper crypt.
        Camps("B3", EnemyType.Minion, 15, 0.08f, 0.10f, 0.92f, 0.90f, salt: 107);
        Camps("B3", EnemyType.Rogue, 5, 0.12f, 0.06f, 0.88f, 0.94f, salt: 109);
        Camps("B3", EnemyType.Mage, 5, 0.55f, 0.12f, 0.94f, 0.88f, salt: 113);

        // Beat 6 — the span. ONE type, and every one of them a rogue: a
        // deliberate valley in the palette, and the enemy that punishes anyone
        // who drifts off a bridge 160 wide.
        Camps("B4c", EnemyType.Rogue, 7, 0.06f, 0.20f, 0.94f, 0.80f, salt: 127);

        // Beat 6b — the pit, optional. Everyone the bridge has ever dropped.
        // Named at the pit's own floor, or they would seat on the deck above.
        Camps("B4", EnemyType.Minion, 8, 0.10f, 0.08f, 0.90f, 0.92f, salt: 131);
        Camps("B4", EnemyType.Mage, 2, 0.20f, 0.55f, 0.80f, 0.92f, salt: 137);

        // Beat 9 — the gallery. One type again: a valley before the Forecourt,
        // and contrast is what makes the next accent land.
        Camps("B5", EnemyType.Minion, 10, 0.06f, 0.15f, 0.94f, 0.85f, salt: 139);

        // Beat 9c — the cubiculum, sealed, optional, and paid for. Its band is
        // held to the SOUTHERN half: the Cubiculum and the Forecourt share plan
        // area (that overlap IS the breach between them), so the Forecourt's own
        // south wall stands inside this room's northern strip.
        Camps("B7", EnemyType.Rogue, 5, 0.14f, 0.56f, 0.86f, 0.96f, salt: 149);
        Camps("B7", EnemyType.Mage, 3, 0.20f, 0.60f, 0.80f, 0.94f, salt: 151);

        // Beat 10 — the forecourt, in sight of the boss. Mages on the flanks so
        // the approach is under fire while the trilithon is still ahead.
        Camps("B8", EnemyType.Minion, 5, 0.15f, 0.15f, 0.85f, 0.85f, salt: 157);
        Camps("B8", EnemyType.Mage, 5, 0.08f, 0.10f, 0.92f, 0.90f, salt: 163);

        // Beat 12 — the Wheel. All three types, the realm's only crescendo,
        // drawn around the cist rather than standing on it.
        Camps("B9", EnemyType.Minion, 5, 0.12f, 0.12f, 0.88f, 0.88f, salt: 167);
        Camps("B9", EnemyType.Rogue, 5, 0.06f, 0.20f, 0.94f, 0.80f, salt: 173);
        Camps("B9", EnemyType.Mage, 5, 0.10f, 0.06f, 0.90f, 0.94f, salt: 179);

        // The lord of the realm stands ON the cist, so the spawn asks at the
        // cist's height rather than the chamber's — the plate is what he is
        // meant to be standing on.
        var wheel = Named("B9");
        _scene.SetBossSpawn(_scene.OnFloor(new Vector3(wheel.MidX, wheel.FloorY + DaisRise, wheel.MidZ)));

        // The way out opens in the FORECOURT, not on the corpse: the run ends
        // with a short walk back up the passage and out through the trilithon,
        // into the space the boss was first seen from. The vista that opened the
        // fight closes it — and without this marker the run would simply stop on
        // the tick of the kill, which is the one beat the pacing sources are
        // unanimous about not skipping.
        var forecourt = Named("B8");
        _scene.SetPortalSpawn(_scene.OnFloor(
            new Vector3(forecourt.MidX, forecourt.FloorY, forecourt.MidZ + forecourt.Depth * 0.18f)));
    }

    /// <summary>
    /// Scatter <paramref name="count"/> camps of one type across a fraction-rect
    /// of a space's floor, deterministically — same garrison every run, no
    /// framework RNG, because the realm has to regenerate byte-identically.
    /// </summary>
    private void Camps(string id, EnemyType type, int count,
                       float u0, float v0, float u1, float v1, int salt)
    {
        var space = Named(id);

        // Inset in WORLD units, not fractions. A fraction of a small chamber
        // stands far closer to its wall than the same fraction of a large one,
        // so a band that reads as comfortable in the Ossuary buries camps in
        // the masonry of the Cubiculum — and a camp inside a wall is not a
        // fight, it is a validator failure a long way from its cause. Clamped,
        // so a narrow space collapses to its centre line rather than inverting.
        const float Inset = 90f;
        float Band(float spaceLo, float spaceHi, float bandLo, float bandHi, float t)
        {
            // Clamp the band INTO the space's safe interior. Insetting the band
            // itself would compound with the fractions the caller already chose
            // and squeeze a deliberate scatter down to a line.
            var lo = Mathf.Max(bandLo, spaceLo + Inset);
            var hi = Mathf.Min(bandHi, spaceHi - Inset);
            return hi > lo ? Mathf.Lerp(lo, hi, t) : (spaceLo + spaceHi) * 0.5f;
        }

        for (var i = 0; i < count; i++)
        {
            // Stratified rather than uniform: a plain hash scatter clumps, and a
            // clump reads as one fight with a gap beside it rather than as a
            // garrison holding a room.
            var columns = Mathf.CeilToInt(Mathf.Sqrt(count));
            var cell = (col: i % columns, row: i / columns);
            var rows = Mathf.CeilToInt(count / (float)columns);
            var u = Mathf.Lerp(u0, u1, (cell.col + 0.15f + Hash(i, salt, 811) * 0.7f) / columns);
            var v = Mathf.Lerp(v0, v1, (cell.row + 0.15f + Hash(i, salt, 823) * 0.7f) / Mathf.Max(1, rows));

            var at = new Vector3(
                Band(space.X0, space.X1, space.X0 + space.Width * u0, space.X0 + space.Width * u1,
                     (u - u0) / Mathf.Max(1e-4f, u1 - u0)),
                space.FloorY,
                Band(space.Z0, space.Z1, space.Z0 + space.Depth * v0, space.Z0 + space.Depth * v1,
                     (v - v0) / Mathf.Max(1e-4f, v1 - v0)));
            _scene.AddEnemy(type, _scene.OnFloor(ClearOfStanding(at)));
        }
    }
}
