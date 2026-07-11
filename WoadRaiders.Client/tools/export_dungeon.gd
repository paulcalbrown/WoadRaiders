# Exports a dungeon scene's collision + spawn markers to the JSON the headless
# server reads (see WoadRaiders.Core/DungeonGeometryFile.cs).
#
# Authoring conventions (all inside any scene built in the Godot editor):
#   - Solids:      CollisionShape3D nodes with a BoxShape3D (rotated boxes are
#                  exported as their world AABB — keep them axis-aligned).
#   - Player spawn: a Marker3D named exactly "PlayerSpawn".
#   - Enemy spawns: Marker3D nodes whose names start with "EnemySpawn".
#
# Run headless (no editor needed):
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/export_dungeon.gd -- <scene.tscn> <out.json>
# Example:
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/export_dungeon.gd -- res://maps/TestArena.tscn res://maps/TestArena.json
extends SceneTree

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
		"spawn": [0.0, 0.0, 0.0],
		"solids": [],
		"enemySpawns": [],
	}
	_walk(root, Transform3D.IDENTITY, out)
	root.free()

	var file := FileAccess.open(args[1], FileAccess.WRITE)
	if file == null:
		push_error("could not open for writing: " + args[1])
		quit(1)
		return
	file.store_string(JSON.stringify(out, "  "))
	file.close()

	print("exported %d solids, %d enemy spawns -> %s" % [out["solids"].size(), out["enemySpawns"].size(), args[1]])
	quit(0)

func _walk(node: Node, parent_xform: Transform3D, out: Dictionary) -> void:
	var xform := parent_xform
	if node is Node3D:
		xform = parent_xform * (node as Node3D).transform

	if node is CollisionShape3D and (node as CollisionShape3D).shape is BoxShape3D:
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
		if node.name == "PlayerSpawn":
			out["spawn"] = [origin.x, origin.y, origin.z]
		elif String(node.name).begins_with("EnemySpawn"):
			out["enemySpawns"].append([origin.x, origin.y, origin.z])

	for child in node.get_children():
		_walk(child, xform, out)
