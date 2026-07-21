# Builds a generated realm's .tscn from its DESIGN (any IRealmDesign listed in
# scripts/tools/IRealmDesign.cs — CragDesign.cs is the first) using Godot's OWN
# serializer (ResourceSaver) — so the file is exactly what a naturally-authored
# scene looks like: built-in nodes only, cut stone, free-form scenery,
# no scripts, no metadata. The served geometry JSON is baked FROM the finished
# scene afterwards (bake_realm.gd), never the other way around — a design can
# place anything Godot can express.
#
# All the real logic lives in C# (scripts/tools/RealmSceneBuilder.cs); this
# shim exists because Godot cannot run a C# script from the command line.
# tools/GenerateRealm.cs drives the whole chain automatically.
#
# Build first (dotnet build WoadRaiders.Client), then:
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/build_realm_scene.gd -- <out.tscn> [realm]
# The realm defaults to the output file's base name. Example:
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/build_realm_scene.gd -- res://maps/Crag.tscn
extends SceneTree

func _init() -> void:
	var builder = load("res://scripts/tools/RealmSceneBuilder.cs").new()
	if builder == null or not builder.has_method("Run"):
		push_error("RealmSceneBuilder not available — build the client first: dotnet build WoadRaiders.Client")
		quit(1)
		return
	quit(builder.Run(OS.get_cmdline_user_args()))
