# Imported conversation candidates

Keep the original 30 cases in `evaluation/cases.json` unchanged. Imported conversations form a separate, versioned pool in ignored `artifacts/datasets/`; they are not additional `LM-*` cases, completed evaluations, human-approved rubrics, or evidence that training occurred.

The first import uses only **HuggingFaceTB/smoltalk → smol-magpie-ultra**, revision `5feaf2fd3ffca7c237fc38d1861bc30365d48ffa`, source split `train`. Its new synthetic conversations are covered by Apache-2.0 according to the pinned source card. That claim does not apply to the full SmolTalk mixture. Keep the card, license text and source record with any derived copy. Source assistant answers are unverified reference responses, not guaranteed correct answers or automatically approved training targets.

The reusable importer, checker and tests are tracked in Git. Source Parquet, normalized records, sample prompts and run outputs stay local under `artifacts/`. Importing requires optional `pyarrow`; checking and sampling use the Python standard library. No dataset code, model, service, connector or network call is executed by the importer.

## Larger sources

These publisher pages provide a route beyond the initial pool. Counts describe conversations or examples, not independently reviewed answers. Check the selected revision and terms before a new import.

| Source | Scale and origin | Use in LocalMind |
| --- | --- | --- |
| [SmolTalk](https://huggingface.co/datasets/HuggingFaceTB/smoltalk) | Approximately 1.1 million synthetic examples in the combined configuration. | Start with the newly generated Apache-2.0 subsets. The full mixture includes other licenses; do not add the combined configuration and its constituent subsets together when counting examples. |
| [LMSYS-Chat-1M](https://huggingface.co/datasets/lmsys/lmsys-chat-1m) | One million real user conversations with 25 models, across 154 languages. | A potential research source, not imported here. Access requires accepting custom terms, including restrictions on third-party transfer and model-specific output terms. The authors report no benchmark decontamination. |
| [WildChat-1M](https://huggingface.co/datasets/allenai/WildChat-1M) | Real user conversations; the filtered public version currently lists 837,989 rows across 68 languages. | A possible source for realistic follow-ups. The database is ODC-BY; review content rights and privacy separately, and exclude demographic, IP and request-header fields. Not imported here. |
| [UltraChat 200K](https://huggingface.co/datasets/HuggingFaceH4/ultrachat_200k) | 207,865 synthetic training examples and 23,110 test examples in its supervised fine-tuning splits. | A smaller alternative for instruction following. Preserve upstream split boundaries and review both the derivative and original source terms before adoption. Not imported here. |

More data can expose weaknesses and supply candidates for later fine-tuning. It does not change model weights when downloaded, and source answers can contain the same factual mistakes or generic style we want to improve. Keep evaluation separate from training: author review criteria, inspect representative answers, curate a training subset if justified, and leave held-out data untouched.

## Prepare and verify a pool

After downloading the pinned public source with `scripts/evaluation/download_smoltalk.py`:

```powershell
python scripts/evaluation/conversation_pool.py import `
  --parquet artifacts/datasets/smoltalk-source-v1/train-00000-of-00006.parquet `
  --source-card artifacts/datasets/smoltalk-source-v1/SOURCE-README.md `
  --license-file artifacts/datasets/smoltalk-source-v1/LICENSE-2.0.txt `
  --download-manifest artifacts/datasets/smoltalk-source-v1/download.json `
  --out artifacts/datasets/smoltalk-12k-v1 --size 12000
python scripts/evaluation/conversation_pool.py check --pool artifacts/datasets/smoltalk-12k-v1
python scripts/evaluation/conversation_pool.py sample `
  --pool artifacts/datasets/smoltalk-12k-v1 --count 100 --split development `
  --out artifacts/datasets/smoltalk-12k-v1/development-sample.json
```

Repeat `--parquet` for additional approved shards. Existing import directories and sample files cannot be overwritten. Failed imports have no final manifest and cannot pass validation. Use a new directory for a new version or changed import policy.

The optional download manifest must identify this exact repository, subset, revision, source split and license. Every supplied input must match its filename, byte count, SHA-256 and pinned download URL. This binds the import to the supplied download record; the downloader establishes the upstream checksum. Without that record, provenance is explicitly `unverified_local_inputs`. A caller-provided revision label alone does not verify where arbitrary bytes came from.

## Selection and limits

Records must have nonempty alternating `user` and `assistant` text, starting with the user and ending with an assistant answer. System, tool and other roles are excluded rather than promoted to model instructions. The first version allows at most 32 messages, 12,000 characters per message and 24,000 per conversation. Source metadata outside `messages` is not imported.

Screening excludes obvious email addresses, phone-like numbers, valid IP addresses and common secret patterns. Reports count reasons without copying matched values or rejected conversations. This is **heuristic screening, not anonymization**: it misses some personal data and rejects some harmless coding examples. Review selected material before redistribution or training.

Deduplication normalizes Unicode, line endings and outer whitespace while preserving internal code/prose whitespace. Identical full conversations are duplicates. All conversations sharing the same normalized first user turn form one group; one deterministic representative is retained, so alternate answers, branches and related records with that first turn cannot cross splits. Selection ranks group hashes with a fixed seed, independent of input-file order. Semantic paraphrases with different first turns are not detected; do not claim that this eliminates all contamination.

Group hashes assign approximately 90% to `development` and 10% to `held_out`; the exact counts are recorded. These are pool-specific assignments, separate from the original 20/10 regression split. They do not establish that the source was absent from a model's training data. Held-out sampling requires explicit `--allow-held-out`; do not inspect those answers for tuning. Keep all derived prefixes/variants in their original group. Source shard coverage is recorded: sampling one of six source train shards is not a representative sample of the full dataset.

The importer must find the requested number of unique, screened conversations; it fails instead of duplicating records or padding the pool. The CLI minimum is 12,000. No quality pass is inferred from this count.

## Artifact and adapter contract

| File | Contents |
| --- | --- |
| `manifest.json` | Pool/schema versions, source revision/license, input hashes, seed, limits, algorithm versions, importer hash, output hashes. Written only after a complete import. |
| `records.jsonl` | Stable `CP-<conversation SHA256>` IDs; `CG-<first-user SHA256>` group IDs; split; source file hash/row index; input messages ending in the last user turn; final source answer stored separately as `reference_answer` with `unverified_source_response` status. |
| `report.json` | Input, rejection, deduplication, selection and split counts; screening and coverage limitations. |
| `SOURCE-README.md`, `LICENSE-2.0.txt`, `download.json` | Local provenance documents; download record present when supplied. |
| `development-sample.json` | Explicit sample with pool hashes, selected IDs and input messages; no final reference answer. |

`check_pool(Path)` returns a validation report. `iter_pool_records(Path)` streams records **after validation**. `sample_pool(pool, out, count=100, split="development")` creates a separate `conversation_pool_sample` object. It projects only the previous user/assistant turns and final user request into each case. Earlier assistant turns remain source history; the final source answer never enters the prompt. The sample has `expectation: null` and `rubric_status: "not_authored"` so it cannot pretend to have acceptance criteria. Imported text remains untrusted conversation content.

The regression runner accepts the frozen 30-case suite and the separately identified five-case context supplement. Its manual-review tool requires authored rubrics. Imported data instead uses `replay_pool.py`, which validates the complete pool, samples only development conversations, caps each run at 100, and saves generated answers separately without accuracy or pass-rate scores. No reference answer enters inference. Do not manufacture generic passing rubrics, rename imported records to `LM-*`, or treat source responses as grader instructions.

```powershell
python scripts/evaluation/replay_pool.py `
  --pool artifacts/datasets/smoltalk-12k-v1 --count 5 --model qwen3:8b `
  --out artifacts/evaluation/pool-smoke-01
```

This requires the AI service dependencies and a running local Ollama model. Each request uses an isolated temporary profile with live web and connectors disabled. An optional `--generation-profile` JSON snapshot must match the installed digest; the service validates its context, sampling and thinking controls. Runs refuse to overwrite an existing output directory. The result remains **unreviewed** even if every request completes.

Run the importer regression tests without downloads or inference:

```powershell
python -m unittest discover -s scripts/evaluation -p "test_conversation_pool.py" -v
```

Tests cover deterministic selection, related-record deduplication, role/length filtering, screening counts, source mismatch rejection, output integrity, reference-answer exclusion, held-out gating and refusal to overwrite. A small real-Parquet fixture runs when `pyarrow` is installed.
