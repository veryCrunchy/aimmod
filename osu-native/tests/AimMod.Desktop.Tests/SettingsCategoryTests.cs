using System.Reflection;
using AimMod.Desktop.Onboarding;
using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Tests;
[TestFixture]
public class SettingsCategoryTests
{
    [Test]
    public void CategorisingSettingsKeepsControlsAndTheirSavedBindingsTogether()
    {
        string root=Path.Combine(Path.GetTempPath(),"aimmod-settings-"+Guid.NewGuid().ToString("N"));
        try {
            var destination=new OsuBeatmapDestinationService(new LazerBeatmapInstallService(Path.Combine(root,"handoff")),new FileOsuClientDestinationPreferenceStore(Path.Combine(root,"client.txt")),Path.Combine(root,"handoff"));
            var automatic=new AutomaticPracticeStore(root);
            var startup=new UserSetupStore(Path.Combine(root,"setup.json"));
            using var view=new OsuClientSettingsScreen(destination,null,null,null,null,null,null,automatic,startup);
            var pages=(List<Drawable>)typeof(OsuClientSettingsScreen).GetField("settingsPages",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
            OsuDropdown<string>[] controls(int index)=>((Container)pages[index]).Children.OfType<FillFlowContainer>().Single().Children.OfType<OsuDropdown<string>>().ToArray();
            Assert.That(controls(0).Select(c=>c.Current.Value),Is.EquivalentTo(new[]{"Auto"}));
            Assert.That(controls(2).Select(c=>c.Current.Value),Is.EquivalentTo(new[]{"Off","25 sets","Automatic"}));
            Assert.That(controls(3).Select(c=>c.Current.Value),Is.EquivalentTo(new[]{"Startup sound on"}));
            controls(2).Single(c=>c.Current.Value=="Off").Current.Value="On";
            Assert.That(automatic.Load().Enabled,Is.True);
            controls(2).Single(c=>c.Current.Value=="25 sets").Current.Value="100 sets";
            Assert.That(automatic.Load().MaximumActiveMaps,Is.EqualTo(100));
            Assert.That(automatic.Load().Enabled,Is.True);
            controls(3).Single().Current.Value="Startup sound off";
            Assert.That(startup.Load().StartupSound,Is.False);
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
