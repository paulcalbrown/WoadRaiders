# Bakes a hand-made realm scene into server geometry JSON â€” needed ONLY when
# the scene's terrain is sculpted meshes (group "terrain"): extracting their
# triangles requires the engine. Scenes that use a RealmTerrain node skip this
# entirely â€” the server loads their .tscn directly (Core.RealmSceneFile).
#
# All the real logic lives in C# (scripts/tools/RealmBaker.cs + the unit-tested
# Core.TerrainSampler / Core.RealmSceneFile); this shim exists because Godot
# cannot run a C# script from the command line.
#
# Build first (dotnet build WoadRaiders.Client), then:
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/bake_realm.gd -- <scene.tscn> <out.json>
# Example:
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/bake_realm.gd -- res://maps/MyRealm.tscn res://maps/MyRealm.json
# Then check it:
#   dotnet run tools/ValidateRealm.cs WoadRaiders.Client/maps/MyRealm.json
extends SceneTree

func _init() -> void:
	var baker = load("res://scripts/tools/RealmBaker.cs").new()
	if baker == null or not baker.has_method("Run"):
		push_error("RealmBaker not available â€” build the client first: dotnet build WoadRaiders.Client")
		quit(1)
		return
	var code = baker.Run(OS.get_cmdline_user_args())
	# A C# exception surfaces here as null - fail LOUDLY, never exit 0.
	quit(1 if code == null else int(code))
