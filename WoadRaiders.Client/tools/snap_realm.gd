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

	# Vantage points over the Crypt's set pieces, riding where the chase camera
	# would (a few hundred units back and a little above the head): [position,
	# look_at]. These follow the ROUTE, west to east and then back west into the
	# cairn, so reading them in order is walking the realm.
	#
	# They are coordinates in a specific realm and they go stale the moment it is
	# reshaped — the first eight here were still aimed at the v1 Crypt, which was
	# a third the size and a different shape, so every shot framed empty air.
	var shots: Array = [
		[Vector3(170, 150, 2200), Vector3(700, 20, 2200)],      # 0 the Broken Porch, under its shaft
		[Vector3(900, 190, 2180), Vector3(2100, 20, 2180)],     # 1 down the Minster nave's arcade
		[Vector3(2900, 170, 2160), Vector3(2000, 20, 2160)],    # 2 the nave, looking back at the shrine
		[Vector3(3680, 60, 2200), Vector3(4700, -120, 2200)],   # 3 the Ossuary from its west door
		[Vector3(4400, 40, 2500), Vector3(4400, -140, 2820)],   # 4 the Ossuary's arcosolium wall
		[Vector3(5600, -40, 2080), Vector3(6400, -520, 1500)],  # 5 the shelf, out over the Fault
		[Vector3(5980, -300, 1280), Vector3(6950, -400, 1280)], # 6 along the span
		[Vector3(6400, -560, 1900), Vector3(6400, -860, 2500)], # 7 the pit floor, from the east stair
		[Vector3(7120, -430, 3180), Vector3(5400, -560, 3180)], # 8 the Deep Gallery, looking west
		[Vector3(4850, -510, 3630), Vector3(4450, -620, 3630)], # 9 the Cubiculum
		[Vector3(4660, -560, 3280), Vector3(4200, -700, 3280)], # 10 the Forecourt
		[Vector3(4150, -620, 3290), Vector3(2900, -840, 3300)], # 11 through the trilithon to the Wheel
		[Vector3(3450, -620, 3320), Vector3(2880, -860, 3320)], # 12 the Chamber of the Wheel
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
