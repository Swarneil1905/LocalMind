import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, expect, it } from "vitest";
import { ResponseDetails } from "../components/ResponseDetails";
import type { ResponseDiagnostics } from "../types";
afterEach(cleanup);
const data: ResponseDiagnostics = { model: "test:8b", preset: "balanced", digest: "fingerprint", context_window: 8192,
  response_tokens: 1024, history_dropped: 2, references_dropped: 1, references_shortened: 0,
  total_seconds: 3, first_token_seconds: 0.4, output_tokens: 50, input_tokens: 120, tokens_per_second: 20, stop_reason: "stop" };
it("shows measured model details and warns when context was omitted", () => {
  render(<ResponseDetails data={data} />);
  expect(screen.getByText("Response details · some context did not fit")).toBeTruthy();
  expect(screen.getByText("test:8b")).toBeTruthy();
  expect(screen.getByText("0.40 s")).toBeTruthy();
  expect(screen.getByText("20 tokens/s")).toBeTruthy();
  expect(screen.getByText(/not its correctness/)).toBeTruthy();
  expect(screen.getByRole("button", { name: "Copy diagnostics" })).toBeTruthy();
});
it("does not show unknown timings as zero", () => {
  render(<ResponseDetails data={{ ...data, total_seconds: null, first_token_seconds: null, tokens_per_second: null, output_tokens: null }} />);
  expect(screen.getAllByText("Unavailable")).toHaveLength(9);
  expect(screen.queryByText("0.00 s")).toBeNull();
});
