using WoadRaiders.Server;
using WoadRaiders.Shared;

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : NetConfig.DefaultPort;

new GameServer().Run(port);
