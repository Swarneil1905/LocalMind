# Architecture implementation plan

Proposed work from the [system design](../SYSTEM_DESIGN.md), 14 November 2026. These IDs are a new design breakdown, not a claim about the remaining count in an older backlog. Nothing below is marked implemented by this research task. Match existing issues before opening new ones, and leave contributor-assigned work with its owner.

Points are relative complexity: 2 small, 3 moderate, 5 substantial, 8 an experiment or cross-platform change that may need splitting. They are not days. Re-estimate after the first increment; do not promise all 40 stories in six weeks. With the previously discussed weekday/weekend availability, complete one small end-to-end slice at a time and include manual acceptance time.

## Increment A: a request survives failure

| ID | Story | Points | Dependency | Acceptance evidence |
|---|---|---:|---|---|
| A01 | Capture a frozen conversation benchmark and direct-model baseline | 3 | None | Separate development cases and 240 distinct held-out multi-turn scenarios for the MVP release, task rubrics, model/hardware/version fields; no training/test overlap |
| A02 | Specify run, step, event, approval, and receipt contracts | 3 | None | Python/TypeScript fixtures agree on states, IDs, and unknown fields; protocol version is explicit |
| A03 | Add versioned runs storage and migration | 5 | A02 | Upgrade, rollback-on-error, locking and disk-full tests; existing stores remain readable |
| A04 | Persist accepted requests and reject mismatched duplicates | 5 | A03 | A crash after acceptance loses no request; same ID returns same run, different payload fails |
| A05 | Move successful answer persistence behind the run service | 5 | A04 | Completion means the answer is durable; outbox replay cannot duplicate a conversation turn |
| A06 | Stream ordered events with reconnect and replay | 5 | A05 | Disconnect/reconnect and slow subscriber cases retain order; UI deduplicates sequence numbers |
| A07 | Connect full chat and mini chat to the same run | 3 | A06 | Opening another window does not submit or cancel a duplicate request |
| A08 | Add cancellation and interrupted-run recovery | 5 | A06 | Queue owner/waiter, provider stream, process death, and restart tests cover resource release |
| A09 | Separate durable job scheduling from GPU admission | 5 | A08 | Approval wait and network-only steps do not hold the inference slot; expired leases reconcile |
| A10 | Add local run diagnostics and a redacted export | 3 | A06 | Stage timings and errors are useful; secrets, prompts, and private file paths absent by default |

**Exit gate:** a normal reply survives a UI disconnect; a killed sidecar produces an honest interrupted state; restarting never repeats an external action. Run the existing test suite and native packaged smoke tests. Do not add a swarm in this increment.

## Increment B: model choice works for ordinary users

| ID | Story | Points | Dependency | Acceptance evidence |
|---|---|---:|---|---|
| B01 | Normalize local/cloud provider contracts | 5 | A02 | Text, terminal status, refusals, usage, cancellation, malformed frames and truncation tested per adapter |
| B02 | Add verified capability records and model-selection reasons | 3 | B01 | Embedding-only and non-vision models cannot be selected for incompatible requests |
| B03 | Finish the BYOK onboarding flow | 5 | B02 | Key storage, revocation, account link, validation and actionable billing errors tested; native walkthrough |
| B04 | Show data-transfer preview and scoped consent | 3 | B03 | Images, memory and connected data have separate categories; changed scope requires renewed consent |
| B05 | Extend spend reservations across attempts and tool costs | 5 | A03, B01 | Concurrent reservations cannot overspend the local limit; unknown usage stays reserved |
| B06 | Add provider health, bounded backoff and circuit breakers | 3 | B01 | Authentication, 429, transient errors and unknown write outcomes follow different policies |
| B07 | Implement opt-in routing within user rules | 5 | B02, B04, B05 | Explicit selection wins; local-only never calls a network model; unknown price blocks budgeted auto-route |
| B08 | Calibrate local profiles with measured warm/cold timing | 3 | A01, A10 | Two local models compared; no claim about untested hardware; simple prompts avoid unnecessary reasoning |
| B09 | Add an optional remote inference endpoint experiment | 5 | B01 | Auth/TLS and model capabilities validated; no native Windows vLLM dependency introduced |
| B10 | Run cloud acceptance with a small approved budget | 3 | B03–B07 | At least one provider tested live; test cost and remaining unknowns recorded locally; mocks alone insufficient |

**Exit gate:** a new user can use local chat or connect a provider, understand where data goes, and recover from an invalid key or rate limit without losing their message. Spend must remain within the explicitly approved test budget. A provider subscription is not assumed to supply API access automatically.

