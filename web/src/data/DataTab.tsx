import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Group, Loader, Menu, Select, Text, Tooltip,
} from "@mantine/core";
import {
  IconArrowBackUp, IconArrowRight, IconChevronDown, IconChevronRight, IconClipboardPlus, IconCopy,
  IconCopyPlus, IconCornerDownLeft, IconDeviceFloppy, IconDownload, IconEye, IconEyeOff,
  IconFilter, IconLayoutColumns, IconLock, IconPlus, IconRefresh, IconRestore, IconRoute,
  IconSortAscending, IconSortDescending, IconSquareArrowRight, IconTrash,
  IconSparkles, IconWand, IconBraces, IconHistory,
} from "@tabler/icons-react";
import { copyAsCsv, copyAsJson, copyAsMarkdown, copyAsSqlInList } from "../export/copyAs";
import {
  browseTable, countRows, getMaskPolicy, getUndoState, historyAvailable, lookupValues,
  referencingKeys, saveMaskPolicy,
  type BrowseAggregate, type BrowseFilter, type BrowseSort, type DataPageDto, type ForeignKeyDto,
  type ReferencingKeyDto, type UndoStateDto,
} from "../api";
import { Pager } from "../grid/Pager";

import { CellValue } from "../grid/CellValue";
import { MenuFilterInput } from "../grid/MenuFilterInput";
import { DistinctValues } from "../grid/DistinctValues";
import { LookupPicker } from "../grid/LookupPicker";
import { JsonColumnDialog } from "./JsonColumnDialog";
import { followColumns, newRows, withoutAddress, ROW_ADDRESS } from "./follow";
import { parsePastedRows } from "../export/pasteRows";
import { EditableCell } from "../grid/editing/EditableCell";
import { ChangePreviewModal } from "../grid/editing/ChangePreviewModal";
import { GenerateDialog } from "./GenerateDialog";
import { BulkUpdateModal } from "../grid/editing/BulkUpdateModal";
import { useChangeSet, type RowChange } from "../grid/editing/useChangeSet";
import { preferences, savePreferences, usePreferences } from "../shell/preferences";
import { RowHistoryModal } from "./RowHistoryModal";
import { carriesZone, describeZone } from "../grid/formatTime";
import { QueryBar } from "./QueryBar";
import { ReferencingRows } from "./ReferencingRows";

/// The same "14:00" means two different moments in `timestamptz` and in `timestamp`, so the header
/// says which of the two this column is.
const zoneNote = (dataType: string) => {
  const zoned = carriesZone(dataType);

  return zoned === null ? null
    : zoned ? `${dataType} — stored with a time zone`
      : `${dataType} — no time zone stored`;
};

/// A column that holds bytes. Typing into one is not what anybody wants; it takes a file.
const isBinary = (type: string) =>
  /(binary|blob|bytea|image|^raw)/i.test(type);

/// The referenced table as a schema node reference; an unqualified name means the same schema.
export const foreignKeyRef = (fk: ForeignKeyDto) =>
  `Table:${fk.referencedSchema ? `${fk.referencedSchema}/` : ""}${fk.referencedTable}`;

/// Where following a foreign key lands: a query tab with the SELECT (the original behaviour), a
/// data tab on the referenced table, or a split next to the tab the key was followed from.
export type FkNavMode = "query" | "tab" | "split";

export const FK_NAV_LABELS: Record<FkNavMode, string> = {
  query: "as a query tab",
  tab: "in a new data tab",
  split: "in a split view",
};

export interface DataTabProps {
  connectionId: string;
  objectRef: string;
  tableName: string;
  foreignKeys?: ForeignKeyDto[];
  /// Filters the tab opens with — how a followed foreign key lands on the referenced rows.
  initialFilters?: BrowseFilter[];
  onFollowForeignKey?: (fk: ForeignKeyDto, value: unknown) => void;
  /// Opens a referencing table filtered to the rows that point at one row here, honouring the
  /// same navigation mode a followed foreign key uses.
  onOpenReferencing?: (key: ReferencingKeyDto, values: unknown[]) => void;
  /// How navigation opens its target, and the way to change it. Shown as a small menu in the
  /// toolbar; the shell owns the value so every data tab agrees.
  fkNavMode?: FkNavMode;
  onFkNavModeChange?: (mode: FkNavMode) => void;
  /// Opens the export dialog on this table. Absent only where there is no shell to open it in.
  onExport?: () => void;
  /// The filter the tab opens with. The data search uses it: a hit opens its table already filtered
  /// on the column that matched.
  initialFilter?: { column: string; value: string } | null;
  /// Opens SQL in a query tab — the flatten of a JSON column goes there rather than running here.
  onOpenInEditor?: (sql: string) => void;
}

/// Whether this column is worth opening the JSON panel on: a declared JSON type, or a text column
/// whose values on this page start like a document. Guessing from the type alone would miss every
/// `text` column that holds JSON, which is most of them outside PostgreSQL.
function jsonish(dataType: string, values: unknown[]): boolean {
  if (/json/i.test(dataType)) return true;
  if (!/char|text|string|clob/i.test(dataType)) return false;

  return values.some(value => typeof value === "string"
    && (value.trimStart().startsWith("{") || value.trimStart().startsWith("[")));
}

