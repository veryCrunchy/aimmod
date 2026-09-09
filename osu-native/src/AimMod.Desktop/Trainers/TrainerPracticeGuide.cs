using osu.Game.Rulesets.Osu.Objects;

namespace AimMod.Desktop.Trainers;

public sealed record TrainerGuideCue(string Stage, string Text, bool Visible = true);

public static class TrainerPracticeGuide
{
    public static TrainerGuideCue At(TrainerSettings settings, IReadOnlyList<OsuHitObject> objects, double time)
    {
        if (!settings.GuidedCues || objects.Count == 0) return new("", "", false);
        double start = objects[0].StartTime, end = objects.Max(TrainerSpinners.End);
        double testStart = start + (end - start) * .75;
        if (time >= testStart) return new("YOUR TURN", "Keep the same control. Finish this part without prompts.", time < testStart + 2000);
        if (time < start) return new("GET READY", settings.Kind == TrainerKind.Spinner
            ? $"Hold either {settings.Keys}. Circle the centre in one direction. The example shows the movement, not a target size."
            : settings.Kind == TrainerKind.Reading ? "Read the numbers and approach circles. Follow each slider through its tail."
            : settings.Kind == TrainerKind.Aim ? "Land on the target, tap, then move to the next one."
            : $"Listen to the count-in. Use {settings.Keys}; keep your fingers relaxed.");
        var current = objects.FirstOrDefault(o => TrainerSpinners.End(o) >= time - 100);
        if (current is null) return new("FINISH", "Let the last note finish before relaxing.");
        if (current.StartTime - time > 1100) return new("RECOVER", "Relax your hand. Find the next entry before it arrives.");
        if (current is Spinner) return new("SPIN", "Hold a key. Keep one direction and an even circle through the finish.");
        if (current is Slider slider) return new(slider.RepeatCount > 0 ? "FOLLOW THE REPEAT" : "HOLD TO THE TAIL",
            slider.RepeatCount > 0 ? "Keep holding as the slider reverses. Read the next circle before leaving."
                : "Tap the head, keep holding and follow the ball to the tail.");
        bool later = time > start + (end - start) * .4;
        return new(later ? "KEEP IT CONTROLLED" : "FIND THE PATTERN", settings.Kind switch
        {
            TrainerKind.Steady => later ? "Listen for the same spacing between taps. Avoid speeding up near the end." : "One tap per circle. Match the approach circle to the music.",
            TrainerKind.Alternating => later ? "Keep the last few taps as even as the first. Relax during the gap." : "Alternate your two keys with even spacing.",
            TrainerKind.Bursts => later ? "Finish the last tap, then release the tension before the next burst." : "Read the whole group. Tap every note, including the last one.",
            TrainerKind.Rhythm => later ? "Keep the beat through the rest. Let the note spacing tell you when to speed up." : "Listen for the change in rhythm before adding more taps.",
            TrainerKind.Aim => later ? "Settle on the circle before tapping. Avoid correcting past it and back." : "Look at the next target and use one controlled movement to reach it.",
            TrainerKind.Reading => later ? "Read the next number while finishing this note. Watch the approach circle at crossings." : "Follow the numbers. The closest circle is not always the next one.",
            _ => "Keep your movement controlled."
        });
    }
}
