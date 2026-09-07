// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { FilePicker } from "./FilePicker";

vi.mock("../api", () => ({
  uploadConnectionFile: vi.fn(async (file: File) => ({
    id: "abc", name: file.name, engine: "sqlite", readOnly: false,
  })),
  browseFiles: vi.fn(async (path?: string) => path === undefined
    ? { path: null, roots: ["/data", "/mounted"], parent: null, directories: [], files: [] }
    : {
        path: "/mounted",
        roots: ["/data", "/mounted"],
        parent: null,
        directories: [{ name: "reports", path: "/mounted/reports" }],
        files: [
          { name: "shop.sqlite3", path: "/mounted/shop.sqlite3", size: 4096, engine: "sqlite" },
          { name: "notes.zip", path: "/mounted/notes.zip", size: 12, engine: null },
        ],
      }),
}));

// jsdom has no scrollIntoView, and Mantine's modal reaches for it after the click.
beforeAll(() => { Element.prototype.scrollIntoView = vi.fn(); });
afterEach(cleanup);

const show = (props: Partial<Parameters<typeof FilePicker>[0]> = {}) =>
  render(
    <MantineProvider>
      <FilePicker onUploaded={() => {}} onPicked={() => {}} {...props} />
    </MantineProvider>,
  );

describe("the file picker", () => {
  it("uploads the file that was chosen and hands back the connection", async () => {
    const onUploaded = vi.fn();
    show({ onUploaded });

    // Mantine's FileInput shows a button and keeps a real file input hidden behind it; the browser
    // changes that one, so the test does too.
    fireEvent.change(document.querySelector("input[type=file]")!, {
      target: { files: [new File(["x"], "shop.sqlite3", { type: "application/octet-stream" })] },
    });

    await waitFor(() => expect(onUploaded).toHaveBeenCalledWith(
      expect.objectContaining({ id: "abc", engine: "sqlite" })));
  });

  it("browses the server and hands back the path that was picked", async () => {
    const onPicked = vi.fn();
    show({ onPicked });

    fireEvent.click(screen.getByRole("button", { name: "Browse the server" }));
    fireEvent.click(await screen.findByText("/mounted"));
    fireEvent.click(await screen.findByText("shop.sqlite3"));

    await waitFor(() => expect(onPicked).toHaveBeenCalledWith("/mounted/shop.sqlite3", "sqlite"));
  });

  it("shows a file nothing opens without letting it be picked", async () => {
    const onPicked = vi.fn();
    show({ onPicked });

    fireEvent.click(screen.getByRole("button", { name: "Browse the server" }));
    fireEvent.click(await screen.findByText("/mounted"));

    const zip = await screen.findByText("notes.zip");
    fireEvent.click(zip);

    // Listed, so nobody wonders where the file went; not pickable, because nothing reads it.
    expect(onPicked).not.toHaveBeenCalled();
  });

  it("says what went wrong instead of throwing it", async () => {
    const { uploadConnectionFile } = await import("../api");
    vi.mocked(uploadConnectionFile).mockRejectedValueOnce(
      new Error("this file is not a SQLite database"));

    show();

    fireEvent.change(document.querySelector("input[type=file]")!, {
      target: { files: [new File(["x"], "shop.db", { type: "application/octet-stream" })] },
    });

    expect(await screen.findByText(/not a SQLite database/)).toBeTruthy();
  });
});
