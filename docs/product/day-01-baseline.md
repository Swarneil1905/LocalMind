# Day 1 baseline

October 14, 2026, America/Chicago. The machine-readable captures use UTC, so their timestamps fall on October 15. No application prompt, model, runtime setting or inference behavior was changed during this baseline.

## Delivered

| Backlog item | Result | Evidence |
| --- | --- | --- |
| BASE-01.1: capture configuration | Recorded hardware, model digests, source defaults and a loaded-model snapshot. The installed app version and saved UI model selection remain unverified. | [Before inference](../../evaluation/baselines/2026-10-14-environment.json), [during inference](../../evaluation/baselines/2026-10-14-active-environment.json) |
| BASE-01.2: assemble conversation cases | 30 synthetic cases with acceptance criteria. IDs LM-001–020 are development; LM-021–030 are held out. | [Corpus](../../evaluation/cases.json), [review protocol](../../evaluation/README.md) |
| BASE-01.3: define demo journeys | Learning and correction, reviewed project repair, and voice-to-task. Current and future capabilities are distinguished. | [Demo journeys](demo-journeys.md) |
| BASE-01.4: arrange testers and native coverage | Checklist prepared. Recruitment and access remain blocked: no testers or additional native devices have been arranged. No invitations were sent. | [Tester checklist](tester-checklist.md) |

The work package remains **In progress** because recruitment is pending. No human work hours were inferred or entered into the workbook.

## Configuration observed

- Windows 11, AMD64, approximately 15.73 GiB usable physical RAM, 20 logical CPU threads.
- NVIDIA GeForce RTX 4050 Laptop GPU, 6,141 MiB VRAM, driver 617.42. This records the installed driver; it does not claim it is the latest.
- Ollama 0.40.4. Installed models: `qwen3:8b`, `qwen2.5:1.5b`, and `nomic-embed-text:latest`.
- Selected **test** model: Qwen3 8B, Q4_K_M, digest `500a1f067a9f782620b40bee6f7b0c89e17ae61f686b92c24933e4ca4b2b8b41`.
- Existing Python request settings: temperature `0.2`, context `16384`. Thinking and other sampling settings were not overridden. Model defaults are recorded separately from request overrides.
- No model was loaded at the initial metadata capture. During the sample, `/api/ps` reported context `16384`, model allocation `7,837,287,709` bytes and VRAM allocation `4,243,566,100` bytes. These are Ollama's reported allocations, not a process-wide peak-memory measurement.
- Source HEAD: `14ce01d2927af024d013f364dae3189ed1d835d4`. The checkout contained uncommitted evaluation/docs changes. The response sample records the unchanged chat-source hash and corpus hash.

The model occupies memory beyond the GPU allocation. That makes context size, thinking behavior, model size and CPU/GPU placement useful comparison variables. This single run does not isolate their individual contribution to latency.

## Controlled response sample

[Raw sample and timings](../../evaluation/baselines/2026-10-14-service-sample.json)

Three development cases exercised the current Python `_stream_ollama` function against the installed local model. A fourth exercised routing with a deterministic empty-search fixture. They used a disposable profile, disabled connected-app context and supplied no real memory or documents. Earlier assistant messages came from the synthetic corpus; this was not a continuous live desktop conversation.

The service's own default prompt was preserved. The desktop's Rust prompt is different and was not used here. The sample therefore does not establish installed-app response quality, native UI behavior, authenticated HTTP transport, memory retrieval or task persistence. First-answer timing measures streamed answer content, excluding the separate thinking field. Thinking content was not saved.

| Case | Observed result | First answer | Total | Interpretation |
| --- | --- | --- | --- | --- |
| LM-001: intuitive XGBoost explanation | No answer content before the harness's 120-second deadline. First stream event arrived at 54.646 seconds, followed by thinking output. | Not reached | 119.990 s | Run error/timeout. No prose-quality score. This was the first request after the empty loaded-model snapshot. |
| LM-007: corrected project deadline | Used October 23 for the demo and preserved October 18 for the UI review. Did not claim a calendar or project mutation. | 11.416 s | 13.910 s | The sampled response met the case's correction requirements. No repeatability claim. |
| LM-016: save a task without tools | Answered that the task had been saved to the Learning project. No action tool was available or invoked. | 18.832 s | 20.702 s | Critical false-completion failure. A fluent sentence is not an execution receipt. |
| LM-006: conceptual explanation with Web enabled | Called the search function and returned a source-retrieval failure instead of explaining the concept, despite the request saying search was unnecessary. | 0.001 s | 0.001 s | Routing failure with mocked empty results. No live search or model inference was performed for this case. |

These four observations do not constitute a 30-case benchmark or an aggregate quality score. The ten held-out cases were not generated or used to tune behavior.

## What the evidence changes

1. **Routing is an application problem.** The current `plan_query` skips greetings and a few rewrite requests, but otherwise produces a query when Web is enabled. `_stream_ollama` then returns early on empty search results even for an established concept. Cover this exact failure when implementing `CHAT-01`.
2. **Action claims need an execution boundary.** The service offers no task dispatcher, yet the model claimed a saved task. `CHAT-01` should make capability limits explicit; `ACT-01` must connect typed commands, persisted results and receipts before the product can claim task completion. Prompt wording alone is not proof of a successful action.
3. **Measure configuration before replacing the model.** The first sample gave no useful answer within two minutes. `RUN-01` and `MODEL-01` should compare matched settings and smaller profiles, separating cold load, first thinking, first answer, completion and cancellation.
4. **Context correction is possible in the current model.** One synthetic correction case worked. Preserve it as a regression case while fixing failures, rather than assuming every weakness comes from model capacity.

The next scheduled package is `EVAL-01`: turn the fixed corpus into a repeatable runner with response/timing capture and human scoring. Keep development and held-out execution separate. Use the failures above as development regression targets before changing defaults.

## Validation and limits

- 22 evaluation-tool tests passed: metadata allowlists, proxy/redirect behavior, timeouts, missing models, output overwrite protection, corpus split/clock/source boundaries and stable IDs.
- Existing Python service suite: 26 tests passed. The restricted shell blocked Windows event-loop IPC; the same suite passed with local IPC available and disposable profiles.
- Ruff checks on the new evaluation scripts and the corpus validator passed. No model, GPU or network is needed by the new CI checks.
- Local tests used Python 3.12.14. CI retains Python 3.11; its next run happens after the user pushes.
- Added the evaluation checks to the existing Python CI workflow. No GitHub run, commit, push, release, native installer test or desktop UI pass is claimed here.

All captured prompts are synthetic. Reports omit personal chat history, credentials, device serial numbers and model prompt/template fields. Before publishing future captures from another machine, review model names and metadata for anything that person considers private.
