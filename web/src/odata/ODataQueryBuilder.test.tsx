// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

const describeObject = vi.fn();
const listSchema = vi.fn();
vi.mock("../api", () => ({
  describeObject: (...args: unknown[]) => describeObject(...args),
  listSchema: (...args: unknown[]) => listSchema(...args),
}));

const { ODataQueryBuilder } = await import("./ODataQueryBuilder");
const { emptyQuery } = await import("./query");

const column = (name: string, dataType: string) => ({
  name, dataType, nullable: true, default: null,
  isPrimaryKey: false, isIdentity: false, comment: null, position: 1,
});

const detail = {
  columns: [
    column("Id", "Edm.Int32"),
    column("Name", "Edm.String"),
    column("UnitPrice", "Edm.Double"),
    column("Category", "Shop.Category"),
  ],
  indexes: [], foreignKeys: [], triggers: [],
  rowCount: 77, sizeBytes: null, comment: null, ddl: null,
};

const draw = (query = emptyQuery("Products"),
  onChange: (...args: unknown[]) => void = () => {}) => render(
  <MantineProvider>
    <ODataQueryBuilder connectionId="c1" variant="data" value={query}
      onChange={(...args) => onChange(...args)} />
  </MantineProvider>,
);

describe("ODataQueryBuilder", () => {
  beforeEach(() => {
    cleanup();
    describeObject.mockReset();
    listSchema.mockReset();
    describeObject.mockResolvedValue(detail);
    listSchema.mockResolvedValue([{ ref: "Table:Products", kind: "Table", label: "Products", hasChildren: false, detail: null }]);
  });

  it("reads the entity's properties from the service", async () => {
    draw();
    await waitFor(() => expect(describeObject).toHaveBeenCalledWith("c1", "Table:Products"));
  });

  it("offers the relations under expand and keeps them out of the fields", async () => {
    draw();
    await waitFor(() => expect(describeObject).toHaveBeenCalled());

    // By its placeholder: Mantine hangs the label on the input and on a hidden twin of it, and
    // the two multi-selects would then be told apart by DOM order rather than by what they are.
    fireEvent.click(screen.getByPlaceholderText("none"));

    // Both multi-selects keep a list in the DOM, so this reads all of them: one holds the fields,
    // the other the relations, and no name is on both.
    const lists = await waitFor(() => {
      const found = [...document.querySelectorAll("[role=listbox]")]
        .map(list => [...list.querySelectorAll("[role=option]")].map(o => o.textContent));
      if (found.length < 2) throw new Error("the dropdown is not open");
      return found;
    });

    // A relation is followed rather than selected: it is the only thing expand offers.
    expect(lists).toContainEqual(["Category"]);
    expect(lists).toContainEqual(["Id", "Name", "UnitPrice"]);
  });

  it("builds the options a condition means, with the value quoted by its type", async () => {
    const onChange = vi.fn();
    const query = {
      ...emptyQuery("Products"),
      terms: [{ property: "UnitPrice", operator: "gt", value: "20" }],
    };
    draw(query, onChange);
    await waitFor(() => expect(describeObject).toHaveBeenCalled());

    // Adding a sort is the gesture; what comes back is the whole query, built.
    fireEvent.click(screen.getByLabelText("Add a sort"));

    expect(onChange).toHaveBeenCalled();
    const [, built] = onChange.mock.calls[0] as [unknown, { text: string; options: string }];
    expect(built.options).toBe("$filter=UnitPrice gt 20&$orderby=Id");
    expect(built.text).toBe("Products?$filter=UnitPrice gt 20&$orderby=Id");
  });

  it("says a filter it could not take apart is being kept as typed", async () => {
    draw({ ...emptyQuery("Products"), rawFilter: "(A eq 1 or B eq 2) and C eq 3" });
    await waitFor(() => expect(screen.getByDisplayValue("(A eq 1 or B eq 2) and C eq 3")).toBeTruthy());
    expect(screen.getByText("Build it instead")).toBeTruthy();
  });

  it("leaves the entity set to the tab in the data variant", async () => {
    draw();
    await waitFor(() => expect(describeObject).toHaveBeenCalled());
    expect(screen.queryByLabelText("Entity set")).toBeNull();
    expect(listSchema).not.toHaveBeenCalled();
  });
});
