using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// The Crypt, checked against its own spec (docs/realms/crypt.md and
/// docs/realms/CONSTITUTION.md).
///
/// Every requirement in that spec carries either a `[checked:]` oracle or a
/// `[judged:]` one. The judged ones are art and belong to a person. The checked
/// ones belong HERE — and most of them were written `[checked: TODO]`, which is
/// a promise rather than a check.
///
/// These read the SHIPPED artefacts: the .tscn Godot's own serializer wrote and
/// the .json baked from it. Nothing is re-derived and no engine is needed — the
/// scene is text, and the whole point of the pipeline is that what ships is what
/// was built. A test that rebuilt the realm to check it would be testing this
/// process's idea of the realm rather than the file the client loads.
/// </summary>
public class CryptSpecTests
{
    // ------------------------------------------------------------- the budgets

    [Fact]
    public void The_baked_realm_stays_under_its_triangle_budget() // BUDGET-001
    {
        if (Baked() is not { } realm)
            return;
        Assert.NotNull(realm.Soup);
        Assert.InRange(realm.Soup!.Triangles.Length / 3, 1, 4_000_000);
    }

    [Fact]
    public void The_scene_stays_small_enough_to_read_a_diff_of() // BUDGET-004
    {
        if (ScenePath() is not { } path)
            return;
        Assert.InRange(new FileInfo(path).Length, 1, 5L * 1024 * 1024);
    }

    [Fact]
    public void Every_shared_mesh_is_referenced_rather_than_inlined() // REALM-C-010a
    {
        if (Scene() is not { } scene)
            return;

        // A library piece inlined as a SubResource would be copied into the scene
        // once per placement — which is the entire cost the sibling .res library
        // exists to avoid, and it would show up only as a scene that had quietly
        // grown by a hundredfold.
        Assert.DoesNotContain("[sub_resource type=\"ArrayMesh\"", scene);
        Assert.Contains("maps/Crypt/", scene);
    }

    [Fact]
    public void Vendored_textures_stay_under_their_disk_budget() // BUDGET-012
    {
        if (Pbr() is not { } pbr)
            return;
        var bytes = Directory.EnumerateFiles(pbr, "*.jpg", SearchOption.AllDirectories)
                             .Sum(f => new FileInfo(f).Length);
        Assert.InRange(bytes, 1, 400L * 1024 * 1024);
    }

