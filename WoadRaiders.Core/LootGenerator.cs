namespace WoadRaiders.Core;

/// <summary>
/// Rolls item drops. Pure and deterministic given a <see cref="Random"/>, so drop
/// behaviour is reproducible and unit-testable. The world assigns the real item id.
/// </summary>
public static class LootGenerator
{
    // Woad-raider flavour, indexed by rarity (Common → Legendary).
    private static readonly string[][] Prefixes =
    {
        new[] { "Worn", "Chipped", "Plain" },
        new[] { "Keen", "Woad-Stained", "Sturdy" },
        new[] { "Painted", "Chieftain's", "Runed" },
        new[] { "Druid's", "Warband", "Storm-Forged" },
        new[] { "Balor's", "Cú Chulainn's", "Morrígan's" },
    };

    private static readonly (int Min, int Max)[] PowerByRarity =
    {
        (5, 10), (10, 20), (20, 35), (35, 55), (55, 85),
    };

    // Relative weights, Common → Legendary.
    private static readonly int[] RarityWeights = { 60, 25, 10, 4, 1 };

    /// <summary>Roll a drop for a slain enemy. Returns null when nothing drops.</summary>
    public static Item? TryRollDrop(Random rng)
    {
        if (rng.NextDouble() > SimConstants.EnemyDropChance)
            return null;

        var rarity = RollRarity(rng);
        var type = (ItemType)rng.Next(Enum.GetValues<ItemType>().Length);
        var (min, max) = PowerByRarity[(int)rarity];
        var power = rng.Next(min, max + 1);
        var name = $"{Pick(rng, Prefixes[(int)rarity])} {type}";

        return new Item(Id: 0, name, rarity, type, power);
    }

    private static ItemRarity RollRarity(Random rng)
    {
        var total = 0;
        foreach (var w in RarityWeights)
            total += w;

        var roll = rng.Next(total);
        var acc = 0;
        for (var i = 0; i < RarityWeights.Length; i++)
        {
            acc += RarityWeights[i];
            if (roll < acc)
                return (ItemRarity)i;
        }
        return ItemRarity.Common;
    }

    private static string Pick(Random rng, string[] options) => options[rng.Next(options.Length)];
}
