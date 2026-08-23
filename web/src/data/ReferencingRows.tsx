import { useEffect, useState } from "react";
import { ActionIcon, Group, Loader, Text, Tooltip } from "@mantine/core";
import { IconExternalLink } from "@tabler/icons-react";
import { browseTable, type BrowseFilter, type DataPageDto, type ReferencingKeyDto } from "../api";
import { CellValue } from "../grid/CellValue";

/// The filter that finds the rows of a referencing table which point at one row here: every column
/// of the incoming key equals this row's referenced value. A null never matches an equals, and it
/// should not — a null foreign key points at nothing.
export const referencingFilters = (key: ReferencingKeyDto, values: unknown[]): BrowseFilter[] =>
  key.columns.map((column, index) => ({
    column, op: "eq", value: String(values[index] ?? ""),
  }));

const PREVIEW_ROWS = 50;

export interface ReferencingRowsProps {
  connectionId: string;
  refKey: ReferencingKeyDto;
  /// This row's values of the referenced columns, in the key's column order.
  values: unknown[];
  /// Opens the referencing table as its own view — tab, split or query, whatever is configured.
  onOpen?: () => void;
}

/// The rows of one referencing table that point at one row here, embedded under it: "the orders of
/// this customer" without leaving the customer. A preview, deliberately — the way to the full table
/// is the open button, which behaves like following a foreign key.
export function ReferencingRows({ connectionId, refKey, values, onOpen }: ReferencingRowsProps) {
  const [page, setPage] = useState<DataPageDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setPage(null);
    setError(null);
    browseTable(connectionId, refKey.tableRef, {
      limit: PREVIEW_ROWS,
      filters: referencingFilters(refKey, values),
    })
      .then(p => { if (!cancelled) setPage(p); })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });
    return () => { cancelled = true; };
    // The values array is fresh on every render; its content is what matters.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectionId, refKey.tableRef, refKey.name, JSON.stringify(values)]);

  const title = `${refKey.table} · ${refKey.columns.join(", ")} = ${values.map(String).join(", ")}`;

  return (
    <div style={{
      margin: "2px 0 6px 22px",
      borderLeft: "2px solid var(--mantine-color-default-border)",
      paddingLeft: 8,
    }}>
      <Group gap={4} wrap="nowrap">
        <Text size="xs" fw={600}>{title}</Text>
        {page && (
          <Text size="xs" c="dimmed">
            {page.rows.length}{page.rows.length === PREVIEW_ROWS ? "+" : ""} row(s)
          </Text>
        )}
        {onOpen && (
          <Tooltip label={`Open ${refKey.table} filtered to these rows`}>
            <ActionIcon size="xs" variant="subtle" aria-label={`Open ${refKey.table}`} onClick={onOpen}>
              <IconExternalLink size={11} />
            </ActionIcon>
          </Tooltip>
        )}
      </Group>

      {error && <Text size="xs" c="red">{error}</Text>}
      {!page && !error && <Loader size="xs" m={4} />}

      {page && page.rows.length > 0 && (
        <div style={{ overflowX: "auto" }}>
          <table style={{ borderCollapse: "collapse", width: "max-content" }}>
            <thead>
              <tr>
                {page.columns.map(c => (
                  <th key={c.name} style={{ textAlign: "left", padding: "1px 8px", whiteSpace: "nowrap" }}>
                    <Text size="10px" fw={600} c="dimmed">{c.name}</Text>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {page.rows.map((row, index) => (
                <tr key={index}>
                  {row.map((value, column) => (
                    <td key={column} style={{ padding: "1px 8px", whiteSpace: "nowrap" }}>
                      <CellValue value={value} />
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {page && page.rows.length === 0 && <Text size="xs" c="dimmed">no referencing rows</Text>}
    </div>
  );
}
