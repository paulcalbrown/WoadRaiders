# Exports a map scene's collision + spawn markers — and, for open realms, its
# TERRAIN and props — to the JSON the headless server reads (see
# WoadRaiders.Core/DungeonGeometryFile.cs). This is how a hand-made .tscn realm
# becomes a playable map.
#
# Authoring conventions (all inside any scene built in the Godot editor):
#   - Solids:       CollisionShape3D nodes with a BoxShape3D (rotated boxes are
#                   exported as their world AABB — keep them axis-aligned).
#   - Player spawn: a Marker3D named exactly "PlayerSpawn".
#   - Enemy spawns: Marker3D nodes whose names start with "EnemySpawn". The enemy
#                   type comes from the name: contains "Rogue" -> Rogue, "Mage" ->
#                   Mage, anything else -> Minion (e.g. "EnemySpawn7_Mage").
#   - Boss:         a Marker3D named "BossSpawn" (optional; at most one).
#   - Terrain, either of (a realm needs one; flat dungeon maps need neither):
#       * a RealmTerrain node (group "realm_terrain", scripts/world/RealmTerrain.cs)
#         — its stored heightfield is copied verbatim (exact, instant); or
#       * any MeshInstance3D nodes in the group "terrain" — the tool SAMPLES
#         them from above on a regular grid (cell size 40, or set metadata
#         "terrain_cell_size" on the scene root) and bakes the heightfield.
#         Sculpt your ground in Blender or with CSG, group it "terrain", done.
#         Cells no terrain mesh covers become deep pits — seal your borders
#         (tools/ValidateRealm.cs will hold you to it).
#         (Also add big ground meshes to "no_fade" so the occlusion fader
#         leaves the land alone.)
#   - Braziers:     nodes in the group "brazier" (or named "Brazier*") become
#                   cosmetic fire props every client renders.
#
# Run headless (no editor needed; BUILD THE CLIENT FIRST — dotnet build
# WoadRaiders.Client — so C# nodes like RealmTerrain carry their data):
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/export_dungeon.gd -- <scene.tscn> <out.json>
# Example:
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/export_dungeon.gd -- res://maps/Crag.tscn res://maps/Crag.json
# Then validate the result:
#   dotnet run tools/ValidateRealm.cs WoadRaiders.Client/maps/YourRealm.json
extends SceneTree

const DEFAULT_CELL_SIZE := 40.0
const MAX_TERRAIN_SAMPLES := 4_000_000 # mirrors the wire guard in DungeonGeometryPacket
const UNHIT_DROP := 200.0 # uncovered cells fall this far below the lowest hit — a sealed pit, not a floor

func _init() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() < 2:
		push_error("usage: -s res://tools/export_dungeon.gd -- <scene.tscn> <out.json>")
		quit(1)
		return

	var packed := load(args[0]) as PackedScene
	if packed == null:
		push_error("could not load scene: " + args[0])
		quit(1)
		return

	var root := packed.instantiate()
	var out := {
		"scene": args[0], # clients render this scene; collision below stays the sim truth
		"spawn": [0.0, 0.0, 0.0],
		"solids": [],
		"enemySpawns": [],
		"enemySpawnTypes": [], # parallel to enemySpawns; WoadRaiders.Core.EnemyType values
	}
	var found := { "realm_terrain": [], "terrain_meshes": [], "props": [] }
	_walk(root, Transform3D.IDENTITY, out, found)

	if not _emit_terrain(root, out, found):
		root.free()
		quit(1)
		return
	if found["props"].size() > 0:
		out["props"] = found["props"]
	root.free()

	var file := FileAccess.open(args[1], FileAccess.WRITE)
	if file == null:
		push_error("could not open for writing: " + args[1])
		quit(1)
		return
	file.store_string(JSON.stringify(out, "  "))
	file.close()

	var terrain_note := ""
	if out.has("terrain"):
		terrain_note = ", %dx%d terrain" % [out["terrain"]["width"], out["terrain"]["depth"]]
	print("exported %d solids, %d enemy spawns%s%s, %d props -> %s" % [
		out["solids"].size(), out["enemySpawns"].size(),
		" + boss" if out.has("bossSpawn") else "", terrain_note,
		found["props"].size(), args[1]])
	quit(0)

