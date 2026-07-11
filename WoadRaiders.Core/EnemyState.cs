namespace WoadRaiders.Core;

/// <summary>Authoritative state for a single server-controlled enemy.</summary>
public sealed class EnemyState : Combatant
{
    public EnemyState(int id) : base(id, SimConstants.EnemyMaxHealth)
    {
    }
}
