using LiteNetLib.Utils;

namespace WoadRaiders.Shared;

/// <summary>How a join request enters a dungeon: forge a fresh instance or join a live one.</summary>
public enum JoinMode : byte
{
    /// <summary>Create a new instance of <see cref="JoinRequest.Dungeon"/> and enter it.</summary>
    Create = 0,

    /// <summary>Enter the existing instance named by <see cref="JoinRequest.InstanceId"/>.</summary>
    Join = 1,
}

/// <summary>Why the server refused a join (the connection stays up; pick again).</summary>
public enum JoinDenyReason : byte
{
    /// <summary>The instance no longer exists (ended, or the id was never real).</summary>
    InstanceGone = 0,

    /// <summary>The instance is at its player cap.</summary>
    InstanceFull = 1,

    /// <summary>The server is at its instance cap — no new instance can be forged.</summary>
    ServerFull = 2,
}

/// <summary>
/// Server → client, riding the connection-layer reject (LiteNetLib's
/// <c>ConnectionRequest.Reject(data)</c> → <c>DisconnectInfo.AdditionalData</c>) —
/// NOT the <see cref="MessageType"/> framing, which never gets a chance to run.
/// Tells a refused client why: a version-skewed build, or a full server.
///
/// FROZEN FORMAT — this is the one packet that must cross version gates, since
/// its whole job is to be read by a client whose build does NOT match the
/// server's. Never remove, reorder, or retype these fields; only append.
/// </summary>
public sealed class ConnectDeniedPacket : INetSerializable
{
    /// <summary>The server's <see cref="NetConfig.ConnectionKey"/>. A client whose own
    /// key differs is outdated and should stop retrying; a client whose key matches was
    /// refused for a transient reason (e.g. a full server) and may retry.</summary>
    public string ServerKey = "";

    /// <summary>Human-readable reason, ready to show on screen (includes the
    /// download URL when the refusal is a version mismatch).</summary>
    public string Message = "";

    public void Serialize(NetDataWriter w)
    {
        w.Put(ServerKey);
        w.Put(Message);
    }

    public void Deserialize(NetDataReader r)
    {
        ServerKey = r.GetString();
        Message = r.GetString();
    }
}

/// <summary>Client → server. Sent once, right after connecting.</summary>
public sealed class JoinRequest : INetSerializable
{
    public string Name = "Raider";
    public byte Class;        // CharacterClass — the server validates and honors it once
    public byte Mode;         // JoinMode — forge a new instance, or join a live one
    public byte Dungeon;      // DungeonId — which dungeon to forge (Create mode)
    public string InstanceName = ""; // what to call the forged instance ("" = server default)
    public int InstanceId;    // which live instance to enter (Join mode)

    public void Serialize(NetDataWriter w)
    {
        w.Put(Name);
        w.Put(Class);
        w.Put(Mode);
        w.Put(Dungeon);
        w.Put(InstanceName);
        w.Put(InstanceId);
    }

    public void Deserialize(NetDataReader r)
    {
        Name = r.GetString();
        Class = r.GetByte();
        Mode = r.GetByte();
        Dungeon = r.GetByte();
        InstanceName = r.GetString();
        InstanceId = r.GetInt();
    }
}

/// <summary>Server → client. Confirms the join and hands out the player's id.</summary>
public sealed class WelcomePacket : INetSerializable
{
    public int PlayerId;
    public int ServerTick;

    /// <summary>The instance the player landed in. A client that forged it pins
    /// this id so a reconnect rejoins the same run instead of forging another.</summary>
    public int InstanceId;

    public void Serialize(NetDataWriter w)
    {
        w.Put(PlayerId);
        w.Put(ServerTick);
        w.Put(InstanceId);
    }

    public void Deserialize(NetDataReader r)
    {
        PlayerId = r.GetInt();
        ServerTick = r.GetInt();
        InstanceId = r.GetInt();
    }
}

/// <summary>Server → one client. The join was refused; the connection stays up so the client can re-browse.</summary>
public sealed class JoinDeniedPacket : INetSerializable
{
    public byte Reason; // JoinDenyReason

