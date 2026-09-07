# Evidence in PP targets

An official PP calculation answers what a completed score would award. It does not establish whether a player can complete the map.

## Personal evidence

- Use explicit pass/fail outcomes from recent history and native lazer scores. Do not infer a successful clear from a legacy replay's accuracy or score alone.
- Best-score listings are selected successes. They can support account-score calculations, but must not train pass frequency.
- Compare the same mod configuration, including custom settings such as clock rate.
- Prefer at least three recent attempts on the target difficulty over outcomes on other maps. Sparse same-map outcomes remain low confidence.
- Otherwise compare recent maps by difficulty, BPM and duration. Balance retries across maps; discount missing metadata. Longer-map extrapolation remains low confidence and cannot establish demonstrated stamina.
- Use median accuracy from completed comparable plays when available. Failed fragments cannot establish completed-score accuracy. Replay pattern evidence can refine the projected score; unknown patterns remain unknown.

## Rewards and ordering

- **Max PP** is the exact 100% full-combo ceiling for the chosen setup.
- The raw projected score's PP is conditional on completing that score. It is shown as such in details.
- **Expected PP per attempt** requires both pass evidence and score evidence, and weights the conditional score PP by estimated pass chance. It is unavailable without that support. A failed attempt contributes no ranked PP in this model; excluded automation/fail-altering mods must not establish pass evidence.
- Account gain is calculated by replacing the difficulty's best score and reweighting known plays, then multiplying the resulting gain by pass chance. Do not insert a probability-discounted score into the account's best-score list.
- Sort supported targets first, then stretch targets, then unverified maps. Apply the selected sorting mode within those groups. Expected-PP filters use the evidence-backed value, never the full-combo ceiling or unsupported fallback.
- Supported means evidence exists, not a guaranteed pass. The supported/stretch boundary uses an estimated pass chance and skill fit of at least 0.5. These are heuristics, not calibrated guarantees. Show the sample counts, uncertainty and comparison type in details.

Old workspace estimates are invalidated when the evidence model changes. Unknown values must remain distinct from zero.

## Discovery and calculation budget

Discovery now searches up to 120 catalog pages and 6,000 unique sets. In addition to broad rating, play-count and favourites queries, focused 0.75-star bands search near the player's preferred difficulty range while respecting the visible star filters. The first 12-page snapshot can start calculations while discovery continues. Partial or rate-limited coverage remains explicitly labelled.

Up to 2,000 difficulties receive exact PP and pattern calculations. Most of this budget goes to the strongest supported candidates, with one fifth reserved for variety across stars, tempo and duration. Ranking accounts for useful account gain so a comfortable map with an already better known score does not crowd out equally suitable improvement opportunities. Existing pass-evidence and pattern-bottleneck requirements still apply.

Metadata filters run before evidence scoring. An index canonicalizes recent attempts by mod configuration once per immutable profile. Calculation batches hold 200 difficulties with at most three isolated workers; completed values are checkpointed every 25 calculations and flushed on exit or cancellation. The exact cache holds up to 16,384 entries, and the beatmap/geometry cache permits 8,192 files within its existing 256 MiB byte budget. Workspace cache version 10 invalidates earlier small-pool snapshots.

These are bounded searches, not proof that every ranked map has been evaluated or that a recommendation is globally optimal.
