using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.HUD;

namespace AimMod.Desktop;

public partial class NativeReplayPlayer : ReplayPlayer
{
    private readonly Action onReady;
    private readonly Action<string> onError;

    private readonly BindableDouble currentTime = new();
    private readonly BindableDouble duration = new();
    private readonly BindableBool isPaused = new(true);
    private readonly BindableDouble playbackRate = new(1);
    private readonly BindableBool isTransportReady = new();
    private readonly ReplayTransportLifetime transportLifetime = new();

    /// <summary>
    /// Current replay position in milliseconds. The value follows osu!'s gameplay clock.
    /// </summary>
    public IBindable<double> CurrentTime => currentTime;

    /// <summary>
    /// Time of the final beatmap object in milliseconds.
    /// </summary>
    public IBindable<double> Duration => duration;

    /// <summary>
    /// Whether osu!'s gameplay clock is paused.
    /// </summary>
    public IBindable<bool> IsPaused => isPaused;

    /// <summary>
    /// Playback rate applied by osu!'s master gameplay clock.
    /// </summary>
    public IBindable<double> PlaybackRate => playbackRate;

    /// <summary>
    /// Whether the official replay player and its gameplay clock are ready for transport commands.
    /// </summary>
    public IBindable<bool> IsTransportReady => isTransportReady;

    protected override bool PauseOnFocusLost => false;

    private readonly BindableBool showGameplayHud = new(true);
    private ReplayOverlay? detachedReplayOverlay;
    private double nextChromeCheck;

    /// <summary>
    /// Whether osu!'s gameplay HUD (score, accuracy, combo and skin HUD elements) is shown.
    /// AimMod owns this value for the embedded player and leaves the user's osu! HUD setting untouched.
    /// </summary>
    public Bindable<bool> ShowGameplayHud => showGameplayHud;

    /// <summary>
    /// True once osu!'s own replay chrome (settings panels, playback message, seek buttons,
    /// quit button and song progress) has been removed from the embedded player.
    /// </summary>
    internal bool ReplayChromeRemoved => detachedReplayOverlay is not null;

    public NativeReplayPlayer(Score score, Action onReady, Action<string> onError)
        : base(score, new PlayerConfiguration
        {
            AllowPause = true,
            AllowRestart = false,
            AllowSkipping = true,
            AllowUserInteraction = true,
            ShowResults = false,
        })
    {
        this.onReady = onReady;
        this.onError = onError;
        // ReplayPlayer enables the solo leaderboard after configuration. It only repeats
        // this run's score beside the playfield, so the embedded player leaves it out.
        Configuration.ShowLeaderboard = false;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        if (!LoadedBeatmapSuccessfully)
        {
            onError("The official osu! replay player could not load this beatmap.");
            return;
        }

        removeReplayChrome();
        // The HUD binds its own visibility handlers in its LoadComplete, which runs after this one.
        // Taking over ShowHud before then would leave the HUD hidden.
        if (HUDOverlay.IsLoaded)
            bindHudVisibility();
        else
            HUDOverlay.OnLoadComplete += _ => bindHudVisibility();
        duration.Value = Math.Max(0, GameplayState.Beatmap.GetLastObjectTime());
        updateTransportState();
        isTransportReady.Value = true;
        onReady();
    }

    /// <summary>
    /// AimMod draws playback, visual settings and the timeline around the viewport, so the
    /// official replay overlay (PLAYBACK and VISUAL SETTINGS panels, seek buttons and the
    /// "Watching" message) is detached. It stays alive so rulesets can still add settings
    /// groups to it through <see cref="ReplayPlayer.AddSettings"/>.
    /// </summary>
    private void removeReplayChrome()
    {
        if (ReplayOverlay is { } overlay && overlay.Parent is Container<Drawable> parent)
        {
            parent.Remove(overlay, false);
            detachedReplayOverlay = overlay;
        }

        // The hold-to-quit button would exit the embedded screen and leave an empty viewport.
        HUDOverlay.BottomRightElements.Hide();
        hideSongProgress();
    }

    private void bindHudVisibility() =>
        showGameplayHud.BindValueChanged(visible => applyHudVisibility(visible.NewValue), true);

    private void applyHudVisibility(bool visible)
    {
        Bindable<bool> showHud = HUDOverlay.ShowHud;
        // Rulesets without gameplay overlays disable the HUD before AimMod sees it.
        if (showHud.Disabled && !hudOverridden)
            return;

        showHud.Disabled = false;
        showHud.Value = visible;
        showHud.Disabled = true;
        hudOverridden = true;
    }

    private bool hudOverridden;

    /// <summary>
    /// osu!'s skinnable song progress duplicates AimMod's scrubber. Skins can recreate HUD
    /// components, so the check repeats at a low rate.
    /// </summary>
    private void hideSongProgress()
    {
        foreach (SongProgress progress in HUDOverlay.ChildrenOfType<SongProgress>())
        {
            if (progress.State.Value == Visibility.Visible)
                progress.Hide();
        }
    }

    protected override void Update()
    {
        base.Update();

        if (!isTransportReady.Value)
            return;

        if (Time.Current >= nextChromeCheck)
        {
            nextChromeCheck = Time.Current + 250;
            hideSongProgress();
        }

        if (ScoreProcessor.HasCompleted.Value)
        {
            completeTransport();
            return;
        }

        if (transportLifetime.CanAcceptCommands)
            updateTransportState();
    }

