# Browsing data

Double-click a table in the explorer and its rows open in a data tab. The tab is more than a page
of rows: a query bar above the grid filters, sorts, joins and groups without writing SQL, and the
foreign keys of the table are the way to the rows they point at — in both directions.

Everything the query bar says runs **on the server**: a page holds 200 of possibly millions of
rows, so filtering or sorting in the browser would work on the wrong set. Masking, read-only
connections and the row cap apply exactly as they do to a hand-written statement.

## The query bar

- **Filter** adds one condition: a column, an operator (`contains`, `=`, `≠`, `>`, `≥`, `<`, `≤`,
  `starts with`, `ends with`, `is null`, `is not null`) and a value. Several filters apply
  together. The column menu's quick filter is a shorthand for a `contains` filter here.
- **Sort** adds one order column; several sort by the first, then the second. The column menu's
  *Sort ascending / descending* replaces the order, *Add to sort* appends.
- **Join** follows the table's own foreign keys: pick a key and the referenced table's columns
  appear in the same grid, prefixed with the referenced table's name (`customers.name`). A join is
  always a LEFT JOIN — a row whose key is null stays a row. Values are joined for reading; a
  joined view is not editable, and the tab says so.
- **Group** groups by one or more columns — the table's own or joined ones — with aggregates
  (`count`, `sum`, `avg`, `min`, `max`). A grouped view shows the group columns and the aggregates,
  can be sorted by either, and grouping without an aggregate shows each group's row count.

Every active piece is a chip that comes off with one click; the grid always shows exactly what the
chips say. A masked column stays masked behind a join, under either of its names.

## Following a foreign key

A column that is a foreign key carries an arrow; the arrow on a cell goes to the row it references.
Where that lands is configurable — the route button in the toolbar switches it, for every data tab
at once, and the choice is saved with the workspace:

| Mode | What opens |
|---|---|
| as a query tab | a query tab with the `SELECT`, the original behaviour and the default |
| in a new data tab | the referenced table as a data tab, filtered to the referenced row |
| in a split view | the same filtered data tab, split **next to** the tab you followed from |

In split mode a chain of follows — order → customer → country — opens a further split each time,
so the path you took stays on screen left to right.

## Referencing rows: the other direction

A table's own keys say where its rows point. The data tab also knows who points **back**: when
other tables reference this one, every row gets an expander, and a column referenced from elsewhere
carries a return-arrow marker in its header.

- One incoming key: the expander shows its referencing rows inline under the row — the orders of
  this customer, under the customer.
- Several incoming keys: the expander asks which — each key on its own, or **Expand all** for all
  of them at once.
- Each expansion is a preview of up to 50 rows; its open button opens the referencing table
  filtered to exactly these rows, honouring the same mode a followed key uses.
