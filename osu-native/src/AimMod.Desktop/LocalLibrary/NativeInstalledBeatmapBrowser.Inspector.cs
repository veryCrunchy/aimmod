using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.LocalLibrary;

public partial class NativeInstalledBeatmapBrowser
{
    /// <summary>
    /// Details for the selected set: cover, difficulty picker, actions, then what the
    /// difficulty asks of the player and how it compares with what they usually play.
    /// </summary>
    private sealed partial class BeatmapInspector : Container
    {
        private readonly Func<int, CancellationToken, Task>? openBeatmap;
        private readonly Action<string>? openPractice;
        private readonly Action<LocalBeatmapSet> selectSet;
        private readonly Action retry;
        private readonly OsuScrollContainer scroll;
        private readonly FillFlowContainer<Drawable> content;
        private LocalBeatmapSet? set;
        private LocalBeatmapDifficulty? difficulty;
        private IReadOnlyList<LocalBeatmapSet> candidates = Array.Empty<LocalBeatmapSet>();
        private InstalledBeatmapHistory history = InstalledBeatmapHistory.Empty;
        private BeatmapSkillDemand? usualSkills;
        private Container skillSlot = null!;
        private Container ppSlot = null!;
        private Container scoresSlot = null!;
        private IReadOnlyDictionary<int, double>? ppValues;
        private Exception? ppError;
        private bool ppAvailable;

        internal string PpStateForTesting { get; private set; } = string.Empty;

