# Bakes a realm scene into the server geometry JSON it is played from.
# Every mesh in the scene becomes collision, whatever it is and wherever it
# sits: no groups, no naming, no privileged mesh type, and no exception for
# instanced kit props — a sarcophagus blocks because it is one. What holds a
# raider up and what blocks them are read afterwards off the geometry —
# each triangle's normal, and what survives Recast's voxels and erosion.
#
# All the real logic lives in C# (scripts/tools/RealmBaker.cs + the
# unit-tested Core.RealmSceneFile); this shim exists because Godot cannot
# run a C# script from the command line.
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
