// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { BringYourOwn } from "./BringYourOwn";
import type { StudioAccess } from "../api";

afterEach(cleanup);

const access = (over: Partial<StudioAccess> = {}): StudioAccess => ({
  scope: "Session",
  mayAdd: true,
  mayUpload: true,
  mayBrowse: true,
  ...over,
});

const show = (over: Partial<StudioAccess> = {}, onAdd = vi.fn()) =>
  render(
    <MantineProvider>
      <BringYourOwn access={access(over)} onAdd={onAdd} />
    </MantineProvider>,
  );

describe("the first thing a visitor sees", () => {
  it("names the ways in that are open", () => {
    show();

    expect(screen.getByText(/connection string/i)).toBeTruthy();
    expect(screen.getByText(/from your own machine/i)).toBeTruthy();
    expect(screen.getByText(/on this server/i)).toBeTruthy();
  });

  it("leaves out a way in the deployment closed", () => {
    show({ mayUpload: false, mayBrowse: false });

    expect(screen.getByText(/connection string/i)).toBeTruthy();
    expect(screen.queryByText(/from your own machine/i)).toBeNull();
    expect(screen.queryByText(/on this server/i)).toBeNull();
  });

  /// A studio that closed every door has nothing to offer, and says that rather than showing an
  /// empty list of ways in.
  it("says so when every way in is closed", () => {
    show({ mayAdd: false, mayUpload: false, mayBrowse: false });

    expect(screen.getByText(/this studio has no connections/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /bring a database/i })).toBeNull();
  });

  it("says what a session connection is worth knowing", () => {
    show();

    expect(screen.getByText(/belongs to this browser/i)).toBeTruthy();
  });

  /// In a studio where connections are shared there is nothing to warn about.
  it("says nothing about browsers when connections are stored", () => {
    show({ scope: "Stored" });

    expect(screen.queryByText(/belongs to this browser/i)).toBeNull();
  });

  it("opens the form", () => {
    const onAdd = vi.fn();
    show({}, onAdd);

    screen.getByRole("button", { name: /bring a database/i }).click();

    expect(onAdd).toHaveBeenCalled();
  });
});
