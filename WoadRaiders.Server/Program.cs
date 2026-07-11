using WoadRaiders.Server;
using WoadRaiders.Shared;

// Usage: WoadRaiders.Server [port] [--map path/to/map.json]
// Maps are authored in the Godot editor and exported with tools/export_dungeon.gd.
// Without --map, the bundled test arena is served (dev convenience).
int port = NetConfig.DefaultPort;
string? mapPath = null;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--map" && i + 1 < args.Length)
        mapPath = args[++i];
    else if (int.TryParse(args[i], out var p))
        port = p;
}

mapPath ??= GameServer.FindDefaultMap();
if (mapPath is null)
{
    Console.Error.WriteLine("No map found. Usage: WoadRaiders.Server [port] --map <map.json>");
    return 1;
}

return new GameServer(mapPath).Run(port) ? 0 : 1;