export function DataTab({ connectionId, objectRef, tableName, foreignKeys = [], initialFilters,
  onFollowForeignKey, onOpenReferencing, fkNavMode, onFkNavModeChange, onExport,
  initialFilter = null, onOpenInEditor }: DataTabProps) {
  // How many rows a page holds is a preference, not a constant: a wide table wants fewer.
  const { pageSize } = usePreferences();
  const [page, setPage] = useState<(DataPageDto & { grouped?: boolean }) | null>(null);
  const [pageIndex, setPageIndex] = useState(1);
  // What a count said, for as long as this page is the one it counted.
  const [exactTotal, setExactTotal] = useState<number | null>(null);
  const [counting, setCounting] = useState(false);
  // Keyed by name, like every other piece of column state in this tab — the row editor, the key
  // badge and the foreign-key lookup all address columns by name.
  const [hidden, setHidden] = useState<Set<string>>(new Set());
  // Whether the database itself kept older versions of these rows. Asked once per tab, so a
  // button that cannot work is never drawn.
  const [historySupported, setHistorySupported] = useState(false);
  const [historyOf, setHistoryOf] = useState<Record<string, string> | null>(null);
  const [error, setError] = useState<string | null>(null);
  // What a paste did, said once rather than as a toast that is gone before it is read.
  const [pasteNote, setPasteNote] = useState<string | null>(null);
  const [pending, setPending] = useState<RowChange[] | null>(null);
  const [bulk, setBulk] = useState<{ rowIndex: number; column: string; value: unknown }[] | null>(null);
  const [selected, setSelected] = useState<{ row: number; col: number }[]>([]);
  const [nonce, setNonce] = useState(0);
  // Everything the query bar says. Filtering, sorting, joining and grouping all happen on the
  // server: a page holds a few hundred of possibly millions of rows, so doing any of it in the
  // browser would work on the wrong set. A column box's quick filter is an "expr" filter here —
  // the same small language the plain browse speaks.
  const [filters, setFilters] = useState<BrowseFilter[]>(
    initialFilters
    ?? (initialFilter ? [{ column: initialFilter.column, op: "expr", value: initialFilter.value }] : []));
  const [sorts, setSorts] = useState<BrowseSort[]>([]);
  const [joins, setJoins] = useState<string[]>([]);
  const [groupBy, setGroupBy] = useState<string[]>([]);
  const [aggregates, setAggregates] = useState<BrowseAggregate[]>([]);
  // The columns a filter or grouping may address: the last ungrouped page's. A grouped page only
  // answers the group columns, which must not shrink what the pickers offer.
  const [addressable, setAddressable] = useState<string[]>([]);
  // The foreign keys of other tables that point at this one, for expanding referencing rows.
  const [incoming, setIncoming] = useState<ReferencingKeyDto[]>([]);
  // Which incoming keys are expanded under which row.
  const [expanded, setExpanded] = useState<Record<number, string[]>>({});
  // What the server says could be taken back on this table, and whether its script is open.
  const [undoState, setUndoState] = useState<UndoStateDto | null>(null);
  const [undoOpen, setUndoOpen] = useState(false);
  const [generateOpen, setGenerateOpen] = useState(false);
  // Which JSON column somebody wanted to look inside.
  const [jsonColumn, setJsonColumn] = useState<string | null>(null);
  // Following the table: which column says what is new, how often to look, and which rows on this
  // page were not on the last one. The seen keys are a ref: they are read inside a fetch, not
  // during a render.
  const [followColumn, setFollowColumn] = useState<string | null>(null);
  const [followSeconds, setFollowSeconds] = useState(5);
  const [fresh, setFresh] = useState<ReadonlySet<number>>(new Set());
  const seenRows = useRef<Set<string>>(new Set());
  // "customer_id.name": a column from the table a foreign key points at, shown next to the id
  // instead of being reached by following it.
  const [lookups, setLookups] = useState<string[]>([]);
  // Masking happens on the server, so revealing is a fresh request rather than a render flag.
  const [reveal, setReveal] = useState(false);

  const columns = useMemo(() => page?.columns.map(c => c.name) ?? [], [page]);

  /// Moves one column between the policy's two lists and re-reads the page, so the change is
  /// visible where it was made rather than after the next refresh.
  const setMasking = async (column: string, mask: boolean) => {
    try {
      const policy = await getMaskPolicy(connectionId);
      await saveMaskPolicy(connectionId, {
        ...policy,
        extra: mask ? [...new Set([...policy.extra, column])] : policy.extra.filter(c => c !== column),
        never: mask ? policy.never.filter(c => c !== column) : [...new Set([...policy.never, column])],
      });
      setNonce(n => n + 1);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };
  // Only a table with keys can have one row followed through time, and only some engines keep it.
  useEffect(() => {
    setHistorySupported(false);

    if (!page || page.keyColumns.length === 0) return;

    let cancelled = false;

    historyAvailable(connectionId, objectRef)
      .then(state => { if (!cancelled) setHistorySupported(state.supported); })
      .catch(() => {});

    return () => { cancelled = true; };
  }, [connectionId, objectRef, page?.keyColumns.length]);

  // Following re-fetches the first page, newest first, and nothing else: a tail that also paged
  // would scroll away from what it is showing.
  useEffect(() => {
    if (followColumn === null) return;

    setSorts([{ column: followColumn, desc: true }]);
    setPageIndex(1);

    const timer = window.setInterval(() => setNonce(n => n + 1), followSeconds * 1000);
    return () => window.clearInterval(timer);
  }, [followColumn, followSeconds]);

  // Turning it off forgets what it had seen, so switching it on again does not flash a whole page.
  useEffect(() => {
    if (followColumn === null) {
      seenRows.current = new Set();
      setFresh(new Set());
    }
  }, [followColumn]);

  // Which columns could order a tail at all: a timestamp, a date, or an increasing key.
  const followable = useMemo(
    () => page && !page.grouped ? followColumns(page.columns, page.keyColumns) : [],
    [page]);

  const rowAt = useCallback((index: number) => page?.rows[index], [page]);
  const changeSet = useChangeSet(page?.keyColumns ?? [], columns, rowAt);

  useEffect(() => {
    let cancelled = false;
    setError(null);
    browseTable(connectionId, objectRef, {
      offset: (pageIndex - 1) * pageSize, limit: pageSize,
      filters, sort: sorts, joins, groupBy, aggregates, lookups,
      reveal: reveal || undefined,
    })
      .then(p => {
        if (cancelled) return;

        // Following: whatever is on this page and was not on the last one is new, and stays marked
        // until the next fetch.
        setFresh(followColumn
          ? newRows(p.rows, p.columns, p.keyColumns, seenRows.current)
          : new Set<number>());

        setPage(p);
        if (!p.grouped) setAddressable(p.columns.map(c => c.name).filter(c => c !== ROW_ADDRESS));
      })
      .catch(e => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
    // followColumn only tints rows; the fetch itself is driven by the states below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectionId, objectRef, pageIndex, pageSize, nonce, filters, sorts, joins, groupBy,
    aggregates, lookups, reveal]);

  // A counted total describes one filter on one table. Anything that changes what is being read
  // makes it a number about something else.
  useEffect(() => setExactTotal(null), [connectionId, objectRef, filters, joins, groupBy, aggregates, nonce]);

  // Re-read after every apply: what can be undone changes with the data, not with the render.
  useEffect(() => {
    let cancelled = false;
    getUndoState(connectionId, objectRef)
      .then(state => { if (!cancelled) setUndoState(state); })
      .catch(() => { if (!cancelled) setUndoState(null); });
    return () => { cancelled = true; };
  }, [connectionId, objectRef, nonce]);

  // Who points at this table, once: the expander column and the header markers hang off it.
  useEffect(() => {
    let cancelled = false;
    referencingKeys(connectionId, objectRef)
      .then(keys => { if (!cancelled) setIncoming(keys); })
      .catch(() => { if (!cancelled) setIncoming([]); });
    return () => { cancelled = true; };
  }, [connectionId, objectRef]);

  // A different page shows different rows; an expansion kept by index would sit under a stranger.
  useEffect(() => { setExpanded({}); }, [pageIndex, nonce, filters, sorts, joins, groupBy, aggregates]);

  if (error) return <Text c="red" size="xs" p="xs">{error}</Text>;
  if (!page) return <Loader size="xs" m="xs" />;

  const copy = (text: string) => navigator.clipboard.writeText(text);
  // Hidden columns are dropped from the render, not from the fetch: the row editor still needs
  // the key columns, and the server has no idea what the browser is showing.
  const visibleColumns = page
    ? page.columns.filter(c => !hidden.has(c.name) && c.name !== ROW_ADDRESS)
    : [];

  // What leaves the tab — the clipboard, and anything reading the whole result — without the
  // address column. The grid keeps it, because the row editor addresses rows by it.
  const shown = withoutAddress(page.rows, page.columns);

  const fkForColumn = (column: string) => foreignKeys.find(fk => fk.columns.includes(column));
  const referencedBy = (column: string) => incoming.some(key =>
    key.referencedColumns.some(c => c.toLowerCase() === column.toLowerCase()));

  // Borrowed columns are read-only: an edit here would be an update to a row this grid is not
  // addressing at all.
  const isLookup = (column: string) => (page?.lookups ?? []).includes(column);
  const isBoolean = (type: string) => /bool|bit/i.test(type);

  // Rows on their way in: what somebody copied out of a spreadsheet becomes pending inserts,
  // which the preview then shows as the statements they are. Nothing is written by pasting.
  const pasteRows = async () => {
    setPasteNote(null);
    let text = "";
    try {
      text = await navigator.clipboard.readText();
    } catch {
      setPasteNote("the browser did not allow reading the clipboard");
      return;
    }

    const insertable = page.columns.filter(c => c.name !== ROW_ADDRESS && !isLookup(c.name));
    const parsed = parsePastedRows(text, insertable);

    if (parsed.rows.length === 0) {
      setPasteNote("there was nothing to paste");
      return;
    }

    for (const row of parsed.rows) changeSet.insertRow(row);

    setPasteNote(`${parsed.rows.length} row${parsed.rows.length === 1 ? "" : "s"} pasted` +
      `${parsed.usedHeader ? " by column name" : ""}` +
      `${parsed.ignored.length > 0 ? `, ignoring ${parsed.ignored.join(", ")}` : ""}` +
      " — review them and apply");
  };

  const sortOf = (column: string) => sorts.find(s => s.column.toLowerCase() === column.toLowerCase());
  const boxFilterOf = (column: string) => filters.find(f =>
    f.column.toLowerCase() === column.toLowerCase() && f.op === "expr");

  const setSort = (column: string, desc: boolean, add: boolean) => {
    setSorts(current => add
      ? [...current.filter(s => s.column.toLowerCase() !== column.toLowerCase()), { column, desc }]
      : [{ column, desc }]);
    setPageIndex(1);
  };

  // The column box's filter is the small expression language (the server's FilterExpression): a
  // plain word still means "contains", and the distinct list types `=a,=b` into the same box.
  const setBoxFilter = (column: string, value: string) => {
    setFilters(current => {
      const rest = current.filter(f =>
        !(f.column.toLowerCase() === column.toLowerCase() && f.op === "expr"));
      return value ? [...rest, { column, op: "expr", value }] : rest;
    });
    setPageIndex(1);
  };

  /// This row's values of the columns an incoming key references, in the key's order.
  const referencedValues = (row: unknown[], key: ReferencingKeyDto) =>
    key.referencedColumns.map(name => {
      const index = columns.findIndex(c => c.toLowerCase() === name.toLowerCase());
      return index >= 0 ? row[index] : null;
    });

  const toggleExpanded = (rowIndex: number, keyName: string) =>
    setExpanded(current => {
      const names = current[rowIndex] ?? [];
      const next = names.includes(keyName)
        ? names.filter(n => n !== keyName)
        : [...names, keyName];
      return { ...current, [rowIndex]: next };
    });

  const insertedRows = Array.from({ length: changeSet.insertedRows }, (_, i) => -(i + 1));
  // The expander column exists only while somebody points at this table and the view is ungrouped:
  // a grouped row is not a row of the table, so nothing references it.
  const expander = incoming.length > 0 && !page.grouped;

  // An exact count needs a question the count endpoint can be asked: no filter, or one column box.
  const countable = !page.grouped
    && (filters.length === 0 || (filters.length === 1 && filters[0].op === "expr"));

  const selectedCells = selected
    .map(s => ({ rowIndex: s.row, column: columns[s.col], value: changeSet.editedValue(s.row, columns[s.col]) }))
    .filter(c => c.column !== undefined);

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={4} p={4} wrap="nowrap">
        <Tooltip label={page.editable ? "Save changes" : page.reason ?? "not editable"}>
          <Button size="compact-xs" leftSection={<IconDeviceFloppy size={13} />}
            disabled={!page.editable || !changeSet.isDirty}
            onClick={() => setPending(changeSet.changes)}>
            Save
          </Button>
        </Tooltip>
        {undoState?.available ? (
          <Tooltip label={`Undo the last applied change (${undoState.label}) — the script is shown first`}>
            <ActionIcon size="sm" variant="subtle" color="orange" aria-label="Undo last change"
              onClick={() => setUndoOpen(true)}><IconArrowBackUp size={14} /></ActionIcon>
          </Tooltip>
        ) : null}
        <Tooltip label="Revert all pending changes">
          <ActionIcon size="sm" variant="subtle" aria-label="Revert" disabled={!changeSet.isDirty}
            onClick={changeSet.revertAll}><IconRestore size={14} /></ActionIcon>
        </Tooltip>
        {/* Following the table: the newest rows first, re-fetched, and whatever arrived since the
            last look tinted. Watch mode does this for a query; this is the table's version. */}
        {followable.length > 0 && (
          <Tooltip label={followColumn
            ? `Following ${followColumn}, every ${followSeconds} s`
            : "Follow this table: newest first, new rows highlighted"}>
            <Group gap={2} wrap="nowrap">
              <Select size="xs" w={132} clearable placeholder="follow off"
                aria-label="Follow column"
                data={followable.map(name => ({ value: name, label: `follow ${name}` }))}
                value={followColumn}
                onChange={(value: string | null) => setFollowColumn(value)} />
              {followColumn && (
                <Select size="xs" w={86} aria-label="Follow interval"
                  data={["2", "5", "10", "30"].map(value => ({ value, label: `${value} s` }))}
                  value={String(followSeconds)}
                  onChange={(value: string | null) => setFollowSeconds(Number(value) || 5)} />
              )}
            </Group>
          </Tooltip>
        )}
        <Tooltip label="Insert row">
          <ActionIcon size="sm" variant="subtle" aria-label="Insert row" disabled={!page.editable}
            onClick={() => changeSet.insertRow({})}><IconPlus size={14} /></ActionIcon>
        </Tooltip>
        <Tooltip label="Paste rows from the clipboard as inserts">
          <ActionIcon size="sm" variant="subtle" aria-label="Paste rows" disabled={!page.editable}
            onClick={pasteRows}><IconClipboardPlus size={14} /></ActionIcon>
        </Tooltip>
        <Tooltip label="Duplicate selected row">
          <ActionIcon size="sm" variant="subtle" aria-label="Duplicate row"
            disabled={!page.editable || selected.length === 0}
            onClick={() => {
              const row = page.rows[selected[0].row];
              if (row) changeSet.duplicateRow(selected[0].row,
                Object.fromEntries(columns.map((c, i) => [c, row[i]])));
            }}><IconCopyPlus size={14} /></ActionIcon>
        </Tooltip>
        <Tooltip label="Delete selected row">
          <ActionIcon size="sm" variant="subtle" color="red" aria-label="Delete row"
            disabled={!page.editable || selected.length === 0}
            onClick={() => changeSet.deleteRow(selected[0].row)}><IconTrash size={14} /></ActionIcon>
        </Tooltip>
        <Tooltip label="Fill this table with generated rows">
          <ActionIcon size="sm" variant="subtle" aria-label="Generate rows" disabled={!page.editable}
            onClick={() => setGenerateOpen(true)}><IconSparkles size={14} /></ActionIcon>
        </Tooltip>
        <Tooltip label="Bulk update the selection">
          <ActionIcon size="sm" variant="subtle" aria-label="Bulk update"
            disabled={!page.editable || selectedCells.length === 0}
            onClick={() => setBulk(selectedCells)}><IconWand size={14} /></ActionIcon>
        </Tooltip>

        <Tooltip label="Reload this page">
          <ActionIcon size="sm" variant="subtle" aria-label="Reload data"
            onClick={() => setNonce(n => n + 1)}><IconRefresh size={14} /></ActionIcon>
        </Tooltip>

        {/* The same copy and export actions the query result has: reading a table through the
            explorer is no reason to lose them. Copy takes the page on screen; export goes to the
            server and streams the whole table. */}
        <Menu withinPortal>
          <Menu.Target>
            <Button size="compact-xs" variant="default" leftSection={<IconCopy size={13} />}>
              Copy
            </Button>
          </Menu.Target>
          <Menu.Dropdown>
            {/* Without the row address: it is how the server writes this table, not a column
                anybody asked for. */}
            <Menu.Item onClick={() => copy(copyAsCsv(shown.rows, shown.columns))}>
              This page as CSV
            </Menu.Item>
            <Menu.Item onClick={() => copy(copyAsJson(shown.rows, shown.columns))}>
              This page as JSON
            </Menu.Item>
            <Menu.Item onClick={() => copy(copyAsMarkdown(shown.rows, shown.columns))}>
              This page as Markdown
            </Menu.Item>
            <Menu.Divider />
            <Menu.Item disabled={selectedCells.length === 0}
              onClick={() => copy(copyAsSqlInList(selectedCells.map(c => c.value)))}>
              Selection as SQL IN-list
            </Menu.Item>
          </Menu.Dropdown>
        </Menu>

        {onExport ? (
          <Tooltip label="Export the whole table">
            <Button size="compact-xs" variant="default" leftSection={<IconDownload size={13} />}
              onClick={onExport}>
              Export
            </Button>
          </Tooltip>
        ) : null}

        {/* Where following a foreign key — outgoing or incoming — opens its target. One setting
            for the whole studio, owned by the shell; this is just the switch. */}
        {fkNavMode && onFkNavModeChange ? (
          <Menu withinPortal position="bottom-end">
            <Menu.Target>
              <Tooltip label={`Foreign keys open ${FK_NAV_LABELS[fkNavMode]}`}>
                <ActionIcon size="sm" variant="subtle" aria-label="Foreign-key navigation">
                  <IconRoute size={14} />
                </ActionIcon>
              </Tooltip>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Label>Follow foreign keys…</Menu.Label>
              {(Object.keys(FK_NAV_LABELS) as FkNavMode[]).map(mode => (
                <Menu.Item key={mode}
                  leftSection={mode === "query" ? <IconSquareArrowRight size={13} />
                    : mode === "tab" ? <IconPlus size={13} /> : <IconLayoutColumns size={13} />}
                  rightSection={mode === fkNavMode ? "✓" : undefined}
                  onClick={() => onFkNavModeChange(mode)}>
                  {FK_NAV_LABELS[mode]}
                </Menu.Item>
              ))}
            </Menu.Dropdown>
          </Menu>
        ) : null}

        {/* The server replaced these values. Saying so — and offering the way to the real ones —
            beats leaving somebody to wonder why a column reads as dots. */}
        {page.columns.some(c => c.masked) || reveal ? (
          <Tooltip label={reveal
            ? "Mask sensitive columns again"
            : `Reveal ${page.columns.filter(c => c.masked).length} masked column(s) — the values are fetched again`}>
            <Button size="compact-xs" variant={reveal ? "light" : "subtle"}
              color={reveal ? "orange" : "gray"}
              leftSection={reveal ? <IconEye size={13} /> : <IconLock size={13} />}
              onClick={() => setReveal(r => !r)}>
              {reveal ? "Revealed" : "Masked"}
            </Button>
          </Tooltip>
        ) : null}

        {/* Hidden columns are invisible by definition; this is the way back to them, the same
            control the query result grid has. */}
        {hidden.size > 0 ? (
          <Menu withinPortal closeOnItemClick={false} position="bottom-end">
            <Menu.Target>
              <Button size="compact-xs" variant="subtle" color="gray"
                aria-label={`${hidden.size} hidden columns`}
                leftSection={<IconEyeOff size={13} />}>{hidden.size}</Button>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Label>Hidden columns</Menu.Label>
              {[...hidden].map(name => (
                <Menu.Item key={name} leftSection={<IconEye size={13} />}
                  onClick={() => setHidden(h => {
                    const next = new Set(h);
                    next.delete(name);
                    return next;
                  })}>{name}</Menu.Item>
              ))}
              <Menu.Divider />
              <Menu.Item onClick={() => setHidden(new Set())}>Show all columns</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        ) : null}

        <Text size="xs" c="dimmed" ml="auto">
          {shown.rows.length} rows
          {page.totalEstimate ? ` of ~${page.totalEstimate}` : ""}
          {filters.length > 0 ? ` · filtered on ${[...new Set(filters.map(f => f.column))].join(", ")}` : ""}
          {sorts.length > 0 ? ` · sorted by ${sorts.map(s => s.column).join(", ")}` : ""}
          {page.grouped ? " · grouped" : ""}
          {changeSet.isDirty && ` · ${changeSet.changes.length} pending`}
          {page.note ? ` · ${page.note}` : ""}
          {/* Which clock the timestamps are on, whenever it is not the reader's own. */}
          {describeZone(preferences().timeZone) ? ` · ${describeZone(preferences().timeZone)}` : ""}
        </Text>
      </Group>

      <QueryBar
        columns={addressable}
        foreignKeys={foreignKeys}
        filters={filters} sorts={sorts} joins={joins} groupBy={groupBy} aggregates={aggregates}
        onChange={next => {
          if (next.filters) setFilters(next.filters);
          if (next.sorts) setSorts(next.sorts);
          if (next.joins) setJoins(next.joins);
          if (next.groupBy) setGroupBy(next.groupBy);
          if (next.aggregates) setAggregates(next.aggregates);
          setPageIndex(1);
        }} />

      {pasteNote && (
        <Alert color="blue" p={6} mx={4} mb={4} withCloseButton onClose={() => setPasteNote(null)}>
          <Text size="xs">{pasteNote}</Text>
        </Alert>
      )}

      {/* Why this table is not editable — or, when it is editable by physical address only,
          what that costs: the address moves when somebody else writes the row. */}
      {page.reason && (
        <Alert color={page.editable ? "yellow" : "gray"} p={6} mx={4} mb={4}>
          <Text size="xs">{page.reason}</Text>
        </Alert>
      )}

      <div style={{ flex: 1, overflow: "auto", minHeight: 0 }}>
        <table style={{ borderCollapse: "collapse", width: "max-content", minWidth: "100%" }}>
          <thead style={{ position: "sticky", top: 0, zIndex: 1, background: "var(--mantine-color-default)" }}>
            <tr>
              {expander && <th style={{ width: 22 }} />}
              {historySupported && <th style={{ width: 24 }} />}
              {visibleColumns.map(c => (
                <th key={c.name} title={zoneNote(c.dataType) ?? c.dataType} style={{
                  textAlign: "left", padding: "2px 8px", whiteSpace: "nowrap",
                  borderBottom: "1px solid var(--mantine-color-default-border)",
                }}>
                  <Menu withinPortal closeOnItemClick={false}>
                    <Menu.Target>
                      <Group gap={3} wrap="nowrap" style={{ cursor: "pointer" }}>
                        <Text size="xs" fw={600}>{c.name}</Text>
                        {page.keyColumns.includes(c.name) && <Badge size="xs" variant="light">key</Badge>}
                        {isLookup(c.name) && (
                          <Badge size="xs" variant="light" color="grape">borrowed</Badge>
                        )}
                        {c.masked && <IconLock size={11} title="masked by the server" />}
                        {fkForColumn(c.name) && <IconArrowRight size={11} />}
                        {referencedBy(c.name) && (
                          <IconCornerDownLeft size={11} title="referenced by other tables" />
                        )}
                        {sortOf(c.name) && (sortOf(c.name)!.desc
                          ? <IconSortDescending size={12} /> : <IconSortAscending size={12} />)}
                        {filters.some(f => f.column.toLowerCase() === c.name.toLowerCase())
                          && <IconFilter size={12} />}
                      </Group>
                    </Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Item onClick={() => setSort(c.name, false, false)}>
                        Sort ascending
                      </Menu.Item>
                      <Menu.Item onClick={() => setSort(c.name, true, false)}>
                        Sort descending
                      </Menu.Item>
                      <Menu.Item disabled={sorts.length === 0 && !sortOf(c.name)}
                        onClick={() => setSort(c.name, false, true)}>
                        Add to sort
                      </Menu.Item>
                      <Menu.Item disabled={sorts.length === 0} onClick={() => { setSorts([]); setPageIndex(1); }}>
                        Clear sort
                      </Menu.Item>
                      {!page.grouped && <>
                        <Menu.Divider />
                        {/* The quick path: the filter language on this column (`ada`, `=a,=b`,
                            `>10 <20`). Anything joined — other operators over several columns —
                            lives in the query bar above the grid. The input lives outside a
                            Menu.Item — inside one it is a button's child and never takes the
                            focus — and it debounces, because this filter is a round trip. */}
                        <MenuFilterInput placeholder={`Filter ${c.name}`} debounceMs={350}
                          value={boxFilterOf(c.name)?.value ?? ""}
                          onChange={value => setBoxFilter(c.name, value)} />
                        <Menu.Item disabled={filters.length === 0}
                          onClick={() => { setFilters([]); setPageIndex(1); }}>
                          Clear filters
                        </Menu.Item>
                      </>}
                      {/* A JSON column is one cell of text in the grid. This says what is inside
                          it — which paths, how often, with which types — and offers the SELECT that
                          turns those paths into columns. */}
                      {jsonish(c.dataType, page.rows.map(row => row[page.columns.indexOf(c)])) && <>
                        <Menu.Divider />
                        <Menu.Item leftSection={<IconBraces size={13} />}
                          onClick={() => setJsonColumn(c.name)}>
                          What is in this JSON
                        </Menu.Item>
                      </>}

                      {/* What is actually in this column, as checkboxes. Ticking values writes them
                          into the box above as `=a,=b` — a way of typing, not a second filter. Only
                          the table's own columns: the endpoint counts columns of this table. */}
                      {!page.grouped && !isLookup(c.name) && !c.name.includes(".") && <>
                        <Menu.Divider />
                        <DistinctValues connectionId={connectionId} objectRef={objectRef}
                          column={c.name}
                          onPick={value => setBoxFilter(c.name, value)} />
                      </>}

                      {/* A column from the other side of the key, shown here rather than reached by
                          following it. */}
                      {fkForColumn(c.name) && (() => {
                        const fk = fkForColumn(c.name)!;
                        return (
                          <>
                            <Menu.Divider />
                            <LookupPicker connectionId={connectionId} targetRef={foreignKeyRef(fk)}
                              targetLabel={fk.referencedTable}
                              taken={lookups
                                .filter(entry => entry.startsWith(`${c.name}.`))
                                .map(entry => entry.slice(c.name.length + 1))}
                              onPick={column => setLookups(current =>
                                current.includes(`${c.name}.${column}`)
                                  ? current
                                  : [...current, `${c.name}.${column}`])} />
                          </>
                        );
                      })()}

                      {isLookup(c.name) && (
                        <Menu.Item leftSection={<IconEyeOff size={13} />}
                          onClick={() => setLookups(current =>
                            current.filter(entry => entry !== c.name))}>
                          Remove this borrowed column
                        </Menu.Item>
                      )}
                      <Menu.Divider />
                      <Menu.Item leftSection={<IconEyeOff size={13} />}
                        onClick={() => setHidden(h => new Set(h).add(c.name))}>
                        Hide column
                      </Menu.Item>
                      {/* The word list guesses; this is how somebody who knows the schema corrects
                          it once, for everybody who opens this connection. */}
                      <Menu.Item leftSection={<IconLock size={13} />}
                        onClick={() => setMasking(c.name, !c.masked)}>
                        {c.masked ? "Never mask this column" : "Always mask this column"}
                      </Menu.Item>
                      <Menu.Item disabled={hidden.size === 0} onClick={() => setHidden(new Set())}>
                        Show all columns
                      </Menu.Item>
                    </Menu.Dropdown>
                  </Menu>
                  <Text size="10px" c="dimmed">{c.dataType}</Text>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {page.rows.map((row, rowIndex) => {
              const expandedKeys = expanded[rowIndex] ?? [];
              const cells = visibleColumns.map((c, colIndex) => {
                const fk = fkForColumn(c.name);
                const isSelected = selected.some(s => s.row === rowIndex && s.col === colIndex);
                return (
                  <td key={c.name}
                    onMouseDown={e => setSelected(prev => e.ctrlKey || e.metaKey
                      ? [...prev, { row: rowIndex, col: colIndex }]
                      : [{ row: rowIndex, col: colIndex }])}
                    style={{
                      padding: "1px 8px", whiteSpace: "nowrap",
                      borderBottom: "1px solid var(--mantine-color-default-border)",
                      background: isSelected ? "var(--mantine-primary-color-light)" : undefined,
                    }}>
                    <Group gap={2} wrap="nowrap" style={{ width: "100%" }}>
                      <EditableCell
                        value={changeSet.editedValue(rowIndex, c.name)}
                        state={changeSet.cellState(rowIndex, c.name)}
                        editable={page.editable && !isLookup(c.name)}
                        boolean={isBoolean(c.dataType)}
                        binary={isBinary(c.dataType)}
                        lookup={fk ? text => lookupValues(
                          connectionId, foreignKeyRef(fk), fk.referencedColumns[0], text) : undefined}
                        onCommit={value => changeSet.edit(rowIndex, c.name, value)} />
                      {fk && onFollowForeignKey && (
                        <Tooltip label={`Go to ${fk.referencedTable}${fkNavMode ? `, ${FK_NAV_LABELS[fkNavMode]}` : ""}`}>
                          <ActionIcon size="xs" variant="subtle" aria-label="Follow foreign key"
                            onClick={() => onFollowForeignKey(fk, row[colIndex])}>
                            <IconArrowRight size={11} />
                          </ActionIcon>
                        </Tooltip>
                      )}
                    </Group>
                  </td>
                );
              });

              return [
                <tr key={rowIndex}
                  // A row that has just arrived is tinted until the next fetch: that is the whole
                  // point of following a table.
                  style={fresh.has(rowIndex)
                    ? { background: "var(--mantine-primary-color-light)" }
                    : undefined}>
                  {expander && (
                    <td style={{ borderBottom: "1px solid var(--mantine-color-default-border)", padding: 0 }}>
                      {/* One incoming key toggles directly; several ask which — each one, or all
                          of them at once. */}
                      {incoming.length === 1 ? (
                        <Tooltip label={`Show ${incoming[0].table} rows referencing this row`}>
                          <ActionIcon size="xs" variant="subtle" aria-label="Expand referencing rows"
                            onClick={() => toggleExpanded(rowIndex, incoming[0].name)}>
                            {expandedKeys.length > 0
                              ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />}
                          </ActionIcon>
                        </Tooltip>
                      ) : (
                        <Menu withinPortal closeOnItemClick={false} position="bottom-start">
                          <Menu.Target>
                            <ActionIcon size="xs" variant="subtle" aria-label="Expand referencing rows">
                              {expandedKeys.length > 0
                                ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />}
                            </ActionIcon>
                          </Menu.Target>
                          <Menu.Dropdown>
                            <Menu.Label>Referenced by</Menu.Label>
                            {incoming.map(key => (
                              <Menu.Item key={key.name}
                                rightSection={expandedKeys.includes(key.name) ? "✓" : undefined}
                                onClick={() => toggleExpanded(rowIndex, key.name)}>
                                {key.table} · {key.columns.join(", ")}
                              </Menu.Item>
                            ))}
                            <Menu.Divider />
                            <Menu.Item onClick={() => setExpanded(current => ({
                              ...current, [rowIndex]: incoming.map(k => k.name),
                            }))}>
                              Expand all
                            </Menu.Item>
                            <Menu.Item disabled={expandedKeys.length === 0}
                              onClick={() => setExpanded(current => ({ ...current, [rowIndex]: [] }))}>
                              Collapse all
                            </Menu.Item>
                          </Menu.Dropdown>
                        </Menu>
                      )}
                    </td>
                  )}
                  {historySupported && (
                    <td style={{
                      padding: "1px 4px",
                      borderBottom: "1px solid var(--mantine-color-default-border)",
                    }}>
                      <Tooltip label="What this row looked like before">
                        <ActionIcon size="xs" variant="subtle"
                          aria-label={`History of row ${rowIndex + 1}`}
                          onClick={() => setHistoryOf(Object.fromEntries(page.keyColumns.map(column => [
                            column,
                            String(row[page.columns.findIndex(one => one.name === column)] ?? ""),
                          ])))}>
                          <IconHistory size={12} />
                        </ActionIcon>
                      </Tooltip>
                    </td>
                  )}
                  {cells}
                </tr>,
                ...(expandedKeys.length > 0 ? [(
                  <tr key={`${rowIndex}-refs`}>
                    <td colSpan={visibleColumns.length + 1 + (historySupported ? 1 : 0)}
                      style={{ borderBottom: "1px solid var(--mantine-color-default-border)" }}>
                      {incoming.filter(key => expandedKeys.includes(key.name)).map(key => (
                        <ReferencingRows key={key.name}
                          connectionId={connectionId} refKey={key}
                          values={referencedValues(row, key)}
                          onOpen={onOpenReferencing
                            ? () => onOpenReferencing(key, referencedValues(row, key))
                            : undefined} />
                      ))}
                    </td>
                  </tr>
                )] : []),
              ];
            })}

            {insertedRows.map(index => (
              <tr key={index} style={{ background: "color-mix(in srgb, var(--mantine-color-green-6) 8%, transparent)" }}>
                {expander && <td />}
                {historySupported && <td />}
                {visibleColumns.map(c => (
                  <td key={c.name} style={{ padding: "1px 8px", whiteSpace: "nowrap" }}>
                    <EditableCell
                      value={changeSet.editedValue(index, c.name)}
                      state="inserted"
                      editable={!isLookup(c.name)}
                      boolean={isBoolean(c.dataType)}
                      onCommit={value => changeSet.edit(index, c.name, value)} />
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <Pager
        page={pageIndex}
        pageSize={pageSize}
        rowsOnPage={shown.rows.length}
        total={exactTotal ?? page.totalEstimate}
        totalIsEstimate={exactTotal === null && (page.totalIsEstimate ?? false)}
        filtered={exactTotal === null && (page.filtered ?? false)}
        onPage={setPageIndex}
        onPageSize={size => {
          // The first row on screen stays on screen: changing the size on page 14 of 200-row pages
          // and landing on page 14 of 25-row pages would be a different part of the table.
          const firstRow = (pageIndex - 1) * pageSize;
          setPageIndex(Math.floor(firstRow / size) + 1);
          void savePreferences({ pageSize: size });
        }}
        counting={counting}
        // The count endpoint answers one column box on one table; a grouped or many-filtered view
        // has no honest question to ask it.
        onCount={countable ? async () => {
          setCounting(true);
          try {
            const box = filters.find(f => f.op === "expr");
            const answer = await countRows(connectionId, objectRef,
              { filterColumn: box?.column, filter: box?.value });
            setExactTotal(answer.total);
          } catch { /* the number stays what it was; the grid is not the place for this error */ }
          finally { setCounting(false); }
        } : undefined} />

      {jsonColumn && (
        <JsonColumnDialog connectionId={connectionId} objectRef={objectRef} column={jsonColumn}
          onClose={() => setJsonColumn(null)} onFlatten={onOpenInEditor} />
      )}

      <RowHistoryModal connectionId={connectionId} objectRef={objectRef} keyValues={historyOf}
        label={tableName} onClose={() => setHistoryOf(null)} />

      <GenerateDialog connectionId={connectionId} objectRef={objectRef} tableName={tableName}
        opened={generateOpen} onClose={() => setGenerateOpen(false)}
        onApplied={() => setNonce(n => n + 1)} />

      {undoOpen && (
        <ChangePreviewModal connectionId={connectionId} objectRef={objectRef} tableName={tableName}
          changes={null} undo onClose={() => setUndoOpen(false)}
          onApplied={() => { changeSet.revertAll(); setNonce(n => n + 1); }} />
      )}

      <ChangePreviewModal
        connectionId={connectionId} objectRef={objectRef} tableName={tableName}
        changes={pending}
        onClose={() => setPending(null)}
        onApplied={() => { changeSet.revertAll(); setNonce(n => n + 1); }} />

      <BulkUpdateModal values={bulk} onClose={() => setBulk(null)}
        onApply={transformed => transformed.forEach(t => changeSet.edit(t.rowIndex, t.column, t.value))} />
    </div>
  );
}

/// Rendered when a cell has no editor: keeps the read-only path visually identical.
export const ReadOnlyCell = CellValue;
