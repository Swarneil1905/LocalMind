# Runtime, orchestration and evaluation decisions

Research checked 14 November 2026. **Status: proposed design, not implemented or benchmarked in this investigation.** Source inspection establishes what the code contains; it does not establish how well a model behaves on this machine. Links to upstream `main` can change.

## Decision

Keep Ollama as the default desktop inference service. Keep the existing Python service and provider adapters. Add a durable, bounded task runtime around them, then compare a small number of agent workflows against a strong single-agent baseline. Offer a separately configured remote inference endpoint later. Do not put a GPU server stack inside every Windows installer.

The model determines much of the reasoning ceiling. LocalMind determines what context reaches it, which tools actually run, whether actions are authorized, how failures recover, and whether users see an honest result. A new runtime or a larger swarm cannot by itself fix an unrelated-memory answer or a false claim that web search is unavailable.

## What the current code already provides

| Evidence inspected | Existing behavior | Consequence for the design |
|---|---|---|
| [`providers.py`](../../services/ai/app/providers.py) | Official OpenAI, Anthropic and Gemini endpoints; native credential access; common streamed text contract; image request translation; no automatic paid retries | Extend these adapters instead of adding a second, competing provider gateway |
| [`provider_store.py`](../../services/ai/app/provider_store.py) | Consent binds provider/model/data scopes; local-only policy; spending reservations; incomplete usage remains uncertain | Keep the reservation model and add run-level reservations across child calls |
| [`inference_queue.py`](../../services/ai/app/inference_queue.py) | One active inference slot per event loop, bounded waiting queue, foreground priority, cancellation cleanup | Local worker agents will normally serialize on the same GPU; parallel agents are not automatically faster |
| [`context_budget.py`](../../services/ai/app/context_budget.py) | Required policy and current request are preserved; recent history/corrections take priority; references have a bounded allowance; conservative byte estimator | Evaluate retained context and topic continuity before replacing this with summarization or compression |
| [`model_profiles.py`](../../services/ai/app/model_profiles.py), [`chat.py`](../../services/ai/app/api/routes/chat.py) | Installed-model metadata, supported thinking settings, generation profiles, foreground queue timing and streamed thinking/text separation | Measure queue, model loading, prefill, first answer text and decode separately; a 20-second reply is not one diagnosable number |
| [`mcp_client.py`](../../services/ai/app/mcp_client.py) | Owned fixture server only; reviewed tool specification; executable/process ownership and termination | This is a useful boundary test, not general third-party MCP support |
| [`recipes.py`](../../services/ai/app/recipes.py) | One synthetic repair recipe with fixed entry point and temporary sample files | Do not describe it as an arbitrary user workflow engine |
| [`ci-python.yml`](../../.github/workflows/ci-python.yml) | Backend unit/database/app integration tests, coverage reports, evaluation inventory checks, platform persistence tests, Windows process/credential/MCP checks, release-tool tests | CI already does more than lint. The missing evidence is real model and whole-product behavior, not simply test volume |

These observations do not constitute a complete security review or a live compatibility test of all providers.

## Inference choices

