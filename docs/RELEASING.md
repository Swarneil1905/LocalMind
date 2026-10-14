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
- Linux builds use `scripts/release/build_linux.py` to expose Ollama's common
  `lib/ollama` directory through `LD_LIBRARY_PATH` only in packaging subprocesses.
  Ollama normally adds this directory when launching its runners; linuxdeploy
  inspects each CUDA library separately and otherwise cannot find the sibling
  `libggml-base.so.0`. See [Ollama's runner environment](https://github.com/ollama/ollama/blob/v0.40.3/llm/llama_server.go).
  The wrapper checks shared-library dependencies with `ldd` before compilation.
  It keeps CUDA variants separate and leaves the subsequent installer smoke
  tests free of staging paths, so missing packaged files cannot be hidden.
- `libcuda.so.1` is supplied by the user's NVIDIA driver, not by Ollama's CUDA
  runtime archive. The CPU-only CI runner does not need that driver to package
  the app. Only the CUDA v12/v13 backend checks allow this missing host library;
  missing GGML, cuBLAS, CUDA runtime, and other dependencies still fail the build.
  The wrapper uses SHA-256-pinned linuxdeploy `07333c6` from Tauri's official
  binary releases in a temporary tool cache. Tauri CLI 2.11.2's default older
  tool cannot exclude an absent dependency. `LINUXDEPLOY_EXCLUDED_LIBRARIES`
  is scoped to packaging and excludes only `libcuda.so*`, keeping the driver
  out of the AppImage while retaining both CUDA backends. Review the tool cache
  filename when upgrading Tauri CLI. Python CI runs a real linuxdeploy fixture
  that first reproduces the missing-driver error and then verifies packaging
  with the exclusion, without installing an NVIDIA driver or using a GPU.
- Updater signatures are checked with the existing public key. The
  `TAURI_SIGNING_PRIVATE_KEY` repository secret must match that key. Never rotate
  the key merely to fix a build: installed versions trust the existing key.
- Tauri's Debian package uses gzip and can exceed the GitHub asset limit with
  the complete Ollama runtime. Its tar writer also expands resource symlinks,
  storing large CUDA library aliases as separate full copies. xz compression
  alone does not remove those copies. Before recompression, the Linux wrapper
  groups regular files within the packaged Ollama runtime by size, mode, owner,
  and SHA-256, then replaces identical copies with hard links. Every installed
  pathname and its contents remain available; existing symlinks, control files,
  and files with different permissions are left intact. It logs the duplicate
  bytes removed, then repacks with `dpkg-deb` using xz level 9. It
  checks the compressed size and signs the final bytes before replacing the
  original package. Installer smoke tests then extract and run that final DEB.
  The AppImage updater and its signature are left intact. The 2 GiB limit is
  still enforced; reducing the package does not remove CPU or GPU libraries.
  The final compressed size is logged before validation so an oversized future
  runtime is diagnosable. A native Linux regression checks the extracted hard
  links, payload bytes, permissions, symlinks, and control scripts.
- The macOS wrapper retries bundling at most twice, only after `hdiutil` reports
  a busy eject/unmount. It reads `hdiutil info -plist`, detaches only writable
  temporary images in the current target's bundle directory with the exact
  product/version/architecture name, and removes those detached intermediates.
  Retries use `tauri bundle`, avoiding another Rust compile. Other failures stop
  immediately; unrelated mounted images are never detached.
- Each Mac runner tests disk-image recovery before building. The native fixture
  creates a blank 64 MiB HFS+ writable image with `-type UDIF` and an explicit
  SPUD partition layout. Do not use `-format UDRW` for blank-image creation:
  `hdiutil` requires `-srcfolder` or `-srcdevice` with `-format`. The fixture's
  create/attach commands have timeouts and expose native diagnostics in the job
  log. A fixture failure remains a failing check rather than being skipped.
- Assembly requires all four platforms, checks hashes and filenames, and rejects
  assets of 2 GiB or larger. `SHA256SUMS.txt` covers all other published assets.

The publisher uploads to a draft, verifies GitHub's uploaded sizes and SHA-256
digests, then publishes it as the latest release. It creates the version tag at
the tested commit if necessary. It never moves an existing tag or replaces a
public release. A failed build leaves the previous release available.

Draft creation uses GitHub's REST response directly, retaining the release ID and
its upload URL. Uploads, asset listing, verification, and publication use that ID;
they do not need to rediscover a newly created draft by its tag. Files stream from
disk. GitHub's returned filename, size, upload state, and SHA-256 must match before
publication. See the [release assets API](https://docs.github.com/en/rest/releases/assets).
Manual and tag-triggered Release runs share one concurrency group so they cannot
write to the same draft simultaneously.

The verification/publishing job runs on Ubuntu 24.04, whose official repositories
provide `minisign`. Ubuntu 22.04 does not package that tool. Linux installers
still build on 22.04; changing the publisher does not change their runtime
compatibility. Signature verification remains required before publication.

## Failure recovery

Read the failed job's logs before retrying. A transient download/upload failure
can be retried on the same commit. Code or packaging fixes require a new commit
and a fresh workflow run. An incomplete draft with assets can be resumed only by
the same tested commit. Matching uploaded files are reused; incomplete or mismatched
files with expected names are replaced. Unexpected assets and duplicate drafts
stop publication for inspection instead of being deleted automatically.

The earlier publisher could create a draft and then fail before uploading anything.
An empty draft can be reused for a newly tested commit only when it was created by
`github-actions[bot]`, has the exact generated title and release notes, and targets
an explicit commit SHA. The existing tag must already match the new tested commit
(or be absent). A draft containing any assets, a manually authored draft, and a
public release are never retargeted automatically. The log identifies the draft
ID and tested SHA. Inspect other obsolete drafts in GitHub Releases before removing
them; do not delete a public release.

Transient read failures have bounded retries. Mutating requests are not blindly
repeated after a lost response: draft creation or an upload may already have
succeeded. Inspect the existing draft and rerun the same tested commit. If the
final publication response is lost, check GitHub Releases first; an already public
release will deliberately be refused on a rerun. Rerunning an old failed workflow
does not load a publisher fix from a newer commit.

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
