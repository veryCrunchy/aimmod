using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Trainers;

/// <summary>
/// Choices on the left, a sticky session summary on the right. Narrow windows stack the summary below the
/// choices. Either way the session actions stick to the bottom edge, so Start is always reachable.
/// </summary>
public partial class NativeTrainersWorkspace
{
    private const float wideLayoutWidth = 940, cardGap = 24;
    private readonly Container pageBody;
    private readonly Container actionsLayer;
    private readonly FillFlowContainer<Drawable> choices, progress;
    private Container sessionCard = null!, sessionHeader = null!, actionsHost = null!, actionsSpacer = null!;
    private Box actionsBackground = null!, actionsDivider = null!;
    private FillFlowContainer<Drawable> sessionBody = null!, sessionActions = null!, planGauges = null!, planChips = null!, inputChips = null!;
    private OsuSpriteText sessionTitle = null!, sessionSubtitle = null!, planHeading = null!;
    private Drawable usualLegend = null!;
    private FillFlowContainer planBasis = null!;
    private TrainerRecentRunsLink recentRunsLink = null!;
    private AimModSubsectionHeader historyHeader = null!;
    private readonly List<ITrainerMenu> cardMenus = [];
    private string inputSource = "Default controls";

    private void buildSessionCard()
    {
        sessionBody = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical,
            Spacing = new(12), Padding = new MarginPadding(16) };
        setup.Add(sessionCard = new Container { AutoSizeAxes = Axes.Y, Depth = -1, Children = [
            new Container { RelativeSizeAxes = Axes.Both, Masking = true, CornerRadius = AimModVisualStyle.CardRadius, BorderThickness = 1,
                BorderColour = AimModPalette.Border, Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel } },
            sessionBody,
        ] });
        var heading = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(3),
            Padding = new MarginPadding { Right = 150, Top = 6 } };
        heading.Add(sessionTitle = new OsuSpriteText { Text = "Your session", Font = new FontUsage(size: 18, weight: "SemiBold"), Colour = AimModPalette.Text });
        heading.Add(sessionSubtitle = new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Font = new FontUsage(size: 13), Colour = AimModPalette.Muted });
        sessionBody.Add(sessionHeader = new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Depth = -10, Child = heading });
        sessionBody.Add(recentRunsLink = new TrainerRecentRunsLink(() => contentScroll.ScrollTo(Math.Max(0, setup.Y + progress.Y - 8))) { Margin = new MarginPadding { Top = -4 } });
        var plan = column(); plan.Spacing = new(8);
        plan.Add(new Container { RelativeSizeAxes = Axes.X, Height = 14, Children = [
            planHeading = new OsuSpriteText { Font = AimModVisualStyle.CaptionStrongFont, Colour = AimModPalette.Muted },
            usualLegend = new FillFlowContainer { AutoSizeAxes = Axes.Both, Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Direction = FillDirection.Horizontal,
                Spacing = new(5), Children = [
                    new Box { Width = 2, Height = 9, Colour = AimModPalette.Text, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                    new OsuSpriteText { Text = "your usual", Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                ] },
        ] });
        plan.Add(planGauges = flow());
        plan.Add(planBasis = new FillFlowContainer { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(6) });
        plan.Add(new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Children = [
            new OsuSpriteText { Text = "OBJECTS", Y = 6, Font = AimModVisualStyle.CaptionStrongFont, Colour = AimModPalette.Muted },
            new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Padding = new MarginPadding { Left = 64 }, Child = planChips = flow() },
        ] });
        planChips.Spacing = new(6);
        sessionBody.Add(plan);
    }

    private void buildSessionActions()
    {
        sessionActions = column(); sessionActions.Spacing = new(8);
        // One primary action, with the less common setup action beside it.
        var primary = new Container { RelativeSizeAxes = Axes.X, Height = 44, Children = [
            new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Right = 152 }, Children = [start, stop] }, advancedToggle] };
        stop.Anchor = stop.Origin = Anchor.CentreRight; stop.Height = 44;
        advancedToggle.Anchor = advancedToggle.Origin = Anchor.CentreRight;
        advancedToggle.AutoSizeAxes = Axes.None; advancedToggle.Width = 144; advancedToggle.Height = 44;
        sessionActions.Add(primary);
        sessionActions.Add(inputChips = flow());
        inputChips.Spacing = new(6);
        sessionActions.Add(status = paragraph("Four-beat count-in. Escape ends the session."));
        status.Colour = AimModPalette.Muted;
        // The actions live above the scroll so they can stick to the bottom edge; the card keeps a matching gap.
        sessionBody.Add(actionsSpacer = new Container { RelativeSizeAxes = Axes.X });
        actionsLayer.Add(actionsHost = new Container { AutoSizeAxes = Axes.Y, Children = [
            actionsBackground = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Header, Alpha = 0 },
            actionsDivider = new Box { RelativeSizeAxes = Axes.X, Height = 1, Colour = AimModPalette.Border, Alpha = 0 },
            new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Padding = new MarginPadding { Horizontal = 16, Vertical = 12 }, Child = sessionActions },
        ] });
        refreshInputChips();
    }

    private static Drawable stretch(Drawable field, float fraction)
    {
        field.RelativeSizeAxes = Axes.X; field.Width = fraction;
        if (field is Container container && fraction < 1) container.Padding = new MarginPadding { Right = 8 };
        return field;
    }

    private void updateLayout()
    {
        float width = pageBody.DrawWidth - pageBody.Padding.Right;
        if (width <= 0) return;
        bool wide = width >= wideLayoutWidth;
        float cardWidth = wide ? Math.Clamp(width * .36f, 340, 430) : width;
        float choicesWidth = wide ? width - cardWidth - cardGap : width;
        if (Math.Abs(choices.Width - choicesWidth) > .5f) { choices.Width = choicesWidth; progress.Width = choicesWidth; }
        if (Math.Abs(sessionCard.Width - cardWidth) > .5f) sessionCard.Width = cardWidth;
        float gaugeWidth = (cardWidth - 32 - 16) / 3 - .01f;
        foreach (var gauge in planGauges)
            if (Math.Abs(gauge.Width - gaugeWidth) > .5f) gauge.Width = gaugeWidth;
        layoutChoiceGrids(choicesWidth);
        float actionsHeight = Math.Max(0, actionsHost.DrawHeight - 24 - 12);
        if (Math.Abs(actionsSpacer.Height - actionsHeight) > .5f) actionsSpacer.Height = actionsHeight;
        float choicesHeight = choices.Alpha > 0 ? choices.DrawHeight : 0;
        if (wide)
        {
            sessionCard.X = choicesWidth + cardGap;
            progress.Y = choicesHeight + cardGap;
            float leftHeight = progress.Y + progress.DrawHeight;
            // Sticky summary: follow the scroll position while it fits, otherwise align its end with the viewport.
            float viewport = contentScroll.DrawHeight;
            float card = sessionCard.DrawHeight;
            float scrolled = (float)contentScroll.Current - (card > viewport ? card - viewport : 0);
            float y = Math.Clamp(scrolled, 0, Math.Max(0, leftHeight - card));
            if (Math.Abs(sessionCard.Y - y) > .5f) sessionCard.Y = y;
            setHeight(Math.Max(leftHeight, card));
        }
        else
        {
            sessionCard.X = 0;
            sessionCard.Y = choicesHeight + cardGap;
            progress.Y = sessionCard.Y + sessionCard.DrawHeight + cardGap;
            // Leave room to scroll the end of the page above the pinned actions.
            setHeight(progress.Y + progress.DrawHeight + actionsHost.DrawHeight);
        }
        placeActions();
    }

    private void placeActions()
    {
        bool visible = skillPage.Alpha > 0 && setup.Alpha > 0 && setup.IsPresent && !showingResults && actionsSpacer.IsPresent;
        // An open menu in the card may extend under the pinned actions. Get out of its way until it closes.
        bool menuOpen = cardMenus.Any(m => m.MenuOpen);
        actionsHost.Alpha = visible && !menuOpen ? 1 : 0;
        if (!visible) return;
        var natural = actionsLayer.ToLocalSpace(actionsSpacer.ScreenSpaceDrawQuad.TopLeft);
        float x = natural.X - 16, width = actionsSpacer.DrawWidth + 32, y = natural.Y - 12;
        float limit = actionsLayer.DrawHeight - actionsHost.DrawHeight;
        bool pinned = y > limit + .5f;
        if (Math.Abs(actionsHost.X - x) > .5f) actionsHost.X = x;
        if (Math.Abs(actionsHost.Width - width) > .5f) actionsHost.Width = width;
        float target = pinned ? limit : y;
        if (Math.Abs(actionsHost.Y - target) > .25f) actionsHost.Y = target;
        actionsBackground.Alpha = actionsDivider.Alpha = pinned ? 1 : 0;
    }

    private void setHeight(float height)
    {
        height += 24;
        if (Math.Abs(setup.Height - height) > .5f) setup.Height = height;
        float total = (results.Alpha > 0 && results.IsPresent ? results.DrawHeight : 0) + (setup.Alpha > 0 ? setup.Height : 0);
        setup.Y = results.Alpha > 0 && results.IsPresent ? results.DrawHeight : 0;
        if (Math.Abs(pageBody.Height - total) > .5f) pageBody.Height = total;
    }

    private void layoutChoiceGrids(float width)
    {
        int skillColumns = width >= 8 * 104 + 56 ? 8 : width >= 4 * 120 + 24 ? 4 : 2;
        grid(exerciseButtons.Values, skillColumns, width);
        // Prefer rows without gaps: 4 or 2 across for four drills, 3 across for six.
        int count = Math.Max(1, presetButtons.Count);
        int[] options = count % 4 == 0 ? [4, 2] : count % 3 == 0 ? [3, 2] : [4, 3, 2];
        int presetColumns = options.FirstOrDefault(c => (width - (c - 1) * 8) / c >= 140, 2);
        grid(presetButtons.Select(p => p.Button), Math.Min(presetColumns, count), width);
        int visibleIntents = intentButtons.Values.Count(b => b.Alpha > 0);
        grid(intentButtons.Values, width >= 3 * 180 + 16 ? Math.Max(1, visibleIntents) : 1, width);
    }

    private static void grid(IEnumerable<AimModButton> buttons, int columns, float width)
    {
        float tile = (width - (columns - 1) * 8) / columns - .01f;
        foreach (var button in buttons)
            if (Math.Abs(button.Width - tile) > .5f) button.Width = tile;
    }

    private void refreshSessionPlan()
    {
        if (planGauges is null) return;
        var planned = adaptiveSettings(settings);
        bool automatic = preferences.AdaptiveDifficulty;
        string drill = presetButtons.FirstOrDefault(p => p.Preset.Matches(settings)).Preset?.Title ?? "Custom drill";
        sessionSubtitle.Text = $"{DisplayName(settings.Kind)} · {drill}";
        planHeading.Text = automatic ? "PLANNED FOR THIS RUN" : "YOUR SETTINGS";
        planGauges.Clear(); planChips.Clear(); planBasis.Clear();
        var typical = usual(settings.Kind);
        usualLegend.Alpha = typical is null ? 0 : 1;
        foreach (var gauge in gauges(planned, typical, manual: !automatic)) planGauges.Add(gauge);
        foreach (var gauge in planGauges) gauge.Width = Math.Max(90, (sessionCard.Width - 48) / 3 - .01f);

        var limits = planned.SkillLimits;
        string basis = !automatic ? "Manual · exact settings in Adjust patterns"
            : settings.Kind is TrainerKind.Reaction or TrainerKind.Spinner ? "Adapts after each run"
            : limits is { EvidenceCount: > 0 } ? $"Matched to {limits.EvidenceCount} recent plays"
            : settings.MinimumStars is not null ? "Starting from your star range" : "Starting gently · no runs yet";
        planBasis.Add(new SpriteIcon { Icon = automatic ? FontAwesome.Solid.ChartLine : FontAwesome.Solid.SlidersH, Size = new(11),
            Colour = automatic ? AimModPalette.Accent : AimModPalette.Muted, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft });
        planBasis.Add(new OsuSpriteText { Text = basis, Font = new FontUsage(size: 12), Colour = AimModPalette.Muted, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft });
        foreach (var chip in objectMix(planned)) planChips.Add(chip);
    }

    /// <summary>Typical values over the player's recent completed runs of this skill, or null without history.</summary>
    private sealed record UsualValues(double? Bpm, double? Nps, double? Jump, double? Ar, double? Cs, double? Od, double? Window, double? Spin);

    private UsualValues? usual(TrainerKind kind)
    {
        var now = DateTimeOffset.UtcNow;
        var runs = history().Load().Where(r => r.Settings.Kind == kind && !r.Assisted && r.WarmupRun is null && r.CompletedAt >= now.AddDays(-30))
            .Take(12).ToArray();
        if (runs.Length < 3) return null;
        double? median(IEnumerable<double> values) { var a = values.Where(double.IsFinite).ToArray(); return a.Length == 0 ? null : TrainerPerformance.Median(a); }
        return new(median(runs.Where(r => r.Settings.Music != "song").Select(r => (double)r.Settings.Bpm)), median(runs.Where(r => r.Demand is not null).Select(r => r.Demand!.PeakNps)),
            median(runs.Where(r => r.Demand is not null).Select(r => r.Demand!.JumpDistance)), median(runs.Select(r => (double)r.Settings.ApproachRate)),
            median(runs.Select(r => (double)r.Settings.CircleSize)), median(runs.Select(r => r.Settings.OverallDifficulty)),
            median(runs.Select(r => (double)r.Settings.ReactionWindowMs)), median(runs.Select(r => (double)r.Settings.SpinnerSeconds)));
    }

    private static string usualTip(string tip, double? value, string format) => value is { } v ? $"{tip} Your usual: {v.ToString(format)}." : tip;

    private IEnumerable<AimModTrainerGauge> gauges(TrainerSettings planned, UsualValues? usual = null, bool manual = false)
    {
        if (planned.Kind == TrainerKind.Reaction)
        {
            yield return new("WINDOW", $"{planned.ReactionWindowMs} ms", 1 - (planned.ReactionWindowMs - 600) / 1400.0,
                usualTip("How long each cue accepts a response. Shorter is harder.", usual?.Window, "0 ms"), 1 - (usual?.Window - 600) / 1400.0);
            yield return new("DELAY", planned.ReactionDelay.ToString(), (int)planned.ReactionDelay / 3.0, "Wait before each visual cue.");
            yield return new("LENGTH", $"{planned.Seconds} s", planned.Seconds / 180.0);
            yield break;
        }
        if (planned.Kind == TrainerKind.Spinner)
        {
            yield return new("SPIN", $"{planned.SpinnerSeconds} s", planned.SpinnerSeconds / 6.0, usualTip("Length of each spinner, up to 6 s.", usual?.Spin, "0 s"), usual?.Spin / 6.0);
            yield return new("OD", $"{planned.OverallDifficulty:0.##}", planned.OverallDifficulty / 10, usualTip("Overall difficulty, 0–10: how precisely spins are judged.", usual?.Od, "0.##"), usual?.Od / 10);
            yield return new("LENGTH", $"{planned.Seconds} s", planned.Seconds / 180.0);
            yield break;
        }
        var limits = planned.SkillLimits;
        bool song = planned.Music == "song";
        if (manual && limits is null)
        {
            // Manual runs keep exact choices, so show those rather than adaptive limits.
            int perBeat = planned.NoteSpeed switch { TrainerNoteSpeed.OnePerBeat => 1, TrainerNoteSpeed.TwoPerBeat => 2, TrainerNoteSpeed.FourPerBeat => 4, _ => 0 };
            yield return new("TEMPO", song ? "Song" : $"{planned.Bpm} BPM", song ? 1 : (planned.Bpm - 60) / 180.0,
                usualTip("Beats per minute, on a 60–240 scale.", usual?.Bpm, "0 BPM"), song ? null : (usual?.Bpm - 60) / 180.0);
            yield return new("NOTES / BEAT", perBeat == 0 ? "Drill" : $"{perBeat}", perBeat == 0 ? .5 : perBeat / 4.0, "Notes per beat. Drill uses the preset's own rhythm.");
            yield return new("SPACING", $"{planned.AimSpacing}%", planned.AimSpacing / 140.0, "Jump distance compared with normal spacing for this drill (70–140%).");
            yield return new("AR", $"{planned.ApproachRate}", planned.ApproachRate / 10.0, usualTip("Approach rate, 0–10: higher gives less time to read each note.", usual?.Ar, "0.#"), usual?.Ar / 10);
            yield return new("CS", $"{planned.CircleSize}", planned.CircleSize / 7.0, usualTip("Circle size, 0–7: higher means smaller targets.", usual?.Cs, "0.#"), usual?.Cs / 7);
            yield return new("OD", $"{planned.OverallDifficulty:0.##}", planned.OverallDifficulty / 10, usualTip("Overall difficulty, 0–10: higher means tighter timing windows.", usual?.Od, "0.##"), usual?.Od / 10);
            yield break;
        }
        yield return new("TEMPO", song ? "Song" : $"{planned.Bpm} BPM", song ? 1 : (planned.Bpm - 60) / 180.0,
            usualTip("Beats per minute, on a 60–240 scale.", usual?.Bpm, "0 BPM"), song ? null : (usual?.Bpm - 60) / 180.0);
        yield return new("TAPS / S", limits is null ? "--" : $"{limits.MaxNps:0.#}", (limits?.MaxNps ?? 0) / 8,
            usualTip("Highest note rate in this run, on a 0–8 scale.", usual?.Nps, "0.#"), usual?.Nps / 8);
        yield return new("JUMPS", limits is null ? "--" : $"{limits.MaxJumpDistance:0} px", (limits?.MaxJumpDistance ?? 0) / 350,
            usualTip("Longest cursor distance between notes, in osu! pixels (0–350).", usual?.Jump, "0 px"), usual?.Jump / 350);
        yield return new("AR", $"{planned.ApproachRate}", planned.ApproachRate / 10.0, usualTip("Approach rate, 0–10: higher gives less time to read each note.", usual?.Ar, "0.#"), usual?.Ar / 10);
        yield return new("CS", $"{planned.CircleSize}", planned.CircleSize / 7.0, usualTip("Circle size, 0–7: higher means smaller targets.", usual?.Cs, "0.#"), usual?.Cs / 7);
        yield return new("OD", $"{planned.OverallDifficulty:0.##}", planned.OverallDifficulty / 10, usualTip("Overall difficulty, 0–10: higher means tighter timing windows.", usual?.Od, "0.##"), usual?.Od / 10);
    }

    private static IEnumerable<AimModTrainerChip> objectMix(TrainerSettings planned)
    {
        if (planned.Kind == TrainerKind.Reaction)
        {
            yield return new(ReactionSession.Name(planned.ReactionMode), FontAwesome.Solid.Bolt);
            yield break;
        }
        if (planned.Kind != TrainerKind.Spinner) yield return new("Circles", FontAwesome.Regular.Circle);
        if (planned.Sliders != TrainerSliderStyle.None && planned.Kind != TrainerKind.Spinner)
            yield return new($"Sliders · up to {planned.SliderBeats} beat{(planned.SliderBeats == 1 ? "" : "s")}", FontAwesome.Solid.GripLines, tooltip: "Slider holds follow held notes in the music.");
        if (planned.Spinners != TrainerSpinnerFrequency.None || planned.Kind == TrainerKind.Spinner)
            yield return new($"Spinners · {planned.SpinnerSeconds} s", FontAwesome.Solid.Redo);
        if (planned.Kind == TrainerKind.Reading && planned.ReadingHidden) yield return new("Hidden", FontAwesome.Solid.EyeSlash);
        if (planned.GuidedCues) yield return new("Guide cues", FontAwesome.Solid.CommentDots, AimModTrainerChipTone.Info);
    }

    private void refreshInputChips()
    {
        if (inputChips is null) return;
        inputChips.Clear();
        inputSource = customSettings ? "Custom" : inheritedSettings?.Source ?? "Default";
        inputChips.Add(new AimModTrainerChip(settings.Keys, FontAwesome.Solid.Keyboard, tooltip: "Tapping keys"));
        inputChips.Add(new AimModTrainerChip($"{settings.OffsetMs:+0;-0;0} ms", FontAwesome.Solid.Stopwatch, tooltip: "Audio offset. Uses the same sign as osu!."));
        if (settings.Kind != TrainerKind.Reaction)
            inputChips.Add(new AimModTrainerChip(mouseButtons ? "Mouse on" : "Mouse off", FontAwesome.Solid.MousePointer,
                tooltip: mouseButtons ? "Mouse buttons also tap." : "Mouse buttons are off, as in osu!."));
        if (!customSettings && inheritedSettings?.Input is { } input)
            inputChips.Add(input.Tablet is { Enabled: true } tablet
                ? new AimModTrainerChip($"Tablet {tablet.AreaSize.X:0.#} × {tablet.AreaSize.Y:0.#} mm", FontAwesome.Solid.PenNib)
                : new AimModTrainerChip($"Sensitivity {input.Sensitivity:0.##}x", FontAwesome.Solid.MousePointer, tooltip: "External tablet drivers keep their own mapping."));
        inputChips.Add(new AimModTrainerChip(customSettings ? "Custom" : inheritedSettings is null ? "Defaults" : $"From {inputSource}",
            tone: AimModTrainerChipTone.Info, tooltip: customSettings ? "You changed the controls here. Restore them under Controls & audio."
                : inheritedSettings is null ? "osu! is not connected yet, so default keys and offset are used." : "Keys and offset follow your osu! settings."));
    }
}
