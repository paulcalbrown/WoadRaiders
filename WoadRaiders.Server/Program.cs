using WoadRaiders.Core;
using WoadRaiders.Server;
using WoadRaiders.Shared;

// Usage: WoadRaiders.Server [port] [--map path/to/map.json]
// Without --map, every catalog dungeon found in the client's maps directory is
// loaded and players forge/join instances of them. With --map, only that map
// is loaded and every forged instance uses it (dev convenience for map work).
// Maps are authored in the Godot editor (tools/export_dungeon.gd) or generated
// (tools/GenerateDungeon.cs).
int port = NetConfig.DefaultPort;
string? mapPath = null;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--map" && i + 1 < args.Length)
        mapPath = args[++i];
    else if (int.TryParse(args[i], out var p))
        port = p;
}

var maps = new Dictionary<DungeonId, string>();
if (mapPath is not null)
{
    maps[DungeonId.Barrow] = mapPath; // single custom map: every forged instance uses it
}
else
{
    var mapsDir = GameServer.FindMapsDirectory();
    if (mapsDir is null)
    {
        Console.Error.WriteLine("No maps directory found. Usage: WoadRaiders.Server [port] --map <map.json>");
        return 1;
    }
    foreach (var info in DungeonCatalog.All)
    {
        var path = Path.Combine(mapsDir, info.MapFile);
        if (File.Exists(path))
            maps[info.Id] = path;
        else
            Console.Error.WriteLine($"Catalog dungeon '{info.Name}' is missing its map ({path}) — not hosting it.");
    }
    if (maps.Count == 0)
    {
        Console.Error.WriteLine($"No catalog maps found under '{mapsDir}'.");
        return 1;
    }
}

return new GameServer(maps).Run(port) ? 0 : 1;
