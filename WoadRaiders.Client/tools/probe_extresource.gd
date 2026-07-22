# Dev probe: does a scene whose mesh library lives in a sibling .res still
# regenerate byte-identically (REALM-C-010)?
#
# The concern is specific. Inlined meshes arrive as [sub_resource] blocks, whose
# random ids tools/GenerateRealm.cs already normalises away. A library saved to
# disk arrives as [ext_resource] instead — a different line, with its own
# generated id and a uid — and nothing normalises those. If they wobble between
# runs, every regeneration of an unchanged realm rewrites the scene.
#
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/probe_extresource.gd
extends SceneTree

func _init() -> void:
	var lib := "res://.probe_lib.res"
	# Case 1: the library is re-saved each run, as a regenerating pipeline would.
	var a := _build(lib, "res://.probe_ext_a.tscn", true)
	var b := _build(lib, "res://.probe_ext_b.tscn", true)
	print("library re-saved each run  → byte-identical: %s (%d vs %d bytes)"
		% ["YES" if a == b else "NO", a.length(), b.length()])

	# Case 2: the library is written once and left alone; only the scene rebuilds.
	var c := _build(lib, "res://.probe_ext_c.tscn", false)
	var d := _build(lib, "res://.probe_ext_d.tscn", false)
	print("library left untouched     → byte-identical: %s (%d vs %d bytes)"
		% ["YES" if c == d else "NO", c.length(), d.length()])

	print("\nthe lines nothing currently normalises:")
	for line in a.split("\n"):
		if line.begins_with("[ext_resource") or line.begins_with("[gd_scene"):
			print("  " + line)

	# And the library itself: a binary resource saved twice from equal input.
	var first := FileAccess.get_file_as_bytes(lib)
	ResourceSaver.save(_sculpt(), lib)
	var second := FileAccess.get_file_as_bytes(lib)
	print("\n.res stable when re-saved from equal input: %s (%d bytes)"
		% ["YES" if first == second else "NO", second.size()])

	for path in [lib, "res://.probe_ext_a.tscn", "res://.probe_ext_b.tscn", "res://.probe_ext_c.tscn", "res://.probe_ext_d.tscn"]:
		DirAccess.remove_absolute(ProjectSettings.globalize_path(path))
	quit(0)

func _build(lib: String, out: String, resave: bool) -> String:
	# Save the library, then reference the DISK copy — that is what makes the
	# scene emit ext_resource rather than inlining the vertices.
	if resave or not FileAccess.file_exists(lib):
		ResourceSaver.save(_sculpt(), lib)
	var mesh: ArrayMesh = load(lib)

	var root := Node3D.new()
	root.name = "Split"
	for i in range(8):
		var mi := MeshInstance3D.new()
		mi.name = "Piece%d" % i
		mi.mesh = mesh
		mi.position = Vector3(i * 80, 0, 0)
		root.add_child(mi)
		mi.owner = root

	var packed := PackedScene.new()
	packed.pack(root)
	ResourceSaver.save(packed, out)
	root.free()
	return FileAccess.get_file_as_string(out)

func _sculpt() -> ArrayMesh:
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)
	for i in range(64):
		var a := float(i) * 0.1
		st.set_color(Color(0.5, 0.5, 0.5))
		st.add_vertex(Vector3(cos(a), 0, sin(a)) * 40.0)
		st.set_color(Color(0.5, 0.5, 0.5))
		st.add_vertex(Vector3(cos(a + 0.1), 0, sin(a + 0.1)) * 40.0)
		st.set_color(Color(0.5, 0.5, 0.5))
		st.add_vertex(Vector3(0, 20, 0))
	st.generate_normals()
	st.index()
	return st.commit()
