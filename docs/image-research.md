# Local image research

Image generation is not installed or enabled in the source beta. This opt-in benchmark is separate from the service and release dependencies. It does not use a paid API, upload prompts, train a model or change the chat model selection.

## Model and download

The trial pins [`segmind/SSD-1B`](https://huggingface.co/segmind/SSD-1B) to revision `60987f37e94cd59c36b1cba832b9f97b57395a10`. The model card declares Apache 2.0 and describes a distilled SDXL model. The repository has no separate license file at that revision; preserve its card, declared license and upstream provenance with any future distribution review. This trial does not redistribute weights or establish rights to training data or outputs.

Four FP16 safetensors files total 4,465,653,694 bytes, approximately 4.16 GiB. Allow at least 20 GiB free before downloading, plus space for the isolated CUDA runtime. Downloads contact Hugging Face. The benchmark saves the pinned inventory and verifies each weight's SHA-256. Reports, runtime, weights and generated images stay under ignored `artifacts/evaluation/images/`.

## Reproduce on a CUDA laptop

Use Python 3.10 or 3.11 in a separate environment. The measured reference configuration targets a 6 GB NVIDIA GPU. Close or explicitly unload competing model work first. The script refuses to start below 4.5 GiB free GPU memory after CUDA initialization, which itself reserves memory on Windows. It never stops other programs itself.

```powershell
python -m venv artifacts/evaluation/images/venv
$imagePython = "$PWD/artifacts/evaluation/images/venv/Scripts/python.exe"
& $imagePython -m pip install --upgrade pip
& $imagePython -m pip install torch==2.7.1 --index-url https://download.pytorch.org/whl/cu126
& $imagePython -m pip install -r scripts/evaluation/image-requirements.txt
& $imagePython scripts/evaluation/image_benchmark.py --download-only
& $imagePython scripts/evaluation/image_benchmark.py
```

Linux uses the same commands with the environment's `bin/python` path. CPU-only and Apple Metal are not part of this benchmark. A benchmark failure does not fall back to a paid service.

The fixed prompts, seeds, notice and acceptance thresholds live in `evaluation/images/ssd-1b.json`. Every image uses batch size one, 512 × 512 pixels, 20 steps, guidance 9, FP16, model CPU offload and VAE tiling. This tests a smaller canvas than SSD-1B's usual 1024-pixel use; results cannot establish 1024-pixel quality or performance. Model loading is measured separately. Per-prompt records include elapsed time, peak process RAM, peak PyTorch-reserved GPU memory and failure details. Reserved GPU memory is not total system VRAM usage. The report also records allocation after unloading.

## Decision rule

All ten images must finish, median generation must be at most 60 seconds, peak reserved VRAM at most 5.5 GiB and process RAM at most 12 GiB. Review every image against its requested subject and composition; at least seven must match. Inspect text and hands explicitly. Hardware success alone cannot enable a product feature: human visual acceptance and a separate installable, cancellable worker are still required. Failed trials stay disabled, with the failure and configuration retained locally.

These thresholds are product trial criteria, not external model benchmarks. A single laptop run is not a cross-platform performance guarantee.

References: [pinned model card](https://huggingface.co/segmind/SSD-1B/blob/60987f37e94cd59c36b1cba832b9f97b57395a10/README.md), [Diffusers memory controls](https://huggingface.co/docs/diffusers/optimization/memory), [official PyTorch version installation instructions](https://pytorch.org/get-started/previous-versions/).
