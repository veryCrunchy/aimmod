# Changelog

## [1.9.0](https://github.com/veryCrunchy/aimmod/compare/v1.8.0...v1.9.0) (2026-09-05)


### Features

* add native osu companion client ([d6b11e1](https://github.com/veryCrunchy/aimmod/commit/d6b11e132e3fc2139301da616ed942340909ccaa))
* **analytics:** add coaching, target-response, and benchmark insights ([f830fd3](https://github.com/veryCrunchy/aimmod/commit/f830fd3aff95b4d25fca0ae2eb2f29250ab57118))
* improve replay capture fidelity and timing ([6ebb1ed](https://github.com/veryCrunchy/aimmod/commit/6ebb1edfcece91a437add85e0865dd1c2466c51b))
* **local-coach:** add coach UI, charts, and settings ([40466bd](https://github.com/veryCrunchy/aimmod/commit/40466bddc888886f54ffc86d8a75601dffabcf66))
* **local-coach:** add local runtime and tool-backed backend ([ad87262](https://github.com/veryCrunchy/aimmod/commit/ad872629ebc3cb3bb6a9545be2d7e501881147e9))
* **osu:** add coaching and exact pp targets ([4d6f36f](https://github.com/veryCrunchy/aimmod/commit/4d6f36f1c2bcbd2be9002386a45b46abf5c93577))
* **overlay:** add unified overlay studio and browser sources ([3083918](https://github.com/veryCrunchy/aimmod/commit/308391853bc72871f58e70c21ebef994d18dc168))
* publish live activity to aimmod hub ([5ca36fc](https://github.com/veryCrunchy/aimmod/commit/5ca36fc35023698d573ac339f1d53fe6772957f3))
* refresh in-game overlay state and pb pacing ([9542f0d](https://github.com/veryCrunchy/aimmod/commit/9542f0d14a87f4dffea0029ba7467635dcf4b5f3))


### Bug Fixes

* **dependencies:** simplify Windows crate versioning ([aa53686](https://github.com/veryCrunchy/aimmod/commit/aa53686e8c08d9058379438cfc56db69acda0867))
* **overlay:** prefer hub pb and suppress duplicate updates ([85831c2](https://github.com/veryCrunchy/aimmod/commit/85831c2d995a4ff6cda5b5355c41a84a21ca2ad0))
* **overlay:** remove desktop private surface and restore palette sync ([c677fef](https://github.com/veryCrunchy/aimmod/commit/c677fef1b6e662bb0b4fbe2f892f0ae98fa7e95f))
* **replay:** wait for replay flush and normalize accuracy handling ([d878baf](https://github.com/veryCrunchy/aimmod/commit/d878baf86ff4258f8b973e2734437a85ec5200d1))
* simplify LoadLibraryW address retrieval in bridge ([7592acc](https://github.com/veryCrunchy/aimmod/commit/7592acccf27359fd13526e140e73ede7a5d2fd78))
* update Windows API usage for improved stability and clarity ([01de554](https://github.com/veryCrunchy/aimmod/commit/01de554b838a0a64e74724743f79d368be1c2d7e))

## [1.8.0](https://github.com/veryCrunchy/aimmod/compare/v1.7.2...v1.8.0) (2026-03-15)


### Features

* Add reconnect event handling and move to networked transport ([5136389](https://github.com/veryCrunchy/aimmod/commit/5136389329b1d8bc80037b932b65709cf3c284cb))


### Bug Fixes

* **logger:** improve log flushing strategy and enhance log level filtering ([333090e](https://github.com/veryCrunchy/aimmod/commit/333090e36b6878231e190a69def92aa097610f5f))

## [1.7.2](https://github.com/veryCrunchy/aimmod/compare/v1.7.1...v1.7.2) (2026-03-12)


### Bug Fixes

* **event-pipe:** implement non-blocking event writing and worker management ([4b0d3cc](https://github.com/veryCrunchy/aimmod/commit/4b0d3cc60a39d6905c4dffda4e2e1ede27e0255b))
* **stats:** reduce hub status refresh interval and add unlisten for hub sync status ([94951b1](https://github.com/veryCrunchy/aimmod/commit/94951b15f13066fee484278ff13c9449b198cbf8))

## [1.7.1](https://github.com/veryCrunchy/aimmod/compare/v1.7.0...v1.7.1) (2026-03-12)


### Bug Fixes

* auto-resync outdated mouse path uploads ([dbd7318](https://github.com/veryCrunchy/aimmod/commit/dbd73188ceaf23590bdbbdc16303e08dbd908912))

## [1.7.0](https://github.com/veryCrunchy/aimmod/compare/v1.6.1...v1.7.0) (2026-03-12)


### Features

* add robust directory removal and clearing functions ([a64d781](https://github.com/veryCrunchy/aimmod/commit/a64d78113e2be65bc77d63cba5edaa9d2029451c))
* **appearance:** sync accent color from KovaaK's Palette.ini ([5d9767a](https://github.com/veryCrunchy/aimmod/commit/5d9767aadac533644bf562d46efb6c34c826c893))
* apply full KovaaK's palette + fix stats window theming + HUD opacity ([8f69a06](https://github.com/veryCrunchy/aimmod/commit/8f69a06c9ed9c1e70b4f4e1b4ae16a2aef413af3))
* auto-save settings and tune post-run flow ([4aef856](https://github.com/veryCrunchy/aimmod/commit/4aef856e093ecd5491c6788ac4d268f925a59f86))
* **bridge:** add user identity profile types and event handlers ([00d0de2](https://github.com/veryCrunchy/aimmod/commit/00d0de2ab725a28084153e1b0a1b5b4465607f34))
* **friend-scores:** refactor serialization and add adjacent scores handling ([ed21a15](https://github.com/veryCrunchy/aimmod/commit/ed21a153f7cd0ee50313979679268f8acd9829d9))
* **hub-api:** add strip_null_fields function and update struct defaults ([ab9821d](https://github.com/veryCrunchy/aimmod/commit/ab9821d60d38d7257efa7be307ad27fd31f201a9))
* **hub-sync:** derive upload identity from in-game bridge user ([4665322](https://github.com/veryCrunchy/aimmod/commit/466532227a993be153f97341d4834e6959beaa94))
* **hub:** add benchmark pages and scenario history ([5cb7883](https://github.com/veryCrunchy/aimmod/commit/5cb7883b31058474222fc8a9404334f48cee68ea))
* **identity:** use live bridge friends and current user ([3b4b001](https://github.com/veryCrunchy/aimmod/commit/3b4b0016b9b06fd302473e468ea6abe15a08ddd6))
* **lib:** add hub API commands and identity-aware friend resolution ([8a40601](https://github.com/veryCrunchy/aimmod/commit/8a4060190b91207c3cfa57a0d9e4821f05eec526))
* **mouse-hook:** track active scenario type for scenario-aware metrics ([e654d0b](https://github.com/veryCrunchy/aimmod/commit/e654d0b408f7058b51561d0d7f842e784757fe6b))
* **stats:** add AimMod Hub tab with overview and replay browser panels ([8339978](https://github.com/veryCrunchy/aimmod/commit/8339978a998cf154e23e8d14b44ac5be13f867a2))
* **ue4ss:** batch pull_snapshot event and add user management module ([050c2ae](https://github.com/veryCrunchy/aimmod/commit/050c2ae6e845b9bb464bd6e4be5c95b61f75bbeb))
* **user-management:** add friend scores caching and comparison functions ([323f24b](https://github.com/veryCrunchy/aimmod/commit/323f24b5e4fa3dbb0695899f7757f9541a344976))
* **vsmode:** use live in-game identity as VS opponent ([3859b9c](https://github.com/veryCrunchy/aimmod/commit/3859b9c0c05ca94b57a9d37fc5f37f83a246d2dc))
* wire all KovaaK's palette colors into app UI ([d8db876](https://github.com/veryCrunchy/aimmod/commit/d8db876b8bd59c476c94e1ab69ae419975db688b))


### Bug Fixes

* **hub-sync:** enrich upload identity and mouse-path payloads ([f0e08d5](https://github.com/veryCrunchy/aimmod/commit/f0e08d5e294feb5151817444a0035d0efcfa7466))
* preserve palette alpha and apply it correctly throughout UI ([d3105c2](https://github.com/veryCrunchy/aimmod/commit/d3105c2a171e1dc092d3812b27578fffaa279d31))
* repair replay timing drift for saved runs ([fe9fa08](https://github.com/veryCrunchy/aimmod/commit/fe9fa0840df764b7d649d9a620dd58853330dd2f))
* **screen-recorder:** bounded encoder queue and frame-rate pacing ([264ae15](https://github.com/veryCrunchy/aimmod/commit/264ae1569ffe5bb2e34f38dc3f1ae1cfba9dc4d4))
* solid UI colors + scoped palette sync ([595bd19](https://github.com/veryCrunchy/aimmod/commit/595bd190efb4b33f0d4e105ed03f7316d7650fd6))
* **ue4ss:** add recursive mutex to rust_bridge API for thread safety ([23712eb](https://github.com/veryCrunchy/aimmod/commit/23712eb9ce964323b5446b1709c993c40e497b1d))
* **user-management:** remove unused W_Text references and streamline user ID extraction ([02e4eb8](https://github.com/veryCrunchy/aimmod/commit/02e4eb888d22dcfaa1f1309c75732409fad55c1b))

## [1.6.1](https://github.com/veryCrunchy/aimmod/compare/v1.6.0...v1.6.1) (2026-03-10)


### Bug Fixes

* implement tracking pause functionality for smoothness metrics ([1dd13b4](https://github.com/veryCrunchy/aimmod/commit/1dd13b4a86d864ce63e057bc207eceb3889a2cf2))
* optimize mouse hook ([fce9b11](https://github.com/veryCrunchy/aimmod/commit/fce9b11d7d4e4879b464691df53f98e9ce6ee8b9))
* update default replay media upload mode to include PB ([037d852](https://github.com/veryCrunchy/aimmod/commit/037d852b6f86235b6aaf215e37c8e39bc80c923d))

## [1.6.0](https://github.com/veryCrunchy/aimmod/compare/v1.5.1...v1.6.0) (2026-03-10)


### Features

* add replay media uploads and sync controls ([0ef5749](https://github.com/veryCrunchy/aimmod/commit/0ef574960ea2ad3effc22a06acdaf7102a0b8733))


### Bug Fixes

* align replay hit markers to bridge timing ([9597781](https://github.com/veryCrunchy/aimmod/commit/95977813cad6284c1da6ca7aee75baf252df5955))

## [1.5.1](https://github.com/veryCrunchy/aimmod/compare/v1.5.0...v1.5.1) (2026-03-10)


### Bug Fixes

* update DEFAULT_HUB_API_BASE_URL to remove redundant 'api' subdomain ([53ba46d](https://github.com/veryCrunchy/aimmod/commit/53ba46d4c4551fa792c9e3e213508fb6b217c397))

## [1.5.0](https://github.com/veryCrunchy/kovaaks/compare/v1.4.0...v1.5.0) (2026-03-10)


### Features

* add update notice functionality to StatsWindow ([f7d9e89](https://github.com/veryCrunchy/kovaaks/commit/f7d9e89d29ec49cd745532cf4caadb8504fdfad4))
* enhance scenario classification and update related logic ([b762e8e](https://github.com/veryCrunchy/kovaaks/commit/b762e8e1a223f44474d39f3bc101410200a39c6b))

## [1.4.0](https://github.com/veryCrunchy/kovaaks/compare/v1.3.0...v1.4.0) (2026-03-10)


### Features

* add hub_uploaded_schema_version to sessions and update schema migration ([e6fb772](https://github.com/veryCrunchy/kovaaks/commit/e6fb772abbcc7d42b87b7e788accb8aa52ceb8d8))


### Bug Fixes

* enhance session classification logic ([37c0458](https://github.com/veryCrunchy/kovaaks/commit/37c0458bb276cb38038df3a844deeb165f1a2bf2))

## [1.3.0](https://github.com/veryCrunchy/kovaaks/compare/v1.2.1...v1.3.0) (2026-03-10)


### Features

* Implement AimMod Hub sync functionality and settings ([6900a91](https://github.com/veryCrunchy/kovaaks/commit/6900a9143d41eee7110b6f07b0cbeee860f2942a))

## [1.2.1](https://github.com/veryCrunchy/kovaaks/compare/v1.2.0...v1.2.1) (2026-03-09)


### Bug Fixes

* enhance bridge stats flow check logic ([d3e75af](https://github.com/veryCrunchy/kovaaks/commit/d3e75af4a5bca46c0e05f36a66232459edbdfbbc))

## [1.2.0](https://github.com/veryCrunchy/kovaaks/compare/v1.1.0...v1.2.0) (2026-03-09)


### Features

* add pause window tracking to run metrics and update related logic ([ecde9ca](https://github.com/veryCrunchy/kovaaks/commit/ecde9caf99cb0c817d3c5423b8a35df0c3b03935))
* add settings tab and enhance settings component functionality ([ac3dd78](https://github.com/veryCrunchy/kovaaks/commit/ac3dd78767e2e5063d4ffa998b9eccb3627f2951))
* enhance overlay notice logic and improve DraggableHUD position handling ([a27aea0](https://github.com/veryCrunchy/kovaaks/commit/a27aea02838ecaf6566d770bf9662084763a0f16))
* enhance schema migration and improve code formatting ([c67abc2](https://github.com/veryCrunchy/kovaaks/commit/c67abc2b29cd532e6418a2af31fa35f52e46b2c8))
* implement overlay runtime notice system and integrate with LiveFeedbackToast ([e84f8e0](https://github.com/veryCrunchy/kovaaks/commit/e84f8e0ecb6e0dd0735e01763e711a0a0f52984b))
* remove smoothness tab and related components from settings ([108d07d](https://github.com/veryCrunchy/kovaaks/commit/108d07d24a12835f1fa263a917c59b86577aff30))


### Bug Fixes

* update log messages to include possessive form for KovaaK's ([6ade03e](https://github.com/veryCrunchy/kovaaks/commit/6ade03ec425a833ab8605710b18980b19c42ed89))
* update screen recorder to capture at 24 fps and adjust frame storage logic ([c384808](https://github.com/veryCrunchy/kovaaks/commit/c384808b224f9d230b565bdf3757a22f68315e91))

## [1.1.0](https://github.com/veryCrunchy/kovaaks/compare/v1.0.1...v1.1.0) (2026-03-09)


### Features

* add app version handling and display in Discord RPC ([1a3067b](https://github.com/veryCrunchy/kovaaks/commit/1a3067b0f00ee86bf57d31fd8b10614f4dc18d96))
* display app version label in settings panel ([d68ac19](https://github.com/veryCrunchy/kovaaks/commit/d68ac19f2f78c4fc3892d14d1555d777ba626029))
* enhance version metadata handling for prereleases and releases ([793f297](https://github.com/veryCrunchy/kovaaks/commit/793f2972741e2ff40460f0bb8ac75eae65da1550))
* normalize run timeline data and improve merging logic ([46367c9](https://github.com/veryCrunchy/kovaaks/commit/46367c91709a80d4dc88b75b2c4a6ad1546b4f0d))
* update aimmod version to 1.0.1 and enhance replay capture handling ([bed0c2e](https://github.com/veryCrunchy/kovaaks/commit/bed0c2e6de5b98a307e7ec1548650a2e8a505cf4))
* update game state handling and remove version label from activity ([4ff877e](https://github.com/veryCrunchy/kovaaks/commit/4ff877e9869f0a74e01fc010ee2edf11f72fe4e0))


### Bug Fixes

* add AIMMOD_DISPLAY_VERSION to Tauri action environment variables ([7b33e5e](https://github.com/veryCrunchy/kovaaks/commit/7b33e5e03740e992ca39c2f57c009d79d5bcb448))
* set default trail fade duration to 5000ms ([ab8f4e1](https://github.com/veryCrunchy/kovaaks/commit/ab8f4e1a44c3b7fd98b9d6c9a3b4adeea4d5a829))

## [1.0.1](https://github.com/veryCrunchy/kovaaks/compare/v1.0.0...v1.0.1) (2026-03-09)


### Bug Fixes

* adjust overlay window to use fixed size instead of fullscreen ([fe4d90e](https://github.com/veryCrunchy/kovaaks/commit/fe4d90e08b2b7b76031930f0d6b71d2dca9d003f))
* remove backdrop filter from BridgeStateDebugHUD and LiveFeedbackToast styles ([4965254](https://github.com/veryCrunchy/kovaaks/commit/4965254d163c9eec1d9d9101538f5e2fa70e990b))

## [1.0.0](https://github.com/veryCrunchy/kovaaks/compare/v0.8.1...v1.0.0) (2026-03-09)


### ⚠ BREAKING CHANGES

* remove OCR pipeline and rebrand app as AimMod

### Features

* Add accuracy normalization and derivation functions for improved accuracy calculations ([ad63d6c](https://github.com/veryCrunchy/kovaaks/commit/ad63d6c57714b2e792089271df8826018b92d95d))
* Add additional fields to BridgeCommand and improve RustBridge startup logic ([1c6b558](https://github.com/veryCrunchy/kovaaks/commit/1c6b55873e5e1275ece1fe513bfa6ec050121ff5))
* Add bridge transport probe for enhanced connection stability ([aede950](https://github.com/veryCrunchy/kovaaks/commit/aede9508b1736e3dc0d85b6f6b5bcce6238f3960))
* Add caching for UE4SS template and payload pipeline artifacts ([1a3494a](https://github.com/veryCrunchy/kovaaks/commit/1a3494a9c3d7d8774ddd4b2fd76174172c9f0008))
* Add challenge lifecycle management and refine game state derivation ([02163e8](https://github.com/veryCrunchy/kovaaks/commit/02163e88d85ee9c573f184c9f144b14ed060a85c))
* Add count field to BridgeShotTelemetryEvent in shot telemetry query ([0d8e265](https://github.com/veryCrunchy/kovaaks/commit/0d8e2653ace8ae234156b824b9b21eb282d06618))
* Add count to BridgeShotTelemetryEvent and enhance telemetry summary calculations ([dc7801d](https://github.com/veryCrunchy/kovaaks/commit/dc7801d1e80417dedef4e6258c54799ef81242ab))
* Add CSV import functionality for session history ([1f4b1a5](https://github.com/veryCrunchy/kovaaks/commit/1f4b1a5fffc3704141e5a3379e2bfb08273342bd))
* Add Discord RPC integration for presence updates ([919e235](https://github.com/veryCrunchy/kovaaks/commit/919e235e24c8465ba441273d93b493c2b2acfab3))
* Add functions for managing Git repository references and logging revisions in dev pipeline scripts ([9244f1a](https://github.com/veryCrunchy/kovaaks/commit/9244f1a261fa86d6f9d631e7f0092d2c82594195))
* Add lifecycle event handling and stale event filtering in live score ([1508bf9](https://github.com/veryCrunchy/kovaaks/commit/1508bf93783c9741ac7ed4aae6ae808db47a9e31))
* Add lifecycle start alignment hint for improved signal inference ([baaeb9c](https://github.com/veryCrunchy/kovaaks/commit/baaeb9c8d5c8eb5b3218538d78f72ab4f984a6f6))
* Add live metric hooks and related functionality for enhanced metrics tracking ([83da1aa](https://github.com/veryCrunchy/kovaaks/commit/83da1aac15fdfb5fa3fe93e403d55f80d0f106b2))
* Add logging for built and staged payload hashes in dev and prod workflows ([e8143a7](https://github.com/veryCrunchy/kovaaks/commit/e8143a76a2f2af3981262c330738c6464334db25))
* Add new live metrics for kills, accuracy, and damage statistics ([bb2589e](https://github.com/veryCrunchy/kovaaks/commit/bb2589ebcd02c85500b0c3a1f7686a1e53fba0ad))
* Add pagination and recent scenarios retrieval to session store ([695ab7c](https://github.com/veryCrunchy/kovaaks/commit/695ab7c7596211f8fa20bb9e0998c837de04b363))
* Add relative time and playtime formatting, enhance session trend analysis ([5c4da0c](https://github.com/veryCrunchy/kovaaks/commit/5c4da0cdcbef09f965499d9c177f8a49e6d5dd40))
* Add replay playback frame structure and extend bridge command with new fields ([0a3fd9c](https://github.com/veryCrunchy/kovaaks/commit/0a3fd9cef76cdac93c1674cf3f41faa5a23209bb))
* Add reset settings command and integrate single instance plugin ([0347e0b](https://github.com/veryCrunchy/kovaaks/commit/0347e0b744cb84e1422f4b4547b38e69d0df9f8d))
* Add runtime restart required flag and suppress state sync warnings ([16c9e56](https://github.com/veryCrunchy/kovaaks/commit/16c9e5603c0a9ec382a64f07780c2275a6aa388b))
* Add scenario sorting functionality to StatsWindow ([78ea49b](https://github.com/veryCrunchy/kovaaks/commit/78ea49ba3a3e247a12f04f8938c9ec71bef093a3))
* Add scenario state and timing variables for improved lifecycle management ([99daa81](https://github.com/veryCrunchy/kovaaks/commit/99daa813742da98a18f8acdfe542d5524a3ef8bd))
* Add scenario subtype classification to stats panel and database schema ([67b5db6](https://github.com/veryCrunchy/kovaaks/commit/67b5db6648c01cb8b174267513e0b4e2146096fa))
* Add scenario subtype to stats panel and telemetry interfaces ([3b23513](https://github.com/veryCrunchy/kovaaks/commit/3b23513360d49e41b0e8f8adaf0fd600cdf63076))
* Add score metrics to BridgeStatsPanelEvent and enhance metric event handling ([68c1231](https://github.com/veryCrunchy/kovaaks/commit/68c123119d105107f43c15c745564e517def39bb))
* Add scripts for syncing UE4SS payload and settings ([9a53cd2](https://github.com/veryCrunchy/kovaaks/commit/9a53cd2c16ee5cc0c3f9ae275adb44856b2f62cb))
* Add session active checks to prevent processing when inactive ([c1db5d5](https://github.com/veryCrunchy/kovaaks/commit/c1db5d511aaea8ad2bfbfc00370dc2fa58d0b949))
* Add session replay context windows for enhanced telemetry analysis ([47a5a99](https://github.com/veryCrunchy/kovaaks/commit/47a5a9943bd1cfd7b76b8971e57b640a6682f4ed))
* Add session replay payload handling and SQLite persistence ([4eed603](https://github.com/veryCrunchy/kovaaks/commit/4eed603ab387cdee798743ca16cef0ae86099d31))
* Add session run summary and timeline commands ([21d740d](https://github.com/veryCrunchy/kovaaks/commit/21d740d2fe4dfb99409eb9f1b753c26b2dbb0300))
* Add session shot telemetry retrieval and database schema update ([27f576d](https://github.com/veryCrunchy/kovaaks/commit/27f576d92060182334880257016d898875de419d))
* Add shot target telemetry emission and JSON snapshot handling ([df8accb](https://github.com/veryCrunchy/kovaaks/commit/df8accbb474fbc26db75df1ff8760b0cc46e1e4c))
* Add shot telemetry display mode and enhance telemetry data handling ([41b9b03](https://github.com/veryCrunchy/kovaaks/commit/41b9b03a54a7db4de18505f87ca3c0f7ea36f713))
* Add shot telemetry interfaces and update stats export ([b4994cc](https://github.com/veryCrunchy/kovaaks/commit/b4994cc214e6f12c90ccecd57a035fdb6590e86c))
* Add shot telemetry structures and processing functions ([107d0bd](https://github.com/veryCrunchy/kovaaks/commit/107d0bd834894d488cc21ba20a969754018ce78e))
* Add shot telemetry tracking and summary to ReplayTab ([0ebe0af](https://github.com/veryCrunchy/kovaaks/commit/0ebe0af996baca6863069a32d341bc0cdce3de51))
* Add SQL audit functionality for session integrity checks and database schema updates ([46d99d4](https://github.com/veryCrunchy/kovaaks/commit/46d99d465ae0aaa606b28a69e1f2862fdf603abd))
* Add stats panel snapshot functionality for session tracking ([6431cbd](https://github.com/veryCrunchy/kovaaks/commit/6431cbd0e094a116f39ae488ebed38f62e6e9a12))
* add UE4SS runtime payload files and README ([3dc5a96](https://github.com/veryCrunchy/kovaaks/commit/3dc5a963bf65c7e0fa7ecf2eb5b4a0a1730cf690))
* add UE4SS_GITHUB_TOKEN support for GitHub Actions and update README ([4c8079c](https://github.com/veryCrunchy/kovaaks/commit/4c8079c0ad5557144183415f9a0535fd8c565ca5))
* **command:** add command polling and processing functionality ([6809b4f](https://github.com/veryCrunchy/kovaaks/commit/6809b4fa47f1a9d502aab6fbbae3369a994a5870))
* **command:** implement command polling and connection handling ([9d6a37c](https://github.com/veryCrunchy/kovaaks/commit/9d6a37cae75b01e43f902a878fc9a4330e65d9fd))
* **debug:** add bridge state overlay HUD toggles ([7eff530](https://github.com/veryCrunchy/kovaaks/commit/7eff5301f24d4b88b6f451ab0da443c45c660445))
* Enhance analytics with new session record structures and calculations ([c8ec775](https://github.com/veryCrunchy/kovaaks/commit/c8ec7756218997f685d3827281bbe7672a15b06b))
* Enhance bridge command handling and state synchronization ([f2b5730](https://github.com/veryCrunchy/kovaaks/commit/f2b5730dc5a4ec6c94f84c4079b51efb371735a5))
* Enhance BridgeStateDebugHUD with improved metric tracing and state updates ([cf3f44e](https://github.com/veryCrunchy/kovaaks/commit/cf3f44e695aa8a8bc9741636fd25d11bb8a59d03))
* Enhance CI pipeline with template submodule initialization and payload verification ([197def5](https://github.com/veryCrunchy/kovaaks/commit/197def5965b1f762c2ebe13c77957738294c8f69))
* enhance dev workflow with staging and verification of AimMod runtime payload ([1b9fefa](https://github.com/veryCrunchy/kovaaks/commit/1b9fefae0ea4cf590e790f8b9fd10cb1ea148cc0))
* Enhance Discord RPC presence with additional stats and improved formatting ([182a87a](https://github.com/veryCrunchy/kovaaks/commit/182a87a7e3263cfa0db5fd03a11dc4b53546a348))
* Enhance DraggableHUD with collision resolution and size management ([9b7b8a2](https://github.com/veryCrunchy/kovaaks/commit/9b7b8a2eb32419301e2e60b22debec038c1cb90b))
* Enhance DraggableHUD with grid snapping and preset positioning ([78083d3](https://github.com/veryCrunchy/kovaaks/commit/78083d36a677952bb019477d9f52f7e34ad7964a))
* enhance in-game overlay functionality and improve URL handling ([bef26a5](https://github.com/veryCrunchy/kovaaks/commit/bef26a56f99c83cf081b79b6f26759dfa0f5d7c3))
* Enhance live metric handling and state receiver logic ([88f90d4](https://github.com/veryCrunchy/kovaaks/commit/88f90d4687ab77c9770bf873adbbce8c42e9747e))
* Enhance live score metrics handling and state management ([23fc092](https://github.com/veryCrunchy/kovaaks/commit/23fc09241dc3fbdfd9a10dacfd14ccb34325bfde))
* Enhance live score metrics with session management and timestamping ([da90e75](https://github.com/veryCrunchy/kovaaks/commit/da90e755b4cbeeb821dd83bccc005fac24b3f576))
* Enhance MousePathViewer with segment labels and timeline markers ([4079430](https://github.com/veryCrunchy/kovaaks/commit/40794309258130d2167633632ac0d5bbb25c1d6b))
* Enhance named pipe handling with GetNamedPipeInfo and improved error checking ([ad500e8](https://github.com/veryCrunchy/kovaaks/commit/ad500e8ac7f22e4f752a2b17ae1f4fd40f203ee4))
* Enhance overlay functionality with cursor detection and HUD interactivity ([41be2fe](https://github.com/veryCrunchy/kovaaks/commit/41be2febf7d6d0f613f1328d0b8ab159395957a4))
* Enhance session completion handling with new payload structure and database schema update ([68eb6ce](https://github.com/veryCrunchy/kovaaks/commit/68eb6ce8cb65b77ca73a33e0d39b305b944534a3))
* Enhance session handling with replay tracking and sorting in stats panel ([dc94347](https://github.com/veryCrunchy/kovaaks/commit/dc94347ed88e7ab2826039500151cbea64baf377))
* Enhance session history management with pagination and local storage support ([c2b41b5](https://github.com/veryCrunchy/kovaaks/commit/c2b41b5776275bba3ebd51fa2d8ee133f5c0a6d9))
* Enhance session summary with bridge snapshot handling and stats panel normalization ([141a290](https://github.com/veryCrunchy/kovaaks/commit/141a29042a5187e4bab6cde7908a51459656b16a))
* Enhance shot telemetry handling with placeholder target checks and telemetry flushing ([e9b6bbf](https://github.com/veryCrunchy/kovaaks/commit/e9b6bbf490929052e9669cc78a04de3a4a9d0102))
* Enhance telemetry metrics handling and introduce batched polling for performance ([8746fc4](https://github.com/veryCrunchy/kovaaks/commit/8746fc470580c4581d207ecda38bfb374fb754eb))
* Enhance tracking candidate logic with sustained damage conditions ([0d6553a](https://github.com/veryCrunchy/kovaaks/commit/0d6553a4ed388e8d9358a38c42df3ebdfe5accd8))
* Implement active game state handling and context checks ([daa14af](https://github.com/veryCrunchy/kovaaks/commit/daa14af6d444c0f1aa871d1cce67075d43d06f90))
* Implement fallback presence and optimize presence updates for Discord integration ([ef415e7](https://github.com/veryCrunchy/kovaaks/commit/ef415e7efb7ddc2794048561c8adfd84fa464421))
* Implement non-blocking named pipe handling and improve connection management ([9820b18](https://github.com/veryCrunchy/kovaaks/commit/9820b18136cf401276318b72c3a4ae02d1fe69e0))
* Implement reconnect worker for improved API connection handling ([d681322](https://github.com/veryCrunchy/kovaaks/commit/d6813223172fb776d8f852050f0977e746a31b4e))
* Implement replay system with state management and sampling ([60d2a75](https://github.com/veryCrunchy/kovaaks/commit/60d2a759533b2acf467520d5116a64566341ab73))
* Implement session SQL audit for enhanced data integrity checks ([aab7815](https://github.com/veryCrunchy/kovaaks/commit/aab781554ecef0d141bdffdbffc6541d40576a61))
* Implement shot telemetry event queuing and flushing mechanism ([ceef9ea](https://github.com/veryCrunchy/kovaaks/commit/ceef9ea507d4d8c06bd9f9eced2a93cf05870e06))
* Implement stats panel snapshot capture in file watcher ([8304272](https://github.com/veryCrunchy/kovaaks/commit/8304272836e76b09286deca8af59bd99f251addc))
* Improve pipe connection handling and instance validation ([13aa524](https://github.com/veryCrunchy/kovaaks/commit/13aa5242343404c41a2fa209bc2c818d8bedc012))
* improve tab layout and grouping ([117f68f](https://github.com/veryCrunchy/kovaaks/commit/117f68f0d833123b8332b2d38643133331255290))
* Integrate SQLite for session and replay asset management ([ce09a74](https://github.com/veryCrunchy/kovaaks/commit/ce09a7430510cbb29e557f253b5106ca707c03c1))
* **kmod:** add UE4SS bridge mod sources and production diagnostics ([b4c6248](https://github.com/veryCrunchy/kovaaks/commit/b4c6248d47ad780da88ef885c5430754f3a73c82))
* major ui improvements ([e3c2b1e](https://github.com/veryCrunchy/kovaaks/commit/e3c2b1e80f3c14b2e1d89ea39dcf0245ae79a447))
* many ux improvements ([15b2069](https://github.com/veryCrunchy/kovaaks/commit/15b206923b126c36dc6e5104ef0b5fb415bd9969))
* Migrate replay data to typed SQLite tables and enhance session snapshot handling ([41336dc](https://github.com/veryCrunchy/kovaaks/commit/41336dcad42b7462a25657b94e3c82e65abc13f9))
* Optimize live pull snapshot handling and cache metrics updates ([34bc988](https://github.com/veryCrunchy/kovaaks/commit/34bc9884cb6793ebe47394a1408bc2b78dca544d))
* **overlay:** add live run snapshot and scenario-aware VS mode ([ec7d354](https://github.com/veryCrunchy/kovaaks/commit/ec7d35458c304e0aaf8c4e971e980d597c215d8c))
* **performance:** enhance performance metrics calculation and display ([45635c8](https://github.com/veryCrunchy/kovaaks/commit/45635c80628861413fb8f8fe1d32f7f4396b4d7c))
* Refactor replay data handling to use ReplayPayloadData interface ([fd33486](https://github.com/veryCrunchy/kovaaks/commit/fd33486c19c77c228182117519c62acc2224a4c6))
* Refactor UI components to use design tokens for consistent styling ([d1cfb2b](https://github.com/veryCrunchy/kovaaks/commit/d1cfb2b6d27cb8b12e64a1d85e7d76778cee19d8))
* **release:** compute and use prerelease tags for dev and prod builds ([f58d5ef](https://github.com/veryCrunchy/kovaaks/commit/f58d5efd6b1b00605d627163ad6513771d0f74db))
* remove OCR pipeline and rebrand app as AimMod ([8d9fa74](https://github.com/veryCrunchy/kovaaks/commit/8d9fa74b5a57c21d639e13d545df1ecdbfcfe6fc))
* Replace session history retrieval with personal best score fetching ([0cb6cd4](https://github.com/veryCrunchy/kovaaks/commit/0cb6cd479d9ad1fa12e7bff74f0d0e116a72116a))
* **replay:** add in-game replay control commands ([7bd363a](https://github.com/veryCrunchy/kovaaks/commit/7bd363ac53f13b6503df48008425048aabea97dd))
* **replay:** add in-game replay controls and status indicators ([e851b64](https://github.com/veryCrunchy/kovaaks/commit/e851b64a64afd0520b291b6e45d4e93a48027f13))
* **state-sync:** implement state request handling and snapshot emission ([5e63d9a](https://github.com/veryCrunchy/kovaaks/commit/5e63d9aaa092d8dc82e000085e4f6657a2b7e3fb))
* Synchronize session tracking finalization with CSV completion ([1bdc3f0](https://github.com/veryCrunchy/kovaaks/commit/1bdc3f0504be0f4c582e74d7d9e11bded45f0816))
* **tauri:** stabilize bridge session lifecycle and replay capture ([16b0eff](https://github.com/veryCrunchy/kovaaks/commit/16b0eff097a15528b31e161ef7c9e966a6e38777))
* Update build process to use new frontend build script ([c29dd5b](https://github.com/veryCrunchy/kovaaks/commit/c29dd5bed4190523e5dad6497d813a9b09dabdba))
* Update database schema and add session run summary and timeline support ([5507b8c](https://github.com/veryCrunchy/kovaaks/commit/5507b8cd1b1a8ec7114c5927bb5ff765c1955b40))
* Update icon assets and add new sizes for improved display ([aceb1b4](https://github.com/veryCrunchy/kovaaks/commit/aceb1b4351abcff04f1c9fcbd83322490cdc49af))
* Update session history retrieval to use recent scenarios with deduplication ([5c7adaf](https://github.com/veryCrunchy/kovaaks/commit/5c7adafb4b8de9b387d67216c5f3d5785b8e0431))


### Bug Fixes

* add step to ensure Tauri resource path exists in CI workflow ([ec49d08](https://github.com/veryCrunchy/kovaaks/commit/ec49d08b55091bab1ef946a77d505435c29b080c))
* Adjust injection process age and ensure foreground window focus ([defeb18](https://github.com/veryCrunchy/kovaaks/commit/defeb18dfb429f18327ddeb4e540235343ab5a5c))
* **bridge:** improve state and score sync resilience ([cea6728](https://github.com/veryCrunchy/kovaaks/commit/cea6728c151ca50c378c778e57af632565a20b7e))
* Correct path for beforeBuildCommand in tauri configuration ([5e25897](https://github.com/veryCrunchy/kovaaks/commit/5e258979f0a7923c51ecc95028e2135d087b087a))
* enhance ReplayTab styling for better usability ([e620770](https://github.com/veryCrunchy/kovaaks/commit/e620770457842d8af5e9c787e234ea72c3fa3f7f))
* improve bridge connection logic ([6071100](https://github.com/veryCrunchy/kovaaks/commit/607110030f466cc01b6ba20528142c202619bb6f))
* Improve error handling for UE4SS injection and runtime deployment ([f3a68e6](https://github.com/veryCrunchy/kovaaks/commit/f3a68e6a9dfd5499731265a3ed58a798124cf7fd))
* Improve replay context handling with fallback mechanisms ([42098b2](https://github.com/veryCrunchy/kovaaks/commit/42098b27f6302116b0dd71860e6ef7d6f028458f))
* improve robocopy error handling and reset exit code for CI ([6417d4d](https://github.com/veryCrunchy/kovaaks/commit/6417d4dbf3167cba8abf1b2914168e6f8c7f060a))
* Prefer authoritative totals for accuracy and damage efficiency metrics ([71689e9](https://github.com/veryCrunchy/kovaaks/commit/71689e928ee8a67cd99c39d0936e1340a67a86f9))
* Prevent event propagation on dismiss button click ([447381d](https://github.com/veryCrunchy/kovaaks/commit/447381dea989eec4f4e271bc8a1999bb9c1f1330))
* redirect output of template submodule initialization to stderr ([c1039cb](https://github.com/veryCrunchy/kovaaks/commit/c1039cba047351594c0a993dd8a9c970429df437))
* Remove duplicate import of JetBrains Mono font ([65e7d4f](https://github.com/veryCrunchy/kovaaks/commit/65e7d4f9b99aacc2ea392d8062f24407a9bdcf26))
* Remove unnecessary checks for UE4SS-settings.ini in build scripts ([41e180f](https://github.com/veryCrunchy/kovaaks/commit/41e180f79e62bf47e0cbeea01f06d03b6e958227))
* Simplify beforeBuildCommand to directly reference build script ([1ff3a63](https://github.com/veryCrunchy/kovaaks/commit/1ff3a636b5e8c9969fc51ec0cd87067fe07cb79c))
* simplify Rust check command and update AimMod staging steps ([b32e4e1](https://github.com/veryCrunchy/kovaaks/commit/b32e4e19a8f60f3c86d5e4a46d12cae2e51548ce))
* unminimize and maximize stats window on open ([404c2bf](https://github.com/veryCrunchy/kovaaks/commit/404c2bfa7993c528f10163932f2e3ea67e20aeba))
* Update beforeBuildCommand to dynamically locate build script ([91ee22e](https://github.com/veryCrunchy/kovaaks/commit/91ee22e8bc8ba9b78fa9eb2f8b8ae134e27ab36c))
* Update event listener from "bridge-parsed-event" to "bridge-metric" ([322aa22](https://github.com/veryCrunchy/kovaaks/commit/322aa2284e38fde22abafb95bc7a906d5ec33762))
* Update mouse path recording messages for clarity ([6048a4a](https://github.com/veryCrunchy/kovaaks/commit/6048a4a412653f6ed2ab623ace7fcb3ee6df23fb))
* Update replay tick logic to improve state handling during playback ([cda6df2](https://github.com/veryCrunchy/kovaaks/commit/cda6df2370c03bde2fd6836ab0e9d35833fc2c32))
* Update updater JSON upload flag in dev workflow ([c513e3f](https://github.com/veryCrunchy/kovaaks/commit/c513e3fce5dba047cad16c8496fc2d279dd96ae6))

## [0.8.1](https://github.com/veryCrunchy/kovaaks/compare/v0.8.0...v0.8.1) (2026-02-27)


### Bug Fixes

* **mouse-hook:** reduce input latency by optimizing raw mouse event processing ([57a0432](https://github.com/veryCrunchy/kovaaks/commit/57a043231b383fae47c455e1e3882ddabc5b30c4))
* **window-tracker:** optimize overlay visibility handling and reassert TOPMOST state ([e855e59](https://github.com/veryCrunchy/kovaaks/commit/e855e59ecea341588f4f22cb965ce09c4400ac7a))

## [0.8.0](https://github.com/veryCrunchy/kovaaks/compare/v0.7.0...v0.8.0) (2026-02-27)


### Features

* **api:** add endpoints for fetching best score and scenario details ([61b2529](https://github.com/veryCrunchy/kovaaks/commit/61b2529bfb4fa157cf6d4661dc2fdaa724bf9398))
* **auto-setup:** enhance auto-setup loop with dynamic OCR capture rect and scenario detection ([0937f4e](https://github.com/veryCrunchy/kovaaks/commit/0937f4e2a2c1ad6021c6efa7f162248e85658afa))
* **leaderboard:** add LeaderboardBrowser component for scenario leaderboard display ([b59e47a](https://github.com/veryCrunchy/kovaaks/commit/b59e47a34d0e19d83d6cd25cad37c3a3b24f5d4a))
* **replay:** add replay loading and scenario search functionality ([fcde742](https://github.com/veryCrunchy/kovaaks/commit/fcde742a0d8df1176a09fed336203bc7b437bdfe))
* **replay:** enhance session handling with replay persistence ([e7ca82d](https://github.com/veryCrunchy/kovaaks/commit/e7ca82d3e15ff164fde15164b87ed2cb3f2e0acc))
* **replay:** implement replay storage and management for session data ([8edeb7d](https://github.com/veryCrunchy/kovaaks/commit/8edeb7ddc1b1404eddd0debe1b8782556c3ffc7d))
* **settings:** add tools section with session stats and logs buttons ([c0bbc9a](https://github.com/veryCrunchy/kovaaks/commit/c0bbc9a98d0af4c26c25c1329628ef71e3f12c83))
* **steam-integration:** add Steam user detection and friend import functionality ([5f97502](https://github.com/veryCrunchy/kovaaks/commit/5f97502cc7bb31d4d801da80b7c708e66c12a69a))


### Bug Fixes

* **feedback:** improve clarity of feedback messages for mouse performance ([7e76128](https://github.com/veryCrunchy/kovaaks/commit/7e76128410b2ae3b5a56f1c6b7947587dab0f06d))
* **mouse-path-viewer:** adjust image positioning and sizing for better alignment with canvas ([c249418](https://github.com/veryCrunchy/kovaaks/commit/c249418afbdf960539232a257ad2cf7279515b4d))
* **ocr:** improve OCR reliability across all per-field stat parsers ([b736b09](https://github.com/veryCrunchy/kovaaks/commit/b736b09ebac225329a80349ee7585025857ea463))
* **ocr:** remove hard upper bound on SPM in sanitize_reading function ([262a8da](https://github.com/veryCrunchy/kovaaks/commit/262a8da3248f8990d2c3e761fe3a57ea103477ba))
* **settings:** OCR region cleared on save, visibility not reloaded ([b553b44](https://github.com/veryCrunchy/kovaaks/commit/b553b449eecf49a9d5513c12115a698ac19df207))
* **single-instance:** improve process termination logic for overlay ([3923312](https://github.com/veryCrunchy/kovaaks/commit/3923312af2e5de7d72b2994e4e5535afa14e8e33))
* **stats-overview:** improve language use to be easier to understand ([2942fe2](https://github.com/veryCrunchy/kovaaks/commit/2942fe274b0c7480a14b8f6a90b3959eb35baeb2))
* **stats:** make session stats window visible by default ([b68ea07](https://github.com/veryCrunchy/kovaaks/commit/b68ea0710d22f26ff6030fbb111f3541da2958af))

## [0.7.0](https://github.com/veryCrunchy/kovaaks/compare/v0.6.1...v0.7.0) (2026-02-26)


### Features

* **friend-manager:** add search type toggle for friend addition ([e63426c](https://github.com/veryCrunchy/kovaaks/commit/e63426c25fb6dbc3e805c43bae79024831ea8af5))
* **friend-manager:** enhance friend addition with Steam profile resolution ([158e0f4](https://github.com/veryCrunchy/kovaaks/commit/158e0f486d1c0f2cac13454ab28e9f3a75fa1d8c))
* **mouse-hook:** enhance raw mouse event handling for improved metrics accuracy ([3bcea1f](https://github.com/veryCrunchy/kovaaks/commit/3bcea1f3ae3021413d46a587d4f20a6b42199895))


### Bug Fixes

* **FieldGroup:** change description type from string to React.ReactNode ([8466e7e](https://github.com/veryCrunchy/kovaaks/commit/8466e7e8c39f4ac72ffd8cc828e42a565084d80b))

## [0.6.1](https://github.com/veryCrunchy/kovaaks/compare/v0.6.0...v0.6.1) (2026-02-25)


### Bug Fixes

* **mouse-hook:** add Windows Raw Input support for accurate mouse tracking ([9e7e346](https://github.com/veryCrunchy/kovaaks/commit/9e7e34641331ee424198ab6bc9c4d6437ad3279d))

## [0.6.0](https://github.com/veryCrunchy/kovaaks/compare/v0.5.0...v0.6.0) (2026-02-25)


### Features

* **mouse-path-viewer:** add mouse path visualization and sensitivity suggestions ([be7ecee](https://github.com/veryCrunchy/kovaaks/commit/be7ecee32c7a204a85a46bfd955d075c5b749812))
* **mouse-path:** raw cursor path recording for post-session visualisation ([58220e1](https://github.com/veryCrunchy/kovaaks/commit/58220e1ff804ffa5be23381ffab27c609614c45a))
* **screen-recorder:** low-res JPEG session recording for mouse-path underlay ([d2fad2d](https://github.com/veryCrunchy/kovaaks/commit/d2fad2df05450bcb4d8ac3ddaf58a0e9098d33c1))
* **session:** add commands to retrieve session mouse data, raw positions, and screen frames ([e50cfc6](https://github.com/veryCrunchy/kovaaks/commit/e50cfc60c8a571af341b5df445a43a21253165bb))
* update kovaaks-overlay version and adjust monitor rectangle synchronization ([0603190](https://github.com/veryCrunchy/kovaaks/commit/0603190c1f39b9d9c87df4bd4e4c6cede812239e))


### Bug Fixes

* **scenario:** strip challenge-mode suffixes; migrate existing sessions ([d916fce](https://github.com/veryCrunchy/kovaaks/commit/d916fcea17d7d28f786941db8dfc0cdae8ddf645))
* **settings:** add serde defaults, log deser errors, migrate region→spm on startup ([d7a831e](https://github.com/veryCrunchy/kovaaks/commit/d7a831e919925ff3555850327c9d1bda54404d78))
* **stats-ocr:** fix damage parse for X/Y format, prevent MultiHit misclassification ([bb3499b](https://github.com/veryCrunchy/kovaaks/commit/bb3499b2a691f6ce529392253b53d78a5a103236))

## [0.5.0](https://github.com/veryCrunchy/kovaaks/compare/v0.4.0...v0.5.0) (2026-02-25)


### Features

* add session stats tab and integrate StatsWindow component ([6efbe2f](https://github.com/veryCrunchy/kovaaks/commit/6efbe2f2b1d00f501e2fcc6ed19c95773c6e3a94))
* **animation:** add spin and pulse keyframes for animations ([74fa88f](https://github.com/veryCrunchy/kovaaks/commit/74fa88fc6aeaaaf90a2438e239d2821e17fe7265))
* **auto-setup:** add auto-setup mode and HUD integration ([90a9c6c](https://github.com/veryCrunchy/kovaaks/commit/90a9c6c4b84e24f035898d8f316f039daf7f6dff))
* **ocr:** add support for capturing words with bounding boxes ([e212e13](https://github.com/veryCrunchy/kovaaks/commit/e212e13d7b27a0fd58946ad962ab846963cab5b9))
* **spm:** deprecate legacy region field, migrate to stats_field_regions.spm ([8a0d071](https://github.com/veryCrunchy/kovaaks/commit/8a0d07117e8bef260fc76d958bd77d6c389c971c))


### Bug Fixes

* **auto-setup:** remove same-row filter that dropped vertically-stacked fields ([a6b8e64](https://github.com/veryCrunchy/kovaaks/commit/a6b8e64f4e56dcfec1d221acd604177330f80177))
* enable createUpdaterArtifacts to produce .nsis.zip.sig for updater ([f49fb65](https://github.com/veryCrunchy/kovaaks/commit/f49fb6558f9254cca9384e7d65d47913226464da))
* remove unused trayIcon configuration from tauri.conf.json ([19a3c76](https://github.com/veryCrunchy/kovaaks/commit/19a3c76a8750cfba80698a21004e334a3ca1b85b))
* update settings panel to be a floating overlay with backdrop dismiss ([c5af49e](https://github.com/veryCrunchy/kovaaks/commit/c5af49eab12698428375d4f83eea3135d0d259df))

## [0.4.0](https://github.com/veryCrunchy/kovaaks/compare/v0.3.0...v0.4.0) (2026-02-25)


### Features

* add README with features, installation instructions, and screenshots; update kovaaks-overlay version to 0.2.3 ([ba8b69b](https://github.com/veryCrunchy/kovaaks/commit/ba8b69b53218272c7c348bd8f9a898b6e3f95085))


### Bug Fixes

* explicitly pass --target to tauri-action so updater sig is found ([bcf9319](https://github.com/veryCrunchy/kovaaks/commit/bcf9319060803047f0aa782b167b8b86aecf41e9))

## [0.3.0](https://github.com/veryCrunchy/kovaaks/compare/v0.2.3...v0.3.0) (2026-02-25)


### Features

* add scale/resize support to DraggableHUD ([84242b4](https://github.com/veryCrunchy/kovaaks/commit/84242b4611312f77a3a5e1f2e76bbf8e865d480d))
* expand StatsWindow with new metrics, charts, and live feedback hook ([4f172f2](https://github.com/veryCrunchy/kovaaks/commit/4f172f26a2ecf80b28b3fb7746e4b4047a8500fe))
* **ocr:** per-field stats OCR with idle feedback suppression ([3bc3749](https://github.com/veryCrunchy/kovaaks/commit/3bc37499b1b14f199daeff2c13c5d233b6a70d01))
* **overlay:** StatsHUD, DebugStatsOCR, LiveFeedbackToast, PostSessionOverview ([632f60a](https://github.com/veryCrunchy/kovaaks/commit/632f60acfbabb0c08a8e396b0075ae676f920fa1))
* **settings:** unified region picker with per-field stats regions ([d87c821](https://github.com/veryCrunchy/kovaaks/commit/d87c8216ce5fc2506b03a4e78de775eb1417ed56))
* **types:** add StatsFieldRegions and per-field types ([2603901](https://github.com/veryCrunchy/kovaaks/commit/2603901ae492def0867f17ee31e62760a11d0454))


### Bug Fixes

* sync selected_friend to settings state when opponent changes ([df3c18e](https://github.com/veryCrunchy/kovaaks/commit/df3c18e8deca4c995c8d89de47188a148efa3e18))
* use IDLE instead of WAITING label when no metrics available ([2200026](https://github.com/veryCrunchy/kovaaks/commit/220002674b11415d788228fff84c588e6da124f7))

## [0.2.3](https://github.com/veryCrunchy/kovaaks/compare/v0.2.2...v0.2.3) (2026-02-25)


### Bug Fixes

* remove apostrophe from productName to fix NSIS build ([152f6b2](https://github.com/veryCrunchy/kovaaks/commit/152f6b2892580815f47e7d6e4046302ad3f9afea))

## [0.2.2](https://github.com/veryCrunchy/kovaaks/compare/v0.2.1...v0.2.2) (2026-02-25)


### Bug Fixes

* downgrade @tauri-apps/cli version to 2.9.4 in package.json and pnpm-lock.yaml ([55b0713](https://github.com/veryCrunchy/kovaaks/commit/55b07131179124980afb320f82370da4117c7e9e))

## [0.2.1](https://github.com/veryCrunchy/kovaaks/compare/v0.2.0...v0.2.1) (2026-02-25)


### Bug Fixes

* update @tauri-apps/cli version to 2.9.6 in package.json and pnpm-lock.yaml ([1614258](https://github.com/veryCrunchy/kovaaks/commit/1614258806848c042fc630a49cb547c8f648bbd3))

## [0.2.0](https://github.com/veryCrunchy/kovaaks/compare/kovaaks-overlay-v0.1.0...kovaaks-overlay-v0.2.0) (2026-02-25)


### Features

* add CI and Release workflows for automated builds and deployments ([3045860](https://github.com/veryCrunchy/kovaaks/commit/30458609aaa38b86723d5eac725863fefa830e64))
* add initial Tauri configuration for KovaaK's Overlay ([590ecec](https://github.com/veryCrunchy/kovaaks/commit/590ececf4d199658a907aad078dcabe7d0443352))
* app shell, settings persistence and region picker ([6d8fc7c](https://github.com/veryCrunchy/kovaaks/commit/6d8fc7c3a006d82e4c325c0c5bfd1bf95f0e0d9b))
* CSV file watcher for session start/end detection ([75a303c](https://github.com/veryCrunchy/kovaaks/commit/75a303c0ea74a9867c8f4e5408b8202625493c6d))
* in-app log viewer and auto-updater ([4f7741e](https://github.com/veryCrunchy/kovaaks/commit/4f7741e41821a20a34c8fb3b39fe66287ff2961f))
* KovaaK's API client, VS Mode, and friend score comparison ([dc63765](https://github.com/veryCrunchy/kovaaks/commit/dc6376573ab7d140adb51729dce3f867a54e821a))
* local scenario index with fuzzy OCR correction and validation cache ([e18c086](https://github.com/veryCrunchy/kovaaks/commit/e18c0867e47fd9891286813eeb424bb4edf2981e))
* mouse hook, smoothness metrics, and live HUD ([eee64a9](https://github.com/veryCrunchy/kovaaks/commit/eee64a9703118a1a21791a501a56f688d01dc1ea))
* screen capture and OCR via Windows.Media.Ocr ([a1e2010](https://github.com/veryCrunchy/kovaaks/commit/a1e2010826730bd85b1d927fb537bf77806ae933))
* session history store and stats analytics window ([1f343bd](https://github.com/veryCrunchy/kovaaks/commit/1f343bdcbe483c43cf7bdf900647772e9b03e2e0))
* window tracker — follow KovaaK's focus, assert TOPMOST overlay ([84cedc3](https://github.com/veryCrunchy/kovaaks/commit/84cedc35733291df432d61031887c6eb8424645f))
