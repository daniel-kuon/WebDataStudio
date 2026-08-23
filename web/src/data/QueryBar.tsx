import { useState } from "react";
import {
  ActionIcon, Badge, Button, Group, MultiSelect, Popover, SegmentedControl, Select, Text, TextInput,
  Tooltip,
} from "@mantine/core";
import {
  IconArrowsJoin, IconFilterPlus, IconLayoutList, IconPlus, IconSortAscending,
  IconSortDescending, IconX,
} from "@tabler/icons-react";
import type { BrowseAggregate, BrowseFilter, BrowseOp, BrowseSort, ForeignKeyDto } from "../api";

/// The operators the browse endpoint understands, worded the way they read in the chip.
const OPS: { value: BrowseOp; label: string; needsValue: boolean }[] = [
  { value: "contains", label: "contains", needsValue: true },
  { value: "eq", label: "=", needsValue: true },
  { value: "neq", label: "≠", needsValue: true },
  { value: "gt", label: ">", needsValue: true },
  { value: "gte", label: "≥", needsValue: true },
  { value: "lt", label: "<", needsValue: true },
  { value: "lte", label: "≤", needsValue: true },
  { value: "startswith", label: "starts with", needsValue: true },
  { value: "endswith", label: "ends with", needsValue: true },
  { value: "null", label: "is null", needsValue: false },
  { value: "notnull", label: "is not null", needsValue: false },
];

const AGGREGATES = ["count", "sum", "avg", "min", "max"] as const;

export const opLabel = (op: BrowseOp) => OPS.find(o => o.value === op)?.label ?? op;

export interface QueryBarProps {
  /// Every addressable column: the table's own plus, once a join is on, the prefixed joined ones.
  columns: string[];
  foreignKeys: ForeignKeyDto[];
  filters: BrowseFilter[];
  sorts: BrowseSort[];
  joins: string[];
  groupBy: string[];
  aggregates: BrowseAggregate[];
  onChange: (next: {
    filters?: BrowseFilter[]; sorts?: BrowseSort[]; joins?: string[];
    groupBy?: string[]; aggregates?: BrowseAggregate[];
  }) => void;
}

/// The row above the data grid where filters, ordering, joins and grouping are put together. Every
/// active piece is a chip that says what it does and comes off with one click; the grid below shows
/// the result of exactly what the chips say.
export function QueryBar({ columns, foreignKeys, filters, sorts, joins, groupBy, aggregates,
  onChange }: QueryBarProps) {
  const active = filters.length + sorts.length + joins.length + groupBy.length + aggregates.length;

  return (
    <Group gap={4} px={4} pb={4} wrap="wrap">
      <AddFilter columns={columns} onAdd={f => onChange({ filters: [...filters, f] })} />
      <AddSort columns={columns} grouped={groupBy.length > 0 || aggregates.length > 0}
        groupBy={groupBy} aggregates={aggregates}
        onAdd={s => onChange({ sorts: [...sorts, s] })} />
      {foreignKeys.length > 0 && (
        <JoinPicker foreignKeys={foreignKeys} joins={joins}
          onChange={next => onChange({ joins: next })} />
      )}
      <GroupPicker columns={columns} groupBy={groupBy} aggregates={aggregates}
        onChange={(nextGroupBy, nextAggregates) =>
          onChange({ groupBy: nextGroupBy, aggregates: nextAggregates })} />

      {filters.map((filter, index) => (
        <Chip key={`f${index}`} color="blue"
          label={`${filter.column} ${opLabel(filter.op)}${filter.value !== undefined && filter.value !== "" ? ` ${filter.value}` : ""}`}
          onRemove={() => onChange({ filters: filters.filter((_, i) => i !== index) })} />
      ))}
      {sorts.map((sort, index) => (
        <Chip key={`s${index}`} color="grape" label={`${sort.column} ${sort.desc ? "↓" : "↑"}`}
          onRemove={() => onChange({ sorts: sorts.filter((_, i) => i !== index) })} />
      ))}
      {joins.map(name => {
        const key = foreignKeys.find(fk => fk.name === name);
        return (
          <Chip key={`j${name}`} color="teal"
            label={`⋈ ${key ? key.referencedTable : name}`}
            onRemove={() => onChange({ joins: joins.filter(j => j !== name) })} />
        );
      })}
      {groupBy.map(column => (
        <Chip key={`g${column}`} color="orange" label={`group: ${column}`}
          onRemove={() => onChange({ groupBy: groupBy.filter(c => c !== column) })} />
      ))}
      {aggregates.map((aggregate, index) => (
        <Chip key={`a${index}`} color="orange"
          label={`${aggregate.function}(${aggregate.column ?? "*"})`}
          onRemove={() => onChange({ aggregates: aggregates.filter((_, i) => i !== index) })} />
      ))}

      {active > 1 && (
        <Button size="compact-xs" variant="subtle" color="gray"
          onClick={() => onChange({ filters: [], sorts: [], joins: [], groupBy: [], aggregates: [] })}>
          Clear all
        </Button>
      )}
    </Group>
  );
}

