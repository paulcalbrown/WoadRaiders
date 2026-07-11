# Prints AnimationPlayer clip names + combined mesh AABB for each glb passed after `--`.
# godot-mono --headless --path WoadRaiders.Client -s res://tools/inspect_character.gd -- res://path/Model.glb ...
extends SceneTree

func _init() -> void:
	for path in OS.get_cmdline_user_args():
		var packed := load(path) as PackedScene
		if packed == null:
			print("%s -> LOAD FAILED" % path); continue
		var root := packed.instantiate()
		print("=== %s ===" % path.get_file())
		var ap := _find_anim_player(root)
		if ap:
			var names := ap.get_animation_list()
			print("  %d animations. Movement/attack candidates:" % names.size())
			for n in names:
				var l := String(n).to_lower()
				if l.find("idle") >= 0 or l.find("walk") >= 0 or l.find("run") >= 0 or l.find("attack") >= 0 or l.find("melee") >= 0 or l.find("death") >= 0 or l.find("hit") >= 0:
					print("    - %s (%.2fs)" % [n, ap.get_animation(n).length])
		else:
			print("  no AnimationPlayer")
		var aabb := _aabb(root, Transform3D.IDENTITY)
		print("  size=%v (height %.3f)" % [aabb.size, aabb.size.y])
		root.free()
	quit(0)

func _find_anim_player(node: Node) -> AnimationPlayer:
	if node is AnimationPlayer: return node
	for c in node.get_children():
		var r := _find_anim_player(c)
		if r: return r
	return null

func _aabb(node: Node, xform: Transform3D) -> AABB:
	var out := AABB()
	var has := false
	var t := xform
	if node is Node3D: t = xform * (node as Node3D).transform
	if node is MeshInstance3D and (node as MeshInstance3D).mesh:
		var a: AABB = t * (node as MeshInstance3D).mesh.get_aabb()
		out = a; has = true
	for c in node.get_children():
		var ca := _aabb(c, t)
		if ca.size != Vector3.ZERO:
			out = out.merge(ca) if has else ca
			has = true
	return out
