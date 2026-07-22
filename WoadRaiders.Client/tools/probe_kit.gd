# Dev probe: for every model under the directories passed after `--`, print its
# authored AABB and its TRIANGLE COUNT — the two facts a realm design needs
# before it may place a kit piece (how big is it really, and what does making it
# collision cost the join wire).
#
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/probe_kit.gd -- res://addons/kaykit_dungeon_remastered/Assets/gltf
extends SceneTree

func _init() -> void:
	var dirs := OS.get_cmdline_user_args()
	print("file\tmin_x\tmin_y\tmin_z\tsize_x\tsize_y\tsize_z\ttris\tsurfaces")
	for dir_path in dirs:
		var dir := DirAccess.open(dir_path)
		if dir == null:
			print("DIR FAILED: %s" % dir_path)
			continue
		var files := dir.get_files()
		files.sort()
		for file in files:
			if not (file.ends_with(".glb") or file.ends_with(".gltf")):
				continue
			var packed: PackedScene = load(dir_path + "/" + file)
			if packed == null:
				print("LOAD FAILED: %s/%s" % [dir_path, file])
				continue
			var node := packed.instantiate()
			var stats := {"min": Vector3.INF, "max": -Vector3.INF, "tris": 0, "surfaces": 0}
			_walk(node, Transform3D.IDENTITY, stats)
			if stats["tris"] == 0:
				print("%s\tNO MESHES" % file)
			else:
				var mn: Vector3 = stats["min"]
				var size: Vector3 = stats["max"] - mn
				print("%s\t%.3f\t%.3f\t%.3f\t%.3f\t%.3f\t%.3f\t%d\t%d" % [
					file, mn.x, mn.y, mn.z, size.x, size.y, size.z,
					stats["tris"], stats["surfaces"]])
			node.free()
	quit(0)

func _walk(node: Node, xf: Transform3D, stats: Dictionary) -> void:
	var local := xf
	if node is Node3D:
		local = xf * (node as Node3D).transform
	if node is MeshInstance3D and (node as MeshInstance3D).mesh != null:
		var mesh: Mesh = (node as MeshInstance3D).mesh
		var aabb: AABB = local * mesh.get_aabb()
		stats["min"] = (stats["min"] as Vector3).min(aabb.position)
		stats["max"] = (stats["max"] as Vector3).max(aabb.end)
		stats["tris"] = int(stats["tris"]) + mesh.get_faces().size() / 3
		stats["surfaces"] = int(stats["surfaces"]) + mesh.get_surface_count()
	for child in node.get_children():
		_walk(child, local, stats)