    /// <summary>
    /// Toggles playback using the replay player's existing gameplay clock.
    /// </summary>
    /// <returns>Whether the command was accepted for scheduling.</returns>
    public bool TogglePause() => scheduleTransportAction(clock =>
    {
        if (clock.IsPaused.Value)
            clock.Start();
        else
            clock.Stop();
    });

    /// <summary>
    /// Sets playback to a paused or playing state using the replay player's existing gameplay clock.
    /// </summary>
    /// <returns>Whether the command was accepted for scheduling.</returns>
    public bool SetPaused(bool paused) => scheduleTransportAction(clock =>
    {
        if (paused)
            clock.Stop();
        else
            clock.Start();
    });

    /// <summary>
    /// Stops playback synchronously before this player is hidden or removed from the drawable hierarchy.
    /// </summary>
    public bool SuspendPlayback()
    {
        if (!isTransportReady.Value || !transportLifetime.CanAcceptCommands)
            return false;

        transportLifetime.InvalidateScheduledActions();

        try
        {
            GameplayClockContainer.Stop();
            updateTransportState();
        }
        catch (ObjectDisposedException)
        {
            terminateTransport();
        }

        return true;
    }

    /// <summary>
    /// Seeks to a time in milliseconds, clamped to the playable beatmap timeline.
    /// </summary>
    /// <returns>Whether the command was accepted for scheduling.</returns>
    public bool SeekTo(double time)
    {
        if (!double.IsFinite(time))
            return false;

        return scheduleTransportAction(_ => Seek(Math.Clamp(time, 0, duration.Value)));
    }

    /// <summary>
    /// Sets playback speed through osu!'s master gameplay clock and its existing track adjustment.
    /// </summary>
    /// <returns>Whether the command was accepted for scheduling.</returns>
    public bool SetPlaybackRate(double rate)
    {
        if (!double.IsFinite(rate))
            return false;

        return scheduleTransportAction(clock =>
        {
            if (clock is not MasterGameplayClockContainer master)
                return;

            master.UserPlaybackRate.Value = Math.Clamp(rate, master.UserPlaybackRate.MinValue, master.UserPlaybackRate.MaxValue);
        });
    }

    /// <summary>
    /// Pauses and moves to the previous or next recorded replay frame using osu!'s own frame stepping.
    /// </summary>
    /// <returns>Whether the command was accepted for scheduling.</returns>
    public bool StepReplayFrame(int direction)
    {
        if (direction == 0)
            return false;

        return scheduleTransportAction(_ => StepFrame(Math.Sign(direction)));
    }

    private bool scheduleTransportAction(Action<GameplayClockContainer> action)
    {
        if (!isTransportReady.Value || !transportLifetime.TryCapture(out int generation))
            return false;

        Schedule(() =>
        {
            if (!isTransportReady.Value || !transportLifetime.CanRun(generation))
                return;

            if (ScoreProcessor.HasCompleted.Value)
            {
                completeTransport();
                return;
            }

            try
            {
                action(GameplayClockContainer);
                updateTransportState();
            }
            catch (ObjectDisposedException)
            {
                // The game-wide working beatmap may replace and dispose the track between
                // scheduling and execution. Treat that player as terminal rather than
                // allowing a stale transport command to crash the update thread.
                terminateTransport();
            }
        });

        return true;
    }

    private void completeTransport()
    {
        if (!transportLifetime.TryComplete())
            return;

        stopClockAtTerminalState();
    }

    private void terminateTransport()
    {
        transportLifetime.Terminate();
        stopClockAtTerminalState();
    }

    private void stopClockAtTerminalState()
    {
        isTransportReady.Value = false;

        try
        {
            GameplayClockContainer.Stop();
            currentTime.Value = Math.Min(Math.Max(0, GameplayClockContainer.CurrentTime), duration.Value);
        }
        catch (ObjectDisposedException)
        {
            // The source track has already been released. Public state still needs to
            // become terminal so no later command attempts to restart it.
        }

        isPaused.Value = true;
    }

    private void updateTransportState()
    {
        currentTime.Value = GameplayClockContainer.CurrentTime;
        isPaused.Value = GameplayClockContainer.IsPaused.Value;

        if (GameplayClockContainer is MasterGameplayClockContainer master)
            playbackRate.Value = master.UserPlaybackRate.Value;
    }

    protected override void Dispose(bool isDisposing)
    {
        transportLifetime.Dispose();
        isTransportReady.Value = false;
        isPaused.Value = true;
        base.Dispose(isDisposing);
        // A detached drawable is no longer disposed by its former parent.
        detachedReplayOverlay?.Dispose();
        detachedReplayOverlay = null;
    }
}

internal sealed class ReplayTransportLifetime
{
    private int generation;
    private bool completed;
    private bool disposed;

    public bool CanAcceptCommands => !completed && !disposed;

    public bool TryCapture(out int capturedGeneration)
    {
        capturedGeneration = generation;
        return CanAcceptCommands;
    }

    public bool CanRun(int capturedGeneration) =>
        CanAcceptCommands && capturedGeneration == generation;

    public void InvalidateScheduledActions() => generation++;

    public bool TryComplete()
    {
        if (!CanAcceptCommands)
            return false;

        completed = true;
        generation++;
        return true;
    }

    public void Terminate()
    {
        completed = true;
        generation++;
    }

    public void Dispose()
    {
        disposed = true;
        generation++;
    }
}
