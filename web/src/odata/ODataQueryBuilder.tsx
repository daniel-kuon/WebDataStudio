import { useEffect, useMemo, useState } from "react";
import {
  ActionIcon, Badge, Button, Code, Group, MultiSelect, NumberInput, SegmentedControl, Select, Stack,
  Text, TextInput, Tooltip,
} from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { describeObject, listSchema } from "../api";
import {
  buildOptions, buildQuery, OPERATORS, type ODataQuery, type ODataSort, type ODataTerm,
} from "./query";

/// A property of the entity the builder is on. Navigation properties are the relations `$expand`
/// follows; everything else is a field `$select`, `$filter` and `$orderby` can name.
interface Property { name: string; type: string; navigation: boolean }

/// Anything that is not an `Edm.` type is a relation: `Collection(Shop.Order)` or `Shop.Category`.
const isNavigation = (dataType: string) => !dataType.startsWith("Edm.");

const FUNCTIONS = ["contains", "startswith", "endswith"];

export function ODataQueryBuilder({ connectionId, value, onChange, variant, onRun }: {
  connectionId: string;
  value: ODataQuery;
  /// The model, and the two forms of it the callers need: `text` is the whole request as a person
  /// would type it, `options` is only the query options, which is what the data tab sends. Both
  /// are built here because only the builder knows the properties' types, and a type decides
  /// whether a value is quoted.
  onChange: (query: ODataQuery, built: { text: string; options: string }) => void;
  /// "query": the tab owns the whole request, so the entity set, `$top` and `$skip` are the
  /// builder's too. "data": the grid owns the paging and the set is the tab it is in.
  variant: "query" | "data";
  onRun?: () => void;
}) {
  const [sets, setSets] = useState<string[]>([]);
  const [properties, setProperties] = useState<Property[]>([]);
  const [error, setError] = useState<string | null>(null);

  // The entity sets, for the picker. Only the query tab needs them: the data tab is already in one.
  useEffect(() => {
    if (variant !== "query") return;
    let cancelled = false;

    listSchema(connectionId)
      .then(nodes => { if (!cancelled) setSets(nodes.map(n => n.label)); })
      .catch(e => { if (!cancelled) setError(e.message); });

    return () => { cancelled = true; };
  }, [connectionId, variant]);

  // What this entity has. Read from the service's own $metadata, so the fields on offer are the
  // ones that exist rather than the ones that were typed correctly.
  useEffect(() => {
    if (!value.set) { setProperties([]); return; }
    let cancelled = false;

    describeObject(connectionId, `Table:${value.set}`)
      .then(detail => {
        if (cancelled) return;
        setError(null);
        setProperties(detail.columns.map(c => ({
          name: c.name, type: c.dataType, navigation: isNavigation(c.dataType),
        })));
      })
      .catch(e => { if (!cancelled) { setError(e.message); setProperties([]); } });

    return () => { cancelled = true; };
  }, [connectionId, value.set]);

  const fields = useMemo(() => properties.filter(p => !p.navigation).map(p => p.name), [properties]);
  const relations = useMemo(() => properties.filter(p => p.navigation).map(p => p.name), [properties]);
  const types = useMemo(
    () => Object.fromEntries(properties.map(p => [p.name, p.type])),
    [properties]);

  const emit = (next: ODataQuery) =>
    onChange(next, { text: buildQuery(next, types), options: buildOptions(next, types) });

  const patch = (change: Partial<ODataQuery>) => emit({ ...value, ...change });

  const setTerm = (index: number, change: Partial<ODataTerm>) =>
    patch({ terms: value.terms.map((t, i) => i === index ? { ...t, ...change } : t) });


  const setSort = (index: number, change: Partial<ODataSort>) =>
    patch({ order: value.order.map((o, i) => i === index ? { ...o, ...change } : o) });

  // An expand entry may carry nested options of its own (`Orders($top=5)`); it stays on the list
  // so choosing another relation does not throw it away.
  const expandOptions = useMemo(
    () => [...new Set([...relations, ...value.expand])],
    [relations, value.expand]);

  const selectOptions = useMemo(
    () => [...new Set([...fields, ...value.select])],
    [fields, value.select]);

  return (
    <Stack gap={6} p={6}>
      {error && <Text size="xs" c="red">{error}</Text>}

      <Group gap={6} align="flex-end" wrap="wrap">
        {variant === "query" && (
          <Select size="xs" label="Entity set" searchable data={sets} value={value.set || null}
            style={{ minWidth: 160 }} nothingFoundMessage="No such entity set"
            // A different set has different properties, so what named the old ones goes with it.
            onChange={set => emit({
              ...value, set: set ?? "", select: [], expand: [], terms: [], order: [],
            })} />
        )}

        <MultiSelect size="xs" label="Fields ($select)" searchable clearable data={selectOptions}
          value={value.select} style={{ minWidth: 200, flex: 1 }}
          placeholder={value.select.length === 0 ? "all" : undefined}
          onChange={select => patch({ select })} />

        <MultiSelect size="xs" label="Expand ($expand)" searchable clearable data={expandOptions}
          value={value.expand} style={{ minWidth: 180, flex: 1 }}
          placeholder={relations.length === 0 ? "no relations" : "none"}
          disabled={expandOptions.length === 0}
          onChange={expand => patch({ expand })} />

        {variant === "query" && (
          <>
            <NumberInput size="xs" label="Top" min={1} max={100000} style={{ width: 90 }}
              value={value.top ?? ""} onChange={top => patch({ top: Number(top) || undefined })} />
            <NumberInput size="xs" label="Skip" min={0} style={{ width: 90 }}
              value={value.skip ?? ""} onChange={skip => patch({ skip: Number(skip) || undefined })} />
          </>
        )}
      </Group>

      <Group gap={6} align="center">
        <Text size="xs" fw={600}>Filter</Text>
        {value.terms.length > 1 && !value.rawFilter && (
          <SegmentedControl size="xs" value={value.conjunction}
            data={[{ label: "and", value: "and" }, { label: "or", value: "or" }]}
            onChange={c => patch({ conjunction: c as "and" | "or" })} />
        )}
        {!value.rawFilter && (
          <Tooltip label="Add a condition">
            <ActionIcon size="sm" variant="subtle" aria-label="Add a condition"
              onClick={() => patch({
                terms: [...value.terms, { property: fields[0] ?? "", operator: "eq", value: "" }],
              })}>
              <IconPlus size={14} />
            </ActionIcon>
          </Tooltip>
        )}
      </Group>

      {/* A filter this form cannot take apart is kept exactly as it was, rather than dropped on
          the way through the builder. */}
      {value.rawFilter ? (
        <Group gap={6} align="flex-end">
          <TextInput size="xs" style={{ flex: 1 }} label="$filter, as typed" value={value.rawFilter}
            onChange={e => patch({ rawFilter: e.currentTarget.value })} />
          <Button size="compact-xs" variant="default"
            onClick={() => patch({ rawFilter: undefined, terms: [] })}>Build it instead</Button>
        </Group>
      ) : value.terms.length === 0 ? (
        <Text size="xs" c="dimmed">Everything.</Text>
      ) : value.terms.map((term, index) => (
        <Group gap={4} key={index} wrap="nowrap">
          <Select size="xs" searchable data={selectOptions} value={term.property || null}
            style={{ flex: 1, minWidth: 120 }} placeholder="property"
            onChange={property => setTerm(index, { property: property ?? "" })} />
          <Select size="xs" data={OPERATORS.map(o => ({ value: o.value, label: o.label }))}
            value={term.operator} style={{ width: 110 }} allowDeselect={false}
            onChange={operator => setTerm(index, { operator: operator ?? "eq" })} />
          <TextInput size="xs" style={{ flex: 1, minWidth: 100 }} value={term.value}
            placeholder={FUNCTIONS.includes(term.operator) ? "text" : "value"}
            onChange={e => setTerm(index, { value: e.currentTarget.value })} />
          <Tooltip label="Remove">
            <ActionIcon size="sm" variant="subtle" color="red" aria-label="Remove the condition"
              onClick={() => patch({ terms: value.terms.filter((_, i) => i !== index) })}>
              <IconTrash size={14} />
            </ActionIcon>
          </Tooltip>
        </Group>
      ))}

      <Group gap={6} align="center">
        <Text size="xs" fw={600}>Order</Text>
        <Tooltip label="Add a sort">
          <ActionIcon size="sm" variant="subtle" aria-label="Add a sort"
            onClick={() => patch({
              order: [...value.order, { property: fields[0] ?? "", descending: false }],
            })}>
            <IconPlus size={14} />
          </ActionIcon>
        </Tooltip>
      </Group>

      {value.order.length === 0
        ? <Text size="xs" c="dimmed">The service's own order.</Text>
        : value.order.map((entry, index) => (
          <Group gap={4} key={index} wrap="nowrap">
            <Select size="xs" searchable data={selectOptions} value={entry.property || null}
              style={{ flex: 1, minWidth: 120 }} placeholder="property"
              onChange={property => setSort(index, { property: property ?? "" })} />
            <SegmentedControl size="xs" value={entry.descending ? "desc" : "asc"}
              data={[{ label: "asc", value: "asc" }, { label: "desc", value: "desc" }]}
              onChange={direction => setSort(index, { descending: direction === "desc" })} />
            <Tooltip label="Remove">
              <ActionIcon size="sm" variant="subtle" color="red" aria-label="Remove the sort"
                onClick={() => patch({ order: value.order.filter((_, i) => i !== index) })}>
                <IconTrash size={14} />
              </ActionIcon>
            </Tooltip>
          </Group>
        ))}

      {value.rest.length > 0 && (
        <Group gap={4}>
          {/* Options the form has no field for travel through it untouched, and say so. */}
          {value.rest.map(([name]) => <Badge key={name} size="xs" variant="light">{name} kept</Badge>)}
        </Group>
      )}

      {variant === "query" && (
        <Group gap={6} justify="space-between" wrap="nowrap">
          <Code style={{ overflowX: "auto", whiteSpace: "nowrap", flex: 1 }}>
            {buildQuery(value, types) || "—"}
          </Code>
          {onRun && <Button size="compact-xs" onClick={onRun} disabled={!value.set}>Run</Button>}
        </Group>
      )}
    </Stack>
  );
}
