import copy
import unittest
from image_benchmark import load_spec, hardware_gate


class ImageGateTests(unittest.TestCase):
    def test_fixed_prompts_and_unreviewed_hardware_gate(self):
        spec = load_spec()
        self.assertEqual(len(spec["revision"]), 40)
        self.assertEqual(spec["configuration"]["batch_size"], 1)
        rows = [{"status": "completed", "seconds": 10, "peak_vram_mib": 5000, "peak_rss_mib": 8000} for _ in range(10)]
        self.assertTrue(hardware_gate(rows, spec))
        self.assertFalse(hardware_gate(rows[:9], spec))
        for key, value in [("status", "failed"), ("peak_vram_mib", 6000), ("peak_rss_mib", 13000)]:
            bad = copy.deepcopy(rows)
            bad[0][key] = value
            self.assertFalse(hardware_gate(bad, spec))
        self.assertTrue(spec["gate"]["human_visual_review_required"])
