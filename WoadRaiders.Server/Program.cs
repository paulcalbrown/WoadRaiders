using WoadRaiders.Server;
using WoadRaiders.Shared;

// Usage: WoadRaiders.Server [port] [--map path/to/map.json]
int port = NetConfig.DefaultPort;
string? mapPath = null;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--map" && i + 1 < args.Length)
        mapPath = args[++i];
    else if (int.TryParse(args[i], out var p))
        port = p;
}

new GameServer(mapPath).Run(port);
