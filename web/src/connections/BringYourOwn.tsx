import { Alert, Button, List, Paper, Stack, Text, Title } from "@mantine/core";
import { IconDatabasePlus, IconInfoCircle } from "@tabler/icons-react";
import type { StudioAccess } from "../api";

/// The empty state of a studio somebody brings their own data to.
///
/// On a viewer instance this *is* the product: there are no connections, no accounts and no
/// instructions anywhere else, so the first screen has to say what the three ways in are — and only
/// the ones this deployment left open.
export function BringYourOwn({ access, onAdd }: {
  access: StudioAccess;
  onAdd: () => void;
}) {
  const ways = [
    access.mayAdd && { key: "string", text: "a connection string, pasted into the form" },
    access.mayUpload && { key: "upload", text: "a SQLite or DuckDB file from your own machine" },
    access.mayBrowse && { key: "browse", text: "a database file already on this server" },
  ].filter((way): way is { key: string; text: string } => way !== false);

  return (
    <Paper withBorder p="lg" radius="md" maw={560}>
      <Stack gap="sm">
        <Title order={4}>Bring a database</Title>

        {ways.length === 0 ? (
          <Text size="sm" c="dimmed">
            This studio has no connections, and it does not take them from its users. Whoever runs it
            configures them.
          </Text>
        ) : (
          <>
            <Text size="sm" c="dimmed">Nothing is open yet. This studio takes:</Text>

            <List size="sm" spacing={4}>
              {ways.map(way => <List.Item key={way.key}>{way.text}</List.Item>)}
            </List>

            <Button leftSection={<IconDatabasePlus size={16} />} onClick={onAdd} w="fit-content">
              Bring a database
            </Button>
          </>
        )}

        {/* Worth saying once, where somebody is about to hand over a connection string: nobody
            else on this studio will see it, and it does not outlive the session. */}
        {access.scope === "Session" && (
          <Alert variant="light" icon={<IconInfoCircle size={16} />} p="xs">
            <Text size="xs">
              What you open here belongs to this browser. Nobody else on this studio sees it, nothing
              about it is written down, and it goes away when you leave.
            </Text>
          </Alert>
        )}
      </Stack>
    </Paper>
  );
}
