extends SceneTree
func _init() -> void:
	for f in ["res://addons/kaykit_dungeon_remastered/Assets/gltf/torch.gltf.glb",
			  "res://addons/kaykit_dungeon_remastered/Assets/gltf/torch_lit.gltf.glb"]:
		var packed = load(f)
		if packed == null:
			print(f, "  MISSING"); continue
		var n = packed.instantiate()
		var box = AABB()
		var first = true
		for m in n.find_children("*", "MeshInstance3D", true, false):
			var a = m.mesh.get_aabb()
			a.position += m.position
			if first: box = a; first = false
			else: box = box.merge(a)
		print("%s  min=(%.2f,%.2f,%.2f) size=(%.2f,%.2f,%.2f)  top@x20=%.0f" %
			[f.get_file(), box.position.x, box.position.y, box.position.z,
			 box.size.x, box.size.y, box.size.z, (box.position.y + box.size.y) * 20.0])
	quit(0)