func _walk(node: Node, parent_xform: Transform3D, out: Dictionary, found: Dictionary) -> void:
	var xform := parent_xform
	if node is Node3D:
		xform = parent_xform * (node as Node3D).transform

	if node.is_in_group("realm_terrain"):
		if not xform.is_equal_approx(Transform3D.IDENTITY):
			push_warning("RealmTerrain node '%s' is transformed — its heightfield ignores transforms; keep it at the origin" % node.name)
		found["realm_terrain"].append(node)
	elif node is MeshInstance3D and node.is_in_group("terrain"):
		found["terrain_meshes"].append([node, xform])
	elif node is Node3D and _is_brazier(node):
		var brazier_origin := xform.origin
		found["props"].append({ "type": 0, "position": [brazier_origin.x, brazier_origin.y, brazier_origin.z] })
	elif node is CollisionShape3D and (node as CollisionShape3D).shape is BoxShape3D:
		var half: Vector3 = ((node as CollisionShape3D).shape as BoxShape3D).size * 0.5
		var mn := Vector3.INF
		var mx := -Vector3.INF
		for sx in [-1.0, 1.0]:
			for sy in [-1.0, 1.0]:
				for sz in [-1.0, 1.0]:
					var corner := xform * Vector3(sx * half.x, sy * half.y, sz * half.z)
					mn = mn.min(corner)
					mx = mx.max(corner)
		out["solids"].append({
			"min": [mn.x, mn.y, mn.z],
			"max": [mx.x, mx.y, mx.z],
		})
	elif node is Marker3D:
		var origin := xform.origin
		var name := String(node.name)
		if name == "PlayerSpawn":
			out["spawn"] = [origin.x, origin.y, origin.z]
		elif name.begins_with("BossSpawn"):
			if out.has("bossSpawn"):
				push_warning("multiple BossSpawn markers; '%s' overrides the earlier one" % name)
			out["bossSpawn"] = [origin.x, origin.y, origin.z]
		elif name.begins_with("EnemySpawn"):
			out["enemySpawns"].append([origin.x, origin.y, origin.z])
			var lower := name.to_lower()
			var type := 0 # Minion
			if lower.contains("rogue"):
				type = 1
			elif lower.contains("mage"):
				type = 2
			out["enemySpawnTypes"].append(type)

	for child in node.get_children():
		_walk(child, xform, out, found)

# A brazier prop: in the group, or named like one ("Braziers" — a plural
# holder node — is just a folder, not a prop).
func _is_brazier(node: Node) -> bool:
	if node.is_in_group("brazier"):
		return true
	var name := String(node.name)
	return name.begins_with("Brazier") and not name.begins_with("Braziers")

# ------------------------------------------------------------------ terrain