    [Fact]
    public void Vendored_textures_are_imported_for_VRAM_and_not_for_disk() // LOOK-001
    {
        if (Pbr() is not { } pbr)
            return;

        var sidecars = Directory.EnumerateFiles(pbr, "*.jpg.import", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(sidecars);

        foreach (var file in sidecars)
        {
            var text = File.ReadAllText(file);
            var normal = file.EndsWith("_nor.jpg.import", StringComparison.Ordinal);

            // Godot's DEFAULTS are Lossless and no mipmaps, which is right for a
            // UI sprite and wrong for every one of these: uncompressed 2K maps at
            // this volume run several times BUDGET-016 in VRAM, and a wall with no
            // mip chain aliases into crawling noise down a hall the moment the
            // chase camera moves. Nothing warns; it just looks cheap.
            Assert.Contains("compress/mode=2", text);
            Assert.Contains("mipmaps/generate=true", text);
            Assert.Contains($"compress/normal_map={(normal ? 1 : 0)}", text);

            // At 1 Godot REWRITES this sidecar the first time the texture is used
            // in 3D — a committed file that changes by itself, and a realm whose
            // regeneration stops being byte-identical for reasons nobody typed.
            Assert.Contains("detect_3d/compress_to=0", text);
        }
    }

    // ------------------------------------------------------------ the lighting

    [Fact]
    public void Flicker_moves_energy_and_never_a_transform() // LOOK-010
    {
        if (Scene() is not { } scene)
            return;

        var tracks = Regex.Matches(scene, @"tracks/\d+/path = NodePath\(""([^""]*)""\)")
                          .Select(m => m.Groups[1].Value)
                          .ToList();
        Assert.NotEmpty(tracks);

        // Godot caches a positional light's shadow map and throws it away the
        // moment the light moves. One transform track on one Light3D turns a
        // static stone interior from paying its shadow cost once into paying it
        // every frame, for an effect indistinguishable from a brightness curve.
        Assert.All(tracks, path => Assert.EndsWith(":light_energy", path));
    }

    [Fact]
    public void Every_lit_chamber_guttes_on_its_own_clock() // LOOK-010
    {
        if (Scene() is not { } scene)
            return;

        var players = Regex.Matches(scene, @"\[node name=""(Flicker_[A-Za-z0-9]+)"" type=""AnimationPlayer""")
                           .Select(m => m.Groups[1].Value).ToList();
        Assert.True(players.Count >= 12, $"only {players.Count} chambers flicker");

        var speeds = Regex.Matches(scene, @"speed_scale = ([\d.]+)")
                          .Select(m => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                          .ToList();
        Assert.All(speeds, s => Assert.InRange(s, 0.85f, 1.25f));
        // If they all ran at one speed the realm would breathe as a single
        // animation rather than as a hundred separate fires.
        Assert.True(speeds.Distinct().Count() > 1, "every chamber pulses in phase");
    }

    [Fact]
    public void No_light_is_stated_in_metres_by_mistake() // the metre law
    {
        if (Scene() is not { } scene)
            return;

        // Godot's attenuation is (1 − (d/range)⁴)² · d^(−decay), and that d is in
        // WORLD UNITS. This realm runs 24 units to the metre, so an energy that
        // reads sensibly in a metric project — 3, 9, 12 — is 576× too dim here.
        // It cost a whole build to find, because the failure is not an error or a
        // missing object: the realm renders perfectly and is simply black, with
        // the lights contributing less than the ambient meant to fill behind them.
        //
        // One metre squared is the floor. Nothing legitimately dimmer than a
        // single candle at one metre is worth putting in a scene file.
        // Anchored to the start of the line, or it also matches
        // `ambient_light_energy`, which is a FRACTION of a fill colour and has
        // nothing to do with distance — the environment's ambient at 0.2 is
        // correct and this test failed on it first time out.
        const float Metre = 24f;
        var energies = Regex.Matches(scene, @"^light_energy = ([\d.e+-]+)", RegexOptions.Multiline)
                            .Select(m => float.Parse(m.Groups[1].Value,
                                                     NumberStyles.Float, CultureInfo.InvariantCulture))
                            .ToList();
        Assert.NotEmpty(energies);
        Assert.All(energies, e => Assert.True(
            e >= Metre,
            $"a light at energy {e} is stated in metres: under this realm's scale it lights nothing"));
    }

    [Fact]
    public void There_is_no_fog() // PLAY-003
    {
        if (Scene() is not { } scene)
            return;

        // This test used to assert the OPPOSITE — that ground mist existed and
        // pooled at the right height. Playing the realm retired the whole idea:
        // fog in a place lit by pooled flame veils what little the torches reach
        // AND lifts the black the look depends on, so it subtracts twice. Kept as
        // a test rather than deleted, because "add some atmospheric fog" is a
        // thing someone will reasonably try again.
        // ABSENCE is the proof, not a `= false`. Godot's serializer omits any
        // property still at its default, and both fog flags default to false —
        // so setting them false writes nothing whatsoever. Asserting on the
        // presence of "fog_enabled = false" fails against a scene that is
        // perfectly correct, which is exactly how this test failed first time.
        Assert.DoesNotContain("type=\"FogVolume\"", scene);
        Assert.DoesNotContain("fog_enabled = true", scene);
        Assert.DoesNotContain("volumetric_fog_enabled = true", scene);
        Assert.DoesNotContain("fog_density", scene);
    }

    [Fact]
    public void The_realm_is_lit_within_its_budget() // PLAY-004, BUDGET-007
    {
        if (Scene() is not { } scene)
            return;

        var lights = Regex.Matches(scene, @"type=""(Spot|Omni|Directional)Light3D""").Count;
        // Lower bound as well as upper. A realm that is under-lit fails just as
        // surely as one that is too expensive, and 193 lights was not enough to
        // see the room you were fighting in.
        Assert.InRange(lights, 300, 900);

        var shadowed = Regex.Matches(scene, @"^shadow_enabled = true", RegexOptions.Multiline).Count;
        Assert.InRange(shadowed, 0, 40);
    }

    // ------------------------------------------------------------- the burials

    [Fact]
    public void Burial_states_are_graded_rather_than_uniform() // LOOK-016
    {
        if (Scene() is not { } scene)
            return;

        var words = Regex.Matches(scene, @"maps/Crypt/loculi_\d+_([SCRO]+)\.res")
                         .Select(m => m.Groups[1].Value)
                         .Distinct().ToList();

        // The trail of a previous expedition is the only story this realm tells
        // without a prop: sealed where the raiders come in, systematically robbed
        // along the line somebody already worked. One word everywhere is a
        // texture; the gradient is the point.
        Assert.True(words.Count >= 4, $"only {words.Count} burial state(s) in the whole realm");
        Assert.Contains(words, w => w.All(c => c == 'S'));   // untouched, at the way in
        Assert.Contains(words, w => w.Contains('R'));        // robbed, further along

        // Robbery climbs from the floor: the top tier is out of reach without a
        // ladder, and nobody brought one.
        Assert.All(words, w => Assert.NotEqual('R', w[^1]));
    }

    [Fact]
    public void All_four_burial_forms_appear() // LOOK-015
    {
        if (Scene() is not { } scene)
            return;
        foreach (var form in new[] { "loculi_", "arcosolium_", "forma_", "revetment_" })
            Assert.Contains($"maps/Crypt/{form}", scene);
    }

    // ------------------------------------------------------------ the dressing

    [Fact]
    public void Nothing_is_mirrored_by_negative_scale() // LOOK-020
    {
        if (Scene() is not { } scene)
            return;

        var inverted = new List<string>();
        foreach (Match m in Regex.Matches(scene, @"transform = Transform3D\(([^)]*)\)"))
        {
            var n = m.Groups[1].Value.Split(',')
                     .Select(v => float.Parse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture))
                     .ToArray();
            if (n.Length < 9)
                continue;
            // Godot's Transform3D takes the basis COLUMNS first, then the origin.
            var determinant = n[0] * (n[4] * n[8] - n[5] * n[7])
                            - n[3] * (n[1] * n[8] - n[2] * n[7])
                            + n[6] * (n[1] * n[5] - n[2] * n[4]);
            if (determinant < 0f)
                inverted.Add(m.Value[..Math.Min(60, m.Value.Length)]);
        }

        // A negative scale gives the mixed-sign basis Node3D warns about, and
        // normals and culling then disagree between MeshInstance3D and
        // MultiMeshInstance3D (godotengine/godot#108739). A 180° yaw is free.
        Assert.True(inverted.Count == 0, $"{inverted.Count} inside-out transform(s): {string.Join("; ", inverted.Take(3))}");
    }

    [Fact]
    public void Passability_is_declared_once_at_the_folder() // REALM-C: no_collide
    {
        if (Scene() is not { } scene)
            return;

        // The opt-out exists for where physics and fiction disagree, not for
        // silencing the validator one prop at a time. Declaring it at the folder
        // is what keeps it auditable: one line to read, one decision to argue
        // with. A realm sprinkling no_collide over individual meshes is a realm
        // whose collision is no longer knowable by looking.
        // Every declaration is on a FOLDER, never on a mesh. This used to assert
        // exactly one folder, which was really a proxy for "not sprinkled" — and
        // it broke the moment a second legitimate one appeared (the night sky's
        // grass surface). Assert the property that actually matters instead of a
        // count that happened to be 1.
        var declarations = Regex.Matches(scene, @"\[node name=""[^""]+"" type=""([^""]+)""[^\]]*groups=\[""no_collide""\]");
        Assert.NotEmpty(declarations);
        Assert.All(declarations, m => Assert.Equal("Node3D", m.Groups[1].Value));

        // And a handful at most: one per layer that is genuinely scenery. Many
        // would mean it had become a way to silence the validator piecemeal.
        Assert.InRange(Regex.Matches(scene, @"groups=\[""no_collide""\]").Count, 1, 4);
    }

    [Fact]
    public void Each_dressed_chamber_has_exactly_one_hero() // LOOK-018
    {
        if (Scene() is not { } scene)
            return;

        var heroes = Regex.Matches(scene, @"\[node name=""Hero\d+""").Count;
        // Seven dressed chambers. The Wheel's hero is its cist, which is
        // architecture; the corridors are starved on purpose.
        Assert.Equal(7, heroes);
        Assert.All(Regex.Matches(scene, @"\[node name=""Hero\d+"" parent=""([^""]+)""")
                        .Select(m => m.Groups[1].Value),
                   parent => Assert.Equal("Monuments", parent));
    }

    // ---------------------------------------------------------- the population

    [Fact]
    public void The_realm_ships_the_cast_the_beat_chart_states() // ENC-001
    {
        if (Baked() is not { } realm)
            return;

        Assert.Equal(100, realm.EnemySpawns.Count);
        Assert.NotNull(realm.BossSpawn);

        // Minion 53 / Rogue 27 / Mage 20 — halved from 200 because one raider
        // could not cross the Ossuary alive, which made every beat past it
        // unauthored in practice (PLAY-002). The accents are what the valleys in
        // between are for, so the mix is as load-bearing as the count.
        var mix = realm.EnemySpawns.GroupBy(s => s.Type).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(53, mix[EnemyType.Minion]);
        Assert.Equal(27, mix[EnemyType.Rogue]);
        Assert.Equal(20, mix[EnemyType.Mage]);
    }

    [Fact]
    public void The_run_has_an_authored_way_out() // REALM-C-002a
    {
        if (Scene() is not { } scene)
            return;
        Assert.Single(Regex.Matches(scene, @"\[node name=""PortalSpawn"""));
        Assert.Single(Regex.Matches(scene, @"\[node name=""PlayerSpawn"""));
        Assert.Single(Regex.Matches(scene, @"\[node name=""BossSpawn"""));
    }

    [Fact]
    public void Solid_structure_offers_itself_as_an_occluder() // REALM-C-022a
    {
        if (Scene() is not { } scene)
            return;
        Assert.True(Regex.Matches(scene, @"type=""OccluderInstance3D""").Count >= 40);
    }

    // ------------------------------------------------------------------ files

    private static string? ScenePath() => Find(Path.Combine("maps", "Crypt.tscn"));

    /// <summary>The vendored PBR directory, or null in a checkout that has not
    /// fetched it — 63 MB of binaries is not something every clone must carry.</summary>
    private static string? Pbr()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "WoadRaiders.Client", "assets", "crypt", "pbr");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string? Scene() => ScenePath() is { } p ? File.ReadAllText(p) : null;

    private static RealmDefinition? Baked() =>
        Find(Path.Combine("maps", "Crypt.json")) is { } p ? RealmDefinitionFile.Load(p) : null;

    /// <summary>
    /// The realm as SHIPPED, walked up from the test binary. Returns null rather
    /// than throwing when it is not there: these tests check a built artefact, and
    /// a checkout that has not generated one yet is not a failing checkout.
    /// </summary>
    private static string? Find(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "WoadRaiders.Client", relative);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
