/// The studio's own URL as a way to open connections, where the deployment allows it.
///
/// The parameter is read once at start-up and posted to the server, which decides what is allowed:
/// the browser is not the place for that decision, and a check made here would be a suggestion.
export interface OpenedConnection {
  id: string | null;
  label: string;
  refused: string | null;
}

export const parameterFrom = (search: string): string | null => {
  const value = new URLSearchParams(search).get("u");

  return value && value.trim().length > 0 ? value : null;
};

/// Only the refusals are worth a line: what opened is in the tree, where it can be seen.
export const announce = (opened: OpenedConnection[]): string[] =>
  opened.filter(o => o.refused).map(o => `${o.label}: ${o.refused}`);

/// The same address without `u`, so a reload does not carry a password around a second time — and
/// so a screenshot of the tab is not a copy of the credentials.
export const withoutParameter = (pathname: string, search: string): string => {
  const query = new URLSearchParams(search);
  query.delete("u");

  const rest = query.toString();

  return rest.length > 0 ? `${pathname}?${rest}` : pathname;
};