        public BeatmapInspector(Func<int, CancellationToken, Task>? openBeatmap, Action<string>? openPractice, Action<LocalBeatmapSet> selectSet, Action retry)
        {
            this.openBeatmap = openBeatmap;
            this.openPractice = openPractice;
            this.selectSet = selectSet;
            this.retry = retry;
            RelativeSizeAxes = Axes.Both;
            Child = scroll = new AimModScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = content = new FillFlowContainer<Drawable>
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new(AimModVisualStyle.SectionSpacing),
                    Padding = new MarginPadding { Right = AimModVisualStyle.RelatedSpacing + 4, Bottom = AimModVisualStyle.SectionSpacing },
                },
            };
            ClearSelection();
        }

        public void ClearSelection(bool showHint = true)
        {
            set = null;
            difficulty = null;
            content.Clear();
            if (showHint)
                content.Add(new EmptyState(FontAwesome.Solid.MousePointer, "Select a beatmap", "Its difficulties, PP and your scores appear here."));
        }

        public void SetHistory(InstalledBeatmapHistory value, IReadOnlyList<LocalBeatmapSet> sets)
        {
            history = value;
            candidates = sets;
            usualSkills = history.UsualSkills(sets);
            if (difficulty is null)
                return;
            skillSlot.Child = createSkillSection(difficulty);
            renderPp();
        }

        public void ShowSelection(LocalBeatmapSet set, LocalBeatmapDifficulty difficulty, IReadOnlyList<LocalBeatmapSet> candidates,
            Action<LocalBeatmapSet, LocalBeatmapDifficulty> selectDifficulty, bool ppAvailable)
        {
            bool sameSet = this.set?.SetId == set.SetId;
            this.set = set;
            this.difficulty = difficulty;
            this.candidates = candidates;
            this.ppAvailable = ppAvailable;
            ppValues = null;
            ppError = null;
            content.Clear();
            content.Add(new InspectorHeader(set));
            content.Add(new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(10),
                Children = new Drawable[]
                {
                    new DifficultyPicker(set, difficulty, selectDifficulty),
                    createActions(set, difficulty),
                },
            });
            // What the map asks of the player comes first; raw settings follow the player's own results.
            content.Add(skillSlot = new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Child = createSkillSection(difficulty) });
            content.Add(ppSlot = new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y });
            content.Add(scoresSlot = new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Child = section("Your scores", null, loadingLine("Reading your scores on this difficulty…")) });
            content.Add(section("Map settings", null, new SettingsGrid(difficulty)));
            content.Add(createSimilar(set, difficulty));
            renderPp();
            if (!sameSet)
                scroll.ScrollToStart(false);
        }

        public void ShowScores(IReadOnlyList<ScoreHistoryEntry> plays, OnlineBeatmapScoreHistoryResult online, Exception? error)
        {
            if (difficulty is null)
                return;
            scoresSlot.Child = createScores(plays, online, error);
        }

        public void ShowPp(IReadOnlyDictionary<int, double> values, Exception? error)
        {
            if (difficulty is null)
                return;
            ppValues = values;
            ppError = error;
            renderPp();
        }

        private Drawable createActions(LocalBeatmapSet set, LocalBeatmapDifficulty difficulty)
        {
            bool canOpen = difficulty.OnlineId > 0 && openBeatmap is not null;
            var open = new AimModButton("Open in osu!", () => { if (canOpen) _ = openBeatmap!(difficulty.OnlineId, CancellationToken.None); }, true);
            open.Enabled.Value = canOpen;
            open.Alpha = canOpen ? 1 : 0.45f;
            var practice = new AimModButton("Practice", () => openPractice?.Invoke(set.Title));
            practice.Enabled.Value = openPractice is not null;
            practice.Alpha = openPractice is null ? 0.45f : 1;
            return new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Horizontal,
                Spacing = new(AimModVisualStyle.RelatedSpacing),
                Children = new Drawable[] { open, practice },
            };
        }

        private Drawable createSkillSection(LocalBeatmapDifficulty difficulty)
        {
            BeatmapSkillDemand demand = BeatmapSkillDemand.From(difficulty);
            BeatmapSkillDemand? usual = usualSkills;
            MapBrowserSkillValue[] values =
            [
                new("Aim", demand.Aim * 10, usual?.Aim * 10),
                new("Speed", demand.Speed * 10, usual?.Speed * 10),
                new("Stamina", demand.Stamina * 10, usual?.Stamina * 10),
                new("Reading", demand.Reading * 10, usual?.Reading * 10),
                new("Precision", demand.Precision * 10, usual?.Precision * 10),
            ];
            (string headline, Colour4 colour) = SkillHeadline(values, difficulty.StarRating, history.UsualStars);
            var body = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(10),
                Children = new Drawable[]
                {
                    new OsuTextFlowContainer(text => { text.Font = AimModVisualStyle.BodyStrongFont; text.Colour = colour; })
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Text = headline,
                    },
                    new MapBrowserSkillBars(values),
                },
            };
            return section("Skill demand", usual is null ? "0–10 scale" : "0–10  ·  white mark = your usual", body);
        }

        /// <summary>One sentence comparing this difficulty with the player's usual maps.</summary>
        internal static (string Text, Colour4 Colour) SkillHeadline(IReadOnlyList<MapBrowserSkillValue> values, double stars, double? usualStars)
        {
            string starPart = usualStars is { } usual
                ? stars - usual >= 0.25 ? $"{stars - usual:0.0} stars above your usual {usual:0.0}."
                  : usual - stars >= 0.25 ? $"{usual - stars:0.0} stars below your usual {usual:0.0}."
                  : $"Right at your usual {usual:0.0} stars."
                : string.Empty;
            if (values.All(value => value.Usual is null))
            {
                return usualStars is null
                    ? ("Set a few scores to compare this with your usual maps.", AimModPalette.Muted)
                    : (starPart, AimModPalette.Text);
            }
            string[] harder = values.Where(value => value.Value - value.Usual >= 1).Select(value => value.Label).ToArray();
            if (harder.Length > 0)
                return ($"Harder than your usual on {string.Join(" and ", harder)}. {starPart}".Trim(), AimModPalette.Yellow);
            if (values.All(value => value.Value - value.Usual <= -1))
                return ($"Easier than your usual on every skill. {starPart}".Trim(), AimModPalette.Muted);
            return ($"Close to what you usually play. {starPart}".Trim(), AimModPalette.Accent);
        }

        private void renderPp()
        {
            if (difficulty is null)
                return;
            string detail = "No mods";
            Drawable body;
            if (!ppAvailable)
            {
                PpStateForTesting = "unavailable";
                body = mutedLine(difficulty.OnlineId > 0
                    ? "PP calculation is not available right now."
                    : "PP needs the submitted osu! version of this difficulty.");
            }
            else if (ppValues is null)
            {
                PpStateForTesting = "loading";
                body = loadingLine("Calculating PP for this difficulty…");
            }
            else if (ppValues.Count == 0)
            {
                PpStateForTesting = "failed";
                body = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(12),
                    Children = new Drawable[]
                    {
                        new AimModButton("Retry", retry) { Height = AimModVisualStyle.CompactControlHeight },
                        new SpriteText { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Text = ppError is TimeoutException ? "PP calculation timed out." : "PP could not be calculated.", Font = AimModVisualStyle.BodyFont, Colour = AimModPalette.Muted },
                    },
                };
            }
            else
            {
                PpStateForTesting = "ready";
                body = new PpByAccuracy(ppValues, history.UsualAccuracy);
            }
            ppSlot.Child = section("PP by accuracy", detail, body);
        }

        private static Drawable createScores(IReadOnlyList<ScoreHistoryEntry> plays, OnlineBeatmapScoreHistoryResult online, Exception? error)
        {
            string coverage = online.IsSuccess ? "Local + osu!" : "Local only";
            if (plays.Count == 0)
            {
                string text = error is not null ? "Your local scores could not be read." : "No scores on this difficulty yet.";
                return section("Your scores", coverage, mutedLine(text));
            }

            ScoreHistoryEntry best = plays.OrderByDescending(play => play.PerformancePoints ?? 0).ThenByDescending(play => play.Accuracy).First();
            var list = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(2),
            };
            foreach (ScoreHistoryEntry play in plays.OrderByDescending(play => play.PlayedAt).Take(5))
                list.Add(new ScoreLine(play, ReferenceEquals(play, best)));

            var body = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(10),
                Children = new Drawable[]
                {
                    new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(10, 0),
                        Children = new Drawable[]
                        {
                            new SpriteText { Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Text = MapBrowserFormat.Accuracy(best.Accuracy), Font = new FontUsage(size: 24, weight: "Bold"), Colour = AimModPalette.Text },
                            new SpriteText { Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Margin = new MarginPadding { Bottom = 3 }, Text = best.PerformancePoints is { } pp ? $"{pp:0}pp" : $"{best.TotalScore:N0}", Font = new FontUsage(size: 16, weight: "Bold"), Colour = AimModPalette.Accent },
                            new SpriteText { Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Margin = new MarginPadding { Bottom = 4 }, Text = $"best · {MapBrowserFormat.Ago(best.PlayedAt)}", Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
                        },
                    },
                    list,
                },
            };
            if (plays.Count > 5)
                body.Add(mutedLine($"{plays.Count - 5} older scores not shown"));
            return section("Your scores", $"{plays.Count} · {coverage}", body);
        }

        private Drawable createSimilar(LocalBeatmapSet selected, LocalBeatmapDifficulty target)
        {
            (LocalBeatmapSet Set, LocalBeatmapDifficulty Difficulty)[] next = candidates
                .Where(candidate => candidate.SetId != selected.SetId && candidate.Difficulties.Count > 0)
                .Select(candidate => (candidate, candidate.Difficulties.MinBy(d => Math.Abs(d.StarRating - target.StarRating))!))
                .OrderBy(pair => Math.Abs(pair.Item2.StarRating - target.StarRating))
                .Take(3)
                .ToArray();
            if (next.Length == 0)
                return new Container();
            var list = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(6),
            };
            foreach ((LocalBeatmapSet candidate, LocalBeatmapDifficulty nearest) in next)
                list.Add(new SimilarMap(candidate, nearest, () => selectSet(candidate)));
            return section("Similar difficulty", "in your library", list);
        }

        private static Drawable section(string title, string? detail, Drawable body) => new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(10),
            Children = new Drawable[]
            {
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 18,
                    Children = new Drawable[]
                    {
                        new SpriteText { Text = title, Font = new FontUsage(size: 15, weight: "Bold"), Colour = AimModPalette.Text },
                        new SpriteText { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Y = 2, Text = detail ?? string.Empty, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
                    },
                },
                body,
            },
        };

        private static Drawable mutedLine(string text) => new OsuTextFlowContainer(part => { part.Font = AimModVisualStyle.BodyFont; part.Colour = AimModPalette.Muted; })
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Text = text,
        };

        private static Drawable loadingLine(string text) => new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new(10, 0),
            Children = new Drawable[]
            {
                new Spinner { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                new SpriteText { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Text = text, Font = AimModVisualStyle.BodyFont, Colour = AimModPalette.Muted },
            },
        };

        private sealed partial class Spinner : SpriteIcon
        {
            public Spinner()
            {
                Icon = FontAwesome.Solid.CircleNotch;
                Size = new(14);
                Colour = AimModPalette.Accent;
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();
                this.RotateTo(360, 1000).Loop();
            }
        }
    }

    private sealed partial class InspectorHeader : CompositeDrawable
    {
        public InspectorHeader(LocalBeatmapSet set)
        {
            RelativeSizeAxes = Axes.X;
            Height = 136;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            BorderThickness = 1;
            BorderColour = AimModPalette.Border;
            LocalBeatmapDifficulty[] difficulties = set.Difficulties.ToArray();
            double maxStars = difficulties.Length == 0 ? 0 : difficulties.Max(d => d.StarRating);
            double bpm = difficulties.Length == 0 ? 0 : difficulties.Max(d => d.Bpm);
            double length = difficulties.Length == 0 ? 0 : difficulties.Max(d => d.LengthMilliseconds);
            InternalChildren = new Drawable[]
            {
                new MapBrowserCover(set.BackgroundPath, null, maxStars, fullResolution: true, showPlaceholderIcon: false, cornerRadius: AimModVisualStyle.CardRadius) { RelativeSizeAxes = Axes.Both },
                new Box { RelativeSizeAxes = Axes.Both, Colour = ColourInfo.GradientVertical(AimModPalette.Canvas.Opacity(0.05f), AimModPalette.Canvas.Opacity(0.92f)) },
                new FillFlowContainer
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new(3),
                    Padding = new MarginPadding(14),
                    Children = new Drawable[]
                    {
                        new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Text = set.Title, Font = new FontUsage(size: 20, weight: "Bold"), Colour = AimModPalette.Text },
                        new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Text = $"{set.Artist}  ·  mapped by {set.Creator}", Font = AimModVisualStyle.BodyFont, Colour = AimModPalette.Text },
                        new TruncatingSpriteText
                        {
                            RelativeSizeAxes = Axes.X,
                            Text = $"{MapBrowserFormat.Bpm(bpm)}  ·  {MapBrowserFormat.Duration(length)}  ·  {MapBrowserFormat.DifficultyCount(difficulties.Length)}",
                            Font = AimModVisualStyle.CaptionStrongFont,
                            Colour = AimModPalette.Muted,
                        },
                    },
                },
            };
        }
    }

    private sealed partial class DifficultyPicker : CompositeDrawable
    {
        private const int scroll_threshold = 12;

        public DifficultyPicker(LocalBeatmapSet set, LocalBeatmapDifficulty selected, Action<LocalBeatmapSet, LocalBeatmapDifficulty> select)
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            var flow = new FillFlowContainer<DifficultyOption>
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Full,
                Spacing = new(6),
            };
            foreach (LocalBeatmapDifficulty difficulty in set.Difficulties.OrderBy(d => d.StarRating))
                flow.Add(new DifficultyOption(difficulty, difficulty.BeatmapId == selected.BeatmapId, () => select(set, difficulty)));

            // Very large sets scroll inside a capped area instead of pushing the details away.
            InternalChild = set.Difficulties.Count > scroll_threshold
                ? new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 108,
                    Child = new AimModScrollContainer { RelativeSizeAxes = Axes.Both, Child = flow },
                }
                : flow;
        }
    }

    private sealed partial class DifficultyOption : MapBrowserPillButton
    {
        public Guid BeatmapId { get; }

        public DifficultyOption(LocalBeatmapDifficulty difficulty, bool selected, Action select)
            : base(30, AimModVisualStyle.ControlRadius)
        {
            BeatmapId = difficulty.BeatmapId;
            Action = select;
            SetColours(selected ? AimModPalette.AccentMuted : AimModPalette.Panel, selected ? AimModPalette.Accent : AimModPalette.Border);
            ContentPadding = new MarginPadding { Left = 5, Right = 10 };
            // osu!'s high-star colours are too dark for text on a dark surface, so the rating sits in its own pill.
            AddContent(new AimModDifficultyPill(difficulty.StarRating));
            AddContent(new TruncatingSpriteText { Text = difficulty.Name, MaxWidth = 130, Font = AimModVisualStyle.CaptionStrongFont, Colour = selected ? AimModPalette.Text : AimModPalette.Muted });
        }
    }

    private sealed partial class SettingsGrid : FillFlowContainer
    {
        public SettingsGrid(LocalBeatmapDifficulty difficulty)
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Full;
            Spacing = new(0, AimModVisualStyle.RelatedSpacing);
            Children = new Drawable[]
            {
                tile("AR", difficulty.ApproachRate, MapBrowserFormat.ApproachRate(difficulty.ApproachRate), true),
                tile("OD", difficulty.OverallDifficulty, MapBrowserFormat.OverallDifficulty(difficulty.OverallDifficulty)),
                tile("CS", difficulty.CircleSize, MapBrowserFormat.CircleSize(difficulty.CircleSize), true),
                tile("HP", difficulty.DrainRate, MapBrowserFormat.DrainRate(difficulty.DrainRate)),
            };
        }

        private static Drawable tile(string label, double value, string meaning, bool left = false) => new Container
        {
            RelativeSizeAxes = Axes.X,
            Width = 0.5f,
            Height = 50,
            Padding = left ? new MarginPadding { Right = 4 } : new MarginPadding { Left = 4 },
            Child = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                CornerRadius = AimModVisualStyle.ControlRadius,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                    new FillFlowContainer
                    {
                        Position = new(12, 7),
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(6, 0),
                        Children = new Drawable[]
                        {
                            new SpriteText { Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Text = label, Font = AimModVisualStyle.LabelFont, Colour = AimModPalette.Muted },
                            new SpriteText { Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Text = $"{value:0.#}", Font = new FontUsage(size: 16, weight: "Bold"), Colour = AimModPalette.Text },
                        },
                    },
                    new TruncatingSpriteText { Position = new(12, 29), RelativeSizeAxes = Axes.X, Padding = new MarginPadding { Right = 12 }, Text = meaning, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
                },
            },
        };
    }

    /// <summary>Five accuracy points with the one nearest the player's usual accuracy marked.</summary>
    private sealed partial class PpByAccuracy : FillFlowContainer
    {
        public PpByAccuracy(IReadOnlyDictionary<int, double> values, double? usualAccuracy)
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Vertical;
            Spacing = new(10);
            int[] points = AccuracyPoints;
            int? highlight = usualAccuracy is { } usual ? points.MinBy(point => Math.Abs(point - usual * 100)) : null;
            var columns = new Container { RelativeSizeAxes = Axes.X, Height = 54 };
            for (int i = 0; i < points.Length; i++)
            {
                int accuracy = points[i];
                bool marked = highlight == accuracy;
                columns.Add(new Container
                {
                    RelativePositionAxes = Axes.X,
                    RelativeSizeAxes = Axes.X,
                    X = i / (float)points.Length,
                    Width = 1f / points.Length,
                    Height = 54,
                    Padding = new MarginPadding { Right = 4 },
                    Child = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Masking = true,
                        CornerRadius = AimModVisualStyle.ControlRadius,
                        BorderThickness = marked ? 1 : 0,
                        BorderColour = AimModPalette.Accent.Opacity(0.5f),
                        Children = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both, Colour = marked ? AimModPalette.AccentMuted : AimModPalette.Panel },
                            new SpriteText { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Y = 8, Text = accuracy == 100 ? "SS" : $"{accuracy}%", Font = AimModVisualStyle.CaptionStrongFont, Colour = marked ? AimModPalette.Accent : AimModPalette.Muted },
                            new SpriteText { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Y = 26, Text = values.TryGetValue(accuracy, out double pp) ? $"{pp:0}" : "–", Font = new FontUsage(size: 17, weight: "Bold"), Colour = AimModPalette.Text },
                        },
                    },
                });
            }
            Add(columns);
            if (usualAccuracy is { } typical && Interpolate(values, typical * 100) is { } expected)
            {
                Add(new OsuTextFlowContainer(text => { text.Font = AimModVisualStyle.BodyFont; text.Colour = AimModPalette.Muted; })
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Text = $"About {expected:0}pp at your usual {typical * 100:0.0}% accuracy.",
                });
            }
        }

        /// <summary>Linear interpolation between the calculated accuracy points.</summary>
        internal static double? Interpolate(IReadOnlyDictionary<int, double> values, double accuracy)
        {
            KeyValuePair<int, double>[] points = values.OrderBy(pair => pair.Key).ToArray();
            if (points.Length == 0 || accuracy < points[0].Key)
                return null;
            for (int i = 1; i < points.Length; i++)
            {
                if (accuracy > points[i].Key)
                    continue;
                double t = (accuracy - points[i - 1].Key) / (points[i].Key - points[i - 1].Key);
                return points[i - 1].Value + (points[i].Value - points[i - 1].Value) * t;
            }
            return points[^1].Value;
        }
    }

    private sealed partial class ScoreLine : CompositeDrawable
    {
        public ScoreLine(ScoreHistoryEntry play, bool best)
        {
            RelativeSizeAxes = Axes.X;
            Height = 28;
            Masking = true;
            CornerRadius = 4;
            string mods = play.Mods.Count == 0 ? "No mods" : string.Join(string.Empty, play.Mods);
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = best ? AimModPalette.AccentMuted : AimModPalette.Panel, Alpha = best ? 0.8f : 0.6f },
                cell(MapBrowserFormat.Ago(play.PlayedAt), 0.02f, AimModPalette.Muted),
                cell(MapBrowserFormat.Accuracy(play.Accuracy), 0.27f, AimModPalette.Text, true),
                cell(play.PerformancePoints is { } pp ? $"{pp:0}pp" : "–", 0.50f, AimModPalette.Accent, true),
                cell(play.MissCount == 0 ? "FC" : $"{play.MissCount} miss", 0.66f, play.MissCount == 0 ? AimModPalette.Success : AimModPalette.Muted),
                cell(mods, 0.84f, AimModPalette.Muted),
            };
        }

        private static Drawable cell(string text, float x, Colour4 colour, bool strong = false) => new SpriteText
        {
            RelativePositionAxes = Axes.X,
            X = x,
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            Text = text,
            Font = strong ? AimModVisualStyle.CaptionStrongFont : AimModVisualStyle.CaptionFont,
            Colour = colour,
        };
    }

    private sealed partial class SimilarMap : AimModInteractiveSurface
    {
        public SimilarMap(LocalBeatmapSet set, LocalBeatmapDifficulty nearest, Action select)
        {
            RelativeSizeAxes = Axes.X;
            Height = 48;
            BorderThickness = 0;
            Action = select;
            Children = new Drawable[]
            {
                new MapBrowserCover(set.BackgroundPath, null, nearest.StarRating, showPlaceholderIcon: false, cornerRadius: 4) { Position = new(6, 6), Size = new(64, 36) },
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Left = 82, Right = 70 },
                    Children = new Drawable[]
                    {
                        new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Y = 7, Text = set.Title, Font = AimModVisualStyle.BodyStrongFont, Colour = AimModPalette.Text },
                        new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Y = 26, Text = nearest.Name, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
                    },
                },
                new AimModDifficultyPill(nearest.StarRating) { Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, Margin = new MarginPadding { Right = 10 } },
            };
        }
    }
}
