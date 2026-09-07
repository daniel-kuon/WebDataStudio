import { useState } from "react";
import {
  Alert, Button, FileInput, Group, Loader, Modal, ScrollArea, Stack, Text, UnstyledButton,
} from "@mantine/core";
import { IconArrowUp, IconDatabase, IconFile, IconFolder, IconServer } from "@tabler/icons-react";
import { browseFiles, uploadConnectionFile, type BrowseDto, type Connection } from "../api";

/// Two ways to name a database file: the one on your machine, and the one the server can see.
///
/// The form's "File path" field has always taken a path on the server, typed as text — right for a
/// mounted share, useless for the file in your downloads folder, because the container cannot see
/// it. Uploading answers that; browsing answers the other half, where typing a path from memory was
/// the only way.
export interface FilePickerProps {
  /// An upload is a connection the moment the server has the file, so there is nothing left to fill
  /// in — the dialog is done.
  onUploaded: (created: Connection) => void;
  /// A picked path is not a connection yet: it fills the form in, and the engine comes with it
  /// because the server already decided which one reads that name.
  onPicked: (path: string, engine: string | null) => void;
}

export function FilePicker({ onUploaded, onPicked }: FilePickerProps) {
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [browsing, setBrowsing] = useState(false);
  const [page, setPage] = useState<BrowseDto | null>(null);

  const upload = async (file: File | null) => {
    if (!file) return;

    setBusy(true);
    setProblem(null);

    try {
      onUploaded(await uploadConnectionFile(file));
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const go = async (path?: string) => {
    setProblem(null);

    try {
      setPage(await browseFiles(path));
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    }
  };

  const open = async () => {
    setBrowsing(true);
    setPage(null);
    await go();
  };

  return (
    <Stack gap="xs">
      <Group align="end" gap="xs" wrap="nowrap">
        <FileInput label="Database file" placeholder="SQLite, DuckDB, Parquet, CSV, NDJSON"
          aria-label="Database file" disabled={busy} style={{ flex: 1 }}
          leftSection={busy ? <Loader size={14} /> : <IconDatabase size={16} />}
          onChange={file => void upload(file)} />

        <Button variant="default" leftSection={<IconServer size={16} />} onClick={() => void open()}>
          Browse the server
        </Button>
      </Group>

      {problem && <Alert color="red" variant="light">{problem}</Alert>}

      <Modal opened={browsing} onClose={() => setBrowsing(false)} title="Files the server can see"
        size="lg">
        {page === null ? <Loader size="sm" /> : (
          <Stack gap={4}>
            {page.path === null
              ? page.roots.map(root => (
                  <UnstyledButton key={root} onClick={() => void go(root)}>
                    <Group gap={6} wrap="nowrap">
                      <IconFolder size={16} />
                      <Text size="sm">{root}</Text>
                    </Group>
                  </UnstyledButton>
                ))
              : (
                <>
                  <Text size="xs" c="dimmed">{page.path}</Text>

                  {page.parent && (
                    <UnstyledButton onClick={() => void go(page.parent!)}>
                      <Group gap={6} wrap="nowrap">
                        <IconArrowUp size={16} />
                        <Text size="sm">up</Text>
                      </Group>
                    </UnstyledButton>
                  )}

                  <ScrollArea.Autosize mah={360}>
                    <Stack gap={2}>
                      {page.directories.map(directory => (
                        <UnstyledButton key={directory.path} onClick={() => void go(directory.path)}>
                          <Group gap={6} wrap="nowrap">
                            <IconFolder size={16} />
                            <Text size="sm">{directory.name}</Text>
                          </Group>
                        </UnstyledButton>
                      ))}

                      {/* A file nothing opens is shown rather than hidden — and not pickable, so the
                          answer to "why can I not choose it" is that it is greyed out. */}
                      {page.files.map(file => (
                        <UnstyledButton key={file.path} disabled={file.engine === null}
                          onClick={() => {
                            if (file.engine === null) return;
                            onPicked(file.path, file.engine);
                            setBrowsing(false);
                          }}>
                          <Group gap={6} wrap="nowrap">
                            {file.engine === null ? <IconFile size={16} /> : <IconDatabase size={16} />}
                            <Text size="sm" c={file.engine === null ? "dimmed" : undefined}>
                              {file.name}
                            </Text>
                            <Text size="xs" c="dimmed">
                              {file.engine ?? "nothing here opens this"}
                            </Text>
                          </Group>
                        </UnstyledButton>
                      ))}
                    </Stack>
                  </ScrollArea.Autosize>
                </>
              )}
          </Stack>
        )}
      </Modal>
    </Stack>
  );
}
