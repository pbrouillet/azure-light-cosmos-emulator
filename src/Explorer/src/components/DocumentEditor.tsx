import { useEffect, useMemo, useState } from 'react'
import Editor from '@monaco-editor/react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Body1,
  Button,
  Card,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Subtitle2,
  tokens,
} from '@fluentui/react-components'
import { DeleteRegular, SaveRegular } from '@fluentui/react-icons'
import { cosmosClient } from '../api/cosmosClient'
import { useTheme } from '../theme'
import type { CosmosDocument } from '../types/cosmos'

interface DocumentEditorProps {
  dbId: string
  collId: string
  docId: string
  partitionKey: unknown
  onDeleted?: () => void
}

const systemProperties = ['_rid', '_etag', '_ts', '_self', '_attachments'] as const

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    flexWrap: 'wrap',
  },
  subtleText: {
    color: tokens.colorNeutralForeground3,
  },
  actions: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
  },
  grid: {
    display: 'grid',
    gap: tokens.spacingHorizontalL,
    minHeight: 0,
    '@media (min-width: 1200px)': {
      gridTemplateColumns: 'minmax(0, 1fr) 20rem',
    },
  },
  card: {
    display: 'flex',
    minHeight: 0,
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    paddingBottom: tokens.spacingVerticalL,
    paddingLeft: tokens.spacingHorizontalL,
    paddingRight: tokens.spacingHorizontalL,
    paddingTop: tokens.spacingVerticalL,
  },
  editorFrame: {
    flex: 1,
    minHeight: '32rem',
    overflow: 'hidden',
    borderRadius: tokens.borderRadiusMedium,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  metadataList: {
    display: 'grid',
    gap: tokens.spacingVerticalS,
  },
  spinnerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
})

