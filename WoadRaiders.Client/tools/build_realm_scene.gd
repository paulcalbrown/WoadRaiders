# Builds a realm's .tscn from its geometry JSON using Godot's OWN serializer
# (ResourceSaver) — so the file is exactly what a naturally-authored scene
# looks like: built-in nodes only, a real terrain mesh, no scripts, no
# metadata. tools/GenerateRealm.cs drives this automatically; run by hand to
# turn any realm geometry JSON into an editable scene.
#
# All the real logic lives in C# (scripts/tools/RealmSceneBuilder.cs); this
# shim exists because Godot cannot run a C# script from the command line.
#
# Build first (dotnet build WoadRaiders.Client), then:
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/build_realm_scene.gd -- <geometry.json> <out.tscn>
# Example:
#   godot-mono --headless --path WoadRaiders.Client -s res://tools/build_realm_scene.gd -- res://maps/Crag.json res://maps/Crag.tscn
extends SceneTree

func _init() -> void:
	var builder = load("res://scripts/tools/RealmSceneBuilder.cs").new()
	if builder == null or not builder.has_method("Run"):
		push_error("RealmSceneBuilder not available — build the client first: dotnet build WoadRaiders.Client")
		quit(1)
		return
	quit(builder.Run(OS.get_cmdline_user_args()))
