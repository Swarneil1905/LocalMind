"""Opt-in SSD-1B research benchmark, outside the shipped app and its installer."""
import argparse
import gc
import hashlib
import json
import os
from pathlib import Path
import shutil
import statistics
import threading
import time

ROOT = Path(__file__).resolve().parents[2]
SPEC = ROOT / "evaluation/images/ssd-1b.json"


def load_spec():
    spec = json.loads(SPEC.read_text(encoding="utf-8"))
    if len(spec["prompts"]) != 10 or len({row["id"] for row in spec["prompts"]}) != 10:
        raise ValueError("Expected ten distinct fixed prompts.")
    if any(not row["id"].isalnum() or not isinstance(row["seed"], int) for row in spec["prompts"]):
        raise ValueError("Invalid prompt IDs or seeds.")
    return spec


def hardware_gate(rows, spec):
    completed = [row for row in rows if row.get("status") == "completed"]
    rule = spec["gate"]
    return (len(completed) == rule["required_completions"]
            and statistics.median(row["seconds"] for row in completed) <= rule["max_median_seconds"]
            and max(row["peak_vram_mib"] for row in completed) <= rule["max_peak_vram_mib"]
            and max(row["peak_rss_mib"] for row in completed) <= rule["max_peak_rss_mib"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=ROOT / "artifacts/evaluation/images/run")
    parser.add_argument("--download", action="store_true", help="Explicitly download pinned model files after license review")
    parser.add_argument("--download-only", action="store_true", help="Download and verify weights without using the GPU")
    args = parser.parse_args()
    spec = load_spec()
    args.out.mkdir(parents=True, exist_ok=True)
    model_dir = args.out.parent / "model"
    if shutil.disk_usage(args.out).free < spec["minimum_free_disk_gib"] * 1024**3:
        raise RuntimeError("Image research needs at least 20 GiB free disk space.")
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    os.environ["HF_HUB_DISABLE_XET"] = "1"
    from huggingface_hub import HfApi, snapshot_download
    if args.download or args.download_only:
        # Save the exact license/card and inventory before downloading weights.
        info = HfApi().model_info(spec["model"], revision=spec["revision"], files_metadata=True)
        manifest = [{"path": file.rfilename, "bytes": file.size, "sha256": getattr(file.lfs, "sha256", None)} for file in info.siblings
                    if file.rfilename.endswith((".json", ".txt", ".md")) or file.rfilename.endswith(".fp16.safetensors")]
        weights = [file for file in manifest if file["path"].endswith(".safetensors")]
        if len(weights) != 4 or any(not file["sha256"] for file in weights):
            raise RuntimeError("Pinned weight inventory is missing checksums.")
        (args.out / "download-manifest.json").write_text(json.dumps({"spec": spec, "files": manifest}, indent=2), encoding="utf-8")
        snapshot_download(spec["model"], revision=spec["revision"], local_dir=model_dir,
                          allow_patterns=["*.json", "*.txt", "*.md", "*.fp16.safetensors"], max_workers=2)
        for file in manifest:
            if file["sha256"]:
                digest = hashlib.sha256()
                with (model_dir / file["path"]).open("rb") as source:
                    for chunk in iter(lambda: source.read(4 * 1024**2), b""):
                        digest.update(chunk)
                if digest.hexdigest() != file["sha256"]:
                    raise RuntimeError("Downloaded weight checksum mismatch.")
        (model_dir / "localmind-verified.json").write_text(json.dumps({"revision": spec["revision"], "weights": weights}), encoding="utf-8")
    receipt = json.loads((model_dir / "localmind-verified.json").read_text(encoding="utf-8"))
    if receipt["revision"] != spec["revision"]:
        raise RuntimeError("Download the pinned model revision before running this trial.")
    if args.download_only:
        return
    import torch
    import psutil
    from diffusers import StableDiffusionXLPipeline
    if not torch.cuda.is_available():
        raise RuntimeError("This reference benchmark requires a CUDA GPU; no paid or CPU fallback.")
    free, total = torch.cuda.mem_get_info()
    # Windows reserves memory for the CUDA context even before tensors exist.
    # Leave room for that overhead on the reference 6 GB card.
    if free < 4.5 * 1024**3:
        raise RuntimeError(f"Insufficient GPU memory ({free / 1024**3:.2f} GiB free after CUDA startup). Close or unload competing work before this benchmark.")
    report = {"model": spec["model"], "revision": spec["revision"], "configuration": spec["configuration"],
              "gpu": torch.cuda.get_device_name(0), "vram_bytes": total, "initial_free_vram_bytes": free,
              "torch": torch.__version__, "rows": [], "decision": "pending"}
    def save():
        (args.out / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    save()
    begin = time.perf_counter()
    try:
        pipe = StableDiffusionXLPipeline.from_pretrained(model_dir, torch_dtype=torch.float16,
                variant="fp16", use_safetensors=True, local_files_only=True, add_watermarker=False)
    except Exception as error:
        report.update(decision="no-go", hardware_pass=False, load_error=f"{type(error).__name__}: {str(error)[:500]}")
        save()
        raise
    pipe.enable_model_cpu_offload()
    pipe.enable_vae_tiling()
    report["load_seconds"] = round(time.perf_counter() - begin, 3)
    process = psutil.Process()
    for prompt in spec["prompts"]:
        peak = [process.memory_info().rss]
        stopped = threading.Event()
        def sample():
            while not stopped.wait(0.05):
                peak[0] = max(peak[0], process.memory_info().rss)
        monitor = threading.Thread(target=sample, daemon=True)
        monitor.start()
        torch.cuda.reset_peak_memory_stats()
        begin = time.perf_counter()
        row = {**prompt, "status": "failed"}
        try:
            config = spec["configuration"]
            image = pipe(prompt["text"], negative_prompt="blurry, poor quality, distorted", width=config["width"], height=config["height"],
                         num_inference_steps=config["steps"], guidance_scale=config["guidance_scale"],
                         num_images_per_prompt=config["batch_size"], generator=torch.Generator("cpu").manual_seed(prompt["seed"])).images[0]
            torch.cuda.synchronize()
            image.save(args.out / f"{prompt['id']}.png")
            row.update(status="completed", image=f"{prompt['id']}.png")
        except Exception as error:
            row["error"] = f"{type(error).__name__}: {str(error)[:500]}"
        finally:
            stopped.set()
            monitor.join()
            row.update(seconds=round(time.perf_counter()-begin, 3), peak_rss_mib=round(peak[0]/1024**2, 1),
                       peak_vram_mib=round(torch.cuda.max_memory_reserved()/1024**2, 1))
            report["rows"].append(row)
            save()
            print(json.dumps(row), flush=True)
            gc.collect()
            torch.cuda.empty_cache()
    del pipe
    gc.collect()
    torch.cuda.empty_cache()
    report["vram_allocated_after_unload"] = torch.cuda.memory_allocated()
    report["hardware_pass"] = hardware_gate(report["rows"], spec)
    report["decision"] = "visual-review-required" if report["hardware_pass"] else "no-go"
    save()


if __name__ == "__main__":
    main()
