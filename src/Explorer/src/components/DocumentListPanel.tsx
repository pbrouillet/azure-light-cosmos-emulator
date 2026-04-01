import { useCallback, useMemo, useState } from 'react'
import Editor from '@monaco-editor/react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Badge,
  Body1,
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Subtitle2,
  Text,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
  tokens,
} from '@fluentui/react-components'
import {
  AddRegular,
  ArrowSyncRegular,
  DeleteRegular,
  SaveRegular,
} from '@fluentui/react-icons'
import { cosmosClient } from '../api/cosmosClient'
import { useTheme } from '../theme'
import type { CosmosDocument } from '../types/cosmos'

interface DocumentListPanelProps {
  dbId: string
  collId: string
  partitionKeyPaths?: string[]
}

const PAGE_SIZE = 50
function validatePartitionKey(doc: Record<string, unknown>, pkPaths: string[]): string[] {
  return pkPaths
    .map((p) => p.replace(/^\//, ''))
    .filter((prop) => !(prop in doc) || doc[prop] === '' || doc[prop] === undefined)
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  toolbar: {
    borderBottom: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
  },
  splitPanel: {
    display: 'grid',
    flex: 1,
    minHeight: 0,
    gap: tokens.spacingHorizontalL,
    gridTemplateColumns: '22rem minmax(0, 1fr)',
  },
  listPane: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: 0,
    borderRight: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  listHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingBottom: tokens.spacingVerticalS,
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
    paddingTop: tokens.spacingVerticalS,
    borderBottom: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  scrollList: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
    overflowX: 'hidden',
  },
  docRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    paddingBottom: tokens.spacingVerticalXS,
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
    paddingTop: tokens.spacingVerticalXS,
    cursor: 'pointer',
    borderBottom: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    '&:hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  docRowSelected: {
    backgroundColor: tokens.colorBrandBackground2,
  },
  docInfo: {
    flex: 1,
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  editorPane: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: 0,
    gap: tokens.spacingVerticalM,
  },
  editorHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
  },
  editorActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexShrink: 0,
  },
  editorFrame: {
    flex: 1,
    minHeight: 0,
    borderRadius: tokens.borderRadiusMedium,
    overflow: 'hidden',
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  loadMoreRow: {
    display: 'flex',
    justifyContent: 'center',
    paddingBottom: tokens.spacingVerticalM,
    paddingTop: tokens.spacingVerticalM,
  },
  emptyState: {
    display: 'flex',
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    color: tokens.colorNeutralForeground3,
  },
})

