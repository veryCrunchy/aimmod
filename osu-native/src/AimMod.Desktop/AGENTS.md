# Desktop UI rules

Read `../../docs/ui-system.md` before changing application UI.

- Reuse `AimModVisualStyle`, `AimModPalette`, `AimModSectionHeader`, `AimModButton`, `AimModTextBox`, `AimModSearchBox`, and `AimModDropdown`.
- Preserve osu!-style range sliders: `AimModStarRatingFilter` for stars and `ShearedRangeSlider` for PP. Their spectrum and sheared handles are an explicit exception to the shared chrome rules.
- Do not add independent page padding, brand palettes, sheared text fields or dropdowns, oversized decorative cards, or a new primary navigation style.
- Improve shared components centrally when multiple routes need the same behaviour. Preserve existing bindables, option sets, cancellation, and actions during visual refactors.
- Verify rendered layout against other routes and check search/reset/menu behaviour. Keep captures and private data outside the repository.
- Popup layering is mandatory. Use the shared AimModDropdown popup ancestor scope; never use bare upstream dropdowns in application forms. Keep popup ancestors unmasked and verify an OPEN menu across all neighbouring rows at wide and narrow sizes. Include a layering regression test whenever adding or rearranging control groups; a closed-menu capture is insufficient.
