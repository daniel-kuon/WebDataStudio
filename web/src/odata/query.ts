/// The model behind the OData query builder, and the two directions between it and the query
/// string a person types. Everything here is a pure function, so the builder itself holds no
/// knowledge of the syntax.

export interface ODataTerm {
  property: string;
  operator: string;
  value: string;
}

export interface ODataSort { property: string; descending: boolean }

export interface ODataQuery {
  /// The entity set, empty in the data tab where the tab already is one.
  set: string;
  select: string[];
  expand: string[];
  terms: ODataTerm[];
  /// How the terms are joined. One connective for the whole list, which is what a two-column form
  /// can honestly offer; anything else stays in `rawFilter`.
  conjunction: "and" | "or";
  order: ODataSort[];
  top?: number;
  skip?: number;
  /// A `$filter` the builder could not take apart, kept exactly as it was so nothing is lost. The
  /// terms above are then empty and the form shows this instead.
  rawFilter?: string;
  /// Options the builder has no field for (`$apply`, `$search`, `$compute`), carried through.
  rest: [string, string][];
}

export const OPERATORS = [
  { value: "eq", label: "=" },
  { value: "ne", label: "≠" },
  { value: "gt", label: ">" },
  { value: "ge", label: "≥" },
  { value: "lt", label: "<" },
  { value: "le", label: "≤" },
  { value: "contains", label: "contains" },
  { value: "startswith", label: "starts with" },
  { value: "endswith", label: "ends with" },
] as const;

const FUNCTIONS = ["contains", "startswith", "endswith"];

export const emptyQuery = (set = ""): ODataQuery => ({
  set, select: [], expand: [], terms: [], conjunction: "and", order: [], rest: [],
});

/// Splits on a character that is not inside brackets or a quoted string, which is what makes
/// `$expand=Orders($select=Id,Total)` and `contains(Name,'a,b')` survive.
export function splitTop(text: string, separator: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let quoted = false;
  let start = 0;

  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === "'") { quoted = !quoted; continue; }
    if (quoted) continue;
    if (c === "(") depth++;
    else if (c === ")") depth--;
    else if (depth === 0 && text.startsWith(separator, i)) {
      parts.push(text.slice(start, i));
      i += separator.length - 1;
      start = i + 1;
    }
  }

  parts.push(text.slice(start));
  return parts.map(p => p.trim()).filter(p => p.length > 0);
}

/// A quoted OData literal becomes the plain text a form field holds; a number or a boolean stays
/// as it reads.
const unquote = (value: string) =>
  value.startsWith("'") && value.endsWith("'") && value.length >= 2
    ? value.slice(1, -1).replaceAll("''", "'")
    : value;

/// Whether the value has to be quoted, decided by the property's own type where it is known. An
/// unknown property falls back to what the text looks like, which is what a person would guess.
export function literal(value: string, type?: string): string {
  const text = value.trim();
  if (text.length === 0) return "''";
  if (text === "null") return "null";

  if (type && type !== "Edm.String")
    return /^(true|false|null|-?\d+(\.\d+)?)$/i.test(text) ? text : `'${text.replaceAll("'", "''")}'`;

  if (!type && /^(true|false|-?\d+(\.\d+)?)$/i.test(text)) return text;
  return `'${text.replaceAll("'", "''")}'`;
}

function term(text: string): ODataTerm | null {
  const fn = /^(contains|startswith|endswith)\(\s*([A-Za-z_][\w./]*)\s*,\s*(.+)\)$/i.exec(text);
  if (fn) return { property: fn[2], operator: fn[1].toLowerCase(), value: unquote(fn[3].trim()) };

  const binary = /^([A-Za-z_][\w./]*)\s+(eq|ne|gt|ge|lt|le)\s+(.+)$/i.exec(text);
  if (binary) return { property: binary[1], operator: binary[2].toLowerCase(), value: unquote(binary[3].trim()) };

  return null;
}

