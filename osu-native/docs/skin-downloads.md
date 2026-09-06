# Skin Download Flow

AimMod first tries the original supported HTTPS, Google Drive, or MediaFire
download. When the provider requires interaction, an isolated browser window
opens on the selected skin's source page. The user completes any provider
verification normally; AimMod does not solve or bypass it.

The window uses an installed Microsoft Edge, Google Chrome, or Chromium browser.
It has its own temporary profile and does not use the user's normal browser
profile, cookies, or default downloads folder. Windows tries Edge first. Linux
tries Chrome, Edge, and installed Chromium. A missing compatible browser produces
an actionable error; AimMod does not install browsers or change the default browser.

## Actions

- **Download skin** prepares a validated temporary skin for later saving/import.
- **Download & save** writes the validated `.osk` into the configured save folder.
- **Download & import** passes the validated `.osk` to the selected osu! client.
- When a provider's catalog cannot be read, select that provider and use
  **Browse & download**, **Browse & save**, or **Browse & import**.

Only `.osk` and `.zip` download candidates are retained long enough for validation.
Other downloads are cancelled and discarded. A candidate must be a valid ZIP with
a recognizable skin configuration; unsafe paths, duplicate entries, scripts,
executables, oversized archives, and suspicious expansion ratios are rejected.
Validation happens again before saving or importing.

The browser and its child windows close after a completed or rejected download.
Closing the window, changing selection, or leaving the workspace cancels the
pending operation. Incomplete sessions time out after ten minutes. Temporary
downloads and the isolated profile are removed; normal browser windows are not
closed. Profiles that remain locked are included in expired preview cleanup.

Direct download caching remains enabled. Browser-selected files are not cached
under the starting catalog URL because a user can navigate to a different skin
or variant. The original suggested filename is retained for the save action,
subject to filename sanitization. All downloads stay on the original hosts;
there is no public AimMod skin archive mirror.

## Verification

The normal test suite covers fallback selection, validation, cancellation results,
save/import continuation, and cache isolation with synthetic data. Explicit
`BrowserCapturesValidSkinsDiscardsOthersAndCloses` tests exercise an installed
browser against intercepted synthetic pages. They do not visit real providers or
complete verification challenges. Set `AIMMOD_SKIN_BROWSER_TEST_CHANNEL` to select
a different installed browser channel for these tests.
