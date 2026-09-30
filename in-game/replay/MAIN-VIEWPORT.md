# Main viewport replay integration

`ReplayMainScene` uses the real player's view target and a native `CameraActor` in the existing game world. It does not render through an embedded texture. Recorded target transforms drive inert native `StaticMeshActor` proxies; no game character, AI, weapon or playback component is created or driven.

The caller owns the independent replay HUD and UI-only input routing. Close the AimMod window only after binding succeeds, retain UI-only input during playback, and restore the menu on exit. Physical fire inputs must not be routed into gameplay by the HUD. The scene adds balanced look/move input locks. An inactive scenario can be paused on entry; an active scenario must already be paused with a visible menu. Its active flags, challenge timers and world clock must remain unchanged throughout playback. Exiting preserves that original pause without cancelling or resuming the run.

## Contract

- `ReplayMainScene.verify(owner, meta)` performs read-only preflight and returns the current identity. Normal metadata contains `scenario`, `mapName`, and `mapScale`. Missing metadata fails closed.
- `ReplayMainScene.create(owner)` allocates only Lua state.
- `scene.bind(meta)` performs preflight before mutation, saves the controller/view/pause state, pauses if necessary, hides current characters reversibly, and selects an owned native camera.
- `scene.verify()` validates the scenario, world, pause and camera ownership. Failure closes the scene and raises an error.
- `scene.frame({camera={x,y,z,pitch,yaw,roll,fov}, actors={{id,x,y,z,radius,halfHeight},...}})` validates and applies one frame. At most 128 proxies exist; removed proxies are destroyed and their references removed.
- `scene.isReady()` returns a boolean and closes a previously active scene if its guard fails.
- `scene.close()` restores saved view, visibility, camera flags and balanced input locks and destroys only owned actors. It restores an originally unpaused state only if the original world and idle scenario still validate. Repeated close is harmless.

For an explicit developer proof, call `ReplayMainScene.proofFrame(owner)` before binding, then `scene.bind({proof=true})` and `scene.frame(proofFrame)`. This uses the currently loaded scene and places one inert sphere in front of its current camera. The proof flag must not be accepted from the playback HTTP command or used as a fallback for missing replay metadata.

## Validation and limits

The Lua mock suite covers preflight with no mutations, active challenge/freeplay rejection, loading and identity rejection, native camera FOV, proxy reuse/removal, partial initialization rollback, restoration, and external world/challenge transitions. Run `main-scene.test.lua` with a Lua 5.3-compatible interpreter from this directory.

The main viewport path still requires live verification of camera refresh while paused. It temporarily enables the controller's full tick while paused and restores its original flag. This is restricted to an inactive scenario, but native observer auditing remains necessary for the live proof.

Matching map name and scale and fully loaded flags are necessary checks, not a geometry fingerprint: the game's native map loader can update its name before a failed parse. A successful-load/geometry validation mechanism is needed before promising arbitrary saved-map fidelity. This module never loads or changes maps. Current recordings contain target capsules, not full mesh/material or animation manifests, so their visual proxies remain approximations. Legacy movement records without camera/map metadata cannot enter this path.
