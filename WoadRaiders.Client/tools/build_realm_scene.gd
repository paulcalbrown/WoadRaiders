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
	# A C# exception inside Run() is LOGGED by Godot's bridge and swallowed —
	# the call simply returns null. Passing that straight to quit() made a
	# failed design exit ZERO, so the pipeline would carry on and bake the
	# STALE .tscn still sitting on disk: a realm that silently did not change,
	# which is the worst way for a build to fail. Treat a missing code as a
	# failure and say why.
	var code = builder.Run(OS.get_cmdline_user_args())
	if typeof(code) != TYPE_INT:
		push_error("the realm design threw — see the exception above; the .tscn was NOT rewritten")
		quit(1)
		return
	quit(code)