| Option | Best role for LocalMind | Constraint and next experiment |
|---|---|---|
| **Ollama** | Default desktop model lifecycle and inference | Its chat API exposes streaming, tool declarations, thinking controls, keep-alive and timing counters. Check controls against installed-model metadata rather than assuming every model supports the same switches. Measure a warm and cold workload on each supported hardware class. [API reference](https://docs.ollama.com/api/chat) |
| **llama.cpp** | Possible direct embedded backend when packaging, startup or memory measurements justify ownership of that integration | It provides portable inference with several hardware backends and quantization. A direct integration would also make us responsible for backend builds, model compatibility, lifecycle and updates. Compare equal model weights, quantization and context before attributing a gain to the engine. [Repository](https://github.com/ggml-org/llama.cpp) |
| **vLLM** | Optional Linux GPU server endpoint for a user or organization that already operates a suitable server | Current GPU installation docs specify no native Windows support; WSL/community alternatives exist. They also describe a community-maintained Apple Silicon plugin. This is not a drop-in cross-platform desktop replacement. Evaluate sustained concurrency and memory use on the actual server, not single-user desktop expectations. [Installation](https://docs.vllm.ai/en/stable/getting_started/installation/gpu/) |
| **Hosted model APIs** | Opt-in stronger reasoning, vision or other capabilities | User-approved data leaves the device and usage may cost money. Select by capabilities and measured task success, not brand alone. LocalMind must preserve its context, approval and recovery contracts for every provider. |

**vLLM adoption gate:** only proceed when a real remote-serving use case exists and the proposed server demonstrates better throughput at its expected concurrency without unacceptable latency or operating cost. No throughput claim has been measured here.

## BYOK: what the user would actually do

1. Choose a provider in LocalMind and follow its official API account/billing link.
2. Create an API credential with appropriate access. Store it through the operating-system credential store.
3. Confirm model access, then explicitly run a small generation test. Listing a model is not a generation test; the existing code already reports this distinction.
4. Review which conversation, attachment, memory and connector content may leave the device.
5. Choose a local spending allowance and per-task limit. Show estimated and confirmed usage separately.
6. Keep local-only mode available and never silently switch from local inference to a paid provider.

An API key is a credential, not a fixed number of purchased tokens. Provider billing can involve prepaid credits, invoicing, free tiers or account-specific benefits. Claude's current documentation describes prepaid API credits and eligible monthly credits linked from some subscriptions. OpenAI documents separate API billing and eligible subscription benefits; Gemini documents free and paid API tiers. LocalMind should direct users to their actual account instead of promising that a chat subscription always includes, or never includes, API use. [Claude billing](https://support.claude.com/en/articles/8977456-how-do-i-pay-for-my-claude-api-usage), [OpenAI Plus and API distinction](https://help.openai.com/en/articles/6950777-what-is-chatgpt-plus), [Gemini billing](https://ai.google.dev/gemini-api/docs/billing).

**Budget rule:** reserve the maximum estimated cost before dispatch, including all planned child calls. Reconcile with provider usage afterward. Cancellation or a disconnected stream may still incur provider charges. Unknown usage remains reserved/uncertain until reconciled or explicitly reviewed. The app's ledger is not an account-wide provider billing guarantee: other applications, stale prices, caching, image/audio accounting and provider tools can change the bill.

## Durable work before swarms

Introduce a small run record: run ID, parent ID, conversation/project scope, model/configuration snapshot, input references, state, deadlines, budget reservation and a versioned workflow definition. Persist step inputs/results and approval receipts. Store raw sensitive content only when needed, with retention controls; diagnostics should use IDs, sizes and timing by default.

Use these states: `queued`, `running`, `waiting_for_approval`, `completed`, `failed`, `cancelled`, and `interrupted`. A restart should mark unfinished work interrupted, not silently pretend it finished. Read-only work may resume from a checkpoint. An external write with an unknown acknowledgement must be reconciled before retrying.

LangGraph is a credible candidate for the workflow implementation: its checkpointers support thread state and its stores hold cross-thread data; its fault-tolerance documentation explicitly addresses retries, idempotency and unknown acknowledgements. A checkpointer does not create exactly-once external effects. [Persistence](https://docs.langchain.com/oss/python/langgraph/persistence), [fault tolerance](https://docs.langchain.com/oss/python/langgraph/fault-tolerance).

Compare two small prototypes behind the same run interface: a direct Python/SQLite state machine and a LangGraph adapter. Use a restartable research task with an approval pause and a fake external write. Choose the framework only if it materially reduces our recovery/state code without weakening migration, licensing, packaging or observability requirements. Do not introduce Redis, Kafka or a hosted workflow platform for this experiment.

### First agent workflow

Start with **research and compare**: one coordinator, at most two bounded read-only workers, then one synthesis pass. Each worker receives a separate question, source policy, output schema, token/tool limits and a deadline. It returns claims with source IDs, uncertainties and unfinished work. The coordinator resolves disagreement and checks citation support. External actions remain separate approval-controlled steps.

Keep ordinary chat on the single-response path. Avoid running a planner and critic for greetings. Avoid concurrent local model swaps unless the hardware budget permits them. A task may benefit from independent searches even when model inference stays serialized.

Anthropic reports benefits for breadth-first research, but also substantially greater token use: around 15 times ordinary chat in its reported system. That is a workload-specific observation, not a LocalMind estimate. Its simpler-agent guidance recommends adding complexity only when it earns its cost. [Multi-agent research](https://www.anthropic.com/research/multi-agent-research-system), [effective agents](https://www.anthropic.com/research/building-effective-agents).

The MAST study categorizes failures across system design, inter-agent alignment and task verification using annotated multi-agent traces. Use those categories to label our failures; the study does not prove that its interventions or scores transfer to LocalMind. [Why Do Multi-Agent LLM Systems Fail?](https://arxiv.org/abs/2503.13657).

**Promotion gate:** on a held-out task set, compare single-call, single-agent and bounded multi-agent runs using the same models, tool access and total spending ceiling. Report success, grounded claims, completion time, cost and failure categories over repeated trials. Ship the multi-agent route only for task classes where its measured benefit justifies the overhead. More agents is not a success metric.

## Voice and multimodal work

A text model can participate in voice conversation through an audio pipeline: microphone capture → speech detection → transcription → normal LocalMind turn → speech synthesis → playback. Speech-to-speech models are another adapter, not a requirement to start. `whisper.cpp` provides a local transcription implementation; Pipecat documents turn detection and interruption handling that are useful references. Neither automatically solves desktop device switching, permissions or feedback from the speakers. [whisper.cpp](https://github.com/ggml-org/whisper.cpp), [Pipecat speech input](https://docs.pipecat.ai/pipecat/learn/speech-input).

Start with push-to-talk and an editable transcript. Then add interruptible playback: user speech cancels generation and unplayed audio; conversation state records what was actually spoken. Explicit microphone state, mute, device selection, cancellation and cleanup belong in the first implementation. Keep transcription/TTS behind adapters so the user can choose local or approved hosted processing independently.

For screenshots, normalize clipboard/drop/file input into one owned attachment record. Route to a tested vision-capable model, or offer OCR as a clearly labeled text-only alternative. Keep image data, previews and revisions tied to the same conversation/project boundary. For generated diagrams, charts and widgets, validate typed artifact data before rendering; arbitrary generated code needs a separate isolation design.

**Voice gate:** record word error rate on a consented/redistributable set covering accents and noise, end-of-utterance to first audio latency, interruption correctness, cancellation cleanup and device-loss recovery. Thresholds must be set per supported hardware/language profile. **Vision gate:** test screenshot tasks with changed numbers, layouts and text, not repeated copies of one fixture.

## CI and model-quality evidence

The existing workflow already runs substantive tests. Expand it by failure mode, with deterministic tests on every PR and paid/model-dependent tests isolated from fork PR secrets.

| Layer | Add or retain | What passing establishes |
|---|---|---|
| Unit and property tests | Topic resolution across varied domains, context boundaries, budget arithmetic, cancellation, malformed provider/MCP events | Code invariants, not model intelligence |
| Database/integration | Migration rollback, two concurrent reservations, stale leases, restart during each state transition, attachment ownership | Persistence and concurrency behavior |
| Provider contracts | Recorded/synthetic streams for text, tools, images, refusal, partial completion, quota, timeout and cancellation | Adapter behavior for tested protocol shapes |
| Native packaging | Clean install, upgrade, duplicate launch, process ownership, locked-file update, signed artifact verification | Behavior of the actual distributed package |
| Live model evaluation | Versioned model/prompt/config, repeated held-out conversations, warm/cold timings and human-reviewed samples | Estimated behavior on a defined workload and machine |
| UX acceptance | First-run model setup, screenshot paste, web follow-up, interrupted research, revoking a connector, spending preview | Whether a person can finish the intended flow |

Do not use millions of uncurated conversations as evidence of quality. A smaller, stratified, independently graded suite exposes regressions more clearly; training data is a separate project with rights, filtering, contamination and evaluation requirements. Preserve a frozen held-out set when adding regression examples. Evaluate task outcome and tool effects as well as answer wording, and repeat probabilistic trials. Anthropic's evaluation guidance distinguishes trials, traces and final environment outcomes and recommends combining grading approaches. [Agent evaluation guidance](https://www.anthropic.com/research/demystifying-evals-for-ai-agents).

Before claiming an improvement, publish the comparison internally: baseline/candidate versions, dataset version, sample counts, repeated-trial variation, per-category results, latency/cost distributions and manual review disagreements. A model judge should not be the sole release gate for authorization, data isolation or external writes.

## Remaining unknowns

- No model benchmark, paid API call, external MCP session or voice-device test ran during this research.
- Available GPU memory, competing applications, quantization and model loading explain possible latency differences but were not measured here.
- The inspected local files establish implementation paths, not the absence of similar capabilities elsewhere in the repository.
- Provider account benefits and model/API capabilities change. Confirm them during onboarding and pin supported protocol behavior in contract tests.
- A framework, small swarm or new inference engine must earn adoption through the experiments above. None is assumed to make LocalMind equivalent to a frontier product.
