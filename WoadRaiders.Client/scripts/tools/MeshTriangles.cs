using System.Collections.Generic;
using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// Real triangles off a Godot subtree, in world space — the one step of the
/// realm pipeline that genuinely needs the engine, and the one place the
/// WINDING rule is written down.
///
/// Shared deliberately. The bake (<see cref="RealmBaker"/>) samples a finished
/// scene through this, and a design samples its own modelled ground through it
/// while building (<see cref="RealmScene.AddGeometry{T}"/>), so what a design
/// seats its cast on at author time and what the server serves at run time are
/// measured by the same code. Two copies of a winding convention is two chances
/// to get it backwards, and backwards is silent: every upward face reads as an
/// overhang and the realm simply has no floor.
/// </summary>
public static class MeshTriangles
{
    /// <summary>Running tally of what <c>no_collide</c> waved through. A
    /// MultiMesh counts once per live instance, since that is how many meshes
    /// were really excused.</summary>
    public sealed class Excluded
    {
        public int Meshes;
        public int Triangles;
    }

    /// <summary>
    /// Append every mesh under <paramref name="node"/> as world-space triangle
    /// vertices (9 floats per triangle). <paramref name="parentXf"/> is the
    /// transform the node hangs under — identity when the node's own transform
    /// is already world.
    ///
    /// Honours <c>no_collide</c> the way the bake does: inherited by the whole
    /// subtree and never revoked, so one tag on a folder of dressing covers all
    /// of it. Excluded meshes are counted into <paramref name="excludedTally"/>
    /// rather than silently dropped.
    ///
    /// Both mesh-bearing node types are taken. A <see cref="MultiMeshInstance3D"/>
    /// is read per instance, because collapsing a thousand grave slabs into one
    /// draw call is a RENDERING decision and must not quietly be a collision one
    /// as well. Skipping it would have made the batch both intangible AND
    /// invisible to the exclusion tally — an exemption with no tag on it and no
    /// count against it, which is precisely the failure <c>no_collide</c> is
    /// counted aloud to prevent. A design that wants its scatter passable says
    /// so, exactly as it does for any other geometry.
    ///
    /// Nothing else here bears triangles. Particles, decals, fog volumes and
    /// occluders are not geometry the realm is modelled from, and no author
    /// could mistake one for a wall.
    /// </summary>
    public static void Collect(Node node, Transform3D parentXf, List<float> triangles,
                               Excluded? excludedTally = null, bool excluded = false)
    {
        var xf = node is Node3D spatial ? parentXf * spatial.Transform : parentXf;
        excluded |= node.IsInGroup(RealmSceneFile.NoCollideGroup);

        switch (node)
        {
            case MeshInstance3D { Mesh: { } mesh }:
                Take(mesh, xf, triangles, excludedTally, excluded);
                break;

            // The instance transforms are relative to the node, so each one
            // composes UNDER it — the same rule as a child node's.
            case MultiMeshInstance3D { Multimesh: { Mesh: { } batched } batch }
                when batch.TransformFormat == MultiMesh.TransformFormatEnum.Transform3D:
                var live = batch.VisibleInstanceCount < 0 ? batch.InstanceCount : batch.VisibleInstanceCount;
                for (var i = 0; i < live; i++)
                    Take(batched, xf * batch.GetInstanceTransform(i), triangles, excludedTally, excluded);
                break;
        }

        foreach (var child in node.GetChildren())
            Collect(child, xf, triangles, excludedTally, excluded);
    }

    /// <summary>One mesh at one world transform: counted if excused, sampled otherwise.</summary>
    private static void Take(Mesh mesh, Transform3D xf, List<float> triangles,
                             Excluded? excludedTally, bool excluded)
    {
        var faces = mesh.GetFaces();
        if (excluded)
        {
            if (excludedTally is not null)
            {
                excludedTally.Meshes++;
                excludedTally.Triangles += faces.Length / 3;
            }
            return;
        }

        // Godot winds front faces CLOCKWISE; the soup (and Recast's slope
        // filter) want counter-clockwise, or every upward face reads as an
        // overhang. Swap two corners per triangle.
        for (var i = 0; i + 2 < faces.Length; i += 3)
        {
            foreach (var vertex in new[] { faces[i], faces[i + 2], faces[i + 1] })
            {
                var world = xf * vertex;
                triangles.Add(world.X);
                triangles.Add(world.Y);
                triangles.Add(world.Z);
            }
        }
    }

    /// <summary>0,1,2,... — for triangle batches that share no vertices, which
    /// is everything sampled straight out of Godot. <see cref="SoupBuilder"/>
    /// welds the duplicates away on Build.</summary>
    public static int[] SequentialIndices(int triangles)
    {
        var indices = new int[triangles * 3];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = i;
        return indices;
    }
}
