# Dev probe: what a large sculpted mesh LIBRARY costs, split into the two things
# that could bind it — total bytes, and the number of separate files.
#
# The library is one .res per piece (RealmScene.SharedMesh), so a realm with a
# big unique-triangle budget has both a lot of vertex data AND a lot of files.
# Those scale differently and only one of them is likely to hurt, so measure
# them apart: hold pieces constant and vary their size, then hold size constant
# and vary the count.
#
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/probe_library_cost.gd
extends SceneTree

const Dir := "res://.probe_lib"

func _init() -> void:
	print("pieces\ttris/piece\tunique_tris\tres_bytes\tB/tri\tsave_ms\tload_ms")
	# Vary piece SIZE at a fixed count.
	for tris in [200, 800, 3200]:
		_measure(64, tris)
	# Vary piece COUNT at the spec's ~200 triangles a piece.
	for pieces in [256, 1024, 4096, 16384, 24576]:
		_measure(pieces, 200)
	DirAccess.remove_absolute(ProjectSettings.globalize_path(Dir))
	quit(0)

func _measure(pieces: int, tris_per_piece: int) -> void:
	if not DirAccess.dir_exists_absolute(Dir):
		DirAccess.make_dir_recursive_absolute(Dir)

	var mesh := _sculpt(tris_per_piece)
	var start := Time.get_ticks_msec()
	for i in range(pieces):
		# Each piece is its own file, as SharedMesh writes them. Same geometry
		# every time — this measures the pipeline, not the sculpting.
		ResourceSaver.save(mesh, "%s/p%d.res" % [Dir, i])
	var save_ms := Time.get_ticks_msec() - start

	var bytes := 0
	var dir := DirAccess.open(Dir)
	for file in dir.get_files():
		bytes += FileAccess.get_file_as_bytes("%s/%s" % [Dir, file]).size()

	# Cold-ish load: Godot caches by path, so drop the cache between runs by
	# loading with CACHE_MODE_IGNORE — otherwise this measures a dictionary.
	start = Time.get_ticks_msec()
	for i in range(pieces):
		ResourceLoader.load("%s/p%d.res" % [Dir, i], "", ResourceLoader.CACHE_MODE_IGNORE)
	var load_ms := Time.get_ticks_msec() - start

	var unique := pieces * tris_per_piece
	print("%d\t%d\t%d\t%d\t%.1f\t%d\t%d" % [
		pieces, tris_per_piece, unique, bytes, float(bytes) / unique, save_ms, load_ms])

	for file in DirAccess.open(Dir).get_files():
		DirAccess.remove_absolute(ProjectSettings.globalize_path("%s/%s" % [Dir, file]))

func _sculpt(triangles: int) -> ArrayMesh:
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)
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
					st.set_color(Color(0.5, 0.5, 0.5))
					st.add_vertex(corners[c] * 20.0)
	st.generate_normals()
	st.index()
	return st.commit()
