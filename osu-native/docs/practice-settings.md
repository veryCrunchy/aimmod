# Imported practice settings

Skill trainers, coaching sections and DT use the shared embedded osu! player. Before preparation, AimMod reads the selected client configuration. Source configuration and account databases remain read-only. Every imported setting is scoped to practice and restored on completion, cancellation, launch failure or application disposal.

For lazer, `game.ini` supplies background dim and blur, dim changes during breaks, storyboard and video preferences, beatmap colours/skins/hitsounds, cursor size, hit lighting, HUD visibility, key overlay, UI scale and playfield size/position. `framework.ini` supplies window mode, windowed/fullscreen resolution, display selection, window position and frame sync. The small osu! ruleset settings table supplies slider snaking, hit animations, cursor trail/ripples and playfield border. Input bindings, offset, mouse sensitivity and tablet mapping use the existing control import.

For stable, the selected user configuration supplies windowed or fullscreen resolution, letterboxing and offsets, background dim, break dimming, storyboard/video preferences, beatmap skin/sample preferences, key overlay, cursor size and slider snaking. Stable dim percentages are converted to lazer fractions. Letterboxing uses a native borderless surface with the requested physical play area, with finer scaling precision to avoid rounding the size to whole percentages. A desktop-sized stable window is represented as borderless.

Only defined, finite values are applied. Missing optional settings retain AimMod's current values. Resolution dimensions are bounded; unavailable displays fall back to the current display. OS-wide DPI, GPU scaling, renderer/backend selection and driver-specific settings are not changed. The embedded engine remains lazer, so stable-only rendering behaviours without a matching engine setting are not promised to be identical. Storyboard/video preferences affect content available in the practice map; generated drills do not duplicate those assets.

DT and installed-song practice retain available background images. Image loading is bounded to 16 MiB and 32 million pixels, rejects paths outside the map directory, and releases private textures after gameplay. It does not create a persistent background cache.

Tests cover adaptive DT progression, stable/lazer configuration conversion, invalid values, scoped restoration, read-only ruleset extraction and private-desktop playback at normal and full DT rates.
