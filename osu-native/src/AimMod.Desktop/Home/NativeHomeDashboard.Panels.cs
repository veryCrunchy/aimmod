using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Home;

internal partial class NativeHomeDashboard
{
    /// <summary>Avatar, name and the account totals with their change since an earlier visit.</summary>
    internal partial class HomeProfileBand : HomePanel
    {
        private const float identity_width = 300;
        private readonly Container avatar;
        private readonly TruncatingSpriteText name;
        private readonly TruncatingSpriteText detail;
        private readonly AimModButton connect;
        private readonly FillFlowContainer<AimModHomeMetric> metrics;
        private readonly AimModHomeMetric pp;
        private readonly AimModHomeMetric rank;
        private readonly AimModHomeMetric country;
        private readonly AimModHomeMetric accuracy;
        private readonly Container identity;
        private Uri? avatarUrl;
        private AimModLayout.ChangeTracker<float> widthTracker;

        public HomeProfileBand(Action connectAccount)
            : base(null)
        {
            RelativeSizeAxes = Axes.X;
            Add(new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Full,
                Spacing = new(AimModVisualStyle.SectionSpacing, 16),
                Children = new Drawable[]
                {
                    identity = new Container
                    {
                        Width = identity_width,
                        Height = 64,
                        Children = new Drawable[]
                        {
                            avatar = new CircularContainer
                            {
                                Size = new(64), Masking = true,
                                Children = placeholderAvatar(),
                            },
                            new FillFlowContainer
                            {
                                Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 80,
                                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Padding = new MarginPadding { Right = 80 },
                                Direction = FillDirection.Vertical, Spacing = new(4),
                                Children = new Drawable[]
                                {
                                    name = Truncating("Loading...", new FontUsage(size: 20, weight: "SemiBold"), AimModPalette.Text),
                                    detail = Truncating(string.Empty, AimModVisualStyle.CaptionFont, AimModPalette.Muted),
                                    connect = new AimModButton("Connect osu! account", connectAccount, primary: true)
                                        { Height = AimModVisualStyle.CompactControlHeight, Alpha = 0, Margin = new MarginPadding { Top = 4 } },
                                },
                            },
                        },
                    },
                    metrics = new FillFlowContainer<AimModHomeMetric>
                    {
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Full,
                        Spacing = new(AimModVisualStyle.SectionSpacing, 12),
                        Children = new[]
                        {
                            pp = new AimModHomeMetric("Total PP"),
                            rank = new AimModHomeMetric("Global rank"),
                            country = new AimModHomeMetric("Country rank"),
                            accuracy = new AimModHomeMetric("Accuracy"),
                        },
                    },
                },
            });
        }

        private static Drawable[] placeholderAvatar() =>
        [
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
            new SpriteIcon { Anchor = Anchor.Centre, Origin = Anchor.Centre, Size = new(26), Icon = FontAwesome.Solid.User, Colour = AimModPalette.Muted },
        ];

        public void SetData(HomeDashboardData data)
        {
            OsuProfile? profile = data.Profile;
            OsuProfileStatistics? statistics = profile?.Statistics;
            bool connected = profile is not null;
            name.Text = data.PlayerName ?? "Not connected";
            detail.Text = connected
                ? profile!.CountryCode is { Length: > 0 } code ? $"{code} · osu! account" : "osu! account"
                : data.PlayerName is null ? "Connect osu! to see your rank and PP." : "Local plays only · connect to see rank and PP";
            connect.Alpha = connected ? 0 : 1;
            identity.Height = connected ? 64 : 92;
            metrics.Alpha = statistics is null ? 0 : 1;
            setAvatar(profile?.AvatarUrl);
            if (statistics is null)
                return;

            HomeProfileChange? change = data.ProfileChange;
            string since = change is null ? string.Empty : $"since {change.Since.ToLocalTime():d MMM}";
            pp.SetValue(statistics.PerformancePoints is { } total ? $"{total:N0}" : null, "pp");
            pp.SetTrend(AimModHomeMetric.Classify(change?.PpDelta, 0, .5), change?.PpDelta ?? 0,
                change?.PpDelta is { } ppDelta ? $"{ppDelta:+#,0;-#,0;0} pp" : string.Empty,
                change?.PpDelta is null ? "change shows from your next visit" : since);
            rank.SetValue(statistics.GlobalRank is > 0 ? $"#{statistics.GlobalRank:N0}" : null);
            rank.SetTrend(AimModHomeMetric.Classify(change?.RankDelta, 0, .5), change?.RankDelta ?? 0,
                change?.RankDelta is { } places ? $"{Math.Abs(places):N0}" : string.Empty,
                change?.RankDelta is { } moved ? moved == 0 ? $"no change {since}" : $"{(moved > 0 ? "places up" : "places down")} {since}" : string.Empty);
            country.SetValue(statistics.CountryRank is > 0 ? $"#{statistics.CountryRank:N0}" : null);
            country.SetTrend(AimModHomeTrend.None, 0, string.Empty, profile!.CountryCode ?? string.Empty);
            accuracy.SetValue(statistics.HitAccuracy is { } hit ? $"{normaliseAccuracy(hit):P2}" : null);
            accuracy.SetTrend(AimModHomeTrend.None, 0, string.Empty, $"{statistics.PlayCount:N0} plays all time");
        }

