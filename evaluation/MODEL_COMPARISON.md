# Local model comparison

`scripts/evaluation/compare_models.py` compares installed `qwen3:4b`, `qwen3.5:4b` and `qwen3:8b` without changing the user's saved profiles or model selection. It never downloads a model. Run with Python 3.11 and the AI service dependencies, with Ollama already running:

```powershell
python scripts/evaluation/compare_models.py --out artifacts/evaluation/model-comparison-01
```

Use a new output directory for every attempt. The default per-trial deadline is 180 seconds. No held-out cases are executed. Avoid using LocalMind or another inference client during a comparison.

The service's read-only `inspect_model` records the actual installed digest, context capacity, completion capabilities and advertised thinking controls. Its profile validator checks a frozen comparison profile: 8,192 context tokens, 1,024 maximum response tokens, temperature 0.2, top-p 0.9 and top-k 40. `think=false` is sent only if the model explicitly advertises boolean false support. Otherwise thinking remains at the model default. Record this difference when interpreting results; it is not an identical reasoning configuration.

For each model, the script runs three cold and three warm probes, followed by the same development cases: LM-001, LM-003, LM-007, LM-010, LM-016, LM-018 and LM-019. Cold trials request unloading only that candidate with `keep_alive: 0`, then verify its absence from Ollama `/ps`. Warm trials require the same digest and 8,192-token context to remain loaded. A failed warm-state check is an error, not a warm timing. Each service trial uses a fresh disposable profile and the normal prompt, context handling and response pipeline.

The small probe is the same two-sentence to-do-list/calendar explanation each time. Full answers, failures, timing data, effective model requests and `/ps` snapshots remain in local artifacts. Service first-content latency is separate from total duration and Ollama's reported load duration. Parent worker wall time additionally includes process startup. `/ps` total allocation minus VRAM is recorded as an **estimated non-VRAM allocation**, not measured process RAM. These snapshots do not capture peak GPU memory.

The output contains:

- `manifest.json`: models, actual capabilities, frozen profiles, source hashes, fixed case IDs and final execution state.
- `rollback.json`: the qwen3:8b legacy service configuration at 16,384 context with model-default thinking and the legacy 2,048-token response limit. This is a recorded rollback condition, not an additional measured benchmark or a settings write.
- One model directory containing environment/profile snapshots, six probe records, and a `quality/` run with the existing case/rubric/result format.
- `summary.json`: latency ranges/medians with explicit sample counts, execution errors and unrun trials. It contains no automatic quality pass.

The production service and relevant replay scripts are hashed before the run and checked between trials. Changes stop the comparison while preserving completed output. Unrelated tests and new tools are excluded from this comparability check. An installed digest change also stops the run. Restore a stable source tree and use a new output directory rather than combining conditions from different code.

Quality results can be reviewed with the existing manual review tooling:

```powershell
python scripts/evaluation/review_run.py prepare `
  --run artifacts/evaluation/model-comparison-01/qwen3-4b/quality `
  --out artifacts/evaluation/model-comparison-01/qwen3-4b/quality/review.json
```

Keep machine execution, an agent's inspection, and human approval distinct. A completed response is not a correct response. Keep failures and truncated outputs visible; only completed, untruncated, source-consistent probe results contribute timing statistics. Retain the current model default unless actual quality review and measured performance support changing it.

Limits: seven development cases once per model are not a broad quality benchmark, and three repeats do not establish statistical confidence. Cold means the model process was unloaded, not that OS disk caches were flushed. Other applications and prior model state can affect memory and latency; snapshots expose some of that state. Model architectures and thinking controls differ. This evaluates the Python service generator, not native desktop rendering, IPC, real accounts, persistent actions, embedding-model selection or retrieval-index quality.

Mocked regression tests require no GPU or running model:

```powershell
python -m unittest discover -s scripts/evaluation -p "test_compare_models.py" -v
```