function Chip({ label, color, onRemove }: { label: string; color: string; onRemove: () => void }) {
  return (
    <Badge size="sm" variant="light" color={color} style={{ textTransform: "none" }}
      rightSection={
        <ActionIcon size={12} variant="transparent" color={color} aria-label={`Remove ${label}`}
          onClick={onRemove}>
          <IconX size={10} />
        </ActionIcon>
      }>
      {label}
    </Badge>
  );
}

function AddFilter({ columns, onAdd }: { columns: string[]; onAdd: (filter: BrowseFilter) => void }) {
  const [opened, setOpened] = useState(false);
  const [column, setColumn] = useState<string | null>(null);
  const [op, setOp] = useState<BrowseOp>("contains");
  const [value, setValue] = useState("");
  const needsValue = OPS.find(o => o.value === op)?.needsValue ?? true;

  const add = () => {
    if (!column) return;
    onAdd({ column, op, value: needsValue ? value : undefined });
    setValue("");
    setOpened(false);
  };

  return (
    <Popover opened={opened} onChange={setOpened} withinPortal position="bottom-start" trapFocus>
      <Popover.Target>
        <Button size="compact-xs" variant="default" leftSection={<IconFilterPlus size={13} />}
          onClick={() => setOpened(o => !o)}>
          Filter
        </Button>
      </Popover.Target>
      <Popover.Dropdown p="xs">
        <Group gap={4} wrap="nowrap" align="end">
          <Select size="xs" comboboxProps={{ withinPortal: false }} w={170} searchable label="Column" data={columns}
            value={column} onChange={setColumn} />
          <Select size="xs" comboboxProps={{ withinPortal: false }} w={110} label="Operator"
            data={OPS.map(o => ({ value: o.value, label: o.label }))}
            value={op} onChange={v => setOp((v as BrowseOp) ?? "contains")} />
          {needsValue && (
            <TextInput size="xs" w={140} label="Value" value={value}
              onChange={e => setValue(e.currentTarget.value)}
              onKeyDown={e => { if (e.key === "Enter") add(); }} />
          )}
          <Button size="xs" onClick={add} disabled={!column} leftSection={<IconPlus size={13} />}>
            Add
          </Button>
        </Group>
      </Popover.Dropdown>
    </Popover>
  );
}

function AddSort({ columns, grouped, groupBy, aggregates, onAdd }: {
  columns: string[]; grouped: boolean; groupBy: string[]; aggregates: BrowseAggregate[];
  onAdd: (sort: BrowseSort) => void;
}) {
  const [opened, setOpened] = useState(false);
  const [column, setColumn] = useState<string | null>(null);
  const [desc, setDesc] = useState(false);

  // A grouped view only answers its group columns and aggregates, so those are what it can be
  // sorted by; offering the rest would only buy a 400 from the server.
  const sortable = grouped
    ? [...groupBy, ...(aggregates.length > 0 ? aggregates : [{ function: "count", column: null } as BrowseAggregate])
        .map(a => `${a.function}(${a.column ?? "*"})`)]
    : columns;

  return (
    <Popover opened={opened} onChange={setOpened} withinPortal position="bottom-start" trapFocus>
      <Popover.Target>
        <Button size="compact-xs" variant="default"
          leftSection={desc ? <IconSortDescending size={13} /> : <IconSortAscending size={13} />}
          onClick={() => setOpened(o => !o)}>
          Sort
        </Button>
      </Popover.Target>
      <Popover.Dropdown p="xs">
        <Group gap={4} wrap="nowrap" align="end">
          <Select size="xs" comboboxProps={{ withinPortal: false }} w={190} searchable label="Column" data={sortable}
            value={column} onChange={setColumn} />
          <SegmentedControl size="xs" value={desc ? "desc" : "asc"}
            onChange={v => setDesc(v === "desc")}
            data={[{ value: "asc", label: "↑" }, { value: "desc", label: "↓" }]} />
          <Button size="xs" disabled={!column} leftSection={<IconPlus size={13} />}
            onClick={() => { if (column) { onAdd({ column, desc }); setOpened(false); } }}>
            Add
          </Button>
        </Group>
      </Popover.Dropdown>
    </Popover>
  );
}