export function DocumentListPanel({ dbId, collId, partitionKeyPaths: propPkPaths }: DocumentListPanelProps) {
  const styles = useStyles()
  const { isDark } = useTheme()
  const queryClient = useQueryClient()

  // Fetch container metadata to resolve partition key paths
  const containerQuery = useQuery({
    queryKey: ['container', dbId, collId],
    queryFn: () => cosmosClient.getContainer(dbId, collId),
    enabled: !propPkPaths,
  })
  const partitionKeyPaths = useMemo(
    () => propPkPaths ?? containerQuery.data?.partitionKey?.paths ?? [],
    [propPkPaths, containerQuery.data],
  )
  const hasPartitionKeyPaths = partitionKeyPaths.length > 0

  const [selectedDocId, setSelectedDocId] = useState<string | null>(null)
  const [checkedIds, setCheckedIds] = useState<Set<string>>(new Set())
  const [editorValue, setEditorValue] = useState('')
  const [saveMessage, setSaveMessage] = useState<string | null>(null)
  const [extraDocs, setExtraDocs] = useState<CosmosDocument[]>([])
  const [continuation, setContinuation] = useState<string | null | undefined>(undefined)
  const [isDeleteOpen, setIsDeleteOpen] = useState(false)
  const [pkWarning, setPkWarning] = useState<{ action: () => void; missingFields: string[] } | null>(null)

  // Fetch first page
  const documentsQuery = useQuery({
    queryKey: ['documents-panel', dbId, collId],
    queryFn: () => cosmosClient.listDocumentsPaged(dbId, collId, PAGE_SIZE),
  })

  const loadMoreMutation = useMutation({
    mutationFn: (token: string) => cosmosClient.listDocumentsPaged(dbId, collId, PAGE_SIZE, token),
    onSuccess: (data) => {
      setExtraDocs((prev) => [...prev, ...data.items])
      setContinuation(data.continuationToken)
    },
  })

  const activeContinuation = extraDocs.length > 0 ? continuation : documentsQuery.data?.continuationToken
  const hasMore = activeContinuation != null && activeContinuation !== ''

  const allDocuments = useMemo(() => {
    const initial = documentsQuery.data?.items ?? []
    return [...initial, ...extraDocs]
  }, [documentsQuery.data, extraDocs])

  // Load selected document detail
  const selectedDoc = useMemo(
    () => allDocuments.find((d) => d.id === selectedDocId) ?? null,
    [allDocuments, selectedDocId],
  )

  const initialEditorValue = useMemo(() => {
    if (!selectedDoc) return ''
    return JSON.stringify(selectedDoc, null, 2)
  }, [selectedDoc])

  // Reset editor value when selected document changes (via key-based tracking)
  const [editorDocKey, setEditorDocKey] = useState<string | null>(null)
  const currentDocKey = selectedDoc ? `${selectedDoc.id}-${selectedDoc._etag ?? ''}` : null
  if (currentDocKey !== editorDocKey) {
    setEditorDocKey(currentDocKey)
    setEditorValue(initialEditorValue)
    setSaveMessage(null)
  }

  // Save
  const saveMutation = useMutation({
    mutationFn: async () => {
      if (!selectedDoc) throw new Error('No document selected.')
      const parsed = JSON.parse(editorValue) as CosmosDocument
      parsed.id = selectedDoc.id
      return cosmosClient.replaceDocument(dbId, collId, selectedDoc.id, {
        ...parsed,
        _etag: selectedDoc._etag,
      })
    },
    onSuccess: async () => {
      setSaveMessage('Saved.')
      await queryClient.invalidateQueries({ queryKey: ['documents-panel', dbId, collId] })
      setExtraDocs([])
      setContinuation(undefined)
    },
  })

  const handleSave = useCallback(() => {
    try {
      const parsed = JSON.parse(editorValue) as Record<string, unknown>
      const missing = validatePartitionKey(parsed, partitionKeyPaths)
      if (missing.length > 0) {
        setPkWarning({ action: () => saveMutation.mutate(), missingFields: missing })
        return
      }
      saveMutation.mutate()
    } catch {
      saveMutation.mutate()
    }
  }, [editorValue, partitionKeyPaths, saveMutation])

  // Delete selected (multi)
  const deleteMutation = useMutation({
    mutationFn: async () => {
      if (!hasPartitionKeyPaths) {
        throw new Error('Partition key metadata has not loaded yet. Please try again.')
      }
      const targets = allDocuments.filter((d) => checkedIds.has(d.id))
      for (const doc of targets) {
        const pk = getPartitionKeyValue(doc, partitionKeyPaths)
        await cosmosClient.deleteDocument(dbId, collId, doc.id, pk)
      }
      return targets.length
    },
    onSuccess: async (count) => {
      setIsDeleteOpen(false)
      setCheckedIds(new Set())
      if (selectedDocId && checkedIds.has(selectedDocId)) {
        setSelectedDocId(null)
      }
      setSaveMessage(`${count} document(s) deleted.`)
      await queryClient.invalidateQueries({ queryKey: ['documents-panel', dbId, collId] })
      setExtraDocs([])
      setContinuation(undefined)
    },
  })

  const handleRefresh = useCallback(async () => {
    setExtraDocs([])
    setContinuation(undefined)
    await queryClient.invalidateQueries({ queryKey: ['documents-panel', dbId, collId] })
  }, [dbId, collId, queryClient])

  const handleToggleCheck = useCallback(
    (docId: string) => {
      setCheckedIds((prev) => {
        const next = new Set(prev)
        if (next.has(docId)) {
          next.delete(docId)
        } else {
          next.add(docId)
        }
        return next
      })
    },
    [],
  )

  const handleSelectAll = useCallback(
    (checked: boolean) => {
      if (checked) {
        setCheckedIds(new Set(allDocuments.map((d) => d.id)))
      } else {
        setCheckedIds(new Set())
      }
    },
    [allDocuments],
  )

  const allChecked = allDocuments.length > 0 && checkedIds.size === allDocuments.length
  const someChecked = checkedIds.size > 0 && !allChecked

  // New document
  const [isNewDocOpen, setIsNewDocOpen] = useState(false)
  const [newDocValue, setNewDocValue] = useState('')

  const openNewDoc = useCallback(() => {
    const template: Record<string, unknown> = { id: '' }
    for (const path of partitionKeyPaths) {
      const prop = path.replace(/^\//, '')
      if (prop !== 'id') template[prop] = ''
    }
    setNewDocValue(JSON.stringify(template, null, 2))
    setIsNewDocOpen(true)
  }, [partitionKeyPaths])

  const createMutation = useMutation({
    mutationFn: async () => {
      const parsed = JSON.parse(newDocValue) as CosmosDocument
      return cosmosClient.createDocument(dbId, collId, parsed)
    },
    onSuccess: async (doc) => {
      setIsNewDocOpen(false)
      setSelectedDocId(doc.id)
      await queryClient.invalidateQueries({ queryKey: ['documents-panel', dbId, collId] })
      setExtraDocs([])
      setContinuation(undefined)
    },
  })

  const handleCreate = useCallback(() => {
    try {
      const parsed = JSON.parse(newDocValue) as Record<string, unknown>
      const missing = validatePartitionKey(parsed, partitionKeyPaths)
      if (missing.length > 0) {
        setPkWarning({ action: () => createMutation.mutate(), missingFields: missing })
        return
      }
      createMutation.mutate()
    } catch {
      createMutation.mutate()
    }
  }, [newDocValue, partitionKeyPaths, createMutation])

  return (
    <section className={styles.root}>
      <Toolbar aria-label="Document actions" className={styles.toolbar} size="small">
        <ToolbarButton icon={<AddRegular />} onClick={openNewDoc}>
          New Document
        </ToolbarButton>
        <ToolbarButton
          disabled={checkedIds.size === 0 || !hasPartitionKeyPaths}
          icon={<DeleteRegular />}
          onClick={() => setIsDeleteOpen(true)}
        >
          Delete Selected{checkedIds.size > 0 ? ` (${checkedIds.size})` : ''}
        </ToolbarButton>
        <ToolbarDivider />
        <ToolbarButton icon={<ArrowSyncRegular />} onClick={handleRefresh}>
          Refresh
        </ToolbarButton>
        {documentsQuery.data && (
          <Badge appearance="outline" style={{ marginLeft: 'auto' }}>
            {allDocuments.length} document{allDocuments.length !== 1 ? 's' : ''} loaded
          </Badge>
        )}
      </Toolbar>

      <div className={styles.splitPanel}>
        {/* ── Left: Document list ── */}
        <div className={styles.listPane}>
          <div className={styles.listHeader}>
            <Checkbox
              checked={allChecked ? true : someChecked ? 'mixed' : false}
              label={<Text size={200} weight="semibold">id</Text>}
              onChange={(_, data) => handleSelectAll(Boolean(data.checked))}
            />
          </div>

          <div className={styles.scrollList}>
            {documentsQuery.isPending && (
              <div className={styles.loadMoreRow}>
                <Spinner label="Loading documents…" size="small" />
              </div>
            )}
            {documentsQuery.isError && (
              <MessageBar intent="error" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>Load failed</MessageBarTitle>
                  {documentsQuery.error instanceof Error ? documentsQuery.error.message : 'Unknown error'}
                </MessageBarBody>
              </MessageBar>
            )}
            {allDocuments.map((doc) => (
              <div
                className={`${styles.docRow} ${selectedDocId === doc.id ? styles.docRowSelected : ''}`}
                key={`${doc.id}-${doc._rid ?? ''}`}
                onClick={() => setSelectedDocId(doc.id)}
              >
                <Checkbox
                  checked={checkedIds.has(doc.id)}
                  onChange={(e) => {
                    e.stopPropagation()
                    handleToggleCheck(doc.id)
                  }}
                  onClick={(e) => e.stopPropagation()}
                />
                <span className={styles.docInfo}>
                  <Text font="monospace" size={200} truncate>
                    {doc.id}
                  </Text>
                </span>
              </div>
            ))}
            {hasMore && (
              <div className={styles.loadMoreRow}>
                <Button
                  appearance="secondary"
                  disabled={loadMoreMutation.isPending}
                  onClick={() => activeContinuation && loadMoreMutation.mutate(activeContinuation)}
                  size="small"
                >
                  {loadMoreMutation.isPending ? 'Loading…' : 'Load more'}
                </Button>
              </div>
            )}
          </div>
        </div>

        {/* ── Right: Editor pane ── */}
        <div className={styles.editorPane}>
          {selectedDoc ? (
            <>
              <div className={styles.editorHeader}>
                <Subtitle2>{selectedDoc.id}</Subtitle2>
                <div className={styles.editorActions}>
                  {saveMessage && (
                    <Body1 style={{ color: tokens.colorNeutralForeground3, alignSelf: 'center' }}>
                      {saveMessage}
                    </Body1>
                  )}
                  {saveMutation.isError && (
                    <Body1 style={{ color: tokens.colorPaletteRedForeground1, alignSelf: 'center' }}>
                      {saveMutation.error.message}
                    </Body1>
                  )}
                  <Button
                    appearance="primary"
                    disabled={saveMutation.isPending || editorValue === initialEditorValue}
                    icon={<SaveRegular />}
                    onClick={handleSave}
                  >
                    {saveMutation.isPending ? 'Saving…' : 'Save'}
                  </Button>
                </div>
              </div>
              <div className={styles.editorFrame}>
                <Editor
                  defaultLanguage="json"
                  height="100%"
                  onChange={(value) => setEditorValue(value ?? '')}
                  options={{
                    automaticLayout: true,
                    folding: true,
                    fontSize: 13,
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
            </>
          ) : (
            <div className={styles.emptyState}>
              <Body1>Select a document to view and edit it here.</Body1>
            </div>
          )}
        </div>
      </div>

      {/* ── Delete confirmation dialog ── */}
      {isDeleteOpen && (
        <Dialog
          modalType="non-modal"
          onOpenChange={(_, data) => {
            if (!data.open) setIsDeleteOpen(false)
          }}
          open
        >
          <DialogSurface backdrop={{ onClick: () => setIsDeleteOpen(false) }}>
            <DialogBody>
              <DialogTitle>Delete documents</DialogTitle>
              <DialogContent>
                Are you sure you want to delete {checkedIds.size} document
                {checkedIds.size !== 1 ? 's' : ''}? This action cannot be undone.
              </DialogContent>
              <DialogActions>
                <Button appearance="secondary" onClick={() => setIsDeleteOpen(false)}>
                  Cancel
                </Button>
                <Button
                  appearance="primary"
                  disabled={deleteMutation.isPending}
                  icon={<DeleteRegular />}
                  onClick={() => deleteMutation.mutate()}
                >
                  {deleteMutation.isPending ? 'Deleting…' : 'Delete'}
                </Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}

      {/* ── New document dialog ── */}
      {isNewDocOpen && (
        <Dialog
          modalType="non-modal"
          onOpenChange={(_, data) => {
            if (!data.open) {
              setIsNewDocOpen(false)
              createMutation.reset()
            }
          }}
          open
        >
          <DialogSurface backdrop={{ onClick: () => { setIsNewDocOpen(false); createMutation.reset() } }}>
            <DialogBody>
              <DialogTitle>New document</DialogTitle>
              <DialogContent>
                <div style={{ height: '300px' }}>
                  <Editor
                    defaultLanguage="json"
                    height="100%"
                    onChange={(value) => setNewDocValue(value ?? '{}')}
                    options={{
                      automaticLayout: true,
                      fontSize: 13,
                      formatOnPaste: true,
                      minimap: { enabled: false },
                      scrollBeyondLastLine: false,
                      wordWrap: 'on',
                    }}
                    theme={isDark ? 'vs-dark' : 'vs'}
                    value={newDocValue}
                  />
                </div>
                {createMutation.isError && (
                  <MessageBar intent="error" layout="multiline" style={{ marginTop: tokens.spacingVerticalS }}>
                    <MessageBarBody>
                      <MessageBarTitle>Create failed</MessageBarTitle>
                      {createMutation.error instanceof Error ? createMutation.error.message : 'Unknown error'}
                    </MessageBarBody>
                  </MessageBar>
                )}
              </DialogContent>
              <DialogActions>
                <Button
                  appearance="secondary"
                  onClick={() => {
                    setIsNewDocOpen(false)
                    createMutation.reset()
                  }}
                >
                  Cancel
                </Button>
                <Button
                  appearance="primary"
                  disabled={createMutation.isPending}
                  onClick={handleCreate}
                >
                  {createMutation.isPending ? 'Creating…' : 'Create'}
                </Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}

      {/* ── Partition key warning dialog ── */}
      {pkWarning && (
        <Dialog
          modalType="non-modal"
          onOpenChange={(_, data) => {
            if (!data.open) setPkWarning(null)
          }}
          open
        >
          <DialogSurface backdrop={{ onClick: () => setPkWarning(null) }}>
            <DialogBody>
              <DialogTitle>Missing partition key</DialogTitle>
              <DialogContent>
                The document is missing the following partition key field{pkWarning.missingFields.length > 1 ? 's' : ''}:{' '}
                <strong>{pkWarning.missingFields.join(', ')}</strong>.
                Documents without a partition key will use <code>null</code>, which may cause issues when reading or deleting.
                Do you want to continue?
              </DialogContent>
              <DialogActions>
                <Button appearance="secondary" onClick={() => setPkWarning(null)}>
                  Cancel
                </Button>
                <Button
                  appearance="primary"
                  onClick={() => {
                    const action = pkWarning.action
                    setPkWarning(null)
                    action()
                  }}
                >
                  Continue anyway
                </Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}
    </section>
  )
}

function getPartitionKeyValue(document: CosmosDocument, partitionKeyPaths: string[]): unknown {
  // Missing PK fields are treated as null (matching Cosmos DB backend behavior)
  const values = partitionKeyPaths.map((path) => {
    const prop = path.replace(/^\//, '')
    return prop in document ? document[prop] : null
  })
  return values.length === 1 ? values[0] : values
}
