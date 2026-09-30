using osu.Framework.Development;
using osu.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Screens;

namespace AimMod.Desktop.Visuals;

/// <summary>Lift the open menu's ancestor containers, then restore their original depths.</summary>
internal sealed class AimModPopupLayer : IDisposable
{
    private static readonly Dictionary<Drawable, Entry> active = [];
    private readonly List<Drawable> owned = [];
    private readonly Action<Action> schedule;
    private sealed record Entry(Container<Drawable> Parent, float Depth) { public int References = 1; }

    public AimModPopupLayer(Drawable menuOwner, Action<Action> schedule)
    {
        this.schedule = schedule;
        Drawable child = menuOwner;
        while (child.Parent is {} parent && parent is not (Game or Screen))
        {
            if (parent is Container<Drawable> container && container.Children.Any(c => ReferenceEquals(c, child)))
            {
                lock (active)
                {
                if (active.TryGetValue(child, out var existing)) existing.References++;
                else
                {
                    active[child] = new(container, child.Depth);
                    container.ChangeChildDepth(child, Math.Min(-10000, child.Depth - 10000));
                }
                owned.Add(child);
                }
            }
            if (parent is AimModScrollContainer || parent.GetType().Name.StartsWith("Native", StringComparison.Ordinal)) break;
            child = parent;
        }
    }

    public void Dispose()
    {
        lock (active)
        {
            foreach (var child in owned)
            {
                if (!active.TryGetValue(child, out var entry) || --entry.References > 0) continue;
                active.Remove(child);
                void restore()
                {
                    lock (active)
                        if (!active.ContainsKey(child) && child.Parent == entry.Parent && entry.Parent.Children.Any(c => ReferenceEquals(c, child)))
                            entry.Parent.ChangeChildDepth(child, entry.Depth);
                }
                if (ThreadSafety.IsUpdateThread) restore();
                else schedule(restore);
            }
            owned.Clear();
        }
    }
}
