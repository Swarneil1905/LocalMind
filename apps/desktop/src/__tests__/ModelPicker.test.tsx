import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { ModelPicker } from "../components/ModelPicker";

afterEach(cleanup);
const props = () => ({ value: "balanced" as const, modelName: "qwen3:8b", models: { speed: "qwen2.5:1.5b", balanced: "qwen3:8b" }, onChange: vi.fn(), disabled: false });

it("shows actual configured models and changes mode by keyboard", async () => {
  const user = userEvent.setup();
  const input = props();
  render(<ModelPicker {...input} />);
  const trigger = screen.getByRole("button", { name: "Model mode: Smart, qwen3:8b" });
  trigger.focus();
  await user.keyboard("{Enter}");
  expect(document.activeElement).toBe(screen.getByRole("button", { name: /Smart: More room/ }));
  expect(screen.getByText("qwen2.5:1.5b")).toBeTruthy();
  await user.keyboard("{Shift>}{Tab}{/Shift}{Enter}");
  expect(input.onChange).toHaveBeenCalledWith("speed");
  expect(screen.queryByRole("region", { name: "Choose response mode" })).toBeNull();
  expect(document.activeElement).toBe(trigger);
});

it("dismisses with Escape or outside input without selecting a mode", async () => {
  const user = userEvent.setup();
  const input = props();
  render(<ModelPicker {...input} />);
  const trigger = screen.getByRole("button", { name: /Model mode/ });
  await user.click(trigger);
  await user.keyboard("{Escape}");
  expect(document.activeElement).toBe(trigger);
  expect(input.onChange).not.toHaveBeenCalled();
  await user.click(trigger);
  fireEvent.pointerDown(document.body);
  expect(screen.queryByRole("region", { name: "Choose response mode" })).toBeNull();
});

it("removes mode choices while a response is running", () => {
  const input = props();
  const view = render(<ModelPicker {...input} />);
  fireEvent.click(screen.getByRole("button", { name: /Model mode/ }));
  view.rerender(<ModelPicker {...input} disabled />);
  expect(screen.queryByRole("region", { name: "Choose response mode" })).toBeNull();
  expect((screen.getByRole("button", { name: /Model mode/ }) as HTMLButtonElement).disabled).toBe(true);
  view.rerender(<ModelPicker {...input} />);
  expect(screen.queryByRole("region", { name: "Choose response mode" })).toBeNull();
});
