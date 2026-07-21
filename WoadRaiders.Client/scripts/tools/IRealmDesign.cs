using System;
using System.Collections.Generic;
using System.Linq;

namespace WoadRaiders.Client;

/// <summary>
/// A realm's design: the code that composes one realm's Godot scene.
///
/// The contract is deliberately almost empty — a name and "build yourself into
/// this scene" — because the scene is built FIRST and the served geometry is
/// baked FROM it. A design therefore has the WHOLE engine to work with: any
/// meshes, materials, particles, or imported asset kits, laid out by whatever
/// math (or hand-written table) suits that realm.
///
/// Nothing prefers boxes. The role verbs a design reaches for —
/// <see cref="RealmScene.AddFloor{T}"/>, <see cref="RealmScene.AddStructure{T}"/>,
/// <see cref="RealmScene.AddGeometry{T}"/> — take any Node3D: a sculpted
/// terrace, an arch, an instanced kit piece. Boxes are a convenience for the
/// rectilinear shapes a wall or a tread already is, offered by a separate helper
/// (<see cref="BoxKit"/> — <c>BoxKit.Floor(scene, ...)</c>) that just builds a
/// BoxMesh and hands it to those same role verbs, so the scene itself carries no
/// notion of boxes. A boxed floor and a modelled floor are filed identically.
/// Both realms shipping today happen to be boxed throughout; that is their level
/// design, not a limit of the pipeline, which bakes whatever geometry it finds.
///
/// State the ROLE rather than building the tree by hand: a mesh parented
/// straight onto <see cref="RealmScene.Root"/> is still collision (the bake
/// takes the whole tree), but nothing then knows it was meant as GROUND, so
/// <see cref="RealmScene.OnFloor"/> cannot seat the cast on it and every marker
/// placed that way lands at height 0.
///
/// To add a realm: write a class implementing this, list it in
/// <see cref="RealmDesigns"/>, and run <c>dotnet run tools/GenerateRealm.cs
/// &lt;Name&gt;</c>.
/// </summary>
public interface IRealmDesign
{
    /// <summary>The realm's name — how the generator and
    /// <c>build_realm_scene.gd</c> select this design, and what the builder
    /// names the saved scene's root. Conventionally it matches the map file's
    /// base name (Crag → maps/Crag.tscn).</summary>
    string Name { get; }

    /// <summary>Build the realm and hand back the finished scene. Everything in
    /// it — the sky, the lights, the ground, the cast — is this design's to
    /// state; a new <see cref="RealmScene"/> starts empty.</summary>
    RealmScene Build();
}

/// <summary>
/// Every realm design the generator can build, by name. This list is the ONLY
/// place a new realm has to register itself.
/// </summary>
public static class RealmDesigns
{
    private static readonly Func<IRealmDesign>[] All =
    {
        () => new CragDesign(),
        () => new CryptDesign(),
    };

    /// <summary>The design with this name, or null. Case-insensitive, so
    /// "crag", "Crag", and a Crag.tscn file name all resolve.</summary>
    public static IRealmDesign? Find(string name) =>
        All.Select(make => make()).FirstOrDefault(
            design => string.Equals(design.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every registered realm name — for usage messages.</summary>
    public static IEnumerable<string> Names => All.Select(make => make().Name);
}
