# Dev probe: what survives ResourceSaver into a .tscn, and what the realm bake
# would see in it. Answers the questions a sculpted, lit realm design hinges on:
#   - does a shared ArrayMesh serialize ONCE and get referenced N times?
#   - do AnimationPlayer / Decal / FogVolume / GPUParticles3D / MultiMeshInstance3D
#     round-trip through PackedScene.pack + ResourceSaver.save?
#   - which of them does the bake's mesh walk (MeshInstance3D only) actually see?
#
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/probe_serialize.gd
extends SceneTree

func _init() -> void:
	var root := Node3D.new()
	root.name = "Probe"

	# --- a sculpted mesh, shared by three instances -------------------------
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)
	for i in range(300):
		var a := float(i) * 0.021
		st.set_normal(Vector3.UP)
		st.set_uv(Vector2(0, 0)); st.add_vertex(Vector3(cos(a), 0, sin(a)) * 40.0)
		st.set_uv(Vector2(1, 0)); st.add_vertex(Vector3(cos(a + 0.02), 0, sin(a + 0.02)) * 40.0)
		st.set_uv(Vector2(0, 1)); st.add_vertex(Vector3(0, 12, 0))
	var shared: ArrayMesh = st.commit()
	shared.resource_name = "SharedSculpt"
	for i in range(3):
		var mi := MeshInstance3D.new()
		mi.name = "Sculpt%d" % i
		mi.mesh = shared
		mi.position = Vector3(i * 100, 0, 0)
		root.add_child(mi)

	# --- a light driven by an AnimationPlayer (flicker without scripts) -----
	var lamp := OmniLight3D.new()
	lamp.name = "Torch"
	lamp.light_color = Color(1.0, 0.55, 0.2)
	lamp.omni_range = 400.0
	root.add_child(lamp)
	var anim := Animation.new()
	var track := anim.add_track(Animation.TYPE_VALUE)
	anim.track_set_path(track, NodePath("Torch:light_energy"))
	anim.track_set_interpolation_type(track, Animation.INTERPOLATION_LINEAR)
	anim.value_track_set_update_mode(track, Animation.UPDATE_CONTINUOUS)
	for k in range(8):
		anim.track_insert_key(track, k * 0.13, 3.0 + sin(k * 2.3) * 0.7)
	anim.length = 8 * 0.13
	anim.loop_mode = Animation.LOOP_LINEAR
	var lib := AnimationLibrary.new()
	lib.add_animation("flicker", anim)
	var player := AnimationPlayer.new()
	player.name = "Flicker"
	player.add_animation_library("", lib)
	player.autoplay = "flicker"
	root.add_child(player)

	# --- the atmosphere layer the bake must not see ------------------------
	var decal := Decal.new()
	decal.name = "Soot"
	decal.size = Vector3(120, 60, 120)
	root.add_child(decal)

	var fog := FogVolume.new()
	fog.name = "GroundMist"
	fog.size = Vector3(600, 80, 600)
	fog.material = FogMaterial.new()
	root.add_child(fog)

	var embers := GPUParticles3D.new()
	embers.name = "Embers"
	embers.amount = 24
	embers.process_material = ParticleProcessMaterial.new()
	embers.draw_pass_1 = QuadMesh.new()
	root.add_child(embers)

	var mm := MultiMesh.new()
	mm.transform_format = MultiMesh.TRANSFORM_3D
	mm.mesh = BoxMesh.new()
	mm.instance_count = 50
	for i in range(50):
		mm.set_instance_transform(i, Transform3D(Basis(), Vector3(i * 7, 0, 0)))
	var mmi := MultiMeshInstance3D.new()
	mmi.name = "BoneScatter"
	mmi.multimesh = mm
	root.add_child(mmi)

	var occ := OccluderInstance3D.new()
	occ.name = "WallOccluder"
	var box_occ := BoxOccluder3D.new()
	box_occ.size = Vector3(600, 280, 40)
	occ.occluder = box_occ
	root.add_child(occ)

	# --- pack, save, measure ------------------------------------------------
	for child in root.get_children():
		child.owner = root
	var packed := PackedScene.new()
	print("pack: %s" % error_string(packed.pack(root)))
	var out := "res://.probe_serialize.tscn"
	print("save: %s" % error_string(ResourceSaver.save(packed, out)))

	var text := FileAccess.get_file_as_string(out)
	print("tscn bytes: %d for %d unique triangles" % [text.length(), shared.get_faces().size() / 3])
	for needle in ["ArrayMesh", "AnimationPlayer", "Animation\"", "AnimationLibrary", "Decal",
				   "FogVolume", "FogMaterial", "GPUParticles3D", "ParticleProcessMaterial",
				   "MultiMeshInstance3D", "MultiMesh\"", "SubResource(\"ArrayMesh",
				   "OccluderInstance3D", "BoxOccluder3D"]:
		var n := text.count(needle)
		print("  %-26s x%d" % [needle, n])

	# --- what the bake's walk would collect --------------------------------
	var reloaded := (load(out) as PackedScene).instantiate()
	print("bake would sample %d triangles from MeshInstance3D, %d from MultiMeshInstance3D" % [
		_mesh_tris(reloaded), _multimesh_tris(reloaded)])
	reloaded.free()
	root.free()

	DirAccess.remove_absolute(ProjectSettings.globalize_path(out))
	quit(0)

func _mesh_tris(node: Node) -> int:
	var n := 0
	if node is MeshInstance3D and (node as MeshInstance3D).mesh != null:
		n += (node as MeshInstance3D).mesh.get_faces().size() / 3
	for c in node.get_children():
		n += _mesh_tris(c)
	return n

func _multimesh_tris(node: Node) -> int:
	var n := 0
	if node is MultiMeshInstance3D:
		var mm: MultiMesh = (node as MultiMeshInstance3D).multimesh
		if mm != null and mm.mesh != null:
			var live: int = mm.instance_count if mm.visible_instance_count < 0 else mm.visible_instance_count
			n += live * (mm.mesh.get_faces().size() / 3)
	for c in node.get_children():
		n += _multimesh_tris(c)
	return n
