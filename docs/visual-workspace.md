# Visual workspace

This feature is in the current source build. Published installers require a new
release to include it.

Ask for a diagram, chart or supported learning simulation. A completed answer
can include an **Open visual workspace** card. Open it beside the conversation,
adjust the panel width or expand it. Opening a visual from mini chat restores the
full window and keeps the current draft.

## Edit and keep versions

Node diagrams support pan, zoom, renaming, moving, connecting and removing nodes.
Use **Arrange nodes** for a grid layout. Undo and redo apply to the open editor.
**Save revision** stores a new version with its parent; it never replaces the
original. Reopening the card loads the latest saved version. Select an older
version and save it to create a restored revision. Unsaved editor changes are
lost when the panel closes.

**Compare with original** shows the two specifications. **Use in next message**
puts the selected specification and version into the draft without sending it.
The next model answer proposes another visual; it does not silently overwrite
your saved edits. Automatic lineage between separate answer cards is not yet
available.

Export node diagrams as PNG, SVG or JSON. JSON preserves the editable structure.
Importing JSON validates it and creates a revision. Exporting does not create a
revision. Mermaid, charts and widgets currently export JSON only. Mermaid source
can be edited, saved and rendered after validation. Invalid diagrams retain their
source so you can correct it or include it in a follow-up.

## Charts, widgets and images

Charts support bounded line, bar and scatter data, series controls, point labels
and a source table. Illustrative values are labelled. A model's interpretation
of a document is not an independently verified measurement; compare plotted
values with the disclosed source excerpts.

The trusted widgets cover gradient descent on x², compound growth and a small
two-layer network. Sliders update local calculations without calling a model.
The network uses tanh followed by a fixed output weight of two. Its displayed
gradient is checked against numerical derivatives in tests. These teaching
examples do not train your chat model.

Saved chat images have an **Open image workspace** action with zoom, dimensions
and PNG download. Image references are restricted to the current conversation.
Generated-image prompts and provider metadata remain in Media.

## Data and execution boundaries

The service validates schema version 1 before rendering. It accepts diagrams,
Mermaid, charts, trusted widget configurations and local image references.
Documents are tied to their saved source message and conversation. Revision
writes check the expected version to prevent one window from overwriting another.
Deleting the conversation removes its visual records.

Limits include 100 KB per specification, 80 nodes, 160 edges, eight chart series
with 200 points each, and 100 revisions per visual. Mermaid uses strict rendering
and SVG sanitization. Remote links, HTML, image directives and executable actions
are rejected. PNG/SVG exports contain plain shapes and escaped text.

Document extraction runs in a disposable process with a 20-second parent timeout,
an 8 MiB input limit, and bounded page counts and excerpts. Linux also applies a
768 MiB address-space limit. macOS and Windows do not have that process memory
cap; the input and output limits do not bound a parser's peak memory use.

Arbitrary generated HTML or JavaScript is not supported. The opt-in prototype
in `scripts/evaluation/miniapp_probe.mjs` tests an opaque-origin iframe with a
restrictive content policy in a disposable browser. Its probes cover parent
document access, synthetic credential and bridge access, storage, network, files
and an infinite loop. It uses no real credentials or application profile.

The current decision is **do not ship this execution path**. The test blocked
the access probes, but an infinite loop prevented the host page from responding
until an external controller terminated the browser. A production design needs
an independently terminable process, resource budgets, narrowly defined messages
and native-platform isolation tests. Passing a Chromium prototype does not prove
isolation in the desktop app's WebViews.

## Verification

The regular test suites cover source ownership, immutable revisions, stale-write
conflicts, conversation deletion, document workers, image references, chart
validation, numerical gradients and export escaping. Windows and macOS CI also
run the visual/document persistence suite. Paid image generation, live OCR and
native Mac/Linux interactions need separate acceptance checks.

`scripts/evaluation/compare_vision.py --execute` compares approved local models
using synthetic screenshot fixtures. Add `--through-localmind` with `services/ai`
on `PYTHONPATH` to test the real attachment, history and chat route in a temporary
profile. These small checks measure exact reading and follow-ups, not general
vision quality. Downloads and GPU use require the user's approval.

For the mini-app research probe, provide Playwright through
`LOCALMIND_BROWSER_DEPS`, optionally set `LOCALMIND_BROWSER_EXECUTABLE`, and run
`node scripts/evaluation/miniapp_probe.mjs --execute`. Its local report is written
under `artifacts/visual-batch`; it is not a production security certification.
