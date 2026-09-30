using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.LocalLibrary;

public partial class NativeInstalledBeatmapBrowser
{
    /// <summary>
    /// One installed set on a single compact line: cover, title, difficulty spread, key facts
    /// and the player's own result. Difficulties are chosen in the inspector.
    /// </summary>
    private sealed partial class BeatmapSetRow : AimModInteractiveSurface
    {
        public const float RowHeight = 80;
        private const float cover_width = 114;
        private const float facts_width = 84;
        private const float status_width = 150;

        private readonly LocalBeatmapSet set;
        private readonly Box selectionBar;
        private readonly Container textColumn;
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText subtitle;
        private readonly Container factsColumn;
        private readonly Container statusColumn;
        private readonly SpriteText statusPrimary;
        private readonly SpriteText statusPp;
        private readonly SpriteText statusSecondary;
        private AimModLayout.ChangeTracker<float> widthTracker;
        private bool selected;

        public Guid SetId => set.SetId;

        public LocalBeatmapSet Set => set;

        internal string StatusTextForTesting => $"{statusPrimary.Text} {statusPp.Text} {statusSecondary.Text}".Trim();

        public BeatmapSetRow(LocalBeatmapSet set, Action<LocalBeatmapSet> select)
        {
            this.set = set;
            RelativeSizeAxes = Axes.X;
            Height = RowHeight;
            BorderThickness = 0;
            BackgroundColour = AimModPalette.Panel;
            Action = () => select(set);

            double[] stars = set.Difficulties.Select(d => d.StarRating).ToArray();
            double maxStars = stars.Length == 0 ? 0 : stars.Max();
            LocalBeatmapDifficulty[] difficulties = set.Difficulties.ToArray();
            double[] bpms = difficulties.Select(d => d.Bpm).Where(bpm => bpm > 0).ToArray();
            double length = difficulties.Length == 0 ? 0 : difficulties.Max(d => d.LengthMilliseconds);
            string bpm = bpms.Length == 0 ? "–" : Math.Abs(bpms.Max() - bpms.Min()) < 1 ? $"{bpms.Max():0}" : $"{bpms.Min():0}–{bpms.Max():0}";

            Children = new Drawable[]
            {
                new MapBrowserCover(set.BackgroundPath, null, maxStars)
                {
                    Position = new(8, 8),
                    Size = new(cover_width, RowHeight - 16),
                },
                textColumn = new Container
                {
                    RelativeSizeAxes = Axes.Y,
                    X = 8 + cover_width + 14,
                    Children = new Drawable[]
                    {
                        title = new TruncatingSpriteText { Y = 11, Text = set.Title, Font = new FontUsage(size: 15, weight: "Bold"), Colour = AimModPalette.Text },
                        subtitle = new TruncatingSpriteText { Y = 32, Text = $"{set.Artist}  ·  {set.Creator}", Font = AimModVisualStyle.BodyFont, Colour = AimModPalette.Muted },
                        new MapBrowserDifficultySpread(stars) { Y = 55 },
                    },
                },
                factsColumn = new Container
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Width = facts_width,
                    RelativeSizeAxes = Axes.Y,
                    Margin = new MarginPadding { Right = status_width + 14 },
                    Children = new Drawable[]
                    {
                        new SpriteText { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Y = 20, Text = $"{bpm} BPM", Font = AimModVisualStyle.BodyStrongFont, Colour = AimModPalette.Text },
                        new SpriteText { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Y = 42, Text = MapBrowserFormat.Duration(length), Font = AimModVisualStyle.BodyFont, Colour = AimModPalette.Muted },
                    },
                },
                statusColumn = new Container
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Width = status_width,
                    RelativeSizeAxes = Axes.Y,
                    Margin = new MarginPadding { Right = 14 },
                    Children = new Drawable[]
                    {
                        new FillFlowContainer
                        {
                            Anchor = Anchor.TopRight,
                            Origin = Anchor.TopRight,
                            Y = 19,
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Horizontal,
                            Spacing = new(6, 0),
                            Children = new Drawable[]
                            {
                                statusPrimary = new SpriteText { Font = new FontUsage(size: 14, weight: "Bold"), Colour = AimModPalette.Text },
                                statusPp = new SpriteText { Font = new FontUsage(size: 14, weight: "Bold"), Colour = AimModPalette.Accent },
                            },
                        },
                        statusSecondary = new SpriteText { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Y = 42, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
                    },
                },
                selectionBar = new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = AimModPalette.Accent, Alpha = 0 },
            };
            SetHistory(null);
        }

        /// <summary>Shows the player's best local result on this set, or when it was last played.</summary>
        public void SetHistory(BeatmapPlaySummary? summary)
        {
            if (summary is not null)
            {
                statusPrimary.Text = MapBrowserFormat.Accuracy(summary.BestAccuracy);
                statusPrimary.Colour = AimModPalette.Text;
                statusPp.Text = summary.BestPp is { } pp ? $"{pp:0}pp" : string.Empty;
                statusSecondary.Text = $"{(summary.Plays == 1 ? "1 play" : $"{summary.Plays} plays")} · {MapBrowserFormat.Ago(summary.LastPlayed)}";
                return;
            }

            statusPp.Text = string.Empty;
            if (set.LastPlayed is { } lastPlayed)
            {
                statusPrimary.Text = "Played";
                statusPrimary.Colour = AimModPalette.Text;
                statusSecondary.Text = MapBrowserFormat.Ago(lastPlayed);
            }
            else if (set.LocalReplayCount is > 0 and var count)
            {
                statusPrimary.Text = "Played";
                statusPrimary.Colour = AimModPalette.Text;
                statusSecondary.Text = count == 1 ? "1 local play" : $"{count} local plays";
            }
            else
            {
                statusPrimary.Text = "Unplayed";
                statusPrimary.Colour = AimModPalette.Muted;
                statusSecondary.Text = $"Added {MapBrowserFormat.Ago(set.DateAdded)}";
            }
        }

        public void SetSelection(Guid setId)
        {
            bool value = set.SetId == setId;
            if (value == selected)
                return;
            selected = value;
            selectionBar.Alpha = value ? 1 : 0;
            BackgroundColour = value ? AimModPalette.PanelRaised : AimModPalette.Panel;
            BorderThickness = value ? 1 : 0;
            BorderColour = AimModPalette.Accent.Opacity(0.35f);
        }

        protected override void Update()
        {
            base.Update();
            if (!widthTracker.Update(DrawWidth))
                return;
            // Narrow rows drop facts first, then status, so the title stays readable.
            bool showStatus = DrawWidth >= 470;
            bool showFacts = DrawWidth >= 640;
            statusColumn.Alpha = showStatus ? 1 : 0;
            factsColumn.Alpha = showFacts ? 1 : 0;
            float reserved = 14 + (showStatus ? status_width + 14 : 0) + (showFacts ? facts_width + 14 : 0);
            float textWidth = Math.Max(0, DrawWidth - textColumn.X - reserved);
            textColumn.Width = textWidth;
            title.MaxWidth = subtitle.MaxWidth = textWidth;
        }
    }
}