    public void Serialize(NetDataWriter w) => w.Put(Reason);
    public void Deserialize(NetDataReader r) => Reason = r.GetByte();
}

/// <summary>Client → server. Asks for the current list of live instances (no payload).</summary>
public sealed class InstanceListRequestPacket : INetSerializable
{
    public void Serialize(NetDataWriter w) { }
    public void Deserialize(NetDataReader r) { }
}

/// <summary>One live instance inside an <see cref="InstanceListPacket"/>.</summary>
public struct InstanceEntry : INetSerializable
{
    public int Id;
    public byte Dungeon; // DungeonId
    public string Name;
    public byte Players;
    public byte MaxPlayers;

    public void Serialize(NetDataWriter w)
    {
        w.Put(Id);
        w.Put(Dungeon);
        w.Put(Name);
        w.Put(Players);
        w.Put(MaxPlayers);
    }

    public void Deserialize(NetDataReader r)
    {
        Id = r.GetInt();
        Dungeon = r.GetByte();
        Name = r.GetString();
        Players = r.GetByte();
        MaxPlayers = r.GetByte();
    }
}

/// <summary>Server → one client. Every live instance, in reply to a list request.</summary>
public sealed class InstanceListPacket : INetSerializable
{
    public InstanceEntry[] Instances = System.Array.Empty<InstanceEntry>();

    public void Serialize(NetDataWriter w)
    {
        w.Put((ushort)Instances.Length);
        foreach (var entry in Instances)
            entry.Serialize(w);
    }

    public void Deserialize(NetDataReader r)
    {
        int count = r.GetUShort();
        Instances = new InstanceEntry[count];
        for (var i = 0; i < count; i++)
        {
            var entry = new InstanceEntry();
            entry.Deserialize(r);
            Instances[i] = entry;
        }
    }
}

/// <summary>Client → server. One frame of movement (ground-plane intent) + aim + attack.</summary>
public sealed class InputPacket : INetSerializable
{
    public float MoveX;
    public float MoveZ;
    public float AimX; // ground-plane aim toward the cursor; (0,0) = no aim
    public float AimZ;
    public uint Sequence;
    public bool Attack;

    public void Serialize(NetDataWriter w)
    {
        w.Put(MoveX);
        w.Put(MoveZ);
        w.Put(AimX);
        w.Put(AimZ);
        w.Put(Sequence);
        w.Put((byte)(Attack ? 1 : 0));
    }

    public void Deserialize(NetDataReader r)
    {
        MoveX = r.GetFloat();
        MoveZ = r.GetFloat();
        AimX = r.GetFloat();
        AimZ = r.GetFloat();
        Sequence = r.GetUInt();
        Attack = r.GetByte() != 0;
    }
}

/// <summary>One player's state inside a <see cref="WorldSnapshotPacket"/>. Positions are 3D, Y-up.</summary>
public struct PlayerSnapshot : INetSerializable
{
    public int Id;
    public string Name; // shown on fellow raiders' overhead nameplates
    public float X;
    public float Y;
    public float Z;
    public float Health;
    public uint LastProcessedInput;
    public bool Attacking;

    // Authoritative attack timers. The local client restores these when reconciling
    // so its predicted swing-root replays exactly (see ClientPrediction.Reconcile);
    // remotes ignore them.
    public float AttackAnim;
    public float AttackCooldown;

    public byte Class; // CharacterClass — picks the model and attack clip

    public void Serialize(NetDataWriter w)
    {
        w.Put(Id);
        w.Put(Name ?? "");
        w.Put(X);
        w.Put(Y);
        w.Put(Z);
        w.Put(Health);
        w.Put(LastProcessedInput);
        w.Put((byte)(Attacking ? 1 : 0));
        w.Put(AttackAnim);
        w.Put(AttackCooldown);
        w.Put(Class);
    }

    public void Deserialize(NetDataReader r)
    {
        Id = r.GetInt();
        Name = r.GetString();
        X = r.GetFloat();
        Y = r.GetFloat();
        Z = r.GetFloat();
        Health = r.GetFloat();
        LastProcessedInput = r.GetUInt();
        Attacking = r.GetByte() != 0;
        AttackAnim = r.GetFloat();
        AttackCooldown = r.GetFloat();
        Class = r.GetByte();
    }
}

