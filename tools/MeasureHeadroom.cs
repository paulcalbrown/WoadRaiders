// Where the chase camera is CONSTRAINED by a ceiling, measured over the realm's
// own walkable floor rather than assumed from the chamber table.
//
// The law comes from CameraRig: at fit 0 (fully open) the boom stands
// OpenBoomLength(430) * sin(OpenPitchDegrees(40)) = 276 above the raider, the
// aim point is AimUpBias(30) up, and the rig keeps CeilingClearance(25) under
// the roof. So a raider needs 276 + 25 = ~302 of clearance before the camera
// stops ducking. A NOMINAL ceiling is not that number: a groin vault's soffit
// hangs well below its crown and a corbelled roof steps in from the walls.
//
//   dotnet run tools/MeasureHeadroom.cs -c Release
#:project C:/Users/Paul/RiderProjects/WoadRaiders/WoadRaiders.Core/WoadRaiders.Core.csproj
#:property PublishAot=false
using System.Numerics;
using WoadRaiders.Core;

const float OpenBoom = 430f, OpenPitchDeg = 40f, AimUpBias = 30f, CeilingClearance = 25f;
var needed = OpenBoom * MathF.Sin(OpenPitchDeg * MathF.PI / 180f) + CeilingClearance;
Console.WriteLine($"camera runs fully open at {needed:0.0} of clearance above the raider\n");

var realm = RealmDefinitionFile.Load("C:/Users/Paul/RiderProjects/WoadRaiders/WoadRaiders.Client/maps/Crypt.json");
var soup = realm.Soup!;
var nav = NavMeshBuilder.BuildMeshData(soup);
var movement = new RealmGeometry(soup, realm.SpawnPoint, (SimConstants.CharacterRadius, NavMeshBuilder.ToNavMesh(nav)));

var min = new Vector3(soup.BoundsMin.X, soup.BoundsMin.Y, soup.BoundsMin.Z);
var max = new Vector3(soup.BoundsMax.X, soup.BoundsMax.Y, soup.BoundsMax.Z);
var pinched = new List<(Vector3 At, float Clearance)>();
int sampled = 0, constrained = 0;

// A 40 grid MISSED EVERY DOORWAY. A threshold is only as deep as the wall is
// thick - 40 to 80 - so a coarse grid steps straight over the one place the
// realm was crushing the camera, and this reported the realm clear while a
// player was being crouched through every door in it. Sample finer than the
// thinnest thing you are looking for.
for (var x = min.X; x <= max.X; x += 12f)
for (var z = min.Z; z <= max.Z; z += 12f)
{
    var groundY = movement.GroundHeight(new Vector3(x, max.Y, z));
    if (float.IsNaN(groundY) || groundY < min.Y - 1f) continue;
    var stand = new Vector3(x, groundY, z);
    // Only judge places a raider can actually be.
    if (Vector3.Distance(movement.Move(stand, Vector3.Zero), stand) > 1f) continue;

    var eye = stand + new Vector3(0, AimUpBias, 0);
    var ceil = movement.CeilingHeight(eye);
    sampled++;
    if (float.IsInfinity(ceil)) continue;      // open sky: camera is free
    var clearance = ceil - stand.Y;
    if (clearance < needed) { constrained++; pinched.Add((stand, clearance)); }
}

// A pinched sample only matters if a raider can GET there. Recast happily
// generates walkable islands on top of walls and vault crowns; nobody stands on
// them, and counting them as constrained hides whether the floor is fixed.
var reachable = new List<(Vector3 At, float Clearance)>();
foreach (var p in pinched)
{
    var way = new List<Vector3>();
    if (movement.TryFindPath(realm.SpawnPoint, p.At, way) && way.Count > 0 &&
        Vector3.Distance(way[^1], p.At) < 40f)
        reachable.Add(p);
}

Console.WriteLine($"walkable samples: {sampled}");
Console.WriteLine($"pinched anywhere: {constrained}  ({100.0 * constrained / Math.Max(1, sampled):0.0}%)");
Console.WriteLine($"pinched AND REACHABLE: {reachable.Count}  " +
                  $"({100.0 * reachable.Count / Math.Max(1, sampled):0.00}%)  <-- the number that matters\n");
foreach (var p in reachable.OrderBy(p => p.Clearance).Take(6))
    Console.WriteLine($"  reachable pinch: ({p.At.X,6:0}, {p.At.Y,6:0}, {p.At.Z,6:0})  clearance {p.Clearance:0}");
Console.WriteLine();
foreach (var g in pinched.GroupBy(p => (int)(p.Clearance / 40) * 40).OrderBy(g => g.Key))
    Console.WriteLine($"  clearance {g.Key,4}-{g.Key + 39,4}: {g.Count(),5} samples");
Console.WriteLine();
foreach (var p in pinched.OrderBy(p => p.Clearance).Take(8))
    Console.WriteLine($"  worst: ({p.At.X,6:0}, {p.At.Y,6:0}, {p.At.Z,6:0})  clearance {p.Clearance:0}");
