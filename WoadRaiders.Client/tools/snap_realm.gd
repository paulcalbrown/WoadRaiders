# Dev probe: render a realm's authored scene from a set of vantage points and
# save screenshots — how a headless-workflow session eyeballs a realm without
# playing it. Needs a GPU (do NOT pass --headless); a window appears briefly.
#   godot-mono --path WoadRaiders.Client -s res://tools/snap_realm.gd -- res://maps/Crypt.tscn <out_dir>
extends SceneTree

func _init() -> void:
	_run()

func _run() -> void:
	await process_frame
	var args := OS.get_cmdline_user_args()
	var scene_path: String = args[0] if args.size() > 0 else "res://maps/Crypt.tscn"
	var out_dir: String = args[1] if args.size() > 1 else "user://"

	var packed: PackedScene = load(scene_path)
	if packed == null:
		push_error("cannot load " + scene_path)
		quit(1)
		return
	root.add_child(packed.instantiate())

	var cam := Camera3D.new()
	cam.far = 9000.0
	cam.fov = 55.0
	root.add_child(cam)
	cam.current = true

	# Vantage points over the Crypt's set pieces, riding where the chase
	# camera would (a few hundred units back and ~40 degrees up): [position, look_at].
	var shots: Array = [
		[Vector3(300, 260, 1800), Vector3(650, 0, 1800)],      # the undercroft + founder's tomb
		[Vector3(880, 210, 1800), Vector3(1350, -72, 1800)],   # down the descent stair into the hub
		[Vector3(1450, 130, 1160), Vector3(1450, -122, 680)],  # the ossuary from its door
		[Vector3(1850, 160, 1800), Vector3(2400, -130, 1800)], # the processional + broken span
		[Vector3(3360, 60, 1400), Vector3(3360, -172, 1000)],  # the candle chapel
		[Vector3(2820, 30, 2960), Vector3(2320, -258, 3260)],  # the mausoleum court
		[Vector3(1450, 90, 2540), Vector3(1450, -135, 2930)],  # the flooded cloister
		[Vector3(2700, 120, 1800), Vector3(3250, -160, 1800)], # the east landing into the maze
	]
	var i := 0
	for shot in shots:
		cam.position = shot[0]
		cam.look_at(shot[1])
		for f in range(24):
			await process_frame
		var img: Image = root.get_viewport().get_texture().get_image()
		var path := "%s/crypt_%02d.png" % [out_dir, i]
		img.save_png(path)
		print("saved ", path)
		i += 1
	quit(0)