func _emit_terrain(root: Node, out: Dictionary, found: Dictionary) -> bool:
	var realm_nodes: Array = found["realm_terrain"]
	var meshes: Array = found["terrain_meshes"]

	if realm_nodes.size() > 0:
		if realm_nodes.size() > 1:
			push_warning("%d RealmTerrain nodes; only the first is exported (the sim has one heightfield)" % realm_nodes.size())
		if meshes.size() > 0:
			push_warning("both a RealmTerrain node and 'terrain'-group meshes found; the RealmTerrain data wins")
		var node: Node = realm_nodes[0]
		var heights = node.get("Heights")
		if heights == null:
			push_error("RealmTerrain node '%s' carries no data — build the client first (dotnet build WoadRaiders.Client) so its C# script loads" % node.name)
			return false
		out["terrain"] = {
			"originX": node.get("OriginX"),
			"originZ": node.get("OriginZ"),
			"cellSize": node.get("CellSize"),
			"width": node.get("TerrainWidth"),
			"depth": node.get("TerrainDepth"),
			"heights": Array(heights),
		}
		return true

	if meshes.size() == 0:
		return true # a flat dungeon map — no terrain block, the sim uses the y=0 plane

	var cell: float = float(root.get_meta("terrain_cell_size", DEFAULT_CELL_SIZE))
	if cell <= 0.0:
		push_error("terrain_cell_size metadata must be positive")
		return false

	# Gather every triangle of every terrain mesh in world space.
	var tris: Array = [] # flat: [a0, b0, c0, a1, b1, c1, ...]
	var aabb_min := Vector3.INF
	var aabb_max := -Vector3.INF
	for pair in meshes:
		var mi: MeshInstance3D = pair[0]
		var xf: Transform3D = pair[1]
		if mi.mesh == null:
			continue
		var faces: PackedVector3Array = mi.mesh.get_faces()
		for v in faces:
			var wv: Vector3 = xf * v
			tris.append(wv)
			aabb_min = aabb_min.min(wv)
			aabb_max = aabb_max.max(wv)
	if tris.size() < 3:
		push_error("the 'terrain' group has no triangles to sample")
		return false

	var width := int(ceil((aabb_max.x - aabb_min.x) / cell)) + 1
	var depth := int(ceil((aabb_max.z - aabb_min.z) / cell)) + 1
	if width < 2 or depth < 2 or width * depth > MAX_TERRAIN_SAMPLES:
		push_error("terrain grid %dx%d is out of range — adjust terrain_cell_size" % [width, depth])
		return false

	# Bucket triangles by the grid cells their XZ footprint overlaps, so each
	# sample only tests the handful of triangles above or below it.
	var buckets := {}
	var tri_count := tris.size() / 3
	for t in tri_count:
		var a: Vector3 = tris[t * 3]
		var b: Vector3 = tris[t * 3 + 1]
		var c: Vector3 = tris[t * 3 + 2]
		var lo_i := int(floor((min(a.x, b.x, c.x) - aabb_min.x) / cell))
		var hi_i := int(floor((max(a.x, b.x, c.x) - aabb_min.x) / cell)) + 1
		var lo_j := int(floor((min(a.z, b.z, c.z) - aabb_min.z) / cell))
		var hi_j := int(floor((max(a.z, b.z, c.z) - aabb_min.z) / cell)) + 1
		for j in range(max(lo_j, 0), min(hi_j + 1, depth)):
			for i in range(max(lo_i, 0), min(hi_i + 1, width)):
				var key := Vector2i(i, j)
				if not buckets.has(key):
					buckets[key] = []
				buckets[key].append(t)

	# Sample straight down through every grid point; the HIGHEST hit is the
	# ground (an overhang's underside never wins).
	var heights := PackedFloat32Array()
	heights.resize(width * depth)
	var unhit: Array = []
	var lowest_hit := INF
	var ray_top := aabb_max.y + 10.0
	var ray_bottom := aabb_min.y - 10.0
	for j in depth:
		for i in width:
			var x := aabb_min.x + i * cell
			var z := aabb_min.z + j * cell
			var from := Vector3(x, ray_top, z)
			var to := Vector3(x, ray_bottom, z)
			var best := -INF
			var key := Vector2i(i, j)
			if buckets.has(key):
				for t in buckets[key]:
					var hit = Geometry3D.segment_intersects_triangle(
						from, to, tris[t * 3], tris[t * 3 + 1], tris[t * 3 + 2])
					if hit != null and hit.y > best:
						best = hit.y
			if best == -INF:
				unhit.append(j * width + i)
			else:
				heights[j * width + i] = snappedf(best, 0.001)
				lowest_hit = min(lowest_hit, best)

	# Uncovered cells become a deep pit below everything — falling in means
	# stranding, which ValidateRealm reports, which makes authors seal borders.
	var pit := snappedf(lowest_hit - UNHIT_DROP, 0.001)
	for idx in unhit:
		heights[idx] = pit
	if unhit.size() > 0:
		push_warning("%d of %d terrain cells had no mesh under them (set to a %s-deep pit) — cover your ground or expect ValidateRealm complaints" % [
			unhit.size(), width * depth, str(UNHIT_DROP)])

	out["terrain"] = {
		"originX": aabb_min.x,
		"originZ": aabb_min.z,
		"cellSize": cell,
		"width": width,
		"depth": depth,
		"heights": Array(heights),
	}
	return true