/// <summary>One enemy's state inside a <see cref="WorldSnapshotPacket"/>. Positions are 3D, Y-up.</summary>
public struct EnemySnapshot : INetSerializable
{
    public int Id;
    public float X;
    public float Y;
    public float Z;
    public float Health;
    public bool Attacking;
    public byte Type; // EnemyType — picks the client model and max health

    public void Serialize(NetDataWriter w)
    {
        w.Put(Id);
        w.Put(X);
        w.Put(Y);
        w.Put(Z);
        w.Put(Health);
        w.Put((byte)(Attacking ? 1 : 0));
        w.Put(Type);
    }

    public void Deserialize(NetDataReader r)
    {
        Id = r.GetInt();
        X = r.GetFloat();
        Y = r.GetFloat();
        Z = r.GetFloat();
        Health = r.GetFloat();
        Attacking = r.GetByte() != 0;
        Type = r.GetByte();
    }
}

/// <summary>One piece of ground loot inside a <see cref="WorldSnapshotPacket"/>.</summary>
public struct GroundItemSnapshot : INetSerializable
{
    public int Id;
    public float X;
    public float Y;
    public float Z;
    public byte Rarity; // equipment rarity (drives the pickup log / future tint); 0 for gold/potions
    public byte Kind;   // LootKind — picks the client visual family
    public byte Type;   // ItemType — picks which weapon mesh to show (equipment only)

    public void Serialize(NetDataWriter w)
    {
        w.Put(Id);
        w.Put(X);
        w.Put(Y);
        w.Put(Z);
        w.Put(Rarity);
        w.Put(Kind);
        w.Put(Type);
    }

    public void Deserialize(NetDataReader r)
    {
        Id = r.GetInt();
        X = r.GetFloat();
        Y = r.GetFloat();
        Z = r.GetFloat();
        Rarity = r.GetByte();
        Kind = r.GetByte();
        Type = r.GetByte();
    }
}

/// <summary>One in-flight projectile inside a <see cref="WorldSnapshotPacket"/>. Positions are 3D, Y-up.</summary>
public struct ProjectileSnapshot : INetSerializable
{
    public int Id;
    public float X;
    public float Y;
    public float Z;
    public byte Kind; // ProjectileKind — picks the client visual (bolt vs arrow)

    public void Serialize(NetDataWriter w)
    {
        w.Put(Id);
        w.Put(X);
        w.Put(Y);
        w.Put(Z);
        w.Put(Kind);
    }

    public void Deserialize(NetDataReader r)
    {
        Id = r.GetInt();
        X = r.GetFloat();
        Y = r.GetFloat();
        Z = r.GetFloat();
        Kind = r.GetByte();
    }
}

/// <summary>Server → clients. The authoritative state of the world this tick.</summary>
public sealed class WorldSnapshotPacket : INetSerializable
{
    public int ServerTick;
    public PlayerSnapshot[] Players = System.Array.Empty<PlayerSnapshot>();
    public EnemySnapshot[] Enemies = System.Array.Empty<EnemySnapshot>();
    public GroundItemSnapshot[] GroundItems = System.Array.Empty<GroundItemSnapshot>();
    public ProjectileSnapshot[] Projectiles = System.Array.Empty<ProjectileSnapshot>();

    /// <summary>The exit portal, once the boss has fallen. Walking into it ends the run.</summary>
    public bool PortalOpen;
    public float PortalX;
    public float PortalY;
    public float PortalZ;

    public void Serialize(NetDataWriter w)
    {
        w.Put(ServerTick);

        w.Put((ushort)Players.Length);
        foreach (var p in Players)
            p.Serialize(w);

        w.Put((ushort)Enemies.Length);
        foreach (var e in Enemies)
            e.Serialize(w);

        w.Put((ushort)GroundItems.Length);
        foreach (var g in GroundItems)
            g.Serialize(w);

        w.Put((ushort)Projectiles.Length);
        foreach (var p in Projectiles)
            p.Serialize(w);

        w.Put(PortalOpen);
        w.Put(PortalX);
        w.Put(PortalY);
        w.Put(PortalZ);
    }

