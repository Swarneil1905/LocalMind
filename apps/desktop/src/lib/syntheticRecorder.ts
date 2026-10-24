export type RecorderScenario = "granted" | "denied" | "missing";
export interface RecordingAdapter {
  devices: { id: string; label: string }[];
  requestPermission: () => Promise<void>;
  start: (device: string) => void;
  finish: (signal: AbortSignal) => Promise<string>;
  cancel: () => void;
}

/** UI pilot only: never accesses navigator.mediaDevices or captures audio. */
export function syntheticRecorder(scenario: RecorderScenario = "granted"): RecordingAdapter {
  let recording = false;
  const devices = scenario === "missing" ? [] : [
    { id: "sample-default", label: "Sample laptop microphone (simulated)" },
    { id: "sample-headset", label: "Sample headset (simulated)" },
  ];
  return {
    devices,
    async requestPermission() {
      if (scenario === "denied") throw new Error("Simulated microphone permission denied. Choose Allowed to try again.");
      if (!devices.length) throw new Error("No simulated input device. Choose Allowed to try again.");
    },
    start(device) {
      if (!devices.some(item => item.id === device)) throw new Error("Select an available input first.");
      recording = true;
    },
    finish(signal) {
      if (!recording) return Promise.reject(new Error("No recording is active."));
      recording = false;
      return new Promise((resolve, reject) => {
        const cancel = () => { clearTimeout(timer); reject(new DOMException("Cancelled", "AbortError")); };
        const timer = setTimeout(() => {
          signal.removeEventListener("abort", cancel);
          resolve("Sample transcript: Help me plan my next project milestone.");
        }, 350);
        if (signal.aborted) cancel();
        else signal.addEventListener("abort", cancel, { once: true });
      });
    },
    cancel() { recording = false; },
  };
}