## Increment C: useful context and one real connection

| ID | Story | Points | Dependency | Acceptance evidence |
|---|---|---:|---|---|
| C01 | Add typed evidence/provenance records | 3 | A02 | Every retrieved excerpt maps to a scoped source; citations cannot point to dropped evidence |
| C02 | Preserve active-topic and correction precedence | 3 | C01 | Parameterized multi-topic tests include ambiguous follow-ups and adversarial unrelated memory |
| C03 | Introduce model-aware budgets and source-linked summaries | 5 | B02, C01 | Long chat retains key constraints; image/tool overhead accounted for; conservative fallback remains |
| C04 | Add memory correction, supersession and index invalidation | 5 | C01 | Deleting/correcting a fact affects retrieval; project scope holds across restart and reindex |
| C05 | Report specific search outcomes and typed current-data evidence | 3 | C01 | Offline, no matches, undated, stale, and relevant results differ; weather uses observation time |
| C06 | Specify tool manifests and approval fingerprints | 3 | A02 | Changed arguments, destination, grant or manifest invalidate approval |
| C07 | Integrate a general MCP client behind the broker | 8 | C06, A08 | SDK-based stdio/remote prototype handles negotiation, cancellation and bounded responses |
| C08 | Add one curated read-only connector end to end | 5 | C07, C01 | Setup → scoped read → evidence → disconnect works; revoked permission stops queued work |
| C09 | Add one external write with receipt and reconciliation | 8 | C08, A09 | Timeout after dispatch does not resend; read-back or explicit reconciliation resolves outcome |
| C10 | Add connector injection and ownership regression cases | 5 | C07–C09 | Retrieved instructions cannot authorize writes or cross projects; malicious manifests fail review |

**Exit gate:** complete a useful task with one supported real connector. The model cannot claim it changed something unless the broker has a matching receipt. Do not expand to a marketplace before this gate passes.

## Increment D: multimodal work and measured delegation

| ID | Story | Points | Dependency | Acceptance evidence |
|---|---|---:|---|---|
| D01 | Verify screenshot paste/drop across supported platforms | 3 | B02, A07 | Native checks cover large/invalid images, ownership, previews and mini-chat handoff |
| D02 | Prototype editable graph artifacts | 5 | C01 | Validated nodes/edges, keyboard interaction, revisions and export; wrong diagram content still fails rubric |
| D03 | Route speech transcription through the normal run service | 5 | A08, B02 | Transcript correction, permissions and local/cloud labeling work without duplicate sends |
| D04 | Add interruptible streamed speech output | 5 | D03 | Stop speech/interrupt/retry are tested; captions and text remain usable |
| D05 | Prototype a bounded research team | 8 | A09, B05, C08 | One coordinator, two read-only workers, explicit call/time/spend limits, no recursive spawning |
| D06 | Compare single agent, fixed workflow and bounded team | 5 | D05, A01 | Blinded task scoring plus latency/cost; keep team disabled if benefit does not justify overhead |
| D07 | Review image/video jobs under the same run lifecycle | 5 | A09, B05 | Quality gate preserved, paid approval and cancel/poll/recovery work; no duplicate paid generation |
| D08 | Harden migration, backup, update and rollback paths | 5 | A03, A08 | Upgrade from two prior supported versions; updater usable with broken model service |
| D09 | Add native release acceptance and repeated crash tests | 5 | D01–D08 | Signed packaged app tested on Windows/macOS/Linux; no fixture result labeled live acceptance |
| D10 | Run user trials and decide the next product release scope | 3 | All relevant exit gates | Five initial target users complete tasks; report observed failures and retest fixes; no invented testimonials |

**Exit gate:** ship only the capabilities that passed their functional, privacy, cost and usability gates. A failed image or multi-agent experiment is a useful result and is not a reason to enable it for users.

## First implementation session

Start A01 and A02, then a minimal A03–A06 vertical slice for text-only local chat. Preserve the current endpoint until the new path passes contract and replay tests. The first review should contain the schema, one durable request, one streamed answer, one reconnect test, and one process-interruption test. This is small enough to assess before migrating every feature.

Do not claim old backlog items are complete because these stories mention the same feature. Reconcile IDs and existing contributor PRs before scheduling. Keep raw evaluation runs, daily notes, and machine-specific reports in ignored local artifacts; source fixtures, contracts and architecture documentation remain reviewable codebase assets.
