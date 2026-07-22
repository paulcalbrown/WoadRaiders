extends SceneTree
func _init() -> void:
	await process_frame
	var root_node = load("res://maps/Crypt.tscn").instantiate()
	root.add_child(root_node)
	for f in range(20):
		await process_frame
	var lights = root_node.get_node("Lights")
	var n := 0
	for c in lights.get_children():
		if c is Light3D and n < 6:
			print("%-22s energy=%.3f range=%.0f colour=%s fade=%s begin=%.0f"
				% [c.name, c.light_energy,
				   (c.spot_range if c is SpotLight3D else (c.omni_range if c is OmniLight3D else 0.0)),
				   c.light_color, str(c.distance_fade_enabled), c.distance_fade_begin])
			n += 1
	var env = root_node.get_node("Gloom").environment
	print("ambient=", env.ambient_light_color, " energy=", env.ambient_light_energy,
		  " tonemap=", env.tonemap_mode, " white=", env.tonemap_white, " exposure=", env.tonemap_exposure)
	print("volfog=", env.volumetric_fog_density, " len=", env.volumetric_fog_length,
		  " fog=", env.fog_density, " glow=", env.glow_enabled)
	print("players=", lights.get_children().filter(func(c): return c is AnimationPlayer).size())
	quit(0)
