using AimMod.Desktop.Onboarding;
using NUnit.Framework;
using osu.Framework.IO.Stores;

namespace AimMod.Desktop.Tests;
[TestFixture]
public class UserSetupTests
{
    [Test]
    public void CompletionAndSoundPreferenceSurviveRestartIndependently()
    {
        string root=Path.Combine(Path.GetTempPath(),"aimmod-setup-"+Guid.NewGuid().ToString("N"));
        try {
            var store=new UserSetupStore(Path.Combine(root,"setup.json"));
            Assert.That(store.Load(),Is.EqualTo(new UserSetupPreferences(false,true)));
            store.Save(store.Load() with {StartupSound=false});
            store.Save(store.Load() with {Completed=true});
            Assert.That(new UserSetupStore(Path.Combine(root,"setup.json")).Load(),Is.EqualTo(new UserSetupPreferences(true,false)));
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Test]
    public void StartupThemeIsBundledAndResolvable()
    {
        using var resources=new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(AimModGame).Assembly),"Resources/Audio");
        var audio=resources.Get("startup.ogg");
        Assert.That(audio,Is.Not.Null);
        Assert.That(audio!.Length,Is.GreaterThan(1000));
        Assert.That(System.Text.Encoding.ASCII.GetString(audio,0,4),Is.EqualTo("OggS"));
    }
}