        /// <summary>The API reports accuracy either as 0–1 or as a percentage.</summary>
        internal static double normaliseAccuracy(double value) => value > 1 ? value / 100 : value;

        private void setAvatar(Uri? url)
        {
            if (url == avatarUrl)
                return;
            avatarUrl = url;
            avatar.Clear();
            avatar.AddRange(placeholderAvatar());
            if (url is not null)
                avatar.Add(new AimModOnlineArtworkHost(url));
        }

        protected override void Update()
        {
            base.Update();
            float width = DrawWidth - panel_padding * 2;
            if (!widthTracker.Update(width) || width <= 0)
                return;
            bool inline = width >= identity_width + AimModVisualStyle.SectionSpacing + 4 * 150 + 3 * AimModVisualStyle.SectionSpacing;
            identity.Width = inline ? identity_width : width;
            metrics.Width = inline ? width - identity_width - AimModVisualStyle.SectionSpacing : width;
            int columns = AimModLayout.ColumnsFor(metrics.Width, 150, 4);
            float metricWidth = (float)Math.Floor((metrics.Width - (columns - 1) * AimModVisualStyle.SectionSpacing) / columns);
            foreach (AimModHomeMetric metric in metrics)
                metric.Width = metricWidth;
        }
    }

    /// <summary>Recent form against the period before it, with a daily chart.</summary>
    internal partial class HomeFormPanel : HomePanel
    {
        private readonly AimModButton weekButton;
        private readonly AimModButton monthButton;
        private readonly FillFlowContainer<AimModHomeMetric> metrics;
        private readonly AimModHomeMetric plays;
        private readonly AimModHomeMetric accuracy;
        private readonly AimModHomeMetric misses;
        private readonly AimModHomeMetric best;
        private readonly FillFlowContainer chartBlock;
        private readonly AimModHomeTrendChart chart;
        private readonly FillFlowContainer empty;
        private readonly OsuSpriteText emptyTitle;
        private readonly TruncatingSpriteText emptyDetail;
        private HomeDashboardData? data;
        private int days = 7;
        private AimModLayout.ChangeTracker<float> widthTracker;

        public HomeFormPanel(HomeDashboardActions actions)
            : base("Your form")
        {
            HeaderActions.AddRange(new Drawable[]
            {
                weekButton = new AimModButton("7 days", () => select(7)) { Height = AimModVisualStyle.CompactControlHeight },
                monthButton = new AimModButton("30 days", () => select(30)) { Height = AimModVisualStyle.CompactControlHeight },
            });
            AddRange(new Drawable[]
            {
                metrics = new FillFlowContainer<AimModHomeMetric>
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Full,
                    Spacing = new(AimModVisualStyle.SectionSpacing, 16),
                    Children = new[]
                    {
                        plays = new AimModHomeMetric("Plays"),
                        accuracy = new AimModHomeMetric("Avg accuracy"),
                        misses = new AimModHomeMetric("Misses per play"),
                        best = new AimModHomeMetric("Best play"),
                    },
                },
                chartBlock = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                    Margin = new MarginPadding { Top = 8 },
                    Children = new Drawable[]
                    {
                        legend(),
                        chart = new AimModHomeTrendChart { Height = 128 },
                    },
                },
                empty = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                    Alpha = 0,
                    Children = new Drawable[]
                    {
                        emptyTitle = Text("No plays yet", AimModVisualStyle.TitleFont, AimModPalette.Text),
                        emptyDetail = Truncating("Play any map in osu!. AimMod reads your scores automatically.", AimModVisualStyle.BodyFont, AimModPalette.Muted),
                        new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(AimModVisualStyle.RelatedSpacing),
                            Margin = new MarginPadding { Top = 8 },
                            Children = new Drawable[]
                            {
                                new AimModButton("Browse beatmaps", actions.ShowBeatmaps, primary: true),
                                new AimModButton("Warm up in trainers", actions.ShowTrainers),
                            },
                        },
                    },
                },
            });
            select(7);
        }

        private static Drawable legend() => new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new(16),
            Children = new Drawable[]
            {
                legendItem(new Circle { Size = new(8), Colour = AimModPalette.Accent }, "Daily accuracy"),
                legendItem(new Box { Size = new(8), Colour = AimModPalette.CyanDark }, "Plays per day"),
                legendItem(new Box { Size = new(12, 2), Colour = AimModPalette.Muted }, "Period average"),
            },
        };

        private static Drawable legendItem(Drawable swatch, string label) => new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new(6),
            Children = new[]
            {
                swatch.With(d => { d.Anchor = Anchor.CentreLeft; d.Origin = Anchor.CentreLeft; }),
                Text(label, AimModVisualStyle.CaptionFont, AimModPalette.Muted).With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
            },
        };

        public void ShowLoading()
        {
            foreach (AimModHomeMetric metric in metrics)
            {
                metric.SetValue(null);
                metric.SetTrend(AimModHomeTrend.None, 0, string.Empty, string.Empty);
            }
            chart.SetData([], null);
            chart.ShowMessage("Loading your plays...");
        }

        public void ShowError()
        {
            foreach (AimModHomeMetric metric in metrics)
            {
                metric.SetValue(null);
                metric.SetTrend(AimModHomeTrend.None, 0, string.Empty, string.Empty);
            }
        }

        public void SetData(HomeDashboardData loaded)
        {
            data = loaded;
            select(days);
        }

        private void select(int period)
        {
            days = period;
            weekButton.SetSelected(period == 7);
            monthButton.SetSelected(period == 30);
            if (data is null)
                return;

            bool hasPlays = data.HasPlays;
            empty.Alpha = hasPlays ? 0 : 1;
            metrics.Alpha = chartBlock.Alpha = hasPlays ? 1 : 0;
            HeaderActions.Alpha = hasPlays ? 1 : 0;
            if (!hasPlays)
            {
                emptyTitle.Text = "No plays yet";
                return;
            }

            HomeFormSummary form = period == 7 ? data.Week : data.Month;
            string prior = form.Days == 7 ? "prev. week" : "prev. 30 days";
            plays.SetValue($"{form.Plays:N0}");
            plays.SetTrend(form.Plays == form.PreviousPlays ? AimModHomeTrend.Flat : AimModHomeTrend.Neutral, form.Plays - form.PreviousPlays,
                $"{form.Plays - form.PreviousPlays:+#,0;-#,0;0}", $"vs {form.PreviousPlays:N0} {prior}");

            accuracy.SetValue(form.Accuracy is { } acc ? $"{acc:P2}" : null);
            accuracy.SetTrend(AimModHomeMetric.Classify(form.Accuracy, form.PreviousAccuracy, .001), (form.Accuracy - form.PreviousAccuracy) ?? 0,
                form.Accuracy is { } a && form.PreviousAccuracy is { } b ? $"{(a - b) * 100:+0.00;-0.00;0.00}%" : string.Empty,
                form.PreviousAccuracy is { } before ? $"vs {before:P2} {prior}" : $"no finished plays {prior}");

            misses.SetValue(form.MissesPerPlay is { } miss ? $"{miss:0.0}" : null);
            misses.SetTrend(AimModHomeMetric.Classify(form.MissesPerPlay, form.PreviousMissesPerPlay, .1, higherIsBetter: false),
                (form.MissesPerPlay - form.PreviousMissesPerPlay) ?? 0,
                form.MissesPerPlay is { } m && form.PreviousMissesPerPlay is { } p ? $"{m - p:+0.0;-0.0;0.0}" : string.Empty,
                form.PreviousMissesPerPlay is { } earlier ? $"vs {earlier:0.0} {prior}" : $"no finished plays {prior}");

            if (form.TopPlay is { PerformancePoints: { } topPp } top)
            {
                best.SetValue($"{topPp:N0}", "pp");
                best.SetTrend(AimModHomeTrend.None, 0, string.Empty, $"{top.Title} · {top.Accuracy:P1}");
            }
            else
            {
                best.SetValue(null);
                best.SetTrend(AimModHomeTrend.None, 0, string.Empty, "no PP recorded this period");
            }

            chart.SetData(form.Daily.Select(point => new AimModHomeTrendPoint(point.Day, point.Plays, point.Accuracy)).ToArray(), form.Accuracy);
        }

        protected override void Update()
        {
            base.Update();
            float width = DrawWidth - panel_padding * 2;
            if (!widthTracker.Update(width) || width <= 0)
                return;
            int columns = width >= 4 * 132 + 3 * AimModVisualStyle.SectionSpacing ? 4 : 2;
            float metricWidth = (float)Math.Floor((width - (columns - 1) * AimModVisualStyle.SectionSpacing) / columns);
            foreach (AimModHomeMetric metric in metrics)
                metric.Width = metricWidth;
        }
    }

    /// <summary>Two or three concrete next steps, each with one action.</summary>
    internal partial class HomeNextUpPanel : HomePanel
    {
        private readonly Action<HomeRecommendation> run;

        public HomeNextUpPanel(Action<HomeRecommendation> run)
            : base("Next up")
        {
            this.run = run;
        }

        public void SetTitle(string title) => TitleText.Text = title;

        public void SetData(IReadOnlyList<HomeRecommendation> recommendations)
        {
            Clear();
            for (int i = 0; i < recommendations.Count; i++)
            {
                HomeRecommendation recommendation = recommendations[i];
                Add(new HomeRecommendationCard(recommendation, () => run(recommendation), primary: i == 0));
            }
        }
    }

    internal partial class HomeRecommendationCard : Container
    {
        private readonly AimModButton action;
        private readonly FillFlowContainer labels;

        public HomeRecommendationCard(HomeRecommendation recommendation, Action run, bool primary)
        {
            RelativeSizeAxes = Axes.X;
            Height = 72;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            (IconUsage icon, Colour4 colour) = style(recommendation.Kind);
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new Container
                {
                    Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 14, Size = new(36),
                    Children = new Drawable[]
                    {
                        new Circle { RelativeSizeAxes = Axes.Both, Colour = colour, Alpha = .16f },
                        new SpriteIcon { Anchor = Anchor.Centre, Origin = Anchor.Centre, Size = new(15), Icon = icon, Colour = colour },
                    },
                },
                labels = new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 64,
                    AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(2),
                    Children = new Drawable[]
                    {
                        Text(recommendation.Label, AimModVisualStyle.CaptionStrongFont, colour),
                        new TruncatingSpriteText { Text = recommendation.Value, Font = new FontUsage(size: 15, weight: "SemiBold"), Colour = AimModPalette.Text, RelativeSizeAxes = Axes.X },
                        new TruncatingSpriteText { Text = recommendation.Detail, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted, RelativeSizeAxes = Axes.X },
                    },
                },
                action = new AimModButton(recommendation.ActionLabel, run, primary)
                {
                    Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -14, Height = AimModVisualStyle.CompactControlHeight,
                },
            };
        }

        private static (IconUsage, Colour4) style(HomeRecommendationKind kind) => kind switch
        {
            HomeRecommendationKind.ContinueCoaching or HomeRecommendationKind.PractiseMap => (FontAwesome.Solid.Bullseye, AimModPalette.Accent),
            HomeRecommendationKind.PpTarget or HomeRecommendationKind.FindPpTargets => (FontAwesome.Solid.Crosshairs, AimModPalette.Cyan),
            HomeRecommendationKind.PlayFirstMap => (FontAwesome.Solid.Music, AimModPalette.Accent),
            _ => (FontAwesome.Solid.Keyboard, AimModPalette.Cyan),
        };

        protected override void Update()
        {
            base.Update();
            labels.Width = Math.Max(40, DrawWidth - 64 - action.DrawWidth - 28);
        }
    }

    /// <summary>The latest plays in one compact table: labels once in the header row, values in the rows.</summary>
    internal partial class HomeRecentPlaysPanel : HomePanel
    {
        private readonly FillFlowContainer rows;
        private readonly OsuSpriteText baseline;
        private readonly Action<LocalReplay> openReplay;

        public HomeRecentPlaysPanel(HomeDashboardActions actions)
            : base("Recent plays")
        {
            openReplay = actions.OpenReplay;
            HeaderActions.AddRange(new Drawable[]
            {
                baseline = Text(string.Empty, AimModVisualStyle.CaptionFont, AimModPalette.Muted).With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
                new AimModButton("All plays", actions.ShowStatistics) { Height = AimModVisualStyle.CompactControlHeight },
            });
            AddRange(new Drawable[]
            {
                new HomePlayRow(null, null, null, DateTimeOffset.Now),
                rows = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(4),
                },
            });
        }

        public void SetData(HomeDashboardData data, DateTimeOffset now)
        {
            baseline.Text = data.RecentAccuracyBaseline is { } average ? $"30-day avg {average:P2}" : string.Empty;
            rows.Clear();
            foreach (LocalReplay play in data.RecentPlays)
                rows.Add(new HomePlayRow(play, data.RecentAccuracyBaseline, play.HasReplayFile && play.IsLocallyStored ? openReplay : null, now));
        }
    }

    internal partial class HomePlayRow : Container
    {
        internal const float accuracy_width = 64;
        internal const float misses_width = 52;
        internal const float pp_width = 44;
        internal const float when_width = 70;
        internal const float action_width = 72;
        private readonly Container title;
        private readonly Container columns;
        private AimModLayout.ChangeTracker<float> widthTracker;

        /// <summary>A null play renders the column header.</summary>
        public HomePlayRow(LocalReplay? play, double? baseline, Action<LocalReplay>? open, DateTimeOffset now)
        {
            RelativeSizeAxes = Axes.X;
            Height = play is null ? 18 : 40;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            bool header = play is null;
            Colour4 headerColour = AimModPalette.Muted;
            FontUsage headerFont = AimModVisualStyle.CaptionStrongFont;

            Drawable cell(string text, Colour4 colour, float right, float width, FontUsage? font = null) => new Container
            {
                Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -right, Width = width, RelativeSizeAxes = Axes.Y,
                Child = Text(text, font ?? (header ? headerFont : AimModVisualStyle.BodyStrongFont), header ? headerColour : colour)
                    .With(t => { t.Anchor = Anchor.CentreRight; t.Origin = Anchor.CentreRight; }),
            };

            float r = 8 + action_width + 8;
            columns = new Container { RelativeSizeAxes = Axes.Both };
            if (header)
            {
                columns.AddRange(new[]
                {
                    cell("When", headerColour, r, when_width),
                    cell("PP", headerColour, r + when_width, pp_width),
                    cell("Misses", headerColour, r + when_width + pp_width, misses_width),
                    cell("Accuracy", headerColour, r + when_width + pp_width + misses_width, accuracy_width),
                });
                title = new Container
                {
                    RelativeSizeAxes = Axes.Y, X = 8,
                    Child = Text("Map", headerFont, headerColour).With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
                };
                Children = new Drawable[] { title, columns };
                return;
            }

            LocalReplay run = play!;
            Colour4 accuracyColour = !run.Passed ? AimModPalette.Muted
                : baseline is { } average && run.Accuracy >= average ? AimModPalette.Accent
                : baseline is { } lower && run.Accuracy < lower - .01 ? AimModPalette.Yellow
                : AimModPalette.Text;
            string missText = !run.Passed ? "Failed" : run.MissCount == 0 ? "FC" : $"{run.MissCount}";
            Colour4 missColour = !run.Passed ? AimModPalette.Yellow : run.MissCount == 0 ? AimModPalette.Accent : AimModPalette.Text;
            columns.AddRange(new[]
            {
                cell(HomeDashboardBuilder.Ago(run.PlayedAt, now), AimModPalette.Muted, r, when_width, AimModVisualStyle.CaptionFont),
                cell(run.PerformancePoints is { } pp ? $"{pp:N0}" : "—", run.PerformancePoints is null ? AimModPalette.Muted : AimModPalette.Text, r + when_width, pp_width),
                cell(missText, missColour, r + when_width + pp_width, misses_width),
                cell($"{run.Accuracy:P2}", accuracyColour, r + when_width + pp_width + misses_width, accuracy_width),
            });
            if (open is not null)
                columns.Add(new AimModButton("Replay", () => open(run))
                {
                    Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -8, Height = 28,
                });

            title = new Container
            {
                RelativeSizeAxes = Axes.Y, X = 8,
                Children = new Drawable[]
                {
                    new AimModDifficultyPill(run.StarRating) { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                    new TruncatingSpriteText
                    {
                        Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 64,
                        Text = string.IsNullOrWhiteSpace(run.Difficulty) ? run.Title : $"{run.Title} [{run.Difficulty}]",
                        Font = AimModVisualStyle.BodyStrongFont, Colour = AimModPalette.Text,
                    },
                },
            };
            Children = new Drawable[] { new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised }, title, columns };
        }

        protected override void Update()
        {
            base.Update();
            if (!widthTracker.Update(DrawWidth))
                return;
            float fixedColumns = 8 + action_width + 8 + when_width + pp_width + misses_width + accuracy_width + 12;
            title.Width = Math.Max(60, DrawWidth - fixedColumns - 8);
            if (title.Children.OfType<TruncatingSpriteText>().FirstOrDefault() is { } name)
                name.MaxWidth = Math.Max(10, title.Width - 72);
        }
    }

    /// <summary>Small links to every area, secondary to the personal content above.</summary>
    internal partial class HomeShortcutsPanel : HomePanel
    {
        private readonly FillFlowContainer<HomeShortcut> grid;
        private AimModLayout.ChangeTracker<float> widthTracker;

        public HomeShortcutsPanel(HomeDashboardActions actions)
            : base("Go to")
        {
            Add(grid = new FillFlowContainer<HomeShortcut>
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Full,
                Spacing = new(AimModVisualStyle.RelatedSpacing),
                Children = new[]
                {
                    new HomeShortcut("Beatmaps", "Find and install maps", FontAwesome.Solid.Music, actions.ShowBeatmaps),
                    new HomeShortcut("Replays", "Watch your plays", FontAwesome.Solid.PlayCircle, actions.ShowReplays),
                    new HomeShortcut("Statistics", "Progress over time", FontAwesome.Solid.ChartLine, actions.ShowStatistics),
                    new HomeShortcut("Coaching", "Fix a hard section", FontAwesome.Solid.Bullseye, actions.ShowCoaching),
                    new HomeShortcut("Trainers", "Aim, tapping, reading", FontAwesome.Solid.Keyboard, actions.ShowTrainers),
                    new HomeShortcut("PP targets", "Maps worth PP to you", FontAwesome.Solid.Crosshairs, actions.ShowPpTargets),
                    new HomeShortcut("Skins", "Change your skin", FontAwesome.Solid.PaintBrush, actions.ShowSkins),
                    new HomeShortcut("Settings", "Account and setup", FontAwesome.Solid.Cog, actions.ShowSettings),
                },
            });
        }

        protected override void Update()
        {
            base.Update();
            float width = DrawWidth - panel_padding * 2;
            if (!widthTracker.Update(width) || width <= 0)
                return;
            int columns = AimModLayout.ColumnsFor(width, 150, 4);
            float tile = (float)Math.Floor((width - (columns - 1) * AimModVisualStyle.RelatedSpacing) / columns);
            foreach (HomeShortcut shortcut in grid)
                shortcut.Width = tile;
        }
    }

    internal partial class HomeShortcut : AimModInteractiveSurface, IHasTooltip
    {
        public HomeShortcut(string title, string description, IconUsage icon, Action action)
        {
            Height = 48;
            Action = action;
            BackgroundColour = AimModPalette.PanelRaised;
            TooltipText = description;
            Children = new Drawable[]
            {
                new SpriteIcon { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 14, Size = new(15), Icon = icon, Colour = AimModPalette.Accent },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 42, RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Right = 50 }, Direction = FillDirection.Vertical, Spacing = new(1),
                    Children = new Drawable[]
                    {
                        Text(title, AimModVisualStyle.BodyStrongFont, AimModPalette.Text),
                        new TruncatingSpriteText { Text = description, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted, RelativeSizeAxes = Axes.X },
                    },
                },
            };
        }

        public LocalisableString TooltipText { get; }
    }
}
