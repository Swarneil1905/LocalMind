# Architecture artwork

The README uses [light](architecture-light.svg) and [dark](architecture-dark.svg) SVG drawings of the same system. GitHub selects the variant through a `picture` element. The SVG files are the editable source; no diagram service, generated image model or build step is required.

## What the diagram represents

- React exchanges commands and events with the Tauri/Rust desktop bridge.
- Rust calls the Python/FastAPI service over authenticated loopback HTTP and forwards streamed responses to the interface.
- Python uses Ollama for local inference and SQLite/LanceDB for profile data and retrieval. Model weights are stored separately from the profile.
- Web search and connected providers sit outside the computer boundary. They are optional; cloud chat and paid media also require approval. Their individual limits and data destinations are documented in the [user guide](../USER_GUIDE.md).

This is an overview, not a complete network or storage inventory. Update checks, model downloads, OS credential storage and WebView preferences are described in the [runtime guide](../DESKTOP_RUNTIME.md). The detailed [chat flow](../CODEBASE.md#follow-one-chat-message) remains the source of truth for request handling.

## Editing

Edit the SVG text directly or open it in a vector editor. Keep node positions, arrows and wording identical in both variants; only the palette changes. Preserve the `viewBox`, title, description and text labels. Avoid scripts, external fonts, linked images and `foreignObject` so the artwork remains self-contained.

Check both variants at README width and open the image at full size to inspect the labels. Local connections use solid bidirectional arrows. Optional external requests use a dashed arrow, so color is not the only way to tell them apart.

## Design references

The artwork is original to LocalMind. These projects informed the presentation and format choice:

- [System Design Primer](https://github.com/donnemartin/system-design-primer): architecture illustrations alongside explanatory prose.
- [Excalidraw](https://github.com/excalidraw/excalidraw): light/dark README artwork and editable vector exports.
- [Diagrams](https://github.com/mingrammer/diagrams): grouping architecture components and keeping their definitions in version control.
- [D2](https://github.com/d2lang/d2): an alternative for larger diagrams that need generated layouts and SVG exports.

The existing Mermaid diagram in the codebase guide remains useful for tracing implementation details. No application diagram renderer or dependency changes are included in this documentation update.
