using LiteNetLib.Utils;

namespace WoadRaiders.Shared;

/// <summary>Client → server. Sent once, right after connecting.</summary>
public sealed class JoinRequest : INetSerializable
{
    public string Name = "Raider";

    public void Serialize(NetDataWriter w) => w.Put(Name);
    public void Deserialize(NetDataReader r) => Name = r.GetString();
}

/// <summary>Server → client. Confirms the join and hands out the player's id.</summary>
public sealed class WelcomePacket : INetSerializable
{
    public int PlayerId;
    public int ServerTick;

    public void Serialize(NetDataWriter w)
    {
        w.Put(PlayerId);
        w.Put(ServerTick);
    }

    public void Deserialize(NetDataReader r)
    {
        PlayerId = r.GetInt();
        ServerTick = r.GetInt();
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
    public float X;
    public float Y;
    public float Z;
    public float Health;
    public uint LastProcessedInput;
    public bool Attacking;

    public void Serialize(NetDataWriter w)
    {
        w.Put(Id);
        w.Put(X);
        w.Put(Y);
        w.Put(Z);
        w.Put(Health);
        w.Put(LastProcessedInput);
        w.Put((byte)(Attacking ? 1 : 0));
    }

    public void Deserialize(NetDataReader r)
    {
        Id = r.GetInt();
        X = r.GetFloat();
        Y = r.GetFloat();
        Z = r.GetFloat();
        Health = r.GetFloat();
        LastProcessedInput = r.GetUInt();
        Attacking = r.GetByte() != 0;
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

    public void Serialize(NetDataWriter w)
    {
        w.Put(Id);
        w.Put(X);
        w.Put(Y);
        w.Put(Z);
    }

    public void Deserialize(NetDataReader r)
    {
        Id = r.GetInt();
        X = r.GetFloat();
        Y = r.GetFloat();
        Z = r.GetFloat();
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
/// Server → client. The dungeon's 3D collision geometry (solid boxes + spawn),
/// sent once on join. The client rebuilds a <c>DungeonGeometry</c> from it so
/// prediction collides against exactly what the server does.
/// </summary>
public sealed class DungeonGeometryPacket : INetSerializable
{
    public float SpawnX;
    public float SpawnY;
    public float SpawnZ;

    /// <summary>res:// scene to render for this map ("" = none; client uses placeholder boxes).</summary>
    public string ScenePath = "";

    /// <summary>Flattened boxes: 6 floats each (minX minY minZ maxX maxY maxZ).</summary>
    public float[] Boxes = System.Array.Empty<float>();

    public void Serialize(NetDataWriter w)
    {
        w.Put(SpawnX);
        w.Put(SpawnY);
        w.Put(SpawnZ);
        w.Put(ScenePath);
        w.Put((ushort)(Boxes.Length / 6));
        foreach (var f in Boxes)
            w.Put(f);
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
