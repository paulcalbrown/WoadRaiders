using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// The KayKit adventurer models ship with their whole armory attached — every
/// shield, both swords, the crossbow, the spellbook — all parented to the rig
/// and drawn at once. This trims each class down to its own body plus the one
/// primary weapon it fights with, hiding the rest. Pipeline-built characters
/// (the Warrior) arrive pre-trimmed and pass through on their body prefix.
/// Applied to the in-game player views and the character-select previews.
/// </summary>
public static class CharacterLoadout
{
    // A model's body and cosmetic meshes carry the character's own name prefix
    // (Warrior_Body, Mage_Hat, Rogue_Cape); detachable gear does not. So a mesh
    // is kept when it starts with the body prefix OR is one of the class's
    // chosen weapons — everything else is hidden.
    private readonly record struct Loadout(string BodyPrefix, string[] Weapons);

    private static readonly Dictionary<CharacterClass, Loadout> Loadouts = new()
    {
        // The Warrior ships pre-trimmed from the art pipeline (Warrior_* meshes
        // only; weapon/shield props attach at ingest when they exist).
        [CharacterClass.Warrior] = new("Warrior", []),
        [CharacterClass.Rogue] = new("Rogue", ["Knife"]),
        [CharacterClass.Mage] = new("Mage", ["2H_Staff"]),
        [CharacterClass.Ranger] = new("Rogue", ["2H_Crossbow"]), // uses the Rogue_Hooded body
    };

    /// <summary>Hide every attached weapon/prop on <paramref name="model"/> except the class's primary loadout.</summary>
    public static void Apply(Node model, CharacterClass cls)
    {
        if (!Loadouts.TryGetValue(cls, out var loadout))
            return;

        foreach (var node in model.SelfAndDescendants())
        {
            if (node is not MeshInstance3D mesh)
                continue;
            var name = mesh.Name.ToString();
            mesh.Visible = name.StartsWith(loadout.BodyPrefix, System.StringComparison.Ordinal)
                        || System.Array.IndexOf(loadout.Weapons, name) >= 0;
        }
    }
}
