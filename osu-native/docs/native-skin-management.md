# Native skin management

AimMod reads installed skins from osu!lazer's `Skin` Realm table through a private, transactionally consistent snapshot. The installed-skin query is bounded to 100 results per page, 10,000 skin rows, and 8,192 files per skin. It returns the real skin name, creator, content hash, built-in status, file count, and a `menu-background` preview reference when the skin contains one.

Preview images use the SHA-256 reference from the Realm snapshot and lazer's fixed hashed-file layout. AimMod does not enumerate the file store or guess asset paths.

Custom skins cannot be shared as live Realm objects between osu!lazer and AimMod. Each process owns a different Realm and file store. When a user selects a custom lazer skin, the sidecar copies exactly one skin's referenced files into private staging, verifies each SHA-256 hash, and hands those files to osu!'s native `SkinImporter`. AimMod then selects the imported `SkinInfo` through its existing `SkinManager`. Built-in skins use their stable ppy GUID and need no copy.

AimMod stores only the external skin GUID, external content hash, and imported local GUID in `cache/external-skin-mappings-v1.json`. Re-selecting an unchanged skin reuses the local import. A changed content hash creates a new local import instead of mutating lazer's copy.

`LazerPreferencesMonitor` follows lazer's selected skin GUID. AimMod applies that skin on connection and when lazer changes it. Selecting another skin inside AimMod changes AimMod's replay player only. It does not write to lazer's `game.ini`, Realm, or file store.

Online skin catalogs are deliberately outside this path. The native screen shows installed lazer skins only and does not scrape `skins.osuck.net`, `osuskins.net`, or any Cloudflare-protected page.
