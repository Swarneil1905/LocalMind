"""Metadata-only tests: no Ollama, model inference, profiles, or GPU required."""

import io
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch
from urllib.error import HTTPError
from urllib.request import ProxyHandler

import capture_environment as inventory


DIGEST = "a1" * 32
MODEL = {
    "name": "qwen3:8b", "model": "qwen3:8b", "digest": DIGEST, "size": 5_000_000_000,
    "details": {"format": "gguf", "family": "qwen3", "families": ["qwen3"],
                "parameter_size": "8.2B", "quantization_level": "Q4_K_M"},
}


class MetadataTests(unittest.TestCase):
    def test_proxy_environment_is_disabled_and_only_fixed_loopback_request_is_used(self):
        response = MagicMock()
        response.__enter__.return_value.read.return_value = b'{"version":"0.40.3"}'
        opener = MagicMock()
        opener.open.return_value = response
        with patch.dict(os.environ, {"HTTP_PROXY": "http://secret:password@proxy.invalid", "ALL_PROXY": "http://proxy.invalid"}), \
                patch.object(inventory, "build_opener", return_value=opener) as factory:
            self.assertEqual(inventory.request_metadata("version"), {"version": "0.40.3"})
        handlers = factory.call_args.args
        self.assertIsInstance(handlers[0], ProxyHandler)
        self.assertEqual(handlers[0].proxies, {})
        self.assertIsInstance(handlers[1], inventory.NoRedirect)
        request = opener.open.call_args.args[0]
        self.assertEqual(request.full_url, "http://127.0.0.1:11434/api/version")
        self.assertEqual(request.get_method(), "GET")
        self.assertEqual(opener.open.call_args.kwargs, {"timeout": 5})
        response.__enter__.return_value.read.assert_called_once_with(inventory.MAX_RESPONSE_BYTES + 1)
        with self.assertRaises(inventory.RedirectRejected):
            handlers[1].redirect_request(request, None, 302, "redirect", {}, "https://external.invalid")

    def test_show_uses_only_model_in_body_and_rejects_generation_endpoints(self):
        response = MagicMock()
        response.__enter__.return_value.read.return_value = b'{}'
        opener = MagicMock()
        opener.open.return_value = response
        with patch.object(inventory, "build_opener", return_value=opener):
            inventory.request_metadata("show", "qwen3:8b")
            request = opener.open.call_args.args[0]
            self.assertEqual(request.get_method(), "POST")
            self.assertEqual(json.loads(request.data), {"model": "qwen3:8b"})
            with self.assertRaises(ValueError):
                inventory.request_metadata("generate", "qwen3:8b")
        self.assertEqual(opener.open.call_count, 1)

    def test_response_size_json_and_top_level_shape_limits(self):
        for data, expected in ((b"x" * 9, inventory.ResponseTooLarge), (b"[1]", inventory.InvalidResponseShape),
                               (b"{oops}", json.JSONDecodeError)):
            response = MagicMock()
            response.__enter__.return_value.read.return_value = data
            opener = MagicMock()
            opener.open.return_value = response
            with self.subTest(expected=expected), patch.object(inventory, "MAX_RESPONSE_BYTES", 8), \
                    patch.object(inventory, "build_opener", return_value=opener):
                with self.assertRaises(expected):
                    inventory.request_metadata("tags")

    def test_metadata_allowlists_discard_prompts_paths_and_unknown_fields(self):
        secret = "PRIVATE_PROMPT_OR_PATH"
        show = {
            "details": {**MODEL["details"], "parent_model": "C:/Users/PRIVATE_PROMPT_OR_PATH", "unknown": secret},
            "model_info": {"general.architecture": "qwen3", "general.parameter_count": 8_200_000_000,
                           "qwen3.context_length": 40960, "general.name": secret, "tokenizer.ggml.tokens": [secret]},
            "parameters": f'temperature 0.2\nnum_ctx 16384\ntop_p 0.9\nstop "{secret}"\nsystem {secret}\nseed NaN\nnum_gpu true\n',
            "modelfile": secret, "template": secret, "system": secret, "license": secret,
        }
        installed = {**MODEL, "path": secret, "prompt": secret}
        active = {**installed, "context_length": 8192, "size_vram": 3_000_000_000, "expires_at": secret}
        responses = {"version": {"version": "0.40.3", "extra": secret}, "tags": {"models": [installed]},
                     "ps": {"models": [active]}, "show": show}
        with patch.object(inventory, "request_metadata", side_effect=lambda endpoint, model: responses[endpoint]) as request:
            report = inventory.ollama_metadata("qwen3:8b")
        encoded = json.dumps(report, allow_nan=False)
        self.assertNotIn(secret, encoded)
        self.assertEqual([call.args[0] for call in request.call_args_list], ["version", "tags", "ps", "show"])
        self.assertEqual(report["installed_models"]["models"][0]["digest"], DIGEST)
        self.assertEqual(report["installed_models"]["models"][0]["size"], MODEL["size"])
        self.assertEqual(report["selected_model_metadata"]["numeric_parameters"], {"temperature": 0.2, "num_ctx": 16384, "top_p": 0.9})
        self.assertEqual(report["selected_model_metadata"]["model_info"]["qwen3.context_length"], 40960)
        self.assertEqual(report["active_models"]["models"][0]["context_length"], 8192)

    def test_timeouts_do_not_leak_exception_messages_or_prevent_valid_json(self):
        with patch.object(inventory, "request_metadata", side_effect=TimeoutError("C:/Users/private; proxy password")):
            report = inventory.ollama_metadata("qwen3:8b")
        for key in ("version", "installed_models", "active_models", "selected_model_metadata"):
            self.assertEqual(report[key], {"status": "unavailable", "reason": "TimeoutError"})
        self.assertNotIn("private", json.dumps(report, allow_nan=False))

    def test_missing_model_is_recorded_without_loading_or_pulling_it(self):
        def request(endpoint, model):
            if endpoint == "show":
                raise HTTPError("http://127.0.0.1:11434/api/show", 404, "private model name", {}, None)
            return {"version": "0.40.3"} if endpoint == "version" else {"models": []}
        with patch.object(inventory, "request_metadata", side_effect=request) as calls:
            report = inventory.ollama_metadata("qwen3:8b")
        self.assertEqual(report["selected_model_metadata"], {"status": "unavailable", "reason": "ModelNotFound"})
        self.assertEqual(report["installed_models"]["models"], [])
        self.assertEqual(calls.call_count, 4)
        json.loads(json.dumps(report, allow_nan=False))

    def test_malformed_endpoint_shapes_are_unavailable_and_missing_active_context_is_explicit(self):
        responses = {"version": {"version": []}, "tags": {"models": {}},
                     "ps": {"models": [MODEL]}, "show": {"details": {}, "model_info": []}}
        with patch.object(inventory, "request_metadata", side_effect=lambda endpoint, model: responses[endpoint]):
            report = inventory.ollama_metadata("qwen3:8b")
        for key in ("version", "installed_models", "selected_model_metadata"):
            self.assertEqual(report[key], {"status": "unavailable", "reason": "InvalidResponseShape"})
        self.assertEqual(report["active_models"]["models"][0]["context_length_status"], "not_reported")
        self.assertNotIn("context_length", report["active_models"]["models"][0])


