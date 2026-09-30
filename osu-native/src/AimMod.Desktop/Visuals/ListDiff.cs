using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Visuals;

/// <summary>Keyed list diffing so screens keep existing rows (and scroll position) when results change.</summary>
public static class ListDiff
{
    public static IReadOnlyList<TKey> Stale<TKey>(IEnumerable<TKey> existing, IEnumerable<TKey> incoming)
        where TKey : notnull
    {
        var keep = incoming as ISet<TKey> ?? incoming.ToHashSet();
        return existing.Where(key => !keep.Contains(key)).ToArray();
    }

    /// <summary>True when <paramref name="next"/> starts with every key of <paramref name="current"/> in order.</summary>
    public static bool IsAppend<TKey>(IReadOnlyList<TKey> current, IReadOnlyList<TKey> next)
        where TKey : notnull
    {
        if (current.Count == 0 || next.Count < current.Count)
            return false;

        EqualityComparer<TKey> comparer = EqualityComparer<TKey>.Default;
        for (int index = 0; index < current.Count; index++)
        {
            if (!comparer.Equals(current[index], next[index]))
                return false;
        }

        return true;
    }

    /// <summary>True when both sequences hold the same keys in the same order.</summary>
    public static bool SameOrder<TKey>(IReadOnlyList<TKey> current, IReadOnlyList<TKey> next)
        where TKey : notnull =>
        current.Count == next.Count && (current.Count == 0 || IsAppend(current, next));
}

/// <summary>
/// Reconciles a fill flow against keyed items. Rows whose key and signature are unchanged are kept,
/// changed rows are replaced, removed rows are disposed and the remaining rows are reordered in place.
/// </summary>
public sealed class KeyedFlow<TKey, TItem, TRow>
    where TKey : notnull
    where TRow : Drawable
{
    private readonly FillFlowContainer<Drawable> flow;
    private readonly Func<TItem, TKey> key;
    private readonly Func<TItem, string> signature;
    private readonly Func<TItem, TRow> create;
    private readonly Dictionary<TKey, (string Signature, TRow Row)> rows = new();
    private readonly List<TKey> order = [];

    public KeyedFlow(FillFlowContainer<Drawable> flow, Func<TItem, TKey> key, Func<TItem, string> signature, Func<TItem, TRow> create)
    {
        this.flow = flow;
        this.key = key;
        this.signature = signature;
        this.create = create;
    }

    public IReadOnlyList<TKey> Keys => order;

    public IEnumerable<TRow> Rows => order.Select(item => rows[item].Row);

    public bool TryGet(TKey itemKey, out TRow row)
    {
        bool found = rows.TryGetValue(itemKey, out var entry);
        row = entry.Row;
        return found;
    }

    /// <summary>Returns the number of rows that had to be created.</summary>
    public int Apply(IReadOnlyList<TItem> items)
    {
        var incoming = new Dictionary<TKey, TItem>(items.Count);
        var nextOrder = new List<TKey>(items.Count);
        foreach (TItem item in items)
        {
            TKey itemKey = key(item);
            if (incoming.TryAdd(itemKey, item))
                nextOrder.Add(itemKey);
        }

        foreach (TKey stale in ListDiff.Stale(order, incoming.Keys))
        {
            flow.Remove(rows[stale].Row, true);
            rows.Remove(stale);
        }

        int created = 0;
        for (int index = 0; index < nextOrder.Count; index++)
        {
            TKey itemKey = nextOrder[index];
            TItem item = incoming[itemKey];
            string itemSignature = signature(item);
            if (rows.TryGetValue(itemKey, out var existing) && existing.Signature != itemSignature)
            {
                flow.Remove(existing.Row, true);
                rows.Remove(itemKey);
            }

            if (!rows.TryGetValue(itemKey, out existing))
            {
                existing = (itemSignature, create(item));
                rows[itemKey] = existing;
                flow.Add(existing.Row);
                created++;
            }

            flow.SetLayoutPosition(existing.Row, index);
        }

        order.Clear();
        order.AddRange(nextOrder);
        return created;
    }

    public void Clear()
    {
        foreach (var entry in rows.Values)
            flow.Remove(entry.Row, true);
        rows.Clear();
        order.Clear();
    }
}
