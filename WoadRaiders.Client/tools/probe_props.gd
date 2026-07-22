# Dev probe: exact property names on a class, for writing C# against Godot.
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/probe_props.gd -- StandardMaterial3D uv2 detail
extends SceneTree

func _init() -> void:
	var args := OS.get_cmdline_user_args()
	var cls: String = args[0]
	var needles := args.slice(1)
	var obj = ClassDB.instantiate(cls)
	for p in obj.get_property_list():
		var n: String = p["name"]
		for needle in needles:
			if n.to_lower().find(needle.to_lower()) >= 0:
				print("%s	%s" % [n, type_string(p["type"])])
				break
	quit(0)
