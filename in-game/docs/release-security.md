# Release security and maintenance

Published feeds and manifests use ECDSA P-256 with SHA-256 and fixed-width
IEEE P1363 signatures, supported by the net8 clients. The signature covers
the complete UTF-8 payload, including channel, version, hashes, expiry and
release counter. The outer fields must equal the signed payload. Old clients
ignore the additional fields and continue their existing HTTPS/hash behavior.

## Enable signing

1. Run `in-game/install/New-ReleaseSigningKey.ps1 -OutputDirectory <private-directory> -KeyId release-1`
   locally in PowerShell 7. Choose a private directory outside every checkout.
   No production key is supplied by this repository.
2. Copy the printed public key map into `in-game/install-core/release-keys.json`.
   This map is embedded into both the installer and service at build time.
3. Store the contents of `release-private.pem` in the repository secret
   `AIMMOD_RELEASE_SIGNING_KEY`. Restrict access to the private file and retain
   an offline backup. Never commit it. Keep `UE4SS_GITHUB_TOKEN` configured for
   the trusted build job and `AIMMOD_UE4SS_ZIP_URL` for the verified runtime.
4. Build and distribute clients with the pin. Publishing fails if the secret
   is absent or its public key does not match exactly one embedded key ID.

The empty map is an explicit migration placeholder. Such builds retain legacy
behavior and provide no metadata authenticity guarantee. During migration,
distribute signed metadata while older clients still read its ordinary fields.
Once a client contains any pin it rejects unsigned, unknown-key, tampered and
expired metadata immediately; there is no network-controlled grace switch.
Signatures expire after 90 days. Publish a newer release before expiry, including
for channels that otherwise have no changes. Offline repair also verifies expiry.

For rotation, first distribute clients containing both old and new public keys,
then change the signing secret, and only remove the old public key after clients
have migrated. Never replace a key's value under an existing ID.

The updater remembers the highest accepted counter per channel, rejecting lower
counters and conflicting metadata at the same counter. This local state protects
against network replay, not a local user deleting their own update state. The
publisher increments the current channel counter under the publication lock.

## Publication

Maintain `in-game/version.txt` as the current stable base. Automatic beta versions
use its next minor version and the beta workflow run number. Publication must
strictly advance the current channel's SemVer. Each channel has one shared
publication concurrency group across callers; the current feed is downloaded
inside that lock. Stable publication updates stable only. Versioned uploads never
use overwrite, so retries cannot replace existing artifacts; use a new version
after a partially published release. Channel assets remain rolling pointers.

Metadata signing does not Authenticode-sign the Windows executable and does not
remove SmartScreen reputation warnings. Artifact attestations are separate.

## Private build data

The release workflow caches only NuGet packages. Private UE4SS source and build
trees are fetched and built afresh on trusted push or manual events. Fork pull
requests cannot run the private build job.

Maintainer action: inspect existing repository caches using `gh cache list`, then
delete every old `re-ue4ss-src-*`, `aimmod-core-build-*` and
`aimmod-steam-build-*` cache with `gh cache delete <cache-id>`. These may remain
readable to fork pull requests until removed. No caches are deleted by this change.
If caching is reintroduced, encrypt the complete archive with a secret before
saving it, authenticate it before extraction, and never cache plaintext source,
build trees or encryption keys. No encrypted cache is implemented here.

## Recovery

Elevated operations use a separate ProgramData recovery directory whose protected
ACL allows only Administrators and SYSTEM. An existing directory with an untrusted
owner or ACL is rejected, never adopted. Recovery is partitioned by validated
installation path and rejects journals naming another installation. All recovery
sources and destinations are checked for links and junctions before mutation.
Legacy per-user journals are never imported into elevated recovery storage.
Non-elevated recovery retains per-user state and still binds it to the selected
installation. Switching privilege level therefore does not expose the other
level's rollback history.
