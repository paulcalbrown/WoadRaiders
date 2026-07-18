# Bakes a hand-made realm scene into server geometry JSON — needed ONLY when
# the scene's terrain is sculpted meshes (group "terrain"): extracting their
# triangles requires the engine. Scenes that use a RealmTerrain node skip this
# entirely — the server loads their .tscn directly (Core.DungeonSceneFile).
#
# All the real logic lives in C# (scripts/tools/RealmBaker.cs + the unit-tested
# Core.TerrainSampler / Core.DungeonSceneFile); this shim exists because Godot
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
		push_error("RealmBaker not available — build the client first: dotnet build WoadRaiders.Client")
		quit(1)
		return
	quit(baker.Run(OS.get_cmdline_user_args()))
