using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The scene-tree and sim↔engine vector helpers shared across the client. The
/// tree walks replace the hand-rolled recursions that scene scans (animation
/// players, fade meshes, environments) each used to carry.
/// </summary>
public static class NodeTreeExtensions
{
    /// <summary>The node and every descendant, depth-first. Iterative — authored scenes run deep.</summary>
    public static IEnumerable<Node> SelfAndDescendants(this Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.GetChildren())
                stack.Push(child);
        }
    }

    /// <summary>First node of the given type in the subtree (including the root), or null.</summary>
    public static T? FindDescendant<T>(this Node root) where T : class
    {
        foreach (var node in root.SelfAndDescendants())
            if (node is T match)
                return match;
        return null;
    }

    /// <summary>Sim space (System.Numerics, Y-up) → Godot. Same axes, 1:1.</summary>
    public static Vector3 ToGodot(this System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Godot → sim space (System.Numerics, Y-up). Same axes, 1:1.</summary>
    public static System.Numerics.Vector3 ToSim(this Vector3 v) => new(v.X, v.Y, v.Z);
}
