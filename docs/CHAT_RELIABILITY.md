# Chat reliability and interface changes

## Diagnosis

The screenshots cannot establish the exact sequence of runtime events, but code inspection found defects that independently explain the observed behavior. Replacing Qwen would not fix them.

| Failure | Root cause | Implemented change |
| --- | --- | --- |
| A news answer appears under “hello” | Global chat events had no request identity; Stop only cleared a frontend ref | Every event carries a request ID. Late events are ignored. Stop, switching chats, and new chats invalidate the active turn and abort the Rust HTTP operation. |
| Search results concern HELLO or unrelated topics | Every message was searched verbatim, including greetings and referential follow-ups | A separate retrieval policy skips greetings and rewrite requests and resolves follow-up subjects from recent conversation text. |
| Old stories presented as current | Web excerpts had no publication metadata or freshness checks | Extract article/main text and publication metadata; latest/current requests accept only sources published within seven days, otherwise return an explicit limitation without generating invented news. |
| Date questions repeat a news digest | No current date, weak grounding instructions, and no reliable source dates | Supply the current UTC date, instruct direct answers to the latest question, distinguish publication and event dates, and display dated source links. |
| Settings do not change responses | Chat used MODEL_MAP instead of selected model names | Selected Speed and Balanced models are passed to inference and remembered locally. Follow-ups use the configured Speed model. |
| Duplicated saves or memories | Network side effects inside React state updater callbacks | Complete a turn once, then persist/extract outside state updates. Capture the original conversation ID. |
| Startup intermittently blocks or errors | One-shot status checks race asynchronous service startup | Gate sending on readiness, poll service status, refresh Ollama, and reload conversations when the sidecar becomes ready. |
| Broken multibyte characters | Rust decoded individual HTTP chunks before assembling SSE frames | Buffer bytes until a whole event is available, then decode UTF-8. |

## Architecture decision

Retain React + Tauri/Rust + FastAPI + SQLite + Ollama. The problem is lifecycle ownership and evidence quality, not the choice of programming language. A rewrite or a distributed agent framework would increase the number of moving parts before fixing these defects.

Responsibilities now have explicit boundaries:

1. React owns the visible conversation and a single active turn. A turn snapshots its request ID, conversation ID, selected models, and completion callback. Event subscriptions are ready before generation starts.
2. Rust owns the cancellable HTTP operation. It labels every UI event with the originating request ID and treats premature EOF as an error.
3. Python plans retrieval, gathers evidence, checks freshness, and builds the inference request. Search policy lives separately from transport and provider adapters.
4. SQLite atomically stores completed user/assistant pairs and source metadata. Existing databases receive an additive sources column. Equal timestamps are ordered by rowid to keep user/assistant order deterministic.
5. Ollama generates language from the prepared context. It is not responsible for assigning responses to UI turns or deciding whether an unverified article is recent.

This follows the preference for simple, composable workflows in [Anthropic's agent engineering guidance](https://www.anthropic.com/engineering/building-effective-agents). [OpenHuman](https://github.com/tinyhumansai/openhuman) and [Open WebUI](https://github.com/open-webui/open-webui) are useful comparison projects, not dependencies copied into LocalMind. Ollama's separate thinking field is handled according to its [chat API contract](https://docs.ollama.com/api/chat).

## Interface decisions

- Cool blue-gray surfaces, a restrained blue accent, and the original LocalMind mark replace the previous purple chat styling. See PRODUCT_REDESIGN.md for the complete workspace redesign.
- Assistant responses use a readable document layout; user messages retain a modest contrasting surface.
- Typography, paragraph spacing, lists, code blocks, tables, focus indicators, and reduced-motion behavior are centralized in CSS.
- Sources belong to the response that used them and survive reopening the conversation. Publication dates are labeled separately from retrieval time.
- Copy and retry controls are keyboard accessible. Scrolling upward stops automatic scrolling; a latest-response control returns to the bottom.
- The composer has an accessible label, pressed states for feature toggles, and respects IME composition. Failed or stopped responses offer retry.
- Chat is the initial screen. Starter prompts describe supported writing and planning tasks. Privacy copy distinguishes local inference from optional outbound searches.

The supplied [Matt Pocock repository](https://github.com/mattpocock/skills) informed the focus on testable behavioral changes, while the [architecture skill pack](https://github.com/45ck/software-architecture-skills) informed explicit boundaries and tradeoffs. The [Snyk UI/UX overview](https://snyk.io/articles/top-claude-skills-ui-ux-engineers/) and [Kimi design overview](https://www.kimi.ai/resources/ui-ux-design-skills-for-agents) were reviewed for design-system and usability themes; their recommendations were treated as references, not installed as executable instructions.

## Verification

- Frontend: TypeScript, ESLint, Vitest, and production Vite build.
- Hook regressions: late tokens/sources/completion, cancellation and resend, double sends, listener readiness, StrictMode persistence, configured model selection, empty responses, split reasoning tags.
- Backend: unittest retrieval and streaming cases, article extraction, freshness rejection, no-model fallback on failed search, source persistence and ordering; Ruff lint. Tests are included in Python CI.
- Rust: cargo check, Clippy with warnings denied, and workspace tests. Added stream-frame tests cover split Unicode bytes and a partial next event.
- Browser: headless Edge against the actual Vite app with an isolated Tauri bridge fixture; light/dark/narrow screenshots, no page errors or horizontal overflow at the tested sizes. This does not substitute for native IPC testing in a packaged installer.
- Local model: Qwen3 8B answered a greeting correctly after news history without searching. With synthetic dated evidence it answered a publication-date follow-up correctly. These are targeted checks, not a broad model-quality benchmark.
- Live provider: DuckDuckGo returned five results for a San Antonio news query. This verifies provider access, not the truth or freshness of every result.

Run frontend checks from `apps/desktop` with `pnpm typecheck`, `pnpm lint`, `pnpm test`, and `pnpm build`. With Python requirements installed, run `python -m unittest discover -s tests -v` and `ruff check .` from `services/ai`. Run `cargo check -p localmind-app` and `cargo clippy -p localmind-app -- -D warnings` at the repository root.

## Limits and next evaluation

The existing native installed app must be rebuilt/restarted to use these changes. Its packaged runtime was not replaced during this work. Changes remain local and uncommitted.

Freshness checks intentionally sacrifice coverage: a relevant article without recognizable publication metadata is excluded from latest-news answers. The seven-day window is a conservative implementation policy, not proof that the reported event happened recently. Models can still misinterpret evidence. Query resolution uses deterministic English heuristics and is not a general semantic research engine. Search providers can fail or rate-limit. Source cards preserve provider metadata; they are not independent fact checks.

Before choosing a different model, compare candidates on the same fixed prompts and evidence: date attribution, ambiguous follow-ups, missing evidence, conflicting reports, local-file questions, and instruction following. Score supported claims and correct abstentions, alongside response latency and memory use. The two local checks here establish that Qwen3 can handle the specific corrected paths; they do not establish that it is the best model for every task.

The subsequent workspace redesign and independent-installation audit are documented in PRODUCT_REDESIGN.md and DESKTOP_RUNTIME.md. Broader model evaluation, stronger semantic retrieval, credential-vault storage, and genuine voice capability remain future work.
