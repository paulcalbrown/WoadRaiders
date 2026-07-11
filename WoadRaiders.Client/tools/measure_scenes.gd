# Prints the combined mesh AABB of each scene passed after `--`.
# Usage: godot-mono --headless --path WoadRaiders.Client -s res://tools/measure_scenes.gd -- res://path/a.glb res://path/b.glb
extends SceneTree

func _init() -> void:
	for path in OS.get_cmdline_user_args():
		var packed := load(path) as PackedScene
		if packed == null:
			print("%s -> LOAD FAILED" % path)
			continue
		var root := packed.instantiate()
		var mn := Vector3.INF
		var mx := -Vector3.INF
		var found := _walk(root, Transform3D.IDENTITY, mn, mx)
		if found.size() == 2:
			var size: Vector3 = found[1] - found[0]
			print("%s -> min=%v max=%v size=%v" % [path.get_file(), found[0], found[1], size])
		else:
			print("%s -> no meshes" % path.get_file())
		root.free()
	quit(0)

func _walk(node: Node, xform: Transform3D, mn: Vector3, mx: Vector3) -> Array:
	var result := []
	var t := xform
	if node is Node3D:
		t = xform * (node as Node3D).transform
	if node is MeshInstance3D:
		var aabb: AABB = (node as MeshInstance3D).mesh.get_aabb()
		for sx in [0.0, 1.0]:
			for sy in [0.0, 1.0]:
				for sz in [0.0, 1.0]:
					var corner := t * (aabb.position + Vector3(sx * aabb.size.x, sy * aabb.size.y, sz * aabb.size.z))
					mn = mn.min(corner)
					mx = mx.max(corner)
	for child in node.get_children():
		var sub := _walk(child, t, mn, mx)
		if sub.size() == 2:
			mn = mn.min(sub[0])
			mx = mx.max(sub[1])
	if mn.x != INF:
		result = [mn, mx]
	return result
