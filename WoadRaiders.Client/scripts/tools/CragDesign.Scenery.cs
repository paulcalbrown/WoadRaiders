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
/// instance= references. The whole pass is declared PASSABLE
/// (<see cref="RealmScene.DeclarePassable{T}"/>) — and that DECLARATION is what
/// keeps it out of the bake, not the fact that it is instanced. Being a kit piece
/// buys nothing: every mesh in the scene is collision until an author says
/// otherwise, so a fire basket left undeclared is a fire basket raiders walk
/// into. Kenney's kit runs miniature, so scales run ~40+ against characters
/// ~44 units tall.
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
            // Deterministic basis (see Det): a boulder's random yaw would otherwise
            // serialise host-dependent bytes through Godot's libm Euler→Basis.
            node.Basis = Det.EulerScale(0f, yaw, 0f, new Vector3(scale, scale, scale));
            return node;
        }
    }

    /// <summary>Dress the ascent: fire baskets flanking each stair's foot and
    /// head, obelisks at the causeway's mouth, baskets marking the boss
    /// court's gate and dais corners, and old rubble on the gate court.</summary>
    private static void Scenery(RealmScene scene)
    {
        var folder = scene.DeclarePassable(scene.Folder("Scenery"));
        var fireBasket = Kit.Load("assets/crypt/kenney_graveyard/fire-basket.glb");
        var obelisk = Kit.Load("assets/crypt/kenney_graveyard/pillar-obelisk.glb");
        var debris = Kit.Load("assets/crypt/kenney_graveyard/debris.glb");

        // Every piece names the TERRACE it stands on — `at`'s Y — so a basket
        // at a stair's foot seats on the court below and its twin at the head
        // on the terrace above, instead of both taking whatever is topmost.
        void Place(Kit kit, Vector3 at, float scale, float yaw = 0f)
        {
            var node = kit.At(scene.OnFloor(at), scale, yaw);
            node.Name = $"Scenery{folder.GetChildCount()}";
            folder.AddChild(node);
        }

        // The first stair, court to processional; the second, up to the ward.
        // Each flight is flanked at its FOOT and its HEAD, so the pairs sit a
        // terrace apart.
        foreach (var (x, terrace, z) in new[]
                 {
                     (1170f, Court, 1820f), (1170f, Court, 2180f),
                     (1530f, Processional, 1820f), (1530f, Processional, 2180f),
                     (2670f, Processional, 1820f), (2670f, Processional, 2180f),
                     (3030f, HighWard, 1820f), (3030f, HighWard, 2180f),
                 })
            Place(fireBasket, new Vector3(x, terrace, z), 42f);

        // The causeway's mouth on the high ward, and the boss court's gate.
        Place(obelisk, new Vector3(3190, HighWard, 2750), 48f);
        Place(obelisk, new Vector3(3610, HighWard, 2750), 48f);
        Place(fireBasket, new Vector3(3200, HighWard, 3260), 42f);
        Place(fireBasket, new Vector3(3600, HighWard, 3260), 42f);

        // The dais corners burn for the one who stands there.
        Place(fireBasket, new Vector3(3270, Dais, 3520), 42f);
        Place(fireBasket, new Vector3(3530, Dais, 3520), 42f);
        Place(fireBasket, new Vector3(3270, Dais, 3780), 42f);
        Place(fireBasket, new Vector3(3530, Dais, 3780), 42f);

        // Giants' leavings on the gate court — and one that rolled on down the
        // processional.
        Place(debris, new Vector3(600, Court, 2350), 45f, 1.1f);
        Place(debris, new Vector3(950, Court, 1550), 45f, 2.4f);
        Place(debris, new Vector3(1600, Processional, 2300), 45f, 0.6f);
    }
}
