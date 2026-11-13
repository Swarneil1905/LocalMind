"""Request-local phase measurements. Never store message or reference text."""
from contextlib import contextmanager
import time


class ChatTiming:
    def __init__(self, clock=None):
        self.clock = clock or time.monotonic
        self.started = self.clock()
        self.phases = {}
        self.first_answer = None

    def elapsed(self):
        return max(0, self.clock() - self.started)

    @contextmanager
    def phase(self, name):
        start = self.clock()
        try:
            yield
        finally:
            self.phases[name] = round(max(0, self.clock() - start), 3)

    def snapshot(self, outcome):
        return {"schema_version": 1, "request_seconds": round(self.elapsed(), 3),
                "request_first_answer_seconds": self.first_answer,
                "phase_seconds": dict(self.phases), "outcome": outcome}
