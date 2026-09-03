using osu.Framework.Bindables;
using osu.Game.Beatmaps;
using osu.Game.Scoring;
using osu.Game.Screens.Play;

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
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        if (!LoadedBeatmapSuccessfully)
        {
            onError("The official osu! replay player could not load this beatmap.");
            return;
        }

        duration.Value = Math.Max(0, GameplayState.Beatmap.GetLastObjectTime());
        updateTransportState();
        isTransportReady.Value = true;
        onReady();
    }

    protected override void Update()
    {
        base.Update();

        if (!isTransportReady.Value)
            return;

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
