# Query builder

The builder is for the queries you would otherwise write by looking up which column points at
which. It produces plain SQL and hands it to a query tab — nothing it makes needs the builder to
run.

![Query builder](../assets/screenshots/builder-dark.png)

## The canvas

Pick a table from the box on the left and it lands on the canvas as a card. Every column on the
card has a checkbox, and ticking it puts the column in the `SELECT`.

Drag from the handle on one card to another and you get a join. If the schema already knows how the
two relate — a foreign key in either direction — the condition is filled in for you; the tables
`orders` and `people` join on `orders.person_id = people.id` without anybody typing it. Where there
is no key, the first column of each side is a starting point you correct in the join row below the
canvas.

- Double-click a join line to remove it.
- The × on a card removes the table, its joins, its selected columns and its filters.
- Joins, filters and sorting are also editable as exact rows under the canvas — a line can express
  that two tables are joined, not that the condition is `>=`.

## While you build

The generated SQL sits under the canvas, and under that the first 50 rows of it, re-run 400 ms
after you stop changing things. A query that does not run yet shows its error there and nothing
stops working; the canvas is unaffected.

Filter values become bind parameters, never string literals — the builder cannot be talked into
writing an injection for you.

## Aggregates

Give any selected column an aggregate (`count`, `sum`, `avg`, `min`, `max`) and the query becomes a
grouped one: every column without an aggregate moves into `GROUP BY` automatically, because that is
what every engine demands. A `Having` section appears once something aggregates, and its conditions
apply to the aggregate rather than the column.

`Distinct` and `Limit` are next to the grouping switch.

## Getting the query back

"Open in query tab" appends the builder's model to the statement as a comment:

```sql
SELECT "a"."name", SUM("b"."total") AS "spent"
  FROM "main"."people" "a"
  INNER JOIN "main"."orders" "b" ON "a"."id" = "b"."person_id"
 GROUP BY "a"."name";
/* wds:model {"tables":[…],"joins":[…]} */
```

That comment is what lets **Open this query in the builder** (command palette) put the query back on
a canvas. Filter values are deliberately left out of it: the comment travels with the SQL into the
history and into anything you paste it in to.

A statement written by hand carries no such comment, and the builder does not pretend to understand
it — there is no SQL parser behind this, and a half-working one would be worse than the honest limit.

## EXISTS and NOT EXISTS

The **Exists** section adds a condition over a table that is *not* part of the query: pick the table,
the column on its side, and the column of the query it lines up with.

`NOT EXISTS` is the reason this exists at all. "Customers with no orders" cannot be written as a
join: a join that finds nothing removes the row instead of keeping it, and `LEFT JOIN … IS NULL` is
the workaround everybody has to look up. Each subquery gets an alias of its own (`x1`, `x2`), so it
cannot collide with the query's own tables.

## OData services

An OData connection has a builder of its own, because there is no SQL to generate: a request is a
resource path with query options, and the form writes those. The wand button opens it in a query
tab and in a data tab both — the same form over the same service, feeding a request in the one and
the grid in the other.

What it reads comes from the service's `$metadata`, so the lists are what exists rather than what
was typed correctly:

- **Fields** is `$select`. Nothing chosen means everything the entity has.
- **Expand** is `$expand`, and it offers the entity's navigation properties — the relations. An
  expanded relation arrives as one column holding the JSON the service sent, which the grid's own
  viewer opens as a tree.
- **Filter** is `$filter`, a row per condition: a property, a comparison or one of `contains`,
  `startswith` and `endswith`, and a value. Whether the value is quoted follows the property's
  type, so `20` on a number is `20` and on a string is `'20'`. Two or more rows are joined by one
  `and` or one `or`.
- **Order** is `$orderby`, a row per property, ascending or descending.
- **Top** and **Skip** are the query tab's only: in the data tab the grid does the paging.

In the query tab the text in the editor stays the one source of truth. The form is read out of it
and written back to it, so typing and clicking are the same gesture said two ways, and the request
under the form is what will be sent.

A `$filter` the form cannot take apart — `(A eq 1 or B eq 2) and C eq 3`, say — is kept exactly as
it was typed, in a field that says so. Nothing is dropped on the way through the builder, and
**Build it instead** clears it when the rows are wanted back. Options the form has no field for,
such as `$apply`, travel through it untouched and say they are being kept.

In the data tab the form and the grid both hold: a column's own sort or filter wins over the same
option in the form, and a filter typed into a column header is added to the form's with `and`.
