import type { ChangeEvent, ReactElement, ReactNode } from 'react'
import { useCallback, useMemo, useRef, useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Button,
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
  Switch,
  Text,
  tokens,
  Tree,
  TreeItem,
  TreeItemLayout,
} from '@fluentui/react-components'
import {
  AddRegular,
  ArrowDownloadRegular,
  ArrowUploadRegular,
  CodeRegular,
  DatabaseRegular,
  DeleteRegular,
  DocumentRegular,
  FlashRegular,
  MathFormulaRegular,
  MoreHorizontalRegular,
  SettingsRegular,
  TableRegular,
} from '@fluentui/react-icons'
import { cosmosClient } from '../api/cosmosClient'
import type { CosmosContainer, CosmosDatabase, CosmosDocument } from '../types/cosmos'

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    backgroundColor: tokens.colorNeutralBackground1,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    paddingBottom: tokens.spacingVerticalL,
    paddingLeft: tokens.spacingHorizontalL,
    paddingRight: tokens.spacingHorizontalL,
    paddingTop: tokens.spacingVerticalL,
  },
  headerText: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  subtleText: {
    color: tokens.colorNeutralForeground3,
  },
  treeArea: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    overflow: 'auto',
    paddingBottom: tokens.spacingVerticalL,
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
  },
  tree: {
    minWidth: 0,
  },
  childTree: {
    marginTop: tokens.spacingVerticalXS,
    marginLeft: tokens.spacingHorizontalS,
  },
  nodeContent: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    width: '100%',
  },
  labelStack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
    flex: 1,
  },
  labelButton: {
    justifyContent: 'flex-start',
    minWidth: 0,
    paddingLeft: 0,
    paddingRight: tokens.spacingHorizontalS,
  },
  nodeMeta: {
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  actionBar: {
    display: 'flex',
    flexWrap: 'wrap',
    justifyContent: 'flex-end',
    gap: tokens.spacingHorizontalXS,
  },
  actionButton: {
    minWidth: 'auto',
  },
  loadingState: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    paddingBottom: tokens.spacingVerticalS,
    paddingTop: tokens.spacingVerticalS,
  },
  dialogFields: {
    display: 'grid',
    gap: tokens.spacingVerticalS,
  },
  documentRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    width: '100%',
  },
  contextMenuBackdrop: {
    position: 'fixed',
    inset: 0,
    zIndex: 1000,
  },
  contextMenu: {
    position: 'fixed',
    zIndex: 1001,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow16,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    padding: `${tokens.spacingVerticalXS} 0`,
    minWidth: '160px',
  },
  contextMenuItem: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    width: '100%',
    border: 'none',
    background: 'none',
    color: tokens.colorNeutralForeground1,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    cursor: 'pointer',
    fontSize: tokens.fontSizeBase300,
    textAlign: 'left',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
})

type ContainerSection = 'query' | 'sprocs' | 'triggers' | 'udfs'

interface ExplorerSelection {
  dbId?: string
  collId?: string
  docId?: string
  section?: ContainerSection
}

interface ContextMenuState {
  type: 'database' | 'container'
  dbId: string
  collId?: string
  partitionKeyPaths?: string[]
  x: number
  y: number
}

interface CreateDocumentTarget {
  dbId: string
  collId: string
  partitionKeyPaths: string[]
}

interface DeleteTarget {
  type: 'database' | 'container'
  dbId: string
  collId?: string
}

interface TreeActionCallbacks {
  onContextMenu: (
    type: 'database' | 'container',
    dbId: string,
    collId: string | undefined,
    x: number,
    y: number,
    partitionKeyPaths?: string[],
  ) => void
  onCreateContainer: (dbId: string) => void
  onCreateDocument: (dbId: string, collId: string, partitionKeyPaths: string[]) => void
  onDelete: (type: 'database' | 'container', dbId: string, collId?: string) => void
  onThroughputSettings: (type: 'database' | 'container', dbId: string, collId?: string) => void
}

interface ThroughputTarget {
  type: 'database' | 'container'
  dbId: string
  collId?: string
}

interface ImportState {
  dbId: string
  collId: string
  status: 'importing' | 'done'
  total: number
  completed: number
  failed: number
  errors: string[]
}