    public void Deserialize(NetDataReader r)
    {
        ServerTick = r.GetInt();

        int playerCount = r.GetUShort();
        Players = new PlayerSnapshot[playerCount];
        for (int i = 0; i < playerCount; i++)
        {
            var p = new PlayerSnapshot();
            p.Deserialize(r);
            Players[i] = p;
        }

        int enemyCount = r.GetUShort();
        Enemies = new EnemySnapshot[enemyCount];
        for (int i = 0; i < enemyCount; i++)
        {
            var e = new EnemySnapshot();
            e.Deserialize(r);
            Enemies[i] = e;
        }

        int groundCount = r.GetUShort();
        GroundItems = new GroundItemSnapshot[groundCount];
        for (int i = 0; i < groundCount; i++)
        {
            var g = new GroundItemSnapshot();
            g.Deserialize(r);
            GroundItems[i] = g;
        }

        int projectileCount = r.GetUShort();
        Projectiles = new ProjectileSnapshot[projectileCount];
        for (int i = 0; i < projectileCount; i++)
        {
            var p = new ProjectileSnapshot();
            p.Deserialize(r);
            Projectiles[i] = p;
        }

        PortalOpen = r.GetBool();
        PortalX = r.GetFloat();
        PortalY = r.GetFloat();
        PortalZ = r.GetFloat();
    }
}

/// <summary>
/// Server → one client. Loot that client just collected. <see cref="Kind"/>
/// (a LootKind) decides which fields matter: equipment fills the item fields;
/// gold carries the coins in <see cref="Amount"/>; a potion carries the health
/// actually restored there (the heal itself arrives in the next snapshot).
/// </summary>
public sealed class ItemPickedUpPacket : INetSerializable
{
    public int ItemId;
    public string Name = "";
    public byte Rarity;
    public byte Type;
    public int Power;
    public byte Kind;
    public int Amount;

    public void Serialize(NetDataWriter w)
    {
        w.Put(ItemId);
        w.Put(Name);
        w.Put(Rarity);
        w.Put(Type);
        w.Put(Power);
        w.Put(Kind);
        w.Put(Amount);
    }

    public void Deserialize(NetDataReader r)
    {
        ItemId = r.GetInt();
        Name = r.GetString();
        Rarity = r.GetByte();
        Type = r.GetByte();
        Power = r.GetInt();
        Kind = r.GetByte();
        Amount = r.GetInt();
    }
}

/// <summary>Client → server. Asks to equip an inventory item by id.</summary>
public sealed class EquipRequestPacket : INetSerializable
{
    public int ItemId;

    public void Serialize(NetDataWriter w) => w.Put(ItemId);
    public void Deserialize(NetDataReader r) => ItemId = r.GetInt();
}

/// <summary>Server → one client. The item ids currently equipped in each slot (0 = empty).</summary>
public sealed class EquipmentUpdatePacket : INetSerializable
{
    public int WeaponItemId;
    public int ArmorItemId;
    public int TrinketItemId;

    public void Serialize(NetDataWriter w)
    {
        w.Put(WeaponItemId);
        w.Put(ArmorItemId);
        w.Put(TrinketItemId);
    }

    public void Deserialize(NetDataReader r)
    {
        WeaponItemId = r.GetInt();
        ArmorItemId = r.GetInt();
        TrinketItemId = r.GetInt();
    }
}

/// <summary>
/// Server → client. The realm's 3D geometry — spawn, solid boxes, an optional
/// smooth heightfield terrain, and cosmetic props — sent once on join (reliable,
/// so LiteNetLib fragments the terrain freely). The client rebuilds a
/// <c>DungeonGeometry</c> from it so prediction collides against exactly what
/// the server does, and renders the realm from the same data.
/// </summary>
public sealed class DungeonGeometryPacket : INetSerializable
{
    public float SpawnX;
    public float SpawnY;
    public float SpawnZ;

    /// <summary>Visual identity of the map ("" = none; terrain realms render from the geometry).</summary>
    public string ScenePath = "";

