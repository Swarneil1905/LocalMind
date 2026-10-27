/* global AudioWorkletProcessor, registerProcessor */
/* Runs on the audio rendering thread. Only the foreground recording connects it. */
class LocalMindPCM extends AudioWorkletProcessor {
  process(inputs) {
    const input = inputs[0]?.[0];
    if (input) this.port.postMessage(input.slice());
    return true;
  }
}
registerProcessor("localmind-pcm", LocalMindPCM);
