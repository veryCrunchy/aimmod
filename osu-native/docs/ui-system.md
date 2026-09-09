# AimMod desktop UI system

AimMod is one workspace for finding maps, reviewing plays and improving. A route changes the task, not the application's visual language.

## Application shell

- The sidebar is the only primary navigation. Group destinations into Library, Improve and App. Keep labels and icons visible; highlight the active route with mint on a muted mint surface.
- Every route uses `AimModVisualStyle.PagePadding`: 184-unit sidebar, 24-unit page inset, identical title origin. Do not pass independent page margins.
- Use `AimModSectionHeader`: 26-unit semibold title, 13-unit muted description, 64-unit header region. Avoid decorative uppercase category headings above page titles.
- Local tabs belong on a single row below the header, starting at Y=72. Use `AimModTabControl<T>` or selected `AimModButton` controls. Content with tabs begins at Y=120; content without tabs begins around Y=80.
- Keep the shell visible during normal page navigation. Task-specific overlays stay within the content area.

## Colour and surfaces

- Read colours from `AimModPalette`; never introduce page-specific brand palettes. Canvas, header, panel, raised panel and hover form a neutral green-grey tonal ladder.
- Mint is the primary action and active-selection colour. A primary action uses mint with dark text; secondary actions use a neutral surface and border. Selection uses muted mint, not a second primary action.
- Reserve red for destructive/error states, amber for caution and cyan for information or chart series. Difficulty ratings retain osu!'s semantic difficulty colours. Do not make unrelated actions pink or blue merely because an upstream widget does.
- Use 6-unit control radii and 8-unit card radii. Borders should define interactive controls and selected regions, not frame every block of text. No shearing on text fields or dropdown menus. Range sliders intentionally retain osu!'s native styling.

## Controls and spacing

- Text inputs: `AimModTextBox`. Search with optional result count: `AimModSearchBox`. Do not use raw `OsuTextBox`, `ShearedSearchTextBox` or `ShearedFilterTextBox` for new application forms.
- Dropdowns: `AimModDropdown<T>`. Existing score filters use `BoundedShearedDropdown<T>` as a compatibility adapter, with the same AimMod header and menu. Keep all model options, bindings, keyboard selection, and viewport-bounded scrolling.
- Popup stacking is relative to every ancestor, not just the menu. Put the filter container ahead of status text and results with a lower `Depth`, and leave popup ancestors unmasked. A low menu depth cannot escape a parent drawn behind a sibling. Verify with a menu open across the status/result boundary.
- Shared dropdowns temporarily raise their containing rows through `AimModPopupLayer` and restore depths on close or disposal. This is mandatory for every new dropdown. Do not bypass it with an upstream control, and do not rely only on fixed depth constants between form rows. Changes to control groups require an open-menu regression check at wide and narrow sizes.
- Ranges: preserve the original osu! sliders. Use `AimModStarRatingFilter` (osu!'s `FilterControl.DifficultyRangeSlider`) for the difficulty spectrum and `ShearedRangeSlider` for expected/max PP. Keep native sheared handles, displayed values, bindings and interactions. Do not replace these with text fields or custom neutral sliders. This is an intentional exception to the application chrome styling.
- Actions: `AimModButton`; reset controls: `AimModResetButton`. Use 36-unit standard height, 32-unit compact height, 14-unit horizontal button padding. Prefer one primary action per task area.
- Use 8-unit gaps between related controls, 24 between major sections. Align input baselines. Labels should be readable, concise and close to their fields.
- Search should update after a short debounce and remain editable during refresh. Put counts and loading status below filters; do not repeatedly cover an already loaded page with a blocking overlay.
- Show a clear reset action and a useful no-results state. Reset all relevant filters together. Search/filter the complete collection, not just the current display page.

## Lists, statistics and details

- Prefer compact rows for collections. Show title, difficulty, useful metadata and a clear next action. Keep row actions consistently aligned on the right.
- Expanded rows fit their actual content; cap long difficulty lists with scrolling. Do not reserve empty height for absent data.
- Use metric cards and charts for statistics. Distinguish missing values from zero. Avoid large explanatory cards when a number, label or short instruction is enough.
- Selection and detail panels use the same surfaces and spacing. Wide layouts may show an inspector alongside results. Narrow layouts must retain an explicit path to details and back; do not squeeze replay playback into an unusably small middle column.
- Explanations describe the user's task. Technical provenance, architecture and implementation status belong in documentation or diagnostics, not normal UI copy.

## Change review

### Feature discovery and next actions

- Give new workflows an entry point that describes what the player wants to do. Keep the destination workspace name visible alongside it.
- Put the next useful action before optional configuration. Advanced controls may collapse, but their values must survive closing and reopening.
- Show the current practice step, its purpose and progress together. Keep the full session and comparisons reachable without making every step compete for attention.
- Empty states must offer an action the player can take now. Results must offer a repeat, continuation or return to a real map.
- Reflect the selected mode honestly: fixed-song comparisons must not promise shuffle, and unavailable evidence must not become a completed step.

### Verification

1. Reuse a shared control or improve it centrally before creating a page-specific variant.
2. Inspect the changed screen alongside two other routes, at the same window size and theme. Check title origin, tab row, control height, colour roles and content density.
3. Exercise search, reset, empty state, dropdown overflow and the primary action. Confirm model options and bindings were preserved.
4. Check a narrower window where columns collapse. Preserve access to details and navigation.
5. Run relevant behaviour tests. Keep QA captures, local reports and private score/replay data outside the repository.

These rules cover application chrome and workflows. The embedded replay playfield and user-selected gameplay skin retain their own rendering.