    /// <summary>Flattened boxes: 6 floats each (minX minY minZ maxX maxY maxZ).</summary>
    public float[] Boxes = System.Array.Empty<float>();

    /// <summary>True when the terrain fields below carry a heightfield.</summary>
    public bool HasTerrain;
    public float TerrainOriginX;
    public float TerrainOriginZ;
    public float TerrainCellSize;
    public int TerrainWidth;
    public int TerrainDepth;

    /// <summary>Row-major height samples ([z * width + x]); empty without terrain.</summary>
    public float[] TerrainHeights = System.Array.Empty<float>();

    public void Serialize(NetDataWriter w)
    {
        w.Put(SpawnX);
        w.Put(SpawnY);
        w.Put(SpawnZ);
        w.Put(ScenePath);
        w.Put((ushort)(Boxes.Length / 6));
        foreach (var f in Boxes)
            w.Put(f);

        w.Put(HasTerrain);
        if (HasTerrain)
        {
            w.Put(TerrainOriginX);
            w.Put(TerrainOriginZ);
            w.Put(TerrainCellSize);
            w.Put(TerrainWidth);
            w.Put(TerrainDepth);
            foreach (var h in TerrainHeights)
                w.Put(h);
        }
    }

    public void Deserialize(NetDataReader r)
    {
        SpawnX = r.GetFloat();
        SpawnY = r.GetFloat();
        SpawnZ = r.GetFloat();
        ScenePath = r.GetString();
        int count = r.GetUShort();
        Boxes = new float[count * 6];
        for (var i = 0; i < Boxes.Length; i++)
            Boxes[i] = r.GetFloat();

        HasTerrain = r.GetBool();
        if (HasTerrain)
        {
            TerrainOriginX = r.GetFloat();
            TerrainOriginZ = r.GetFloat();
            TerrainCellSize = r.GetFloat();
            TerrainWidth = r.GetInt();
            TerrainDepth = r.GetInt();
            // The dimensions come off a hostile wire: bound the allocation before
            // trusting the product (the server's handler disconnects on a throw).
            long samples = (long)TerrainWidth * TerrainDepth;
            if (TerrainWidth < 2 || TerrainDepth < 2 || samples > 4_000_000)
                throw new System.IO.InvalidDataException($"unreasonable terrain dimensions {TerrainWidth}x{TerrainDepth}");
            TerrainHeights = new float[samples];
            for (var i = 0; i < TerrainHeights.Length; i++)
                TerrainHeights[i] = r.GetFloat();
        }
        else
        {
            TerrainHeights = System.Array.Empty<float>();
        }
    }
}

/// <summary>
/// Server → one client. Their run is over — they stepped through the boss
/// portal. Carries the summary the client shows before returning to the menus;
/// the connection is unbound from the instance the moment this is sent.
/// </summary>
public sealed class RunCompletePacket : INetSerializable
{
    public byte Dungeon;          // DungeonId that was raided
    public string RaidName = "";  // the instance's name, for the summary heading
    public int DurationSeconds;   // this player's time inside
    public int Gold;              // coins carried out
    public int ItemsLooted;       // equipment pieces carried out
    public int FoesSlain;         // the warband's shared kill tally

    public void Serialize(NetDataWriter w)
    {
        w.Put(Dungeon);
        w.Put(RaidName);
        w.Put(DurationSeconds);
        w.Put(Gold);
        w.Put(ItemsLooted);
        w.Put(FoesSlain);
    }

    public void Deserialize(NetDataReader r)
    {
        Dungeon = r.GetByte();
        RaidName = r.GetString();
        DurationSeconds = r.GetInt();
        Gold = r.GetInt();
        ItemsLooted = r.GetInt();
        FoesSlain = r.GetInt();
    }
}

/// <summary>Helpers for framing a packet as [type byte][payload].</summary>
public static class NetProtocol
{
    public static NetDataWriter Frame(MessageType type, INetSerializable packet)
    {
        var writer = new NetDataWriter();
        writer.Put((byte)type);
        packet.Serialize(writer);
        return writer;
    }
}
