using System;
using Godot;
using WoadRaiders.Core;
using Aabb = WoadRaiders.Core.Aabb; // the sim's world box, not Godot's

namespace WoadRaiders.Client;

/// <summary>
/// Box convenience helpers over a <see cref="RealmScene"/>. A box is the honest
/// shape for a rectilinear wall, floor, or stair tread, and these spare a design
/// from hand-building a BoxMesh under a transform.
///
/// A PURE helper, and deliberately separate from <see cref="RealmScene"/>: it
/// holds no state and touches nothing the scene does not expose publicly. Each
/// call builds a BoxMesh node and hands it to the scene's own role verbs
/// (<see cref="RealmScene.AddFloor{T}"/> / <see cref="RealmScene.AddStructure{T}"/>),
/// so a box is filed, named, and floor-registered exactly as any modelled mesh
/// is — the scene never learns it was a box. That is the whole point: a box is
/// one convenient shape a design may reach for, not something a realm is made
/// of, so it lives out here rather than in the scene's own surface.
/// </summary>
public static class BoxKit
{
    /// <summary>A box floor raiders walk on, filed under "Ground".</summary>
    public static MeshInstance3D Floor(RealmScene scene, Aabb box, Material material, string? name = null) =>
        Add(scene, box, material, floor: true, name);

    /// <summary>A blocking box — wall, roof, monument — filed under "Structure".</summary>
    public static MeshInstance3D Structure(RealmScene scene, Aabb box, Material material, string? name = null) =>
        Add(scene, box, material, floor: false, name);

    /// <summary>A box in either role, from its world-space bounds.</summary>
    public static MeshInstance3D Add(RealmScene scene, Aabb box, Material material, bool floor, string? name = null) =>
        At(scene, new Transform3D(Basis.Identity, box.Center.ToGodot()), box.Size.ToGodot(), material, floor, name);

    /// <summary>
    /// A box under any transform — the primitive behind ramps and stairs, and
    /// the cheapest way to state a shape a box already fits. The transform lands
    /// verbatim in the saved scene, where the bake (and the engine-free parser)
    /// turn it back into world triangles.
    /// </summary>
    public static MeshInstance3D At(RealmScene scene, Transform3D xform, Vector3 size, Material material,
                                    bool floor, string? name = null)
    {
        var node = new MeshInstance3D
        {
            Transform = xform,
            Mesh = new BoxMesh { Size = size, Material = material },
        };
        return floor ? scene.AddFloor(node, name) : scene.AddStructure(node, name);
    }

    /// <summary>
    /// A pitched floor whose top surface runs from one point to another — a
    /// ramp. Both ends are the CENTRE of their edge; the deck hangs its
    /// thickness below the surface.
    /// </summary>
    public static MeshInstance3D Ramp(RealmScene scene, Vector3 from, Vector3 to, float width, Material material,
                                      float thickness = 12f, string? name = null)
    {
        var dir = (to - from).Normalized();
        var side = new Vector3(dir.Z, 0, -dir.X);
        if (side.LengthSquared() < 1e-6f)
            throw new ArgumentException("a ramp must run somewhere on the ground plane, not straight up");
        side = side.Normalized();
        var normal = side.Cross(dir);
        if (normal.Y < 0)
            normal = -normal;
        var centre = (from + to) * 0.5f - normal * (thickness * 0.5f);
        return At(scene, new Transform3D(new Basis(side, normal, dir), centre),
                  new Vector3(width, thickness, (to - from).Length()), material, floor: true, name);
    }

    /// <summary>
    /// A stair of treads from one point up (or down) to another, each tread
    /// rising less than the sim's StepHeight so feet flow up it.
    /// </summary>
    public static void Stairs(RealmScene scene, Vector3 from, Vector3 to, float width, Material material)
    {
        var run = new Vector3(to.X - from.X, 0, to.Z - from.Z);
        if (run.LengthSquared() < 1e-6f)
            throw new ArgumentException("a stair must run somewhere on the ground plane, not straight up");
        var dir = run.Normalized();
        var side = new Vector3(dir.Z, 0, -dir.X);
        var steps = Math.Max(1, Mathf.CeilToInt(Mathf.Abs(to.Y - from.Y) / (SimConstants.StepHeight - 2f)));

        for (var i = 0; i < steps; i++)
        {
            var a = from.Lerp(to, i / (float)steps);
            var b = from.Lerp(to, (i + 1) / (float)steps);
            var top = MathF.Max(a.Y, b.Y);
            var depth = MathF.Max(MathF.Abs(b.Y - a.Y) + 10f, 14f); // overlap the tread below
            var centre = new Vector3((a.X + b.X) * 0.5f, top - depth * 0.5f, (a.Z + b.Z) * 0.5f);
            At(scene, new Transform3D(new Basis(side, Vector3.Up, dir), centre),
               new Vector3(width, depth, (b - a with { Y = a.Y }).Length()), material, floor: true);
        }
    }
}
