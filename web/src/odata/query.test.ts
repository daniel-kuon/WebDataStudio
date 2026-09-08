import { describe, expect, it } from "vitest";
import { buildFilter, buildOptions, buildQuery, emptyQuery, literal, parseQuery, splitTop } from "./query";

describe("splitTop", () => {
  it("keeps a nested option together", () =>
    expect(splitTop("Orders($select=Id,Total),Category", ",")).toEqual(["Orders($select=Id,Total)", "Category"]));

  it("keeps a comma inside a string together", () =>
    expect(splitTop("contains(Name,'a,b') and Id gt 1", " and "))
      .toEqual(["contains(Name,'a,b')", "Id gt 1"]));
});

describe("parseQuery", () => {
  it("reads the entity set and the options", () => {
    const query = parseQuery("Products?$select=Id,Name&$expand=Category&$orderby=Name desc&$top=25&$skip=50");

    expect(query.set).toBe("Products");
    expect(query.select).toEqual(["Id", "Name"]);
    expect(query.expand).toEqual(["Category"]);
    expect(query.order).toEqual([{ property: "Name", descending: true }]);
    expect(query.top).toBe(25);
    expect(query.skip).toBe(50);
  });

  it("takes a filter apart into terms", () => {
    const query = parseQuery("Products?$filter=UnitPrice gt 20 and contains(Name,'ch')");

    expect(query.conjunction).toBe("and");
    expect(query.terms).toEqual([
      { property: "UnitPrice", operator: "gt", value: "20" },
      { property: "Name", operator: "contains", value: "ch" },
    ]);
    expect(query.rawFilter).toBeUndefined();
  });

  it("keeps a filter it cannot take apart, rather than dropping it", () => {
    const query = parseQuery("Products?$filter=(A eq 1 or B eq 2) and C eq 3");

    expect(query.rawFilter).toBe("(A eq 1 or B eq 2) and C eq 3");
    expect(query.terms).toEqual([]);
  });

  it("keeps an option it has no field for", () =>
    expect(parseQuery("Products?$apply=groupby((Name))").rest).toEqual([["$apply", "groupby((Name))"]]));

  it("decodes a percent-encoded value", () =>
    expect(parseQuery("Products?$filter=Name%20eq%20%27ada%27").terms)
      .toEqual([{ property: "Name", operator: "eq", value: "ada" }]));

  it("reads a bare entity set", () => expect(parseQuery("Products").set).toBe("Products"));
});

describe("literal", () => {
  it("quotes text and leaves a number alone", () => {
    expect(literal("ada", "Edm.String")).toBe("'ada'");
    expect(literal("20", "Edm.Int32")).toBe("20");
  });

  it("quotes a number-shaped value when the property is text", () =>
    expect(literal("20", "Edm.String")).toBe("'20'"));

  it("doubles a quote inside the value", () =>
    expect(literal("o'brien", "Edm.String")).toBe("'o''brien'"));

  it("guesses from the text when the type is unknown", () => {
    expect(literal("true")).toBe("true");
    expect(literal("ada")).toBe("'ada'");
  });
});

describe("buildFilter", () => {
  const types = { UnitPrice: "Edm.Double", Name: "Edm.String" };

  it("writes one term per row, joined by the connective", () =>
    expect(buildFilter({
      ...emptyQuery(), conjunction: "or",
      terms: [
        { property: "UnitPrice", operator: "gt", value: "20" },
        { property: "Name", operator: "startswith", value: "ch" },
      ],
    }, types)).toBe("UnitPrice gt 20 or startswith(Name,'ch')"));

  it("is null when there is nothing to filter by", () =>
    expect(buildFilter(emptyQuery(), types)).toBeNull());

  it("hands back a filter it never took apart", () =>
    expect(buildFilter({ ...emptyQuery(), rawFilter: "A eq 1 and (B eq 2 or C eq 3)" }, types))
      .toBe("A eq 1 and (B eq 2 or C eq 3)"));
});

describe("buildOptions", () => {
  it("leaves out what was never set", () =>
    expect(buildOptions(emptyQuery("Products"))).toBe(""));

  it("writes the options in a stable order", () =>
    expect(buildOptions({
      ...emptyQuery("Products"), select: ["Id", "Name"], expand: ["Category"],
      terms: [{ property: "Id", operator: "eq", value: "1" }],
      order: [{ property: "Name", descending: false }], top: 10,
    }, { Id: "Edm.Int32" }))
      .toBe("$select=Id,Name&$expand=Category&$filter=Id eq 1&$orderby=Name&$top=10"));
});

describe("round trip", () => {
  it("comes back as it went in", () => {
    const text = "Products?$select=Id,Name&$expand=Category&$filter=UnitPrice gt 20&$orderby=Name desc&$top=25";
    expect(buildQuery(parseQuery(text), { UnitPrice: "Edm.Double" })).toBe(text);
  });

  it("keeps a filter it could not read through the round trip", () => {
    const text = "Products?$filter=(A eq 1 or B eq 2) and C eq 3";
    expect(buildQuery(parseQuery(text))).toBe(text);
  });
});