function JoinPicker({ foreignKeys, joins, onChange }: {
  foreignKeys: ForeignKeyDto[]; joins: string[]; onChange: (joins: string[]) => void;
}) {
  const [opened, setOpened] = useState(false);

  return (
    <Popover opened={opened} onChange={setOpened} withinPortal position="bottom-start">
      <Popover.Target>
        <Tooltip label="Join the tables this one references, along its foreign keys">
          <Button size="compact-xs" variant="default" leftSection={<IconArrowsJoin size={13} />}
            onClick={() => setOpened(o => !o)}>
            Join{joins.length > 0 ? ` (${joins.length})` : ""}
          </Button>
        </Tooltip>
      </Popover.Target>
      <Popover.Dropdown p="xs">
        {foreignKeys.map(key => {
          const on = joins.includes(key.name);
          return (
            <Button key={key.name} size="compact-xs" fullWidth mb={2}
              variant={on ? "light" : "subtle"} color={on ? "teal" : "gray"}
              justify="start"
              onClick={() => onChange(on ? joins.filter(j => j !== key.name) : [...joins, key.name])}>
              {key.columns.join(", ")} → {key.referencedTable}
            </Button>
          );
        })}
      </Popover.Dropdown>
    </Popover>
  );
}

function GroupPicker({ columns, groupBy, aggregates, onChange }: {
  columns: string[]; groupBy: string[]; aggregates: BrowseAggregate[];
  onChange: (groupBy: string[], aggregates: BrowseAggregate[]) => void;
}) {
  const [opened, setOpened] = useState(false);
  const [fn, setFn] = useState<(typeof AGGREGATES)[number]>("count");
  const [column, setColumn] = useState<string | null>(null);

  const addAggregate = () => {
    if (fn !== "count" && !column) return;
    onChange(groupBy, [...aggregates, { function: fn, column: fn === "count" ? null : column }]);
  };

  return (
    <Popover opened={opened} onChange={setOpened} withinPortal position="bottom-start">
      <Popover.Target>
        <Tooltip label="Group the rows and aggregate per group">
          <Button size="compact-xs" variant="default" leftSection={<IconLayoutList size={13} />}
            onClick={() => setOpened(o => !o)}>
            Group{groupBy.length > 0 ? ` (${groupBy.length})` : ""}
          </Button>
        </Tooltip>
      </Popover.Target>
      <Popover.Dropdown p="xs" maw={340}>
        <MultiSelect size="xs" comboboxProps={{ withinPortal: false }} searchable label="Group by" data={columns}
          value={groupBy} onChange={next => onChange(next, aggregates)} />
        <Group gap={4} mt={6} align="end" wrap="nowrap">
          <Select size="xs" comboboxProps={{ withinPortal: false }} w={90} label="Aggregate" data={[...AGGREGATES]}
            value={fn} onChange={v => setFn((v as (typeof AGGREGATES)[number]) ?? "count")} />
          {fn !== "count" && (
            <Select size="xs" comboboxProps={{ withinPortal: false }} w={150} searchable label="of" data={columns}
              value={column} onChange={setColumn} />
          )}
          <Button size="xs" leftSection={<IconPlus size={13} />} onClick={addAggregate}
            disabled={fn !== "count" && !column}>
            Add
          </Button>
        </Group>
        {groupBy.length > 0 && aggregates.length === 0 && (
          <Text size="10px" c="dimmed" mt={4}>Without an aggregate, each group shows its row count.</Text>
        )}
      </Popover.Dropdown>
    </Popover>
  );
}