/// Reads a query string — `People?$filter=...&$top=10`, or just the options — into the model. A
/// filter that does not come apart into terms is kept whole rather than dropped.
export function parseQuery(text: string): ODataQuery {
  const trimmed = (text ?? "").trim();
  const mark = trimmed.indexOf("?");
  const query = emptyQuery(mark === -1 ? trimmed : trimmed.slice(0, mark));
  const options = mark === -1 ? "" : trimmed.slice(mark + 1);

  for (const part of options.split("&")) {
    const at = part.indexOf("=");
    if (at === -1) continue;

    const name = part.slice(0, at).trim().toLowerCase();
    let value = part.slice(at + 1).trim();
    try { value = decodeURIComponent(value.replaceAll("+", " ")); } catch { /* left as typed */ }
    if (value.length === 0) continue;

    switch (name) {
      case "$select": query.select = splitTop(value, ","); break;
      case "$expand": query.expand = splitTop(value, ","); break;
      case "$orderby":
        query.order = splitTop(value, ",").map(entry => {
          const words = entry.split(/\s+/);
          return { property: words[0], descending: words[1]?.toLowerCase() === "desc" };
        });
        break;
      case "$top": query.top = Number(value) || undefined; break;
      case "$skip": query.skip = Number(value) || undefined; break;
      case "$filter": {
        // One connective for the whole filter, or it is not a form: a mix of and and or is kept
        // as text, and so is a term the form has no shape for.
        const ands = splitTop(value, " and ");
        const ors = splitTop(value, " or ");
        const conjunction = ands.length > 1 && ors.length === 1 ? "and" : "or";
        const parts = conjunction === "and" ? ands : ors;
        const terms = parts.map(term);

        if ((ands.length > 1 && ors.length > 1) || terms.some(t => t === null)) query.rawFilter = value;
        else {
          query.terms = terms as ODataTerm[];
          query.conjunction = conjunction;
        }
        break;
      }
      default: query.rest.push([part.slice(0, at).trim(), value]);
    }
  }

  return query;
}

/// The filter alone, which is what the data tab sends: null when there is nothing to filter by.
export function buildFilter(query: ODataQuery, types: Record<string, string> = {}): string | null {
  if (query.rawFilter) return query.rawFilter;

  const parts = query.terms
    .filter(t => t.property.length > 0)
    .map(t => FUNCTIONS.includes(t.operator)
      ? `${t.operator}(${t.property},${literal(t.value, types[t.property])})`
      : `${t.property} ${t.operator} ${literal(t.value, types[t.property])}`);

  if (parts.length === 0) return null;
  // A single connective, so a mixed filter cannot be produced by accident.
  return parts.length === 1 ? parts[0] : parts.join(` ${query.conjunction} `);
}

/// The query options, without the entity set and without the `?`. This is what the data tab hands
/// the server; the query tab prefixes the set.
export function buildOptions(query: ODataQuery, types: Record<string, string> = {}): string {
  const options: [string, string][] = [];

  if (query.select.length > 0) options.push(["$select", query.select.join(",")]);
  if (query.expand.length > 0) options.push(["$expand", query.expand.join(",")]);

  const filter = buildFilter(query, types);
  if (filter) options.push(["$filter", filter]);

  if (query.order.length > 0)
    options.push(["$orderby", query.order
      .filter(o => o.property.length > 0)
      .map(o => `${o.property}${o.descending ? " desc" : ""}`).join(",")]);

  if (query.top !== undefined) options.push(["$top", String(query.top)]);
  if (query.skip !== undefined) options.push(["$skip", String(query.skip)]);
  options.push(...query.rest);

  return options.filter(([, value]) => value.length > 0).map(([name, value]) => `${name}=${value}`).join("&");
}

/// The whole thing as a person would type it into the query tab.
export function buildQuery(query: ODataQuery, types: Record<string, string> = {}): string {
  const options = buildOptions(query, types);
  return options.length === 0 ? query.set : `${query.set}?${options}`;
}
