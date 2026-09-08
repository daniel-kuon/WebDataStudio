// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { announce, parameterFrom, withoutParameter } from "./openFromUrl";

describe("what the studio does with its own URL", () => {
  it("finds the parameter and leaves other queries alone", () => {
    expect(parameterFrom("?u=/data/shop.db")).toBe("/data/shop.db");
    expect(parameterFrom("?tab=query&u=/data/shop.db")).toBe("/data/shop.db");
    expect(parameterFrom("?tab=query")).toBeNull();
    expect(parameterFrom("")).toBeNull();
  });

  it("says what was refused and stays quiet about what opened", () => {
    const lines = announce([
      { id: "a", label: "sales", refused: null },
      { id: null, label: "orders", refused: "download is not allowed on this studio" },
    ]);

    expect(lines).toEqual(["orders: download is not allowed on this studio"]);
  });

  it("drops the parameter and keeps the rest of the address", () => {
    expect(withoutParameter("/studio", "?tab=query&u=/data/shop.db")).toBe("/studio?tab=query");
    expect(withoutParameter("/studio", "?u=/data/shop.db")).toBe("/studio");
  });
});
