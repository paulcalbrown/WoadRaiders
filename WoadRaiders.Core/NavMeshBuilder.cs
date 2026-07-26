using System.Numerics;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace WoadRaiders.Core;

/// <summary>
/// The offline navmesh bake: a <see cref="TriangleSoup"/> in, a Detour navmesh
/// out, with the movement rules the sim enforces per tick baked into the
/// polygons instead — climbs beyond <see cref="SimConstants.StepHeight"/>
/// become disconnected edges, slopes beyond what a player tick-step can climb
/// become holes, and the mesh is eroded by the agent radius so runtime checks
/// are center-point only.
///
/// EVERY triangle goes in, and nothing tells this bake what any of them was
/// meant to be. Walkability falls out of the geometry: slope against
/// <see cref="MaxWalkableSlopeDegrees"/>, headroom against CharacterHeight,
/// and erosion by the agent's radius. That erosion is also why a realm can be
/// baked from its whole scene without anyone sorting the architecture from
/// the ornament — detail smaller than the mover simply cannot survive it
/// (8,000 candle-scale props measured: navmesh 100% intact), while anything
/// big enough to walk into blocks, because it should.
///
/// Drops are the one sim rule polygons cannot carry: falling off a ledge (or
/// riding a steep face downhill) leaves the mesh, and on a navmesh those edges
/// simply end. So after the first bake a scout literally walks off every
/// boundary edge with <see cref="ScoutMover"/>'s rules — the sim's physical
/// hatches, frozen for the bake; wherever it lands back on the mesh below, the
/// bake adds a one-way off-mesh drop link and rebuilds. The RUNTIME is
/// navmesh-only (<see cref="RealmGeometry.Move"/> never leaves the mesh), so
/// these links are the only crossings a mover ever performs: the planner
/// routes through them and the sim executes them as LinkTraversal arcs.
///
/// Bake once (tools), serialize, and ship the same bytes to server and client
/// — never bake independently per peer, so every peer clamps to identical
/// polygons.
/// </summary>
public static class NavMeshBuilder
{
    /// <summary>Voxel size on XZ, ~CharacterRadius/3 per Recast guidance.</summary>
    public const float DefaultCellSize = 5f;

    /// <summary>Voxel size on Y; divides StepHeight and CharacterHeight exactly, so neither quantizes.</summary>
    public const float CellHeight = 2f;

    /// <summary>
    /// The steepest walkable slope: the grade a player beats by climbing
    /// StepHeight per tick-step (~2.45, ≈67.8°). One number for everyone —
    /// the per-tick rules let slower movers inch slightly steeper grades
    /// (see RealmValidator.InchingClimbSlope); the mesh deliberately unifies
    /// that to the player's practical limit.
    /// </summary>
    public static readonly float MaxWalkableSlopeDegrees =
        MathF.Atan(SimConstants.StepHeight / (SimConstants.PlayerMoveSpeed * SimConstants.TickDelta)) * (180f / MathF.PI);

    public const int VertsPerPoly = 6;

    private const int WalkableArea = 63; // Recast's RC_WALKABLE_AREA

    /// <summary>How far apart drop-link scouts are seeded along a boundary edge.</summary>
    private const float DropLinkSpacing = 50f;

    /// <summary>How many tick-steps a scout rides a face before giving up on a landing.</summary>
    private const int DropScoutMaxTicks = 80;

    /// <summary>Bake the soup into serializable navmesh tile data.</summary>
    public static DtMeshData BuildMeshData(TriangleSoup soup,
                                           float agentRadius = SimConstants.CharacterRadius,
                                           float cellSize = DefaultCellSize)
    {
        var (mesh, detail) = BakePolyMesh(soup, agentRadius, cellSize);
        var bare = CreateData(mesh, detail, agentRadius, cellSize, links: null);
        var links = FindDropLinks(bare, soup, agentRadius);
        return links.Count == 0 ? bare : CreateData(mesh, detail, agentRadius, cellSize, links);
    }

    /// <summary>Bake straight to a queryable navmesh (tools and tests; the wire ships bytes).</summary>
    public static DtNavMesh Build(TriangleSoup soup,
                                  float agentRadius = SimConstants.CharacterRadius,
                                  float cellSize = DefaultCellSize) =>
        ToNavMesh(BuildMeshData(soup, agentRadius, cellSize));