export function DatabaseTree() {
  const styles = useStyles()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const location = useLocation()
  const selection = parseSelection(location.pathname)
  const [isCreateDatabaseOpen, setIsCreateDatabaseOpen] = useState(false)
  const [databaseId, setDatabaseId] = useState('')
  const [contextMenu, setContextMenu] = useState<ContextMenuState | null>(null)
  const [createContainerTarget, setCreateContainerTarget] = useState<string | null>(null)
  const [containerId, setContainerId] = useState('')
  const [partitionKeyPath, setPartitionKeyPath] = useState('/id')
  const [createDocumentTarget, setCreateDocumentTarget] = useState<CreateDocumentTarget | null>(null)
  const [documentId, setDocumentId] = useState('')
  const [documentFieldValues, setDocumentFieldValues] = useState<Record<string, string>>({})
  const [documentDialogError, setDocumentDialogError] = useState<string | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<DeleteTarget | null>(null)
  const [throughputTarget, setThroughputTarget] = useState<ThroughputTarget | null>(null)
  const [throughputValue, setThroughputValue] = useState('')
  const [throughputEnabled, setThroughputEnabled] = useState(false)
  const [importState, setImportState] = useState<ImportState | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const importTargetRef = useRef<{ dbId: string; collId: string } | null>(null)

  const documentPartitionKeyProperties = useMemo(
    () => (createDocumentTarget ? getPartitionKeyProperties(createDocumentTarget.partitionKeyPaths) : []),
    [createDocumentTarget],
  )

  const databasesQuery = useQuery({
    queryKey: ['databases'],
    queryFn: () => cosmosClient.listDatabases(),
  })

  const createDatabaseMutation = useMutation({
    mutationFn: (id: string) => cosmosClient.createDatabase(id),
    onSuccess: async (database) => {
      setDatabaseId('')
      setIsCreateDatabaseOpen(false)
      await queryClient.invalidateQueries({ queryKey: ['databases'] })
      navigate(`/db/${encodeURIComponent(database.id)}`)
    },
  })

  const createContainerMutation = useMutation({
    mutationFn: ({ dbId, id, partitionKeyPaths }: { dbId: string; id: string; partitionKeyPaths: string[] }) =>
      cosmosClient.createContainer(dbId, id, partitionKeyPaths),
    onSuccess: async (container, variables) => {
      setContainerId('')
      setPartitionKeyPath('/id')
      setCreateContainerTarget(null)
      await queryClient.invalidateQueries({ queryKey: ['containers', variables.dbId] })
      navigate(buildContainerSectionPath(variables.dbId, container.id, 'query'))
    },
  })

  const createDocumentMutation = useMutation({
    mutationFn: ({
      dbId,
      collId,
      document,
    }: {
      dbId: string
      collId: string
      partitionKeyPaths: string[]
      document: CosmosDocument
    }) => cosmosClient.createDocument(dbId, collId, document),
    onSuccess: async (document, variables) => {
      setDocumentId('')
      setDocumentFieldValues({})
      setDocumentDialogError(null)
      setCreateDocumentTarget(null)
      await queryClient.invalidateQueries({ queryKey: ['documents', variables.dbId, variables.collId] })
      navigate(buildDocumentPath(variables.dbId, variables.collId, variables.partitionKeyPaths, document))
    },
  })

  const deleteDatabaseMutation = useMutation({
    mutationFn: ({ dbId }: { dbId: string }) => cosmosClient.deleteDatabase(dbId),
    onSuccess: async (_, variables) => {
      setDeleteTarget(null)
      await queryClient.invalidateQueries({ queryKey: ['databases'] })
      if (selection.dbId === variables.dbId) {
        navigate('/')
      }
    },
  })

  const deleteContainerMutation = useMutation({
    mutationFn: ({ dbId, collId }: { dbId: string; collId: string }) => cosmosClient.deleteContainer(dbId, collId),
    onSuccess: async (_, variables) => {
      setDeleteTarget(null)
      await queryClient.invalidateQueries({ queryKey: ['containers', variables.dbId] })
      if (selection.dbId === variables.dbId && selection.collId === variables.collId) {
        navigate(`/db/${encodeURIComponent(variables.dbId)}`)
      }
    },
  })

  const throughputQuery = useQuery({
    queryKey: ['throughput', throughputTarget?.type, throughputTarget?.dbId, throughputTarget?.collId],
    queryFn: async () => {
      if (!throughputTarget) return null
      if (throughputTarget.type === 'database') {
        return cosmosClient.getDatabaseThroughput(throughputTarget.dbId)
      }
      return cosmosClient.getContainerThroughput(throughputTarget.dbId, throughputTarget.collId!)
    },
    enabled: throughputTarget !== null,
  })

  const saveThroughputMutation = useMutation({
    mutationFn: async () => {
      if (!throughputTarget) return
      const value = throughputEnabled ? (parseInt(throughputValue, 10) || null) : null
      if (throughputTarget.type === 'database') {
        return cosmosClient.updateDatabaseThroughput(throughputTarget.dbId, value)
      }
      return cosmosClient.updateContainerThroughput(throughputTarget.dbId, throughputTarget.collId!, value)
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['throughput'] })
      setThroughputTarget(null)
    },
  })

  const callbacks: TreeActionCallbacks = {
    onContextMenu: (type, dbId, collId, x, y, partitionKeyPaths) =>
      setContextMenu({ type, dbId, collId, partitionKeyPaths, x, y }),
    onCreateContainer: (dbId) => {
      setContextMenu(null)
      openCreateContainerDialog(dbId)
    },
    onCreateDocument: (dbId, collId, partitionKeyPaths) => {
      setContextMenu(null)
      openCreateDocumentDialog(dbId, collId, partitionKeyPaths)
    },
    onDelete: (type, dbId, collId) => {
      setContextMenu(null)
      openDeleteDialog({ type, dbId, collId })
    },
    onThroughputSettings: (type, dbId, collId) => {
      setContextMenu(null)
      setThroughputValue('')
      setThroughputEnabled(false)
      setThroughputTarget({ type, dbId, collId })
    },
  }

  const submitCreateDatabase = () => {
    const id = databaseId.trim()
    if (!id) {
      return
    }

    createDatabaseMutation.mutate(id)
  }

  const submitCreateContainer = () => {
    if (!createContainerTarget) {
      return
    }

    const id = containerId.trim()
    if (!id) {
      return
    }

    createContainerMutation.mutate({
      dbId: createContainerTarget,
      id,
      partitionKeyPaths: [normalizePartitionKeyPath(partitionKeyPath)],
    })
  }

  const submitCreateDocument = () => {
    if (!createDocumentTarget) {
      return
    }

    const id = documentId.trim()
    if (!id) {
      setDocumentDialogError('Document id is required.')
      return
    }

    const document: CosmosDocument = { id }
    for (const property of documentPartitionKeyProperties) {
      const rawValue = documentFieldValues[property]?.trim()
      if (!rawValue) {
        setDocumentDialogError(`A value for "${property}" is required.`)
        return
      }

      document[property] = coercePromptValue(rawValue)
    }

    setDocumentDialogError(null)
    createDocumentMutation.mutate({
      dbId: createDocumentTarget.dbId,
      collId: createDocumentTarget.collId,
      partitionKeyPaths: createDocumentTarget.partitionKeyPaths,
      document,
    })
  }

  function openCreateContainerDialog(dbId: string) {
    createContainerMutation.reset()
    setContainerId('')
    setPartitionKeyPath('/id')
    setCreateContainerTarget(dbId)
  }

  function closeCreateContainerDialog() {
    createContainerMutation.reset()
    setContainerId('')
    setPartitionKeyPath('/id')
    setCreateContainerTarget(null)
  }

  function openCreateDocumentDialog(dbId: string, collId: string, partitionKeyPaths: string[]) {
    createDocumentMutation.reset()
    setDocumentId('')
    setDocumentFieldValues({})
    setDocumentDialogError(null)
    setCreateDocumentTarget({ dbId, collId, partitionKeyPaths })
  }

  function closeCreateDocumentDialog() {
    createDocumentMutation.reset()
    setDocumentId('')
    setDocumentFieldValues({})
    setDocumentDialogError(null)
    setCreateDocumentTarget(null)
  }

  function openDeleteDialog(target: DeleteTarget) {
    deleteDatabaseMutation.reset()
    deleteContainerMutation.reset()
    setDeleteTarget(target)
  }

  function closeDeleteDialog() {
    deleteDatabaseMutation.reset()
    deleteContainerMutation.reset()
    setDeleteTarget(null)
  }

  async function handleExportData(dbId: string, collId: string) {
    setContextMenu(null)
    try {
      const result = await cosmosClient.listDocuments(dbId, collId)
      const exportData = {
        databaseId: dbId,
        containerId: collId,
        documents: result.items,
      }
      const blob = new Blob([JSON.stringify(exportData, null, 2)], { type: 'application/json' })
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = `${dbId}_${collId}_export.json`
      document.body.appendChild(a)
      a.click()
      document.body.removeChild(a)
      URL.revokeObjectURL(url)
    } catch {
      // Export failures are non-critical; the browser download simply won't appear
    }
  }

  function handleImportData(dbId: string, collId: string) {
    setContextMenu(null)
    importTargetRef.current = { dbId, collId }
    if (fileInputRef.current) {
      fileInputRef.current.value = ''
      fileInputRef.current.click()
    }
  }

  async function handleImportFileSelected(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    const target = importTargetRef.current
    if (!file || !target) return

    importTargetRef.current = null
    const { dbId, collId } = target

    try {
      const text = await file.text()
      const data: unknown = JSON.parse(text)

      let documents: CosmosDocument[]
      if (Array.isArray(data)) {
        documents = data as CosmosDocument[]
      } else if (
        data !== null &&
        typeof data === 'object' &&
        Array.isArray((data as Record<string, unknown>).documents)
      ) {
        documents = (data as Record<string, unknown>).documents as CosmosDocument[]
      } else {
        documents = []
      }

      if (documents.length === 0) {
        setImportState({
          dbId, collId,
          status: 'done',
          total: 0, completed: 0, failed: 0,
          errors: ['No documents found in the file.'],
        })
        return
      }

      let completed = 0
      let failed = 0
      const errors: string[] = []

      setImportState({
        dbId, collId,
        status: 'importing',
        total: documents.length,
        completed: 0, failed: 0,
        errors: [],
      })

      for (const doc of documents) {
        try {
          await cosmosClient.upsertDocument(dbId, collId, doc)
          completed++
        } catch (err) {
          failed++
          const docId = typeof doc.id === 'string' ? doc.id : '(unknown id)'
          errors.push(`${docId}: ${err instanceof Error ? err.message : 'Unknown error'}`)
        }

        setImportState({
          dbId, collId,
          status: 'importing',
          total: documents.length,
          completed,
          failed,
          errors: errors.slice(),
        })
      }

      setImportState({
        dbId, collId,
        status: 'done',
        total: documents.length,
        completed,
        failed,
        errors: errors.slice(),
      })

      await queryClient.invalidateQueries({ queryKey: ['documents', dbId, collId] })
    } catch (error) {
      setImportState({
        dbId, collId,
        status: 'done',
        total: 0, completed: 0, failed: 1,
        errors: [`Failed to read file: ${error instanceof Error ? error.message : 'Unknown error'}`],
      })
    }
  }

  return (
    <aside className={styles.root}>
      <div className={styles.header}>
        <div className={styles.headerText}>
          <Text block size={500} weight="semibold">
            Explorer
          </Text>
          <Text block className={styles.subtleText} size={200}>
            Databases, containers, and documents
          </Text>
        </div>

        <Dialog
          modalType="non-modal"
          open={isCreateDatabaseOpen}
          onOpenChange={(_, data) => {
            setIsCreateDatabaseOpen(data.open)
            if (!data.open) {
              setDatabaseId('')
            }
          }}
        >
          <DialogTrigger>
            <Button appearance="primary" icon={<AddRegular />} size="small">
              DB
            </Button>
          </DialogTrigger>
          <DialogSurface backdrop={{ onClick: () => setIsCreateDatabaseOpen(false) }}>
            <DialogBody>
              <DialogTitle>Create database</DialogTitle>
              <DialogContent>
                <div className={styles.dialogFields}>
                  <Field label="Database id">
                    <Input
                      autoFocus
                      onChange={(_, data) => setDatabaseId(data.value)}
                      placeholder="Enter a database id"
                      value={databaseId}
                    />
                  </Field>
                  {createDatabaseMutation.isError && (
                    <StatusMessage intent="error" title="Create failed">
                      {toErrorMessage(createDatabaseMutation.error)}
                    </StatusMessage>
                  )}
                </div>
              </DialogContent>
              <DialogActions>
                <DialogTrigger>
                  <Button appearance="secondary">Cancel</Button>
                </DialogTrigger>
                <Button
                  appearance="primary"
                  disabled={!databaseId.trim() || createDatabaseMutation.isPending}
                  onClick={submitCreateDatabase}
                >
                  {createDatabaseMutation.isPending ? 'Creating…' : 'Create'}
                </Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      </div>

      <div className={styles.treeArea}>
        {databasesQuery.isPending && <LoadingState label="Loading databases…" />}
        {databasesQuery.isError && (
          <StatusMessage intent="error" title="Could not load databases">
            {toErrorMessage(databasesQuery.error)}
          </StatusMessage>
        )}
        {databasesQuery.isSuccess && (databasesQuery.data?.items.length ?? 0) === 0 && (
          <StatusMessage title="No databases yet">Create one to get started.</StatusMessage>
        )}
        {databasesQuery.isSuccess && (databasesQuery.data?.items.length ?? 0) > 0 && (
          <Tree aria-label="Cosmos explorer" appearance="subtle" className={styles.tree} size="small">
            {databasesQuery.data?.items.map((database) => (
              <DatabaseNode
                callbacks={callbacks}
                database={database}
                key={database.id}
                selection={selection}
              />
            ))}
          </Tree>
        )}
      </div>

      {contextMenu && (
        <>
          <div
            className={styles.contextMenuBackdrop}
            onClick={() => setContextMenu(null)}
            onContextMenu={(e) => { e.preventDefault(); setContextMenu(null) }}
          />
          <div
            className={styles.contextMenu}
            style={{ left: contextMenu.x, top: contextMenu.y }}
          >
            {contextMenu.type === 'database' && (
              <>
                <button className={styles.contextMenuItem} onClick={() => callbacks.onCreateContainer(contextMenu.dbId)}>
                  <AddRegular /> New Container
                </button>
                <button className={styles.contextMenuItem} onClick={() => callbacks.onThroughputSettings('database', contextMenu.dbId)}>
                  <SettingsRegular /> Throughput Settings
                </button>
                <button className={styles.contextMenuItem} onClick={() => callbacks.onDelete('database', contextMenu.dbId)}>
                  <DeleteRegular /> Delete Database
                </button>
              </>
            )}
            {contextMenu.type === 'container' && contextMenu.collId && (
              <>
                <button
                  className={styles.contextMenuItem}
                  onClick={() =>
                    callbacks.onCreateDocument(
                      contextMenu.dbId,
                      contextMenu.collId!,
                      contextMenu.partitionKeyPaths ?? [],
                    )
                  }
                >
                  <AddRegular /> New Document
                </button>
                <button className={styles.contextMenuItem} onClick={() => callbacks.onThroughputSettings('container', contextMenu.dbId, contextMenu.collId)}>
                  <SettingsRegular /> Throughput Settings
                </button>
                <button className={styles.contextMenuItem} onClick={() => callbacks.onDelete('container', contextMenu.dbId, contextMenu.collId)}>
                  <DeleteRegular /> Delete Container
                </button>
                <button className={styles.contextMenuItem} onClick={() => void handleExportData(contextMenu.dbId, contextMenu.collId!)}>
                  <ArrowDownloadRegular /> Export Data
                </button>
                <button className={styles.contextMenuItem} onClick={() => handleImportData(contextMenu.dbId, contextMenu.collId!)}>
                  <ArrowUploadRegular /> Import Data
                </button>
              </>
            )}
          </div>
        </>
      )}

      {createContainerTarget && (
        <Dialog
          modalType="non-modal"
          open
          onOpenChange={(_, data) => {
            if (!data.open) {
              closeCreateContainerDialog()
            }
          }}
        >
          <DialogSurface backdrop={{ onClick: closeCreateContainerDialog }}>
            <DialogBody>
              <DialogTitle>Create container</DialogTitle>
              <DialogContent>
                <div className={styles.dialogFields}>
                  <Field label="Container id">
                    <Input
                      autoFocus
                      onChange={(_, data) => setContainerId(data.value)}
                      placeholder={`Container id for ${createContainerTarget}`}
                      value={containerId}
                    />
                  </Field>
                  <Field label="Partition key path">
                    <Input
                      onChange={(_, data) => setPartitionKeyPath(data.value)}
                      placeholder="/id"
                      value={partitionKeyPath}
                    />
                  </Field>
                  {createContainerMutation.isError && (
                    <StatusMessage intent="error" title="Create failed">
                      {toErrorMessage(createContainerMutation.error)}
                    </StatusMessage>
                  )}
                </div>
              </DialogContent>
              <DialogActions>
                <Button appearance="secondary" onClick={closeCreateContainerDialog}>
                  Cancel
                </Button>
                <Button
                  appearance="primary"
                  disabled={!containerId.trim() || createContainerMutation.isPending}
                  onClick={submitCreateContainer}
                >
                  {createContainerMutation.isPending ? 'Creating…' : 'Create'}
                </Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}

      {createDocumentTarget && (
        <Dialog
          modalType="non-modal"
          open
          onOpenChange={(_, data) => {
            if (!data.open) {
              closeCreateDocumentDialog()
            }
          }}
        >
          <DialogSurface backdrop={{ onClick: closeCreateDocumentDialog }}>
            <DialogBody>
              <DialogTitle>Create document</DialogTitle>
              <DialogContent>
                <div className={styles.dialogFields}>
                  <Field label="Document id">
                    <Input
                      autoFocus
                      onChange={(_, data) => setDocumentId(data.value)}
                      placeholder="Enter a document id"
                      value={documentId}
                    />
                  </Field>
                  {documentPartitionKeyProperties.map((property) => (
                    <Field key={property} label={`Partition key: ${property}`}>
                      <Input
                        onChange={(_, data) =>
                          setDocumentFieldValues((current) => ({
                            ...current,
                            [property]: data.value,
                          }))
                        }
                        placeholder="JSON or string value"
                        value={documentFieldValues[property] ?? ''}
                      />
                    </Field>
                  ))}
                  {documentDialogError && (
                    <StatusMessage intent="error" title="Document details required">
                      {documentDialogError}
                    </StatusMessage>
                  )}
                  {createDocumentMutation.isError && (
                    <StatusMessage intent="error" title="Create failed">
                      {toErrorMessage(createDocumentMutation.error)}
                    </StatusMessage>
                  )}
                </div>
              </DialogContent>
              <DialogActions>
                <Button appearance="secondary" onClick={closeCreateDocumentDialog}>
                  Cancel
                </Button>
                <Button
                  appearance="primary"
                  disabled={!documentId.trim() || createDocumentMutation.isPending}
                  onClick={submitCreateDocument}
                >
                  {createDocumentMutation.isPending ? 'Creating…' : 'Create'}
                </Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}

      {deleteTarget && (
        <ConfirmDialog
          confirmLabel={
            deleteTarget.type === 'database'
              ? deleteDatabaseMutation.isPending
                ? 'Deleting…'
                : 'Delete'
              : deleteContainerMutation.isPending
                ? 'Deleting…'
                : 'Delete'
          }
          error={deleteTarget.type === 'database' ? deleteDatabaseMutation.error : deleteContainerMutation.error}
          message={
            deleteTarget.type === 'database'
              ? `Delete database “${deleteTarget.dbId}”?`
              : `Delete container “${deleteTarget.collId}”?`
          }
          onConfirm={() => {
            if (deleteTarget.type === 'database') {
              deleteDatabaseMutation.mutate({ dbId: deleteTarget.dbId })
              return
            }

            if (deleteTarget.collId) {
              deleteContainerMutation.mutate({ dbId: deleteTarget.dbId, collId: deleteTarget.collId })
            }
          }}
          onOpenChange={(open) => {
            if (!open) {
              closeDeleteDialog()
            }
          }}
          open
          title={deleteTarget.type === 'database' ? 'Delete database' : 'Delete container'}
        />
      )}

      {throughputTarget && (
        <ThroughputDialog
          isLoading={throughputQuery.isPending}
          currentMaxThroughput={throughputQuery.data?.maxThroughput ?? null}
          isSaving={saveThroughputMutation.isPending}
          error={saveThroughputMutation.error ?? throughputQuery.error}
          target={throughputTarget}
          throughputEnabled={throughputEnabled}
          throughputValue={throughputValue}
          onThroughputEnabledChange={setThroughputEnabled}
          onThroughputValueChange={setThroughputValue}
          onSave={() => saveThroughputMutation.mutate()}
          onClose={() => {
            saveThroughputMutation.reset()
            setThroughputTarget(null)
          }}
          onLoaded={(maxThroughput) => {
            const hasValue = maxThroughput !== null && maxThroughput !== undefined && maxThroughput > 0
            setThroughputEnabled(hasValue)
            setThroughputValue(hasValue ? String(maxThroughput) : '400')
          }}
        />
      )}

      <input
        ref={fileInputRef}
        type="file"
        accept=".json"
        style={{ display: 'none' }}
        onChange={handleImportFileSelected}
      />

      {importState && (
        <Dialog
          modalType="non-modal"
          open
          onOpenChange={(_, data) => {
            if (!data.open && importState.status === 'done') {
              setImportState(null)
            }
          }}
        >
          <DialogSurface
            backdrop={importState.status === 'done' ? { onClick: () => setImportState(null) } : undefined}
          >
            <DialogBody>
              <DialogTitle>
                {importState.status === 'importing' ? 'Importing Data' : 'Import Complete'}
              </DialogTitle>
              <DialogContent>
                <div className={styles.dialogFields}>
                  {importState.status === 'importing' && (
                    <div className={styles.loadingState}>
                      <Spinner size="tiny" />
                      <Text>
                        Importing {importState.completed + importState.failed}/{importState.total}…
                      </Text>
                    </div>
                  )}
                  {importState.status === 'done' && (
                    <>
                      <Text block>
                        {importState.completed} of {importState.total} document{importState.total !== 1 ? 's' : ''} imported successfully.
                      </Text>
                      {importState.failed > 0 && (
                        <StatusMessage intent="warning" title={`${importState.failed} document${importState.failed !== 1 ? 's' : ''} failed`}>
                          {importState.errors.slice(0, 10).map((err, i) => (
                            <Text key={i} block size={200}>
                              {err}
                            </Text>
                          ))}
                          {importState.errors.length > 10 && (
                            <Text block size={200}>
                              …and {importState.errors.length - 10} more
                            </Text>
                          )}
                        </StatusMessage>
                      )}
                      {importState.failed === 0 && importState.total > 0 && (
                        <StatusMessage intent="success" title="Success">
                          All documents were imported successfully.
                        </StatusMessage>
                      )}
                      {importState.total === 0 && (
                        <StatusMessage intent="warning" title="No documents">
                          {importState.errors[0] ?? 'The file contained no documents.'}
                        </StatusMessage>
                      )}
                    </>
                  )}
                </div>
              </DialogContent>
              {importState.status === 'done' && (
                <DialogActions>
                  <Button appearance="primary" onClick={() => setImportState(null)}>
                    Close
                  </Button>
                </DialogActions>
              )}
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}
    </aside>
  )
}

function DatabaseNode({
  callbacks,
  database,
  selection,
}: {
  callbacks: TreeActionCallbacks
  database: CosmosDatabase
  selection: ExplorerSelection
}) {
  const styles = useStyles()
  const [expanded, setExpanded] = useState(selection.dbId === database.id)
  const isExpanded = expanded || selection.dbId === database.id

  const containersQuery = useQuery({
    queryKey: ['containers', database.id],
    queryFn: () => cosmosClient.listContainers(database.id),
    enabled: isExpanded,
  })

  return (
    <TreeItem
      itemType="branch"
      onOpenChange={(_, data) => setExpanded(data.open)}
      open={isExpanded}
      value={`db:${database.id}`}
    >
      <TreeItemLayout
        iconBefore={<DatabaseRegular />}
        actions={
          <Button
            appearance="subtle"
            aria-label={`Open actions for ${database.id}`}
            className={styles.actionButton}
            icon={<MoreHorizontalRegular />}
            onClick={(event) => {
              event.stopPropagation()
              const rect = event.currentTarget.getBoundingClientRect()
              callbacks.onContextMenu('database', database.id, undefined, rect.left, rect.bottom)
            }}
            size="small"
          />
        }
        onContextMenu={(event) => {
          event.preventDefault()
          event.stopPropagation()
          callbacks.onContextMenu('database', database.id, undefined, event.clientX, event.clientY)
        }}
      >
        {database.id}
      </TreeItemLayout>

      <Tree appearance="subtle" className={styles.childTree} size="small">
        {containersQuery.isPending && <LoadingState label="Loading containers…" />}
        {containersQuery.isError && (
          <StatusMessage intent="error" title="Could not load containers">
            {toErrorMessage(containersQuery.error)}
          </StatusMessage>
        )}
        {(containersQuery.data?.items ?? []).map((container) => (
          <ContainerNode
            callbacks={callbacks}
            container={container}
            dbId={database.id}
            key={container.id}
            selection={selection}
          />
        ))}
        {containersQuery.isSuccess && (containersQuery.data?.items.length ?? 0) === 0 && (
          <StatusMessage title="No containers yet">Create a container to continue.</StatusMessage>
        )}
      </Tree>
    </TreeItem>
  )
}

function ContainerNode({
  callbacks,
  dbId,
  container,
  selection,
}: {
  callbacks: TreeActionCallbacks
  dbId: string
  container: CosmosContainer
  selection: ExplorerSelection
}) {
  const styles = useStyles()
  const navigate = useNavigate()
  const [expanded, setExpanded] = useState(selection.collId === container.id)
  const isExpanded = expanded || selection.collId === container.id

  const programmabilitySections: Array<{ icon: ReactElement; label: string; section: ContainerSection }> = [
    { icon: <CodeRegular />, label: 'Stored Procedures', section: 'sprocs' },
    { icon: <FlashRegular />, label: 'Triggers', section: 'triggers' },
    { icon: <MathFormulaRegular />, label: 'UDFs', section: 'udfs' },
  ]

  return (
    <TreeItem
      itemType="branch"
      onOpenChange={(_, data) => setExpanded(data.open)}
      open={isExpanded}
      value={`container:${dbId}:${container.id}`}
    >
      <TreeItemLayout
        iconBefore={<TableRegular />}
        aside={
          <Text className={styles.nodeMeta} size={200}>
            {container.partitionKey.paths.join(', ')}
          </Text>
        }
        actions={
          <Button
            appearance="subtle"
            aria-label={`Open actions for ${container.id}`}
            className={styles.actionButton}
            icon={<MoreHorizontalRegular />}
            onClick={(event) => {
              event.stopPropagation()
              const rect = event.currentTarget.getBoundingClientRect()
              callbacks.onContextMenu(
                'container',
                dbId,
                container.id,
                rect.left,
                rect.bottom,
                container.partitionKey.paths,
              )
            }}
            size="small"
          />
        }
        onContextMenu={(event) => {
          event.preventDefault()
          event.stopPropagation()
          callbacks.onContextMenu(
            'container',
            dbId,
            container.id,
            event.clientX,
            event.clientY,
            container.partitionKey.paths,
          )
        }}
      >
        {container.id}
      </TreeItemLayout>

      <Tree appearance="subtle" className={styles.childTree} size="small">
        <DocumentsSectionNode
          dbId={dbId}
          collId={container.id}
          partitionKeyPaths={container.partitionKey.paths}
          selection={selection}
        />
        {programmabilitySections.map(({ icon, label, section }) => {
          const isSelected = selection.collId === container.id && selection.section === section

          return (
            <TreeItem
              itemType="leaf"
              key={section}
              value={`container-section:${dbId}:${container.id}:${section}`}
            >
              <TreeItemLayout iconBefore={icon}>
                <Button
                  appearance={isSelected ? 'secondary' : 'transparent'}
                  className={styles.labelButton}
                  onClick={(event) =>
                    handleTreeAction(event, () => navigate(buildContainerSectionPath(dbId, container.id, section)))
                  }
                  size="small"
                >
                  {label}
                </Button>
              </TreeItemLayout>
            </TreeItem>
          )
        })}
      </Tree>
    </TreeItem>
  )
}

const DOCUMENTS_PAGE_SIZE = 25

function DocumentsSectionNode({
  dbId,
  collId,
  partitionKeyPaths,
  selection,
}: {
  dbId: string
  collId: string
  partitionKeyPaths: string[]
  selection: ExplorerSelection
}) {
  const styles = useStyles()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [expanded, setExpanded] = useState(false)
  const [extraDocs, setExtraDocs] = useState<CosmosDocument[]>([])
  const [continuation, setContinuation] = useState<string | null | undefined>(undefined)

  const isDocumentsSection = selection.collId === collId && selection.section === 'query'

  const documentsQuery = useQuery({
    queryKey: ['tree-documents', dbId, collId],
    queryFn: () => cosmosClient.listDocumentsPaged(dbId, collId, DOCUMENTS_PAGE_SIZE),
    enabled: expanded,
  })

  const loadMoreMutation = useMutation({
    mutationFn: (token: string) =>
      cosmosClient.listDocumentsPaged(dbId, collId, DOCUMENTS_PAGE_SIZE, token),
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

  const handleOpenChange = useCallback(
    (_: unknown, data: { open: boolean }) => {
      setExpanded(data.open)
      if (!data.open) {
        setExtraDocs([])
        setContinuation(undefined)
        loadMoreMutation.reset()
        queryClient.removeQueries({ queryKey: ['tree-documents', dbId, collId] })
      }
    },
    [dbId, collId, queryClient, loadMoreMutation],
  )

  const handleLoadMore = useCallback(() => {
    if (activeContinuation) {
      loadMoreMutation.mutate(activeContinuation)
    }
  }, [activeContinuation, loadMoreMutation])

  return (
    <TreeItem
      itemType="branch"
      onOpenChange={handleOpenChange}
      open={expanded}
      value={`container-section:${dbId}:${collId}:query`}
    >
      <TreeItemLayout iconBefore={<DocumentRegular />}>
        <Button
          appearance={isDocumentsSection ? 'secondary' : 'transparent'}
          className={styles.labelButton}
          onClick={(event) =>
            handleTreeAction(event, () => navigate(buildContainerSectionPath(dbId, collId, 'query')))
          }
          size="small"
        >
          Documents
        </Button>
      </TreeItemLayout>

      <Tree appearance="subtle" className={styles.childTree} size="small">
        {documentsQuery.isPending && <LoadingState label="Loading documents…" />}
        {documentsQuery.isError && (
          <StatusMessage intent="error" title="Could not load documents">
            {toErrorMessage(documentsQuery.error)}
          </StatusMessage>
        )}
        {allDocuments.map((doc) => {
          const isSelected = selection.docId === doc.id
          return (
            <TreeItem
              itemType="leaf"
              key={`${doc.id}-${doc._rid ?? ''}`}
              value={`doc:${dbId}:${collId}:${doc.id}`}
            >
              <TreeItemLayout iconBefore={<DocumentRegular />}>
                <Button
                  appearance={isSelected ? 'secondary' : 'transparent'}
                  className={styles.labelButton}
                  onClick={(event) =>
                    handleTreeAction(event, () =>
                      navigate(buildDocumentPath(dbId, collId, partitionKeyPaths, doc)),
                    )
                  }
                  size="small"
                >
                  {doc.id}
                </Button>
              </TreeItemLayout>
            </TreeItem>
          )
        })}
        {loadMoreMutation.isPending && <LoadingState label="Loading more…" />}
        {hasMore && !loadMoreMutation.isPending && (
          <TreeItem itemType="leaf" value={`load-more:${dbId}:${collId}`}>
            <TreeItemLayout>
              <Button
                appearance="transparent"
                className={styles.labelButton}
                onClick={(event) => handleTreeAction(event, handleLoadMore)}
                size="small"
              >
                Load more…
              </Button>
            </TreeItemLayout>
          </TreeItem>
        )}
        {documentsQuery.isSuccess && allDocuments.length === 0 && (
          <StatusMessage title="No documents">This container has no documents yet.</StatusMessage>
        )}
      </Tree>
    </TreeItem>
  )
}

function LoadingState({ label }: { label: string }) {
  const styles = useStyles()

  return (
    <div className={styles.loadingState}>
      <Spinner size="tiny" />
      <Text size={200}>{label}</Text>
    </div>
  )
}

function StatusMessage({
  children,
  intent = 'info',
  title,
}: {
  children: ReactNode
  intent?: 'info' | 'success' | 'warning' | 'error'
  title: string
}) {
  return (
    <MessageBar intent={intent} layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>{title}</MessageBarTitle>
        {children}
      </MessageBarBody>
    </MessageBar>
  )
}

function ConfirmDialog({
  confirmLabel,
  error,
  message,
  onConfirm,
  onOpenChange,
  open,
  title,
}: {
  confirmLabel: string
  error?: unknown
  message: string
  onConfirm: () => void
  onOpenChange: (open: boolean) => void
  open: boolean
  title: string
}) {
  return (
    <Dialog modalType="non-modal" open={open} onOpenChange={(_, data) => onOpenChange(data.open)}>
      <DialogSurface backdrop={{ onClick: () => onOpenChange(false) }}>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent>
            <div>
              <Text block>{message}</Text>
              {error ? (
                <div>
                  <StatusMessage intent="error" title="Operation failed">
                    {toErrorMessage(error)}
                  </StatusMessage>
                </div>
              ) : null}
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button appearance="primary" onClick={onConfirm}>
              {confirmLabel}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

function parseSelection(pathname: string): ExplorerSelection {
  const segments = pathname.split('/').filter(Boolean).map(decodeURIComponent)
  if (segments[0] !== 'db') {
    return {}
  }

  const sectionSegment = segments[4]
  const section: ContainerSection | undefined =
    segments[2] === 'container' && segments[3]
      ? sectionSegment === 'sprocs' || sectionSegment === 'triggers' || sectionSegment === 'udfs'
        ? sectionSegment
        : 'query'
      : undefined

  return {
    dbId: segments[1],
    collId: segments[3],
    docId: sectionSegment === 'doc' ? segments[5] : undefined,
    section,
  }
}

function handleTreeAction(event: React.MouseEvent<HTMLElement>, action: () => void) {
  event.stopPropagation()
  action()
}

function buildContainerSectionPath(dbId: string, collId: string, section: ContainerSection): string {
  const basePath = `/db/${encodeURIComponent(dbId)}/container/${encodeURIComponent(collId)}`
  return section === 'query' ? `${basePath}/query` : `${basePath}/${section}`
}

function normalizePartitionKeyPath(value: string): string {

  const trimmed = value.trim()
  if (!trimmed) {
    return '/id'
  }

  return trimmed.startsWith('/') ? trimmed : `/${trimmed}`
}

function coercePromptValue(value: string): unknown {
  try {
    return JSON.parse(value)
  } catch {
    return value
  }
}

function buildDocumentPath(
  dbId: string,
  collId: string,
  partitionKeyPaths: string[],
  document: CosmosDocument,
): string {
  const pathname = `/db/${encodeURIComponent(dbId)}/container/${encodeURIComponent(collId)}/doc/${encodeURIComponent(document.id)}`
  const partitionKey = getPartitionKeyValue(document, partitionKeyPaths)

  if (partitionKey === undefined) {
    return pathname
  }

  const searchParams = new URLSearchParams({ pk: JSON.stringify(partitionKey) })
  return `${pathname}?${searchParams.toString()}`
}

function getPartitionKeyProperties(partitionKeyPaths: string[]): string[] {
  return partitionKeyPaths
    .map((path) => path.replace(/^\//, ''))
    .filter((property) => property && property !== 'id')
}

function getPartitionKeyValue(document: CosmosDocument, partitionKeyPaths: string[]): unknown {
  const values = partitionKeyPaths.map((path) => document[path.replace(/^\//, '')])
  if (values.every((value) => value === undefined)) {
    return undefined
  }

  return values.length === 1 ? values[0] : values
}

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Unexpected error'
}

function ThroughputDialog({
  isLoading,
  currentMaxThroughput,
  isSaving,
  error,
  target,
  throughputEnabled,
  throughputValue,
  onThroughputEnabledChange,
  onThroughputValueChange,
  onSave,
  onClose,
  onLoaded,
}: {
  isLoading: boolean
  currentMaxThroughput: number | null | undefined
  isSaving: boolean
  error: unknown
  target: ThroughputTarget
  throughputEnabled: boolean
  throughputValue: string
  onThroughputEnabledChange: (enabled: boolean) => void
  onThroughputValueChange: (value: string) => void
  onSave: () => void
  onClose: () => void
  onLoaded: (maxThroughput: number | null | undefined) => void
}) {
  const styles = useStyles()
  const label = target.type === 'database'
    ? `Database: ${target.dbId}`
    : `Container: ${target.dbId}/${target.collId}`

  // Sync loaded value into the form once it arrives
  const [initialized, setInitialized] = useState(false)
  if (!isLoading && !initialized) {
    setInitialized(true)
    onLoaded(currentMaxThroughput)
  }

  return (
    <Dialog
      modalType="non-modal"
      open
      onOpenChange={(_, data) => {
        if (!data.open) onClose()
      }}
    >
      <DialogSurface backdrop={{ onClick: onClose }}>
        <DialogBody>
          <DialogTitle>Throughput Settings</DialogTitle>
          <DialogContent>
            <div className={styles.dialogFields}>
              <Text block size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                {label}
              </Text>
              {isLoading ? (
                <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS }}>
                  <Spinner size="tiny" />
                  <Text size={200}>Loading current settings…</Text>
                </div>
              ) : (
                <>
                  <Field label="Enable RU/s cap">
                    <Switch
                      checked={throughputEnabled}
                      label={throughputEnabled ? 'RU/s cap enabled' : 'No cap (unlimited)'}
                      onChange={(_, data) => onThroughputEnabledChange(data.checked)}
                    />
                  </Field>
                  {throughputEnabled && (
                    <Field
                      label="Max throughput (RU/s)"
                      hint="Maximum request units per second. Requests exceeding this rate will receive HTTP 429."
                    >
                      <Input
                        type="number"
                        min={1}
                        value={throughputValue}
                        onChange={(_, data) => onThroughputValueChange(data.value)}
                        placeholder="400"
                      />
                    </Field>
                  )}
                </>
              )}
              {error ? (
                <StatusMessage intent="error" title="Error">
                  {toErrorMessage(error)}
                </StatusMessage>
              ) : null}
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={isLoading || isSaving}
              onClick={onSave}
            >
              {isSaving ? 'Saving…' : 'Save'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
