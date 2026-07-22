# Dev probe: what a realm's .tscn actually costs, split into its two independent
# terms — the UNIQUE mesh library (paid once per distinct piece) and the
# PLACEMENTS (paid once per instance, whatever it holds).
#
# The distinction is the whole point. Baked collision triangles scale with
# placements; .tscn bytes do not. A budget that ties the two together is
# guessing.
#
# Surfaces are indexed and carry position + normal + colour but NO UV or
# tangent, because the realm's materials are world-triplanar and ignore UVs
# entirely — the spec's own PIPE-001.
#
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/probe_scene_cost.gd
extends SceneTree

func _init() -> void:
	print("unique_tris\tplacements\ttscn_bytes\tB/unique_tri\tB/placement")
	# Vary one term at a time so the two costs separate cleanly.
	for unique in [200, 2000, 12000]:
		_measure(unique, 200)
	for placements in [500, 2000, 8000]:
		_measure(2000, placements)
	# And the same realm with its mesh library saved BESIDE the scene as a
	# binary resource, referenced rather than inlined — which is what decides
	# whether a regeneration rewrites the geometry or only the layout.
	print("")
	print("--- library split out to a sibling .res ---")
	_measure(12000, 2000, true)
	_measure(2000, 8000, true)
	quit(0)

func _measure(unique_tris: int, placements: int, split: bool = false) -> void:
	var root := Node3D.new()
	root.name = "Costed"

	# One library piece holding the whole unique budget, then N placements of it.
	var mesh := _sculpt(unique_tris)
	var lib := "res://.probe_lib.res"
	if split:
		# Saved to disk first, so the scene references it instead of inlining it.
		ResourceSaver.save(mesh, lib)
		mesh = load(lib)
	for i in range(placements):
		var mi := MeshInstance3D.new()
		mi.name = "P%d" % i
		mi.mesh = mesh
		# A real placement is a full transform, not just a translation.
		mi.transform = Transform3D(Basis(Vector3.UP, float(i) * 0.37), Vector3(i * 80, 0, i * 40))
		root.add_child(mi)

	for child in root.get_children():
		child.owner = root
	var packed := PackedScene.new()
	packed.pack(root)
	var out := "res://.probe_scene_cost.tscn"
	ResourceSaver.save(packed, out)
	var size := FileAccess.get_file_as_string(out).length()

	if split:
		# The scene now holds placements ONLY — the per-unique-triangle column
		# would be meaningless here, since none of that data is in the file.
		var lib_bytes := FileAccess.get_file_as_bytes(lib).size()
		print("%d\t%d\t%d\tplacements only; %d B of mesh moved to the .res" % [
			unique_tris, placements, size, lib_bytes])
		DirAccess.remove_absolute(ProjectSettings.globalize_path(lib))
	else:
		# Take two rows at the same unique count to read the marginal costs off;
		# neither column alone separates the fixed overhead.
		print("%d\t%d\t%d\t%.1f\t%.1f" % [
			unique_tris, placements, size,
			float(size) / unique_tris, float(size) / placements])
	root.free()
	DirAccess.remove_absolute(ProjectSettings.globalize_path(out))

func _sculpt(triangles: int) -> ArrayMesh:
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)
	# A strip of connected quads — welds well, like real coursed masonry.
	var span := int(sqrt(float(triangles) / 2.0)) + 1
	for x in range(span):
		for z in range(span):
			var corners := [
				Vector3(x, sin(float(x) * 0.3) * 4.0, z),
				Vector3(x + 1, sin(float(x + 1) * 0.3) * 4.0, z),
				Vector3(x + 1, sin(float(x + 1) * 0.3) * 4.0, z + 1),
				Vector3(x, sin(float(x) * 0.3) * 4.0, z + 1),
			]
			for tri in [[0, 1, 2], [0, 2, 3]]:
				for c in tri:
					st.set_color(Color(0.5 + 0.4 * sin(float(x)), 0.5, 0.5))
					st.add_vertex(corners[c] * 20.0)
	st.generate_normals()
	st.index()
	return st.commit()