export function DocumentEditor({
  dbId,
  collId,
  docId,
  partitionKey,
  onDeleted,
}: DocumentEditorProps) {
  const styles = useStyles()
  const { isDark } = useTheme()
  const queryClient = useQueryClient()
  const [editorValue, setEditorValue] = useState('{}')
  const [saveMessage, setSaveMessage] = useState<string | null>(null)
  const [isDeleteOpen, setIsDeleteOpen] = useState(false)

  const documentQuery = useQuery({
    queryKey: ['document', dbId, collId, docId, JSON.stringify(partitionKey)],
    queryFn: () => cosmosClient.getDocument(dbId, collId, docId, partitionKey),
  })

  const document = documentQuery.data
  const initialEditorValue = useMemo(
    () => (document ? JSON.stringify(toEditableDocument(document), null, 2) : '{}'),
    [document],
  )

  useEffect(() => {
    setEditorValue(initialEditorValue)
  }, [initialEditorValue])

  const saveMutation = useMutation({
    mutationFn: async () => {
      if (!document) {
        throw new Error('Document is not loaded yet.')
      }

      const parsed = parseJsonObject(editorValue)
      parsed.id = document.id

      return cosmosClient.replaceDocument(dbId, collId, docId, {
        ...parsed,
        _etag: document._etag,
      })
    },
    onSuccess: async () => {
      setSaveMessage('Document saved successfully.')
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['document', dbId, collId, docId] }),
        queryClient.invalidateQueries({ queryKey: ['documents', dbId, collId] }),
      ])
    },
  })

  const deleteMutation = useMutation({
    mutationFn: () => cosmosClient.deleteDocument(dbId, collId, docId, partitionKey),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['documents', dbId, collId] }),
        queryClient.removeQueries({ queryKey: ['document', dbId, collId, docId] }),
      ])
      setIsDeleteOpen(false)
      onDeleted?.()
    },
  })

  const metadata = useMemo(
    () =>
      systemProperties.map((property) => ({
        property,
        value: document?.[property],
      })),
    [document],
  )

  if (documentQuery.isPending) {
    return (
      <div className={styles.spinnerRow}>
        <Spinner />
        <Body1>Loading document…</Body1>
      </div>
    )
  }

  if (documentQuery.isError) {
    return (
      <MessageBar intent="error" layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>Unable to load document</MessageBarTitle>
          {toErrorMessage(documentQuery.error)}
        </MessageBarBody>
      </MessageBar>
    )
  }

  if (!document) {
    return <Body1>Select a different item.</Body1>
  }

  return (
    <section className={styles.root}>
      <div className={styles.header}>
        <div>
          <Subtitle2>{document.id}</Subtitle2>
          <Body1 className={styles.subtleText}>
            Edit the JSON body below. System properties remain read-only in the metadata panel.
          </Body1>
        </div>

        <div className={styles.actions}>
          <Button
            appearance="primary"
            disabled={saveMutation.isPending || editorValue === initialEditorValue}
            icon={<SaveRegular />}
            onClick={() => {
              setSaveMessage(null)
              saveMutation.mutate()
            }}
          >
            {saveMutation.isPending ? 'Saving…' : 'Save'}
          </Button>

          <Dialog modalType="non-modal" open={isDeleteOpen} onOpenChange={(_, data) => setIsDeleteOpen(data.open)}>
            <DialogTrigger>
              <Button appearance="secondary" icon={<DeleteRegular />}>
                Delete
              </Button>
            </DialogTrigger>
            <DialogSurface backdrop={{ onClick: () => setIsDeleteOpen(false) }}>
              <DialogBody>
                <DialogTitle>Delete document</DialogTitle>
                <DialogContent>
                  <Body1>Delete document “{document.id}”?</Body1>
                  {deleteMutation.isError && (
                    <MessageBar intent="error" layout="multiline">
                      <MessageBarBody>
                        <MessageBarTitle>Delete failed</MessageBarTitle>
                        {toErrorMessage(deleteMutation.error)}
                      </MessageBarBody>
                    </MessageBar>
                  )}
                </DialogContent>
                <DialogActions>
                  <DialogTrigger>
                    <Button appearance="secondary">Cancel</Button>
                  </DialogTrigger>
                  <Button appearance="primary" onClick={() => deleteMutation.mutate()}>
                    {deleteMutation.isPending ? 'Deleting…' : 'Delete'}
                  </Button>
                </DialogActions>
              </DialogBody>
            </DialogSurface>
          </Dialog>
        </div>
      </div>

      {saveMessage && (
        <MessageBar intent="success">
          <MessageBarBody>
            <MessageBarTitle>Saved</MessageBarTitle>
            {saveMessage}
          </MessageBarBody>
        </MessageBar>
      )}

      {saveMutation.isError && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Save failed</MessageBarTitle>
            {toErrorMessage(saveMutation.error)}
          </MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.grid}>
        <Card className={styles.card}>
          <Subtitle2>Document body</Subtitle2>
          <div className={styles.editorFrame}>
            <Editor
              defaultLanguage="json"
              height="100%"
              onChange={(value) => setEditorValue(value ?? '{}')}
              options={{
                automaticLayout: true,
                fontSize: 14,
                formatOnPaste: true,
                formatOnType: true,
                minimap: { enabled: false },
                scrollBeyondLastLine: false,
                wordWrap: 'on',
              }}
              theme={isDark ? 'vs-dark' : 'vs'}
              value={editorValue}
            />
          </div>
        </Card>

        <Card className={styles.card}>
          <Subtitle2>Metadata</Subtitle2>
          <div className={styles.metadataList}>
            {metadata.map((entry) => (
              <Field key={entry.property} label={entry.property}>
                <Input readOnly value={formatMetadataValue(entry.value)} />
              </Field>
            ))}
            <Field label="Database">
              <Input readOnly value={dbId} />
            </Field>
            <Field label="Container">
              <Input readOnly value={collId} />
            </Field>
            <Field label="Partition key">
              <Input readOnly value={JSON.stringify(partitionKey)} />
            </Field>
          </div>
        </Card>
      </div>
    </section>
  )
}

function toEditableDocument(document: CosmosDocument): CosmosDocument {
  return Object.entries(document).reduce<CosmosDocument>((next, [key, value]) => {
    if (!systemProperties.includes(key as (typeof systemProperties)[number])) {
      next[key] = value
    }

    return next
  }, { id: document.id })
}

function parseJsonObject(value: string): CosmosDocument {
  const parsed = JSON.parse(value) as unknown
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('Document JSON must be an object.')
  }

  return parsed as CosmosDocument
}

function formatMetadataValue(value: unknown): string {
  if (value === undefined) {
    return '—'
  }

  return typeof value === 'string' ? value : JSON.stringify(value)
}

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Unexpected error'
}
