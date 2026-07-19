# Dev probe: print the combined AABB of every model under assets/crypt/, so a
# design placing them knows their authored size and origin. Run headless:
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/probe_assets.gd
extends SceneTree

func _init() -> void:
	var dirs := ["res://assets/crypt/kaykit_dungeon", "res://assets/crypt/kaykit_halloween",
				 "res://assets/crypt/kenney_graveyard", "res://assets/crypt/polypizza"]
	for dir_path in dirs:
		var dir := DirAccess.open(dir_path)
		if dir == null:
			continue
		for file in dir.get_files():
			if not (file.ends_with(".glb") or file.ends_with(".gltf")):
				continue
			var packed: PackedScene = load(dir_path + "/" + file)
			if packed == null:
				print("LOAD FAILED: %s/%s" % [dir_path, file])
				continue
			var node := packed.instantiate()
			var aabb := _combined_aabb(node, Transform3D.IDENTITY)
			print("%s/%s  min=(%.2f, %.2f, %.2f) size=(%.2f, %.2f, %.2f)" % [
				dir_path.get_file(), file,
				aabb.position.x, aabb.position.y, aabb.position.z,
				aabb.size.x, aabb.size.y, aabb.size.z])
			node.free()
	quit(0)

func _combined_aabb(node: Node, xf: Transform3D) -> AABB:
	var result := AABB()
	var has := false
	var local := xf
	if node is Node3D:
		local = xf * (node as Node3D).transform
	if node is MeshInstance3D and (node as MeshInstance3D).mesh != null:
		var mesh_aabb: AABB = (node as MeshInstance3D).mesh.get_aabb()
		result = local * mesh_aabb
		has = true
	for child in node.get_children():
		var child_aabb := _combined_aabb(child, local)
		if child_aabb.size != Vector3.ZERO or child_aabb.position != Vector3.ZERO:
			result = result.merge(child_aabb) if has else child_aabb
			has = true
	return result