class HostAndSourceTests(unittest.TestCase):
    def test_nvidia_readonly_command_timeout_and_redacted_failure(self):
        completed = subprocess.CompletedProcess([], 0, "NVIDIA GeForce RTX 4090, 24564, 560.70\n", "")
        with patch.object(inventory.subprocess, "run", return_value=completed) as run:
            report = inventory.nvidia_gpus()
        self.assertEqual(report["devices"][0]["vram_bytes"], 24564 * 1024 * 1024)
        self.assertEqual(run.call_args.kwargs["timeout"], 8)
        self.assertNotIn("shell", run.call_args.kwargs)
        if inventory.sys.platform == "win32":
            self.assertEqual(run.call_args.kwargs["creationflags"], subprocess.CREATE_NO_WINDOW)
        with patch.object(inventory.subprocess, "run", side_effect=subprocess.TimeoutExpired("private-path", 8)):
            self.assertEqual(inventory.nvidia_gpus(), {"status": "unavailable", "reason": "TimeoutExpired"})

    def test_ram_linux_positive_and_unavailable(self):
        with patch.object(inventory.platform, "system", return_value="Linux"), \
                patch.object(inventory.os, "sysconf", side_effect=[100, 4096], create=True):
            self.assertEqual(inventory.physical_ram(), {"status": "ok", "bytes": 409600})
        with patch.object(inventory.platform, "system", return_value="Linux"), \
                patch.object(inventory.os, "sysconf", side_effect=[-1, -1], create=True):
            self.assertEqual(inventory.physical_ram(), {"status": "unavailable", "reason": "InvalidResponseShape"})

    def test_static_source_defaults_are_not_imported_and_git_paths_are_not_emitted(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            chat, models = root / inventory.CHAT_SOURCE, root / inventory.MODEL_SOURCE
            chat.parent.mkdir(parents=True)
            models.parent.mkdir(parents=True)
            chat.write_text('raise RuntimeError("Must never import this file")\nasync def _stream_ollama(request):\n    payload = {"options": {"temperature": 0.2, "num_ctx": 16384}, "prompt": "PRIVATE"}\n', encoding="utf-8")
            models.write_text('export const MODEL_MAP: Record<ModelMode, string> = {speed: "qwen2.5:1.5b", balanced: "qwen3:8b", boost: "qwen3:8b",};', encoding="utf-8")
            with patch.object(inventory, "run_readonly", side_effect=["b" * 40 + "\n", "?? private/file.txt\n"]) as git:
                report = inventory.repository_metadata(root)
            self.assertIn("--no-optional-locks", git.call_args_list[1].args[0])
            self.assertEqual(inventory.find_repository(chat), root.resolve())
            self.assertEqual(report["git"], {"status": "ok", "head": "b" * 40, "dirty": True})
            self.assertEqual(report["defaults"]["chat_options"]["values"], {"temperature": 0.2, "num_ctx": 16384})
            self.assertEqual(report["defaults"]["model_mapping"]["values"]["balanced"], "qwen3:8b")
            encoded = json.dumps(report)
            self.assertNotIn("private", encoded)
            self.assertNotIn("PRIVATE", encoded)
            self.assertNotIn(str(root), encoded)
            chat.write_text('async def _stream_ollama(request):\n    payload = {"options": get_options()}\n', encoding="utf-8")
            models.write_text('export const MODEL_MAP = getDefaults();', encoding="utf-8")
            changed = inventory.source_defaults(root)
            self.assertEqual(changed["chat_options"]["status"], "unavailable")
            self.assertEqual(changed["model_mapping"]["status"], "unavailable")
            self.assertEqual(changed["model_mapping"]["source"], inventory.MODEL_SOURCE)


class OutputTests(unittest.TestCase):
    def test_overwrite_requires_force_and_refusal_does_not_query_environment(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "inventory.json"
            inventory.write_report({"original": True}, output)
            with self.assertRaises(FileExistsError):
                inventory.write_report({"replacement": True}, output)
            with patch.object(inventory, "capture") as capture, patch("sys.stderr", new_callable=io.StringIO):
                with self.assertRaises(SystemExit) as caught:
                    inventory.main(["--out", str(output)])
            self.assertEqual(caught.exception.code, 1)
            capture.assert_not_called()
            self.assertEqual(json.loads(output.read_text()), {"original": True})
            with patch.object(inventory, "capture", return_value={"replacement": True}) as capture, patch("builtins.print"):
                inventory.main(["--out", str(output), "--force", "--model", "qwen3:8b"])
            capture.assert_called_once_with("qwen3:8b")
            self.assertEqual(json.loads(output.read_text()), {"replacement": True})

    def test_capture_scope_does_not_claim_app_version_ui_selection_or_benchmark(self):
        with patch.object(inventory, "physical_ram", return_value=inventory.unavailable("NotReported")), \
                patch.object(inventory, "cpu_name", return_value=inventory.unavailable("NotReported")), \
                patch.object(inventory, "nvidia_gpus", return_value=inventory.unavailable("NotReported")), \
                patch.object(inventory, "ollama_metadata", return_value={}), \
                patch.object(inventory, "repository_metadata", return_value={}):
            report = inventory.capture(root=Path("unused"))
        self.assertFalse(report["scope"]["benchmark_performed"])
        self.assertEqual(report["scope"]["installed_app_version"]["status"], "not_verified")
        self.assertEqual(report["scope"]["actual_ui_model_selection"]["status"], "unknown")
        self.assertTrue(report["captured_at_utc"].endswith("+00:00"))
        self.assertEqual(json.loads(json.dumps(report, allow_nan=False)), report)

    def test_path_like_model_names_are_rejected_before_any_metadata_request(self):
        for name in ("C:/Users/private/model.gguf", "/home/private/model.gguf", "../model", "qwen3:8b\nprivate"):
            with self.subTest(name=name), patch.object(inventory, "ollama_metadata") as request:
                with self.assertRaises(ValueError):
                    inventory.capture(name)
                request.assert_not_called()


if __name__ == "__main__":
    unittest.main()
