using osu.Game.Graphics.Containers;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Onboarding;

public partial class UserSetupScreen : CompositeDrawable
{
    private readonly UserSetupStore store;
    private readonly Func<Drawable> settings;
    private readonly Action complete;
    private readonly Action help;
    private readonly FillFlowContainer<Drawable> body;
    private readonly FillFlowContainer<Drawable> steps;
    private readonly FillFlowContainer<Drawable> actions;
    private int step;
    private readonly AimModScrollContainer scroll;
    public UserSetupScreen(UserSetupStore store,Func<Drawable> settings,Action complete,Action help)
    {
        this.store=store;this.settings=settings;this.complete=complete;this.help=help;
        RelativeSizeAxes=Axes.Both;
        InternalChildren=[
            text("Set up your AimMod",26,AimModPalette.Text),
            steps=new FillFlowContainer<Drawable>{Y=44,AutoSizeAxes=Axes.Both,Direction=FillDirection.Horizontal,Spacing=new(8)},
            new Container {RelativeSizeAxes=Axes.Both,Padding=new MarginPadding{Top=96,Bottom=58},Child=scroll=new AimModScrollContainer {
                RelativeSizeAxes=Axes.Both,Child=body=new FillFlowContainer<Drawable>{RelativeSizeAxes=Axes.X,AutoSizeAxes=Axes.Y,Direction=FillDirection.Vertical,Spacing=new(12),Padding=new MarginPadding{Right=18,Bottom=12}}
            }},
            actions=new FillFlowContainer<Drawable>{Anchor=Anchor.BottomLeft,Origin=Anchor.BottomLeft,AutoSizeAxes=Axes.Both,Direction=FillDirection.Horizontal,Spacing=new(10)}
        ];
        render();
    }
    private static OsuSpriteText text(string value,float size,Colour4 colour)=>new(){Text=value,Font=new osu.Framework.Graphics.Sprites.FontUsage("Torus",size),Colour=colour};
    private void paragraph(string value)=>body.Add(new OsuTextFlowContainer(t=>{t.Font=new osu.Framework.Graphics.Sprites.FontUsage("Torus",14);t.Colour=AimModPalette.Muted;}){RelativeSizeAxes=Axes.X,AutoSizeAxes=Axes.Y,Text=value});
    public void Restart(){step=0;render();}
    private void finish()
    {
        try {store.Save(store.Load() with {Completed=true});complete();}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){paragraph("Your setup could not be saved. Try again.");}
    }
    private void render()
    {
        body.Clear();steps.Clear();actions.Clear();
        scroll.ScrollTo(0,false);
        foreach(var (title,index) in new[]{"1  Welcome","2  Your settings","3  App tour","4  Ready"}.Select((title,index)=>(title,index)))
            steps.Add(new SetupButton(title,()=>{step=index;render();},index==step));
        if(step==0)
        {
            body.Add(new IntroTile("Make practice part of your next session", "Connect your osu! library, understand your plays, and work on the sections that need attention.", "WELCOME",120));
            body.Add(new IntroTiles([
                new IntroTile("Choose your preferences","Pick your osu! client and decide how sharing, practice and startup sound work for you.","01 · PERSONALISE"),
                new IntroTile("Find your way around","Get a short introduction to maps, replays, statistics, coaching and PP targets.","02 · EXPLORE"),
                new IntroTile("Start with one play","Turn a completed play into focused practice and follow the results on its coaching page.","03 · PRACTISE")
            ]));
            body.Add(new SetupButton("Read the app guide",help));
        }
        else if(step==1)
        {
            paragraph("Set your osu! destination, connect your AimMod account, review sharing, and choose automatic practice and startup sound. Use the categories below; changes save as you make them.");
            body.Add(settings());
        }
        else if(step==2)
        {
            var tourTiles=new List<Drawable>();
            foreach(var (title,description) in new[]{
                ("Home","Your starting point and shortcuts to the workspaces."),
                ("Beatmaps","Browse and search maps, inspect difficulties, then open or install them in your selected osu! client."),
                ("Skins","Browse installed and online skins and choose the look you want to play with."),
                ("Replays","Open a recorded play to inspect timing, misses and difficult sections."),
                ("Statistics","Track your results over time. Compare similar mods and speeds to judge progress fairly."),
                ("Coaching","Find a map, open its coaching page and generate repeatable sections. My coaching keeps your sets, attempts and original-map comparisons together."),
                ("PP Targets","Explore maps and accuracy targets matched to your current performance. Targets are estimates, not guaranteed gains."),
                ("Settings","Manage your client, account, sharing, automatic practice and startup sound. Reopen this setup whenever you need it.")})
            {
                tourTiles.Add(new IntroTile(title,description));
            }
            body.Add(new IntroTiles(tourTiles.ToArray()));
        }
        else
        {
            body.Add(text("Start with one play",23,AimModPalette.Text));
            paragraph("Play a familiar map in osu!, then open Coaching → Find a map. Choose a completed play, prepare a practice set, and repeat the sections you want to improve.");
            paragraph("Return to the original map after practice. Your beatmap coaching page shows your section results and comparable original-map plays.");
            paragraph("Automatic sets are imported into your selected osu! client and grouped in AimMod coaching. Completed sets leave the collection as you improve; your practice history stays in AimMod.");
            body.Add(new SetupButton("Open the app guide",help));
        }
        actions.Add(new SetupButton("Skip setup",finish));
        if(step>0)actions.Add(new SetupButton("Back",()=>{step--;render();}));
        actions.Add(new SetupButton(step==3?"Finish setup":"Next",()=>{if(step==3)finish();else{step++;render();}},true));
    }
    private sealed partial class IntroTile : Container
    {
        public IntroTile(string title,string description,string? eyebrow=null,float height=116)
        {
            RelativeSizeAxes=Axes.X;Height=height;Masking=true;CornerRadius=8;
            BorderThickness=1;BorderColour=Colour4.White.Opacity(.07f);
            var copy=new FillFlowContainer<Drawable>{RelativeSizeAxes=Axes.X,AutoSizeAxes=Axes.Y,Direction=FillDirection.Vertical,Spacing=new(8),Padding=new MarginPadding(18)};
            if(eyebrow is not null)copy.Add(text(eyebrow,11,Colour4.FromHex("38D9A9")));
            copy.Add(text(title,eyebrow=="WELCOME"?24:18,AimModPalette.Text));
            copy.Add(new OsuTextFlowContainer(t=>{t.Font=new osu.Framework.Graphics.Sprites.FontUsage("Torus",14);t.Colour=AimModPalette.Muted;}){RelativeSizeAxes=Axes.X,AutoSizeAxes=Axes.Y,Text=description});
            Children=[new Box{RelativeSizeAxes=Axes.Both,Colour=AimModPalette.Panel},copy];
        }
    }
    private sealed partial class IntroTiles : Container
    {
        private readonly Drawable[] tiles;
        public IntroTiles(Drawable[] tiles){this.tiles=tiles;RelativeSizeAxes=Axes.X;Children=tiles;}
        protected override void Update()
        {
            base.Update();
            int columns=tiles.Length==3 && DrawWidth>=900?3:DrawWidth>=700?2:1;
            float tileHeight=tiles.Length==3?144:112;
            float width=(DrawWidth-(columns-1)*12)/columns;
            for(int i=0;i<tiles.Length;i++){
                tiles[i].RelativeSizeAxes=Axes.None;tiles[i].Width=width;tiles[i].Height=tileHeight;
                tiles[i].Position=new((i%columns)*(width+12),(i/columns)*(tileHeight+12));
            }
            Height=((tiles.Length+columns-1)/columns)*(tileHeight+12)-12;
        }
    }
    internal sealed partial class SetupButton : AimModButton
    {
        public SetupButton(string title, Action action, bool primary = false) : base(title, action, primary) { }
    }
}
