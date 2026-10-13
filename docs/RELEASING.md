# Releasing LocalMind

Update the version together in `apps/desktop/src-tauri/tauri.conf.json`,
`apps/desktop/src-tauri/Cargo.toml`, and the `localmind-app` entry in `Cargo.lock`.
Add `docs/releases/vX.Y.Z.md`. Do not replace an already published version.

Push the changes to `main` and run the Release workflow with `publish=true`.
The workflow checks the exact dispatched commit, runs all three CI workflows,
then builds each platform on a matching native runner. `publish=false` runs the
same build and verification steps without creating a public release. Version
tags also trigger the workflow when the tag matches the configured version.

## Build and verification

- Python 3.11, Node 22, and pnpm 11.0.9 are used consistently by the release.
- The frozen Python service is tested against a disposable profile before and
  after installer packaging. Tests check authentication, chats, project/task
  persistence, restart, and cleanup without accessing a user's accounts.
  Linux checks both the extracted DEB and AppImage service payloads. AppImage
  extraction does not launch the desktop GUI or require FUSE.
- Ollama 0.40.3 is downloaded from its official release with digest verification.
  Its complete standard archive layout, including runtime libraries and notices,
  is included as a resource. Its loopback API is tested with an empty model store.
- macOS Intel and Apple Silicon use different native runners and unique updater
  archive names. macOS 14 is the minimum supported version for the bundled runtime.
- Linux packaging sets `NO_STRIP=true` for linuxdeploy. Its bundled strip tool
  cannot handle the `.relr.dyn` sections in newer WebKit libraries, and stripping
  a PyInstaller executable can damage its appended Python archive. Keep this
  setting when updating the packaging tools. See the
  [upstream Tauri issue](https://github.com/tauri-apps/tauri/issues/14796).
- Tauri runs with `--verbose` so a packaging failure includes linuxdeploy's
  actual error instead of only `failed to run linuxdeploy`.
- Updater signatures are checked with the existing public key. The
  `TAURI_SIGNING_PRIVATE_KEY` repository secret must match that key. Never rotate
  the key merely to fix a build: installed versions trust the existing key.
- Assembly requires all four platforms, checks hashes and filenames, and rejects
  assets of 2 GiB or larger. `SHA256SUMS.txt` covers all other published assets.

The publisher uploads to a draft, verifies GitHub's uploaded sizes and SHA-256
digests, then publishes it as the latest release. It creates the version tag at
the tested commit if necessary. It never moves an existing tag or replaces a
public release. A failed build leaves the previous release available.

## Failure recovery

Read the failed job's logs before retrying. A transient download/upload failure
can be retried on the same commit. Code or packaging fixes require a new commit
and a fresh workflow run. An incomplete draft can be resumed only by the same
tested commit; inspect its assets before making any manual change.

A rerun of a failed tagged workflow still uses the original tagged commit. After
committing a packaging fix, push the commit and update the failed, unpublished
version tag to that commit before pushing the tag again. Check that no release
has been published for that version, and cancel any remaining jobs from the old
run first. Use an explicit tag lease when pushing the replacement so another
person's tag change cannot be overwritten. Never move a published release tag.

GitHub's updater signing is separate from OS signing. These builds are not
Windows Authenticode-signed or Apple-notarized. The service smoke checks do not
prove model inference quality or every native window interaction; test those on
the intended hardware before recommending a model or distributing widely.

macOS uses ad-hoc signing with hardened runtime disabled so the frozen Python
service can load its extracted libraries. Before notarization, configure a
Developer ID identity consistently for Tauri and PyInstaller, then restore
hardened runtime with the required entitlements and repeat the payload tests.
