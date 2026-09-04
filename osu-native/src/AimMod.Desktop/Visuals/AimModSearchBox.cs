using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Visuals;

public partial class AimModSearchBox : ShearedSearchTextBox
{
    public event Action? Committed;

    public AimModSearchBox()
    {
        TextBox.OnCommit += (_, _) => Committed?.Invoke();
    }
}
