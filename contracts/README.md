# Application and provider contracts

These fixtures contain synthetic data only. They are reusable tests, not conversation exports
or model-training data.

`app-v1.json` is produced by real backend storage APIs in a disposable profile. It covers image
attachments, saved errors, cancelled partial responses, memory provenance and preference defaults.
Generated identifiers and timestamps are normalized. The PNG attachment's byte count is checked
against its actual stored data, then normalized to a representative value for the fixture:
Pillow and zlib builds can encode identical pixels into different file sizes. Tests exercise
different compression levels and reject incorrect size metadata before normalization.
Python compares a fresh result with
the committed fixture; Rust deserializes it into the desktop types; frontend tests use the same
fixture through the saved-message decoder.

If the application contract intentionally changes, run this from the repository root with the
backend dependencies installed:

```powershell
$env:PYTHONPATH = "services/ai"
python services/ai/tests/test_api_contracts.py --write
python -m unittest discover -s services/ai/tests -p test_api_contracts.py -v
pnpm --filter desktop test
cargo test --workspace
```

Review the fixture diff before accepting it. Do not refresh snapshots just to silence a failure.
These tests are discovered by the existing Python, frontend and Rust CI jobs.

`providers-v1.json` contains synthetic OpenAI, Anthropic and Gemini wire events and vision inputs.
The adapter tests cover completion, usage, refusal, incomplete streams, unrecognized protocols,
HTTP failures and cancellation. Anthropic refusals are recognized from the documented
[`message_delta.stop_reason` signal](https://platform.claude.com/docs/en/test-and-evaluate/strengthen-guardrails/handle-streaming-refusals)
and shown as a declined request, rather than an accidental disconnection. Tests verify one request, no fallback and no replay of a cancelled
generation. They also exercise local-only mode and exact data-scope consent. These fixtures catch
internal regressions; they cannot detect an upstream API change without updated examples or a
live check.

Live chat smoke tests remain off by default. `test_provider_live.py` requires explicit opt-in,
a configured isolated profile, approved provider/model, known prices and
`LOCALMIND_PROVIDER_LIVE_BUDGET_USD` between 0 and 1. The synthetic request is rejected before
sending if its estimate exceeds that limit. Local estimates do not replace provider-side billing
limits. Do not put credentials in fixtures, logs or reports.