    public static byte[] Serialize(DtMeshData data)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        new DtMeshDataWriter().Write(writer, data, RcByteOrder.LITTLE_ENDIAN, cCompatibility: false);
        writer.Flush();
        return stream.ToArray();
    }

    public static DtNavMesh Deserialize(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        return ToNavMesh(new DtMeshDataReader().Read(reader, VertsPerPoly));
    }

    private static (RcPolyMesh mesh, RcPolyMeshDetail detail) BakePolyMesh(TriangleSoup soup, float agentRadius, float cellSize)
    {
        var geom = new RcSampleInputGeomProvider(soup.Vertices, soup.Triangles);
        var cfg = new RcConfig(RcPartition.WATERSHED, cellSize, CellHeight,
            MaxWalkableSlopeDegrees, SimConstants.CharacterHeight, agentRadius, SimConstants.StepHeight,
            regionMinSize: 8, regionMergeSize: 20,
            edgeMaxLen: 40 * cellSize, edgeMaxError: 1.3f,
            VertsPerPoly,
            detailSampleDist: 6f, detailSampleMaxError: 1f,
            filterLowHangingObstacles: true, filterLedgeSpans: true, filterWalkableLowHeightSpans: true,
            new RcAreaModification(WalkableArea), buildMeshDetail: true);

        // Pad the vertical bounds so a perfectly flat realm still voxelizes.
        var bmin = geom.GetMeshBoundsMin();
        var bmax = geom.GetMeshBoundsMax();
        bmin.Y -= CellHeight;
        bmax.Y += CellHeight;
        var result = new RcBuilder().Build(geom, new RcBuilderConfig(cfg, bmin, bmax), keepInterResults: false);
        var mesh = result.Mesh;
        if (mesh.npolys == 0)
            throw new InvalidOperationException("the bake produced no walkable polygons — is anything flat enough to stand on?");

        // One "walkable" flag on every polygon; the default query filter skips flag-0 polys.
        for (var i = 0; i < mesh.npolys; i++)
            mesh.flags[i] = 1;
        return (mesh, result.MeshDetail);
    }

    private static DtMeshData CreateData(RcPolyMesh mesh, RcPolyMeshDetail detail,
                                         float agentRadius, float cellSize,
                                         List<(RcVec3f start, RcVec3f end, bool bidir)>? links)
    {
        var option = new DtNavMeshCreateParams
        {
            verts = mesh.verts, vertCount = mesh.nverts,
            polys = mesh.polys, polyFlags = mesh.flags, polyAreas = mesh.areas,
            polyCount = mesh.npolys, nvp = mesh.nvp,
            detailMeshes = detail.meshes, detailVerts = detail.verts, detailVertsCount = detail.nverts,
            detailTris = detail.tris, detailTriCount = detail.ntris,
            walkableHeight = SimConstants.CharacterHeight, walkableRadius = agentRadius,
            walkableClimb = SimConstants.StepHeight,
            bmin = mesh.bmin, bmax = mesh.bmax, cs = cellSize, ch = CellHeight,
            buildBvTree = true,
        };
        if (links is { Count: > 0 })
        {
            option.offMeshConCount = links.Count;
            option.offMeshConVerts = new float[links.Count * 6];
            option.offMeshConRad = new float[links.Count];
            option.offMeshConDir = new int[links.Count];
            option.offMeshConAreas = new int[links.Count];
            option.offMeshConFlags = new int[links.Count];
            option.offMeshConUserID = new int[links.Count];
            for (var i = 0; i < links.Count; i++)
            {
                var (s, e, bidir) = links[i];
                option.offMeshConDir[i] = bidir ? 1 : 0; // drops fall one way; boardings walk both
                option.offMeshConVerts[i * 6 + 0] = s.X;
                option.offMeshConVerts[i * 6 + 1] = s.Y;
                option.offMeshConVerts[i * 6 + 2] = s.Z;
                option.offMeshConVerts[i * 6 + 3] = e.X;
                option.offMeshConVerts[i * 6 + 4] = e.Y;
                option.offMeshConVerts[i * 6 + 5] = e.Z;
                option.offMeshConRad[i] = agentRadius;
                option.offMeshConAreas[i] = WalkableArea;
                option.offMeshConFlags[i] = 1;
                option.offMeshConUserID[i] = i;
            }
        }
        return DtNavMeshBuilder.CreateNavMeshData(option)
               ?? throw new InvalidOperationException("Detour rejected the baked polygons");
    }

    /// <summary>
    /// Walk a scout off every boundary edge of the bare mesh, using the sim's
    /// PHYSICAL rules (<see cref="ScoutMover"/> — the old escape hatches,
    /// frozen for the bake). A scout that comes to rest back on the mesh below
    /// its lip earns a one-way drop link; one that boards a surface above (a
    /// bridge deck its footprint reaches) earns a two-way link. The runtime is
    /// navmesh-only, so these links are the ONLY crossings a mover will ever
    /// perform — a fall the scout cannot finish is a wall, by design.
    /// </summary>
    private static List<(RcVec3f start, RcVec3f end, bool bidir)> FindDropLinks(DtMeshData bare, TriangleSoup soup, float agentRadius)
    {
        var links = new List<(RcVec3f start, RcVec3f end, bool bidir)>();
        var navMesh = ToNavMesh(bare);
        var scout = new ScoutMover(navMesh, soup);
        var query = new DtNavMeshQuery(navMesh);
        var filter = new DtQueryDefaultFilter();
        // The landing search must reach across the eroded band at a wall's
        // base (~agentRadius of meshless floor), or every sheer drop lands
        // just outside the mesh and earns nothing. Y stays tight: a landing
        // is only a landing at its own level.
        var landedExtents = new RcVec3f(agentRadius + 12f, 12f, agentRadius + 12f);
        var tickStep = SimConstants.PlayerMoveSpeed * SimConstants.TickDelta;

        for (var pi = 0; pi < bare.header.polyCount; pi++)
        {
            var poly = bare.polys[pi];
            if (poly.GetPolyType() != 0)
                continue;
            float cx = 0, cz = 0;
            for (var k = 0; k < poly.vertCount; k++)
            {
                cx += bare.verts[poly.verts[k] * 3];
                cz += bare.verts[poly.verts[k] * 3 + 2];
            }
            cx /= poly.vertCount;
            cz /= poly.vertCount;

            for (var j = 0; j < poly.vertCount; j++)
            {
                if (poly.neis[j] != 0)
                    continue; // shared edge — not a rim
                var a = poly.verts[j] * 3;
                var b = poly.verts[(j + 1) % poly.vertCount] * 3;
                var ax = bare.verts[a]; var ay = bare.verts[a + 1]; var az = bare.verts[a + 2];
                var ex = bare.verts[b] - ax;
                var ey = bare.verts[b + 1] - ay;
                var ez = bare.verts[b + 2] - az;
                var len = MathF.Sqrt(ex * ex + ez * ez);
                if (len < 1f)
                    continue;
                // XZ normal pointing away from the polygon: over the edge.
                var nx = ez / len;
                var nz = -ex / len;
                if (nx * (cx - (ax + ex * 0.5f)) + nz * (cz - (az + ez * 0.5f)) > 0)
                {
                    nx = -nx;
                    nz = -nz;
                }

                // Ceil, not truncate: seeding must guarantee neighbouring lips
                // sit within DropLinkSpacing, or the midpoint between them
                // falls outside LinkBoardRadius and that stretch of rim reads
                // as a phantom wall to a mesh-only mover.
                var scouts = Math.Max(1, (int)MathF.Ceiling(len / DropLinkSpacing));
                for (var s = 0; s < scouts; s++)
                {
                    var t = (s + 0.5f) / scouts;
                    var lip = new Vector3(ax + ex * t, ay + ey * t, az + ez * t);

                    // Ride the scout over the edge until it rests on mesh
                    // again. A crossing is an UNBROKEN CHAIN OF HATCH STEPS
                    // between two mesh rests: a first step that is plain mesh
                    // walking means this edge connects by walking — no link —
                    // and a plain step AFTER hatching is the far rest, where
                    // the crossing ends. Without that discipline a scout that
                    // hops one tread and then strolls up a staircase records
                    // the stroll as a 70-unit "boarding", and movers get
                    // hoisted through the flight from beside it. A long steep
                    // face is still ridden tick by tick to its foot (every
                    // tick a floor-ride hatch); the link's end is the point ON
                    // the mesh, not the raw rest — a landing inside the eroded
                    // band would strand a mesh-only mover the moment it set
                    // down.
                    var pos = lip;
                    var step = new Vector3(nx * tickStep, 0, nz * tickStep);
                    var crossing = false;
                    for (var tick = 0; tick < DropScoutMaxTicks; tick++)
                    {
                        var next = scout.Move(pos, step, out var hatchedStep);
                        if ((next - pos).LengthSquared() < 0.01f)
                            break; // stuck — a wall or the border seal, not a crossing
                        if (!hatchedStep && !crossing)
                            break; // walked, not crossed — the mesh already joins here
                        var resting = crossing && !hatchedStep; // back to plain walking: land at the PREVIOUS rest
                        crossing = true;
                        if (!resting)
                            pos = next;
                        var fell = pos.Y < lip.Y - SimConstants.StepHeight;
                        var boarded = pos.Y > lip.Y + 9.9f;
                        if (!fell && !boarded)
                        {
                            if (resting)
                                break; // a hatch that went nowhere — band wobble, not a crossing
                            continue;  // mid-hatch near lip level — keep riding
                        }
                        var status = query.FindNearestPoly(new RcVec3f(pos.X, pos.Y, pos.Z), landedExtents,
                                                           filter, out var landedRef, out var onMesh, out _);
                        if (status.Succeeded() && landedRef != 0 && NudgeIsClear(soup, pos, onMesh))
                        {
                            var restY = soup.SurfaceNear(onMesh.X, onMesh.Z, onMesh.Y, 2f * CellHeight + 0.5f) ?? onMesh.Y;
                            var start = new RcVec3f(lip.X - nx, lip.Y, lip.Z - nz);
                            var end = new RcVec3f(onMesh.X, restY, onMesh.Z);
                            // Dedup on the WHOLE crossing — lip AND landing. Keyed
                            // on the lip alone, a plunge to the floor far below
                            // shadows the boarding hop onto the deck at the same
                            // rim, and a mover at a broken join is offered only
                            // the fall (the Fault's east flight shipped exactly
                            // that: climbers plunged through the stair top).
                            if (!links.Any(l => NearSame(l.start, start) && NearSame(l.end, end)))
                                links.Add((start, end, boarded));
                            break;
                        }
                        if (resting || boarded)
                            break; // a rest or a boarding lands where it stands, or nowhere
                    }
                }
            }
        }
        return links;
    }

    /// <summary>Two crossing endpoints count as the same when they sit within
    /// half the seed spacing on the ground plane and a step of each other.</summary>
    private static bool NearSame(RcVec3f a, RcVec3f b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return dx * dx + dz * dz < DropLinkSpacing * DropLinkSpacing * 0.25f &&
               MathF.Abs(a.Y - b.Y) < SimConstants.StepHeight;
    }

    /// <summary>The nudge from where the scout rests to the mesh must not pass
    /// through a standing solid — nearest-poly can otherwise reach across a
    /// thin wall and land the crossing in a different room.</summary>
    private static bool NudgeIsClear(TriangleSoup soup, Vector3 rest, RcVec3f onMesh)
    {
        var y = MathF.Max(rest.Y, onMesh.Y) + SimConstants.StepHeight + 0.5f;
        return !soup.SegmentHits(new Vector3(rest.X, y, rest.Z),
                                 new Vector3(onMesh.X, y, onMesh.Z), blockersOnly: true);
    }

    /// <summary>
    /// Attach baked tile data as a queryable navmesh — so one bake can be both
    /// serialized for the wire and simulated against, without baking twice.
    /// </summary>
    public static DtNavMesh ToNavMesh(DtMeshData data)
    {
        var navMesh = new DtNavMesh();
        var status = navMesh.Init(data, VertsPerPoly, 0);
        if (!status.Succeeded())
            throw new InvalidOperationException($"navmesh init failed: {status.Value:x}");
        return navMesh;
    }
}
