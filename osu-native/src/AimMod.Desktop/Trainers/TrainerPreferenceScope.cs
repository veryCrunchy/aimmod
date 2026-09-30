using osu.Framework.Bindables;

namespace AimMod.Desktop.Trainers;

internal sealed class TrainerPreferenceScope : IDisposable
{
    private readonly List<Action> restore = [];
    public void Remember<T>(Bindable<T> binding)
    {
        T previous = binding.Value;
        restore.Add(() => binding.Value = previous);
    }
    public void Set<T>(Bindable<T> binding, T value) { Remember(binding); binding.Value = value; }
    public void SetExactScale(Bindable<float> binding, float value)
    {
        if (binding is BindableFloat number)
        {
            float precision = number.Precision, minimum = number.MinValue;
            restore.Add(() => { number.MinValue = minimum; number.Precision = precision; });
            number.Precision = .000001f;
            number.MinValue = Math.Min(minimum, .01f);
        }
        Set(binding, value);
    }
    public void Dispose()
    {
        foreach (var undo in restore.AsEnumerable().Reverse()) undo();
        restore.Clear();
    }
}
