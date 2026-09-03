# osu!lazer workspace design QA

Source visual truth:

- `/home/crunchy/.codex/generated_images/01a05e02-5e71-7ca1-b89a-c1c8557759d2/exec-ad307a8b-582e-4903-b447-0881669a223f.png`
- `/home/crunchy/.codex/generated_images/01a05e02-5e71-7ca1-b89a-c1c8557759d2/exec-6c9873d0-1930-4a69-9dda-5003e27d1ca7.png`
- User-selected hybrid: option 1 structure and cleanliness, option 2 `aimmod!lazer` identity and filter depth, with no hexagonal border or page-wide skew.

Implementation evidence:

- `/home/crunchy/src/aimmod-osu-foundation/client-osu-checkpoint.png`
- Route: `http://127.0.0.1:1420/stats.html`
- State: osu!lazer game selected, Beatmaps tab selected, first beatmap selected.
- Browser viewport and implementation capture: 1280 x 720 CSS px, 1280 x 720 image pixels, device scale factor 1.
- Source images: 1672 x 941 pixels. Both sources and the implementation use a desktop 16:9 composition; comparison used full-frame aspect normalization rather than pixel-perfect scaling.

## Full-view comparison evidence

The implementation matches the selected high-level composition: flat AimMod shell, rectangular filter drawer, central artwork-backed beatmap list, right-side selected-map detail, persistent bottom action rail, and restrained lazer color accents. The implementation correctly avoids the rejected hexagonal filter surround and whole-page skew. It also removes roadmap, preview, MVP, and external-search copy.

## Focused region comparison evidence

The left filter drawer and top/list typography were compared at original image resolution because these details are too small to judge from composition alone. The implementation currently exposes only provider, maximum star rating, skillset, and local-library filters. Option 2 also shows ranked status, BPM, length, and unplayed controls. The implementation uses inherited monospace text at a materially smaller optical size than both selected sources, which use a friendly rounded sans for primary UI text.

## Findings

- [P1] The filter drawer is missing requested controls.
  - Location: Beatmaps left filter drawer.
  - Evidence: the selected option 2 reference includes provider, star range, ranked status, BPM range, length, skillset, and only-unplayed controls. The implementation omits ranked status, BPM, length, and only-unplayed.
  - Impact: the filter panel loses the main part of option 2 the user explicitly selected and cannot express common beatmap searches.
  - Fix: add the missing controls using the existing rectangular drawer and wire each to visible client-side filtering or the search request.

- [P2] Primary UI typography is too small and terminal-like.
  - Location: brand, navigation, search, filter labels, beatmap rows, and selected-map details.
  - Evidence: both selected references use larger rounded sans text with clear title/body separation. The implementation inherits AimMod's compact monospace face almost everywhere and many labels render below a comfortable desktop reading size at 1280 x 720.
  - Impact: the osu workspace feels like a dense developer console instead of the familiar lazer-adjacent product selected by the user.
  - Fix: scope a rounded osu-like sans to `.osu-shell`, increase the optical sizes and line heights of primary controls, and retain mono only for compact numeric metadata where it helps scanning.

## Required fidelity surfaces

- Fonts and typography: blocked by P2. Hierarchy exists, but family and optical sizing drift from the selected references.
- Spacing and layout rhythm: acceptable. The three-column frame, bottom rail, selected row, and rectangular filter drawer are coherent at 1280 x 720.
- Colors and visual tokens: acceptable. Near-black, charcoal, osu pink, cyan, lime, and muted borders follow the selected direction.
- Image quality and asset fidelity: passed. Local generated artwork is sharp, consistently cropped, and used as real image assets rather than placeholders or CSS drawings.
- Copy and content: blocked by P1 because the visible product omits selected filter capabilities. Roadmap and release-status copy has been removed as requested.

## Comparison history

### Iteration 1

- Earlier findings: missing filter depth and undersized terminal-like typography.
- Fixes made: pending.
- Post-fix evidence: pending.

## Implementation checklist

1. Add ranked status, BPM, length, and only-unplayed filters.
2. Apply a rounded sans and increase primary text/control sizing.
3. Rebuild, recapture at 1280 x 720 in the same Beatmaps state, and repeat the visual comparison.
4. Test search, provider/filter changes, tab switching, selection, and import-queue actions; check the browser console.

## Follow-up polish

- [P3] Consider slightly stronger selected-row artwork contrast after typography is fixed.

final result: blocked
