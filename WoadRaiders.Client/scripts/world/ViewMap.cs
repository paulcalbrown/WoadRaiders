namespace WoadRaiders.Client;

/// <summary>
/// A snapshot-diffed view registry: one live view per entity id. Applying a
/// snapshot means <see cref="Touch"/>ing every id it contains (and <see cref="Add"/>ing
/// views for new ids), then one <see cref="Prune"/> — views whose ids vanished
/// from the stream are destroyed. Players, enemies, loot, and projectiles all
/// ride the same lifecycle. The scratch collections are reused, so a 20 Hz
/// snapshot stream allocates nothing here in steady state.
/// </summary>
public sealed class ViewMap<TView> where TView : class
{
    private readonly Dictionary<int, TView> _views = new();
    private readonly HashSet<int> _seen = new();
    private readonly List<int> _dead = new();
    private readonly Action<TView> _destroy;

    public ViewMap(Action<TView> destroy) => _destroy = destroy;

    /// <summary>Live views by id — iterate for per-frame updates. Mutate only via Add/Prune.</summary>
    public Dictionary<int, TView> Items => _views;

    /// <summary>
    /// Mark <paramref name="id"/> as live in the current snapshot. Returns true and
    /// yields the view if one already exists; otherwise create one and <see cref="Add"/> it.
    /// </summary>
    public bool Touch(int id, out TView view)
    {
        _seen.Add(id);
        return _views.TryGetValue(id, out view!);
    }

    /// <summary>Register the view created for a just-<see cref="Touch"/>ed new id.</summary>
    public void Add(int id, TView view) => _views[id] = view;

    /// <summary>Destroy and forget one view now (its visual identity changed and must rebuild).</summary>
    public void Remove(int id)
    {
        if (_views.Remove(id, out var view))
            _destroy(view);
    }

    /// <summary>Destroy every view whose id was not <see cref="Touch"/>ed since the last prune.</summary>
    public void Prune()
    {
        foreach (var (id, view) in _views)
        {
            if (_seen.Contains(id))
                continue;
            _dead.Add(id);
            _destroy(view);
        }
        foreach (var id in _dead)
            _views.Remove(id);
        _dead.Clear();
        _seen.Clear();
    }
}
