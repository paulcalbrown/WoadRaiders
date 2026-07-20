using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The Crag's imported dressing — a LIGHT pass, because the terraces speak for
/// themselves. Of the committed kits (under assets/crypt/; see each pack's
/// LICENSE and ATTRIBUTION.md) only the pieces that read as megalithic belong
/// up here: Kenney's stone obelisks, iron fire baskets, and rubble. The
/// graves, coffins, and candles stay below, in the Crypt where they belong.
///
/// Instanced .glb pieces, like the Crypt's relics: the saved scene carries
/// instance= references, and none of it reaches the bake. Kenney's kit runs
/// miniature, so scales run ~40+ against characters ~44 units tall.
/// </summary>
public sealed partial class CragDesign
{
    /// <summary>One kit piece, loaded once and instanced everywhere.</summary>
    private readonly record struct Kit(PackedScene Scene)
    {
        public static Kit Load(string file) =>
            new(GD.Load<PackedScene>($"res://{file}"));

        public Node3D At(Vector3 position, float scale, float yaw = 0f)
        {
            var node = Scene.Instantiate<Node3D>();
            node.Position = position;
            node.Rotation = new Vector3(0f, yaw, 0f);
            node.Scale = new Vector3(scale, scale, scale);
            return node;
        }
    }

    /// <summary>Dress the ascent: fire baskets flanking each stair's foot and
    /// head, obelisks at the causeway's mouth, baskets marking the boss
    /// court's gate and dais corners, and old rubble on the gate court.</summary>
    private static void Scenery(RealmScene scene)
    {
        var folder = scene.Folder("Scenery");
        var fireBasket = Kit.Load("assets/crypt/kenney_graveyard/fire-basket.glb");
        var obelisk = Kit.Load("assets/crypt/kenney_graveyard/pillar-obelisk.glb");
        var debris = Kit.Load("assets/crypt/kenney_graveyard/debris.glb");

        void Place(Kit kit, float x, float z, float scale, float yaw = 0f)
        {
            var node = kit.At(scene.OnFloor(x, z), scale, yaw);
            node.Name = $"Scenery{folder.GetChildCount()}";
            folder.AddChild(node);
        }

        // The first stair, court to processional; the second, up to the ward.
        foreach (var (x, z) in new[]
                 {
                     (1170f, 1820f), (1170f, 2180f), (1530f, 1820f), (1530f, 2180f),
                     (2670f, 1820f), (2670f, 2180f), (3030f, 1820f), (3030f, 2180f),
                 })
            Place(fireBasket, x, z, 42f);

        // The causeway's mouth on the high ward, and the boss court's gate.
        Place(obelisk, 3190, 2750, 48f);
        Place(obelisk, 3610, 2750, 48f);
        Place(fireBasket, 3200, 3260, 42f);
        Place(fireBasket, 3600, 3260, 42f);

        // The dais corners burn for the one who stands there.
        Place(fireBasket, 3270, 3520, 42f);
        Place(fireBasket, 3530, 3520, 42f);
        Place(fireBasket, 3270, 3780, 42f);
        Place(fireBasket, 3530, 3780, 42f);

        // Giants' leavings on the gate court.
        Place(debris, 600, 2350, 45f, 1.1f);
        Place(debris, 950, 1550, 45f, 2.4f);
        Place(debris, 1600, 2300, 45f, 0.6f);
    }
}
