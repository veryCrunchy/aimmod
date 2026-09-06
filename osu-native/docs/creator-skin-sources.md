# Creator-hosted skin catalog

AimMod links to original creator-published `.osk` releases. Archives are downloaded
to the user's device for preview, saving, and import; AimMod does not mirror them
on a public server. The bundled catalog is metadata, not skin content.

`Resources/Skins/creator-releases.json` pins selected releases so browsing and
filtering require no GitHub API calls. Updating the catalog is deliberate: the
newest release in a repository can be a different skin or a derivative, rather
than an update to the listed skin. Download counts are a snapshot, not live totals.

## Selected Sources

| Collection | Release | Variants | Source |
| --- | --- | --- | --- |
| Retome | v1.0 | 3 language variants | https://github.com/kisaragi-hiu/osuskin-retome/releases/tag/v1.0 |
| Story skin | 2024-12-08 | 1 | https://github.com/storycraft/osu-story-skin-edited/releases/tag/2024-12-08 |
| Mathyzin | 3 | 1 | https://github.com/Mathyzin/Mathyzin-Skins/releases/tag/3 |
| Monomal | v1.0 | 1 | https://github.com/UlyssesZh/monomal/releases/tag/v1.0 |
| yr32 | v0.6.0 | Dot and number; standard and 4K mania | https://github.com/yanorei32/yr32-skinbuilder/releases/tag/v0.6.0 |
| Project Minimalist | v1.2.1 | NM, HD, DT, InstaNM, InstaDT | https://github.com/PopCat19/Project-Minimalist/releases/tag/v1.2.1 |
| std::lite | v1.2.0 | 7; designed for lazer | https://github.com/sineplusx/lite/releases/tag/v1.2.0 |
| Ruby | v1.2.1 | 1 | https://github.com/eclipsedteam/Ruby/releases/tag/v1.2.1 |
| Simplify | v1.1 | 1 | https://github.com/eclipsedteam/Simplify/releases/tag/v1.1 |

## Rights And Attribution

A creator-hosted download is not a blanket redistribution license. Preserve the
original archive, creator, variant, and release link. Do not infer skin rights
from a catalog website's license or the license of a skin-management tool.

- Monomal declares MIT and credits JetBrains Mono under OFL-1.1:
  https://github.com/UlyssesZh/monomal/blob/v1.0/README.md
- yr32 declares BSD-2-Clause; its build generates graphics and sounds and uses
  third-party fonts: https://github.com/yanorei32/yr32-skinbuilder/tree/v0.6.0
- Project Minimalist explicitly permits sharing. Its README and license file
  differ (Unlicense versus 0BSD), and its credits include a template skin. Do not
  use this catalog as a full-archive redistribution clearance:
  https://github.com/PopCat19/Project-Minimalist
- std::lite excludes audio from its CC BY 4.0 grant. Audio credits include
  CC BY-NC 4.0 resources and permission whose downstream scope is unspecified:
  https://github.com/sineplusx/lite/blob/main/src/audio/LICENSES.md
- Ruby and Simplify declare MIT. Individual release contents still require a
  separate review before any public rehosting:
  https://github.com/eclipsedteam/Ruby and https://github.com/eclipsedteam/Simplify

## Adding A Source

1. Use the creator's repository and an explicitly published skin archive, not a
   scraped third-party archive collection or a guessed CDN path.
2. Pin the exact release and each intended variant; retain the GitHub asset ID,
   original filename, size, publish date, and download URL.
3. Verify the original download through AimMod's resolver and archive validator.
   Do not add authentication tokens or attempt to bypass verification pages.
4. Check the declared rulesets and client compatibility. Never substitute a
   different skin merely because its name is similar.
5. Run the catalog tests, and update their expected collection/variant inventory.

The importer continues to enforce host restrictions, download limits, and archive
validation. Broken or withdrawn upstream releases need a reviewed catalog update;
they must not silently redirect to unrelated mirrors.
