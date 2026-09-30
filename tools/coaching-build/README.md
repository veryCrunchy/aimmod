# In-game coaching bundle

Run `npm ci` from `tools/coaching-build`, then run `node tools/build-ingame-coaching.cjs` from the repository root. Run `node --test in-game/ui/coaching.test.cjs` after rebuilding. Build dependencies are pinned by the adjacent lockfile. `AIMMOD_COACHING_BUILD_TOOLS` can select an existing dependency directory.

The bundle compiles the shared pure KovaaK coaching engine and the in-game DOM adapter to ES5. It includes only the compatibility modules needed for symbols/iterators, Map/Set, array find/flatMap/includes, string includes, finite-number checks, hypot and object assignment. No Tauri, React, or osu runtime is included.

Global and scenario profiles use up to 1,000 actual history records, exact historical measurement joins and the desktop duration-outlier filter. Accuracy percentages are converted to the engine's accepted ratio input to preserve values below 1 percent. Missing measurements remain null. Practice-block and warm-up personalization, feedback persistence, and local model chat remain separate work.
