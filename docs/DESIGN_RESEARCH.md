# LocalMind design research and implementation brief

Reviewed on October 10, 2026. This document records source research and the code audit that informed the redesign. Recommendations below are design decisions, not a claim that every item has shipped. Validation results belong with the implementation report.

## Product constraints

LocalMind is a personal desktop workspace for conversations, local documents, projects, and remembered context. The user explicitly chose **independent desktop installations for each person**. The main experience must make writing, reading, finding previous work, and recovering from failures straightforward. A website landing-page aesthetic, fake collaboration controls, and decorative dashboards do not serve that job.

The requested change covers the whole product, including the app identity. Improving one chat view while leaving emoji-heavy dashboards, inconsistent settings, or a stock application icon would leave the experience unfinished. Existing backend reliability work must survive the visual rewrite.

## Sources actually read

The [Snyk article](https://snyk.io/articles/top-claude-skills-ui-ux-engineers/) and [Kimi article](https://www.kimi.ai/resources/ui-ux-design-skills-for-agents) were discovery indexes. The implementation guidance below comes from the linked primary repositories. External skill files were reviewed as reference material; no global installer or bundled script was run.

### Anthropic frontend-design

The [current primary SKILL.md](https://github.com/anthropics/skills/blob/main/skills/frontend-design/SKILL.md) differs materially from the older summary in the Snyk article. It advocates an identity grounded in the product, a small token plan, deliberate typography, constrained reading lengths, and one memorable element surrounded by restraint. It explicitly identifies cream/serif/clay palettes, neon-on-black, newspaper styling, repeated rounded cards, and ornamental uppercase labels as common generated defaults. It calls for screenshot critique and useful recovery copy.

Application: use the LocalMind mark as the signature. Keep navigation and messages quiet; let task hierarchy determine spacing and surfaces. No claim that changing a font or removing purple is sufficient. Distinctiveness must coexist with ordinary, learnable controls.

### Vercel web interface guidelines

The [skill entry](https://github.com/vercel-labs/agent-skills/blob/main/skills/web-design-guidelines/SKILL.md) points to the [actual guidelines](https://github.com/vercel-labs/web-interface-guidelines/blob/main/command.md). Applicable rules cover semantic actions, explicit names for icon buttons and fields, visible focus, asynchronous announcements, keyboard access, reduced motion, long-content handling, destructive-action confirmation or undo, and correct native theming.

Application: keyboard-accessible rename and delete, native labelled selects where suitable, focus-visible styling, semantic status text, consistent light/dark controls, and date formatting with `Intl`. Copy tone follows LocalMind's sentence-case convention; source preferences that conflict with the product are not blindly combined.

### UI Craft, found through Kimi

The [primary skill](https://github.com/educlopez/ui-craft/blob/main/skills/ui-craft/SKILL.md) treats token consistency, purposeful information density, a restrained accent, and a distinctive detail as a system. It rejects emoji standing in for product icons, identical card grids, and decoration that has no task. Its [state-design reference](https://github.com/educlopez/ui-craft/blob/main/skills/ui-craft/references/state-design.md) explicitly includes loading, empty, error, partial, and offline states.

Application: retain one SVG icon family, add a reusable brand asset, share spacing and typography tokens, and specify actions in every empty or failure state. Do not add feedback buttons, sharing controls, or status claims without working persistence or real underlying functionality.

The [chat-specific reference](https://github.com/educlopez/ui-craft/blob/main/skills/ui-craft/references/ai-chat.md) supplies particularly relevant interaction rules: visible activity before the first token; a stop control throughout generation; progressive content; user-controlled scrolling; a persistent composer; narrow reading measure; clear citations; and distinct retry, regenerate, and continue semantics.

Application: a sent message and cancellable response state should appear promptly; scrolling up pauses automatic following; a new send resumes it. Sources should show enough title/domain/date context to inspect credibility. Model reasoning belongs in optional disclosure. Product copy must describe actual behavior: a button that only retries the same prompt should say Retry.

### 45ck architecture skills

Read the actual [component-boundary reviewer](https://github.com/45ck/software-architecture-skills/blob/main/.agents/skills/component-boundary-reviewer/SKILL.md), [quality-attribute scenario writer](https://github.com/45ck/software-architecture-skills/blob/main/.agents/skills/quality-attribute-scenario-writer/SKILL.md), and [ADR writer](https://github.com/45ck/software-architecture-skills/blob/main/.agents/skills/adr-writer/SKILL.md), not only the repository README. They require concrete system context, explicit assumptions, testable quality goals, tradeoffs, and deployment consequences. They discourage choosing an architecture because it sounds modern.

Application: preserve a modular local application. Separate presentation, orchestration, IPC, persistence, and model/search integrations. Independent installation support is an isolation and packaging requirement; it does not imply a shared SaaS server. Record why local data ownership and process isolation were selected, and which future requirements would justify a shared service.

## Initial code audit

These findings describe the source at the beginning of this redesign, before parallel implementation changes.

| Surface | Observed issue | Required design outcome |
| --- | --- | --- |
| App shell | Generic letter-in-square logo; collapsed navigation by default; separate history pane consumes more width; repeated inline sizes | Recognizable custom identity, readable navigation, deliberate space allocation, coherent tokens |
| Conversation history | Rename requires double-click; delete is tiny and hover-dependent; deletion is immediate; no search or date grouping | Discoverable keyboard-accessible actions, confirmation or undo, searchable and grouped history |
| Composer | Mode chooser lacks Escape/focus behavior; draft clears before asynchronous send succeeds; toggles are very small | Preserve work on failure, use accessible controls, distinguish sending from generating, explain settings in plain language |
| Transcript | Scroll-follow state persists across changed content; dates sliced from strings; generic Try again label | Resume following on a new turn, preserve position while reading, readable dates, action labels matching semantics |
| Sources | Titles exist, but source identity is harder to scan; publication metadata is treated as a raw substring | Source title and domain, real publication date when available, honest unknown state |
| Today | Emoji-led statistic/action grids and repeated uppercase labels | Useful continuation and workspace status, one clear primary action, real icons |
| Connections | Raw JSON debug alerts, implementation-centric startup text, undefined text token usage | Product-facing account and sync states, diagnostics separated from normal use, shared tokens |
| Settings/projects/memory | Mixed row/card patterns, dense small type, inconsistent control dimensions and empty states | Shared page headers, grouped settings rows, consistent controls, explicit next actions |
| Theme | System preference read once; native control color scheme unspecified | Follow OS theme changes and render native controls consistently |
| CSS | Inline declarations overridden by `!important`; scattered fixed values | Reusable semantic classes and tokens with predictable specificity |

## Visual direction and review criteria

The product should feel like a focused desktop workspace. The identity needs a simple vector silhouette that stays legible at tray/icon sizes and in monochrome. Use its motif sparingly in the brand area and empty chat state; repeated avatars and decorative logo placements are unnecessary. Reading content gets most of the space. Messages use asymmetry and subtle surface contrast to distinguish authors without an oversized bubble rhythm.

A compact token system should define surface levels, text hierarchy, one functional accent, status colors, spacing steps, control heights, type sizes, radii, and focus treatment. Keep readable body text and conventional controls; distinctiveness comes from deliberate composition and the mark. Brand identity and functional iconography have different jobs.

Evaluate the actual product at desktop and compact window sizes, in both themes, with keyboard-only navigation. Include an empty chat, a long transcript, code and tables, source metadata, long conversation names, a filtered history with no matches, model startup, interrupted generation, recoverable failure, and settings with unavailable capabilities. The goal is a complete interaction, not a single flattering screenshot.

## Independent-installation quality scenarios

These are proposed acceptance scenarios, not measured guarantees:

1. A second installation or process starts without silently connecting to another LocalMind sidecar; per-process communication has a verifiable owner.
2. Separate OS accounts resolve to separate application-data locations. Application files never contain another user's chats, tokens, or source paths.
3. A slow or failed model startup leaves navigation and existing chats usable and provides a clear next action.
4. Stopping a response cancels backend work; subsequent events cannot modify another conversation.
5. Restarting restores persisted conversation content and sources without requiring a repository checkout or a developer-specific path.
6. Distribution documents external runtime/model prerequisites honestly; a successful frontend build alone does not establish that an installer works on a clean machine.

A shared hosted service, organization accounts, synchronized workspaces, or simultaneous editing would be a separate architecture decision with authorization, isolation, migrations, operational ownership, and conflict semantics of its own.
