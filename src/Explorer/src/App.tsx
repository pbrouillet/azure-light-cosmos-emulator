import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react'
import type { ReactNode, RefObject } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Button,
  Card,
  Combobox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Option,
  makeStyles,
  Tab,
  TabList,
  Text,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
  tokens,
} from '@fluentui/react-components'
import {
  ArrowSyncRegular,
  DatabaseRegular,
  DocumentSearchRegular,
  PlayRegular,
  SettingsRegular,
  TableRegular,
  WeatherMoonRegular,
  WeatherSunnyRegular,
} from '@fluentui/react-icons'
import {
  Navigate,
  Route,
  Routes,
  useLocation,
  useNavigate,
  useParams,
  useSearchParams,
} from 'react-router-dom'
import { cosmosClient } from './api/cosmosClient'
import { ClusterSettings } from './components/ClusterSettings'
import { DatabaseTree } from './components/DatabaseTree'
import { DocumentEditor } from './components/DocumentEditor'
import { ProgrammabilityEditor } from './components/ProgrammabilityEditor'
import { QueryEditor } from './components/QueryEditor'
import type { QueryEditorHandle } from './components/QueryEditor'
import { useTheme } from './theme'
import type { CosmosContainer, CosmosDatabase } from './types/cosmos'

type ContainerTab = 'query' | 'sprocs' | 'triggers' | 'udfs'

interface ExplorerSelection {
  dbId?: string
  collId?: string
  section?: ContainerTab
}

const QueryExecuteContext = createContext<RefObject<QueryEditorHandle | null> | null>(null)

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    backgroundColor: tokens.colorNeutralBackground2,
    color: tokens.colorNeutralForeground1,
  },
  header: {
    paddingBottom: tokens.spacingVerticalL,
    paddingLeft: tokens.spacingHorizontalXL,
    paddingRight: tokens.spacingHorizontalXL,
    paddingTop: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow4,
    zIndex: 1,
  },
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    flexWrap: 'wrap',
  },
  branding: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  eyebrow: {
    color: tokens.colorBrandForeground1,
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
  },
  subtleText: {
    color: tokens.colorNeutralForeground3,
  },
  toolbarStrip: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    paddingLeft: tokens.spacingHorizontalL,
    paddingRight: tokens.spacingHorizontalL,
    paddingTop: tokens.spacingVerticalS,
    paddingBottom: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  scopeLabel: {
    color: tokens.colorNeutralForeground2,
  },
  scopePopover: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: '20rem',
  },
  rescopeBackdrop: {
    position: 'fixed',
    inset: 0,
    zIndex: 1000,
  },
  rescopePanel: {
    position: 'absolute',
    zIndex: 1001,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow16,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    padding: tokens.spacingHorizontalL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: '20rem',
  },
  dialogFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    paddingTop: tokens.spacingVerticalXS,
  },
  content: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
  },
  iconNav: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalS,
    paddingTop: tokens.spacingVerticalM,
    paddingLeft: tokens.spacingHorizontalXS,
    paddingRight: tokens.spacingHorizontalXS,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    width: '48px',
    flexShrink: 0,
  },
  sidebar: {
    minWidth: '14rem',
    maxWidth: '40rem',
    minHeight: 0,
    flexShrink: 0,
  },
  resizeHandle: {
    width: '4px',
    cursor: 'col-resize',
    backgroundColor: 'transparent',
    flexShrink: 0,
    ':hover': {
      backgroundColor: tokens.colorBrandStroke1,
    },
  },
  resizeHandleActive: {
    backgroundColor: tokens.colorBrandStroke1,
  },
  main: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
    overflow: 'auto',
    paddingBottom: tokens.spacingVerticalXL,
    paddingLeft: tokens.spacingHorizontalXL,
    paddingRight: tokens.spacingHorizontalXL,
    paddingTop: tokens.spacingVerticalXL,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  workspaceSection: {
    display: 'flex',
    flex: 1,
    minHeight: '100%',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  statusBar: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    paddingLeft: tokens.spacingHorizontalL,
    paddingRight: tokens.spacingHorizontalL,
    paddingTop: tokens.spacingVerticalS,
    paddingBottom: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    flexWrap: 'wrap',
  },
  statusBarLeft: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
  },
  statusBarDivider: {
    width: '1px',
    height: '1rem',
    backgroundColor: tokens.colorNeutralStroke2,
  },
  workspaceContent: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
  },
  messageCard: {
    paddingBottom: tokens.spacingVerticalXL,
    paddingLeft: tokens.spacingHorizontalXL,
    paddingRight: tokens.spacingHorizontalXL,
    paddingTop: tokens.spacingVerticalXL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  neutralMessage: {
    borderLeft: `${tokens.strokeWidthThick} solid ${tokens.colorBrandStroke1}`,
  },
  errorMessage: {
    borderLeft: `${tokens.strokeWidthThick} solid ${tokens.colorPaletteRedBorder2}`,
  },
  toolbar: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
  },
  tabbedContent: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  tabPanel: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
  },
})

function App() {
  const styles = useStyles()
  const location = useLocation()
  const { isDark, toggle } = useTheme()
  const queryEditorRef = useRef<QueryEditorHandle | null>(null)
  const selection = parseSelection(location.pathname)

  const [sidebarWidth, setSidebarWidth] = useState(384) // 24rem
  const [isDragging, setIsDragging] = useState(false)
  const [activeView, setActiveView] = useState<'explorer' | 'settings'>('explorer')

  const startResize = useCallback((e: React.MouseEvent) => {
    e.preventDefault()
    setIsDragging(true)
  }, [])

  useEffect(() => {
    if (!isDragging) return

    const onMouseMove = (e: MouseEvent) => {
      const newWidth = Math.min(640, Math.max(224, e.clientX))
      setSidebarWidth(newWidth)
    }

    const onMouseUp = () => setIsDragging(false)

    document.addEventListener('mousemove', onMouseMove)
    document.addEventListener('mouseup', onMouseUp)
    document.body.style.cursor = 'col-resize'
    document.body.style.userSelect = 'none'

    return () => {
      document.removeEventListener('mousemove', onMouseMove)
      document.removeEventListener('mouseup', onMouseUp)
      document.body.style.cursor = ''
      document.body.style.userSelect = ''
    }
  }, [isDragging])

  return (
    <QueryExecuteContext.Provider value={queryEditorRef}>
      <div className={styles.root}>
        <header className={styles.header}>
          <div className={styles.headerRow}>
            <div className={styles.branding}>
              <Text block className={styles.eyebrow} size={300} weight="semibold">
                Azure Cosmos DB
              </Text>
              <Text as="h1" block size={800} weight="bold">
                Cosmos DB Emulator Explorer
              </Text>
              <Text block className={styles.subtleText} size={300}>
                Explorer UI served from /explorer
              </Text>
            </div>

            <Toolbar aria-label="Theme actions">
              <ToolbarButton
                appearance="subtle"
                icon={isDark ? <WeatherSunnyRegular /> : <WeatherMoonRegular />}
                onClick={toggle}
              >
                {isDark ? 'Light theme' : 'Dark theme'}
              </ToolbarButton>
            </Toolbar>
          </div>
        </header>

        <GlobalToolbar queryEditorRef={queryEditorRef} selection={selection} />

        <div className={styles.content}>
          <div className={styles.iconNav}>
            <Button
              appearance={activeView === 'explorer' ? 'subtle' : 'transparent'}
              icon={<DatabaseRegular />}
              onClick={() => setActiveView('explorer')}
              title="Explorer"
            />
            <Button
              appearance={activeView === 'settings' ? 'subtle' : 'transparent'}
              icon={<SettingsRegular />}
              onClick={() => setActiveView('settings')}
              title="Cluster Settings"
            />
          </div>

          {activeView === 'explorer' ? (
            <>
              <aside className={styles.sidebar} style={{ width: sidebarWidth }}>
                <DatabaseTree />
              </aside>
              <div
                className={`${styles.resizeHandle} ${isDragging ? styles.resizeHandleActive : ''}`}
                onMouseDown={startResize}
              />
              <main className={styles.main}>
                <Routes>
                  <Route
                    element={
                      <WorkspaceMessage
                        description="Create a database, drill into a container, or open a document from the tree to begin exploring the emulator."
                        title="Welcome to the Explorer"
                      />
                    }
                    path="/"
                  />
                  <Route element={<DatabaseLanding />} path="/db/:dbId" />
                  <Route element={<ContainerView />} path="/db/:dbId/container/:collId" />
                  <Route element={<ContainerView />} path="/db/:dbId/container/:collId/query" />
                  <Route element={<ContainerView />} path="/db/:dbId/container/:collId/sprocs" />
                  <Route element={<ContainerView />} path="/db/:dbId/container/:collId/triggers" />
                  <Route element={<ContainerView />} path="/db/:dbId/container/:collId/udfs" />
                  <Route element={<DocumentRoute />} path="/db/:dbId/container/:collId/doc/:docId" />
                  <Route element={<Navigate replace to="/" />} path="*" />
                </Routes>
              </main>
            </>
          ) : (
            <main className={styles.main}>
              <ClusterSettings />
            </main>
          )}
        </div>
      </div>
    </QueryExecuteContext.Provider>
  )
}

function GlobalToolbar({
  selection,
  queryEditorRef,
}: {
  selection: ExplorerSelection
  queryEditorRef: RefObject<QueryEditorHandle | null>
}) {
  const styles = useStyles()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [isCreateDatabaseOpen, setIsCreateDatabaseOpen] = useState(false)
  const [databaseId, setDatabaseId] = useState('')
  const [isCreateContainerOpen, setIsCreateContainerOpen] = useState(false)
  const [containerId, setContainerId] = useState('')
  const [partitionKeyPath, setPartitionKeyPath] = useState('/id')
  const [isRescopeOpen, setIsRescopeOpen] = useState(false)
  const [scopeDbId, setScopeDbId] = useState(selection.dbId ?? '')
  const [scopeCollId, setScopeCollId] = useState(selection.collId ?? '')

  const canCreateContainer = Boolean(selection.dbId)
  const canOpenQuery = Boolean(selection.dbId && selection.collId)
  const isQueryView = Boolean(selection.dbId && selection.collId && selection.section === 'query')
  const scopeLabel = selection.dbId && selection.collId ? `${selection.dbId} / ${selection.collId}` : 'No scope'

  const databasesQuery = useQuery({
    queryKey: ['databases'],
    queryFn: () => cosmosClient.listDatabases(),
    enabled: isRescopeOpen,
  })

  const containersQuery = useQuery({
    queryKey: ['containers', scopeDbId],
    queryFn: () => cosmosClient.listContainers(scopeDbId),
    enabled: isRescopeOpen && Boolean(scopeDbId),
  })

  const createDatabaseMutation = useMutation({
    mutationFn: (id: string) => cosmosClient.createDatabase(id),
    onSuccess: async (database) => {
      resetCreateDatabaseDialog()
      setIsCreateDatabaseOpen(false)
      await queryClient.invalidateQueries({ queryKey: ['databases'] })
      navigate(`/db/${encodeURIComponent(database.id)}`)
    },
  })

  const createContainerMutation = useMutation({
    mutationFn: ({ dbId, id, partitionKeyPath }: { dbId: string; id: string; partitionKeyPath: string }) =>
      cosmosClient.createContainer(dbId, id, [normalizePartitionKeyPath(partitionKeyPath)]),
    onSuccess: async (container, variables) => {
      resetCreateContainerDialog()
      setIsCreateContainerOpen(false)
      await queryClient.invalidateQueries({ queryKey: ['containers', variables.dbId] })
      navigate(buildContainerSectionPath(variables.dbId, container.id, 'query'))
    },
  })

  function resetCreateDatabaseDialog() {
    setDatabaseId('')
    createDatabaseMutation.reset()
  }

  function resetCreateContainerDialog() {
    setContainerId('')
    setPartitionKeyPath('/id')
    createContainerMutation.reset()
  }

  function submitCreateDatabase() {
    const id = databaseId.trim()
    if (!id) {
      return
    }

    createDatabaseMutation.mutate(id)
  }

  function submitCreateContainer() {
    const dbId = selection.dbId?.trim()
    const id = containerId.trim()
    if (!dbId || !id) {
      return
    }

    createContainerMutation.mutate({ dbId, id, partitionKeyPath })
  }

  return (
    <>
      <Toolbar aria-label="Explorer actions" className={styles.toolbarStrip}>
        <ToolbarButton icon={<DatabaseRegular />} onClick={() => setIsCreateDatabaseOpen(true)}>
          New Database
        </ToolbarButton>
        <ToolbarButton
          disabled={!canCreateContainer}
          icon={<TableRegular />}
          onClick={() => setIsCreateContainerOpen(true)}
        >
          New Container
        </ToolbarButton>
        <ToolbarButton
          disabled={!canOpenQuery}
          icon={<DocumentSearchRegular />}
          onClick={() => {
            if (selection.dbId && selection.collId) {
              navigate(buildContainerSectionPath(selection.dbId, selection.collId, 'query'))
            }
          }}
        >
          New Query
        </ToolbarButton>
        <ToolbarDivider />
        <ToolbarButton
          appearance={isQueryView ? 'primary' : 'subtle'}
          disabled={!isQueryView}
          icon={<PlayRegular />}
          onClick={() => queryEditorRef.current?.execute()}
        >
          Execute
        </ToolbarButton>
        <ToolbarDivider />
        {isQueryView ? (
          <div style={{ position: 'relative' }}>
            <ToolbarButton icon={<ArrowSyncRegular />} onClick={() => {
              if (!isRescopeOpen) {
                setScopeDbId(selection.dbId ?? '')
                setScopeCollId(selection.collId ?? '')
              }
              setIsRescopeOpen(!isRescopeOpen)
            }}>
              Re-scope <span className={styles.scopeLabel}>{scopeLabel}</span>
            </ToolbarButton>
            {isRescopeOpen && (
              <>
                <div className={styles.rescopeBackdrop} onClick={() => setIsRescopeOpen(false)} />
                <div className={styles.rescopePanel}>
                  <Text block size={300} weight="semibold">
                    Select query scope
                  </Text>
                  <Field label="Database">
                    <Combobox
                      onOptionSelect={(_, data) => {
                        const nextDbId = data.optionValue ?? ''
                        setScopeDbId(nextDbId)
                        setScopeCollId(nextDbId === selection.dbId ? selection.collId ?? '' : '')
                      }}
                      placeholder="Select a database"
                      selectedOptions={scopeDbId ? [scopeDbId] : []}
                      value={scopeDbId}
                    >
                      {(databasesQuery.data?.items ?? []).map((database: CosmosDatabase) => (
                        <Option key={database.id} value={database.id}>
                          {database.id}
                        </Option>
                      ))}
                    </Combobox>
                  </Field>
                  <Field label="Container">
                    <Combobox
                      disabled={!scopeDbId || containersQuery.isPending || (containersQuery.data?.items.length ?? 0) === 0}
                      onOptionSelect={(_, data) => {
                        const nextCollId = data.optionValue ?? ''
                        setScopeCollId(nextCollId)
                        if (scopeDbId && nextCollId) {
                          setIsRescopeOpen(false)
                          navigate(buildContainerSectionPath(scopeDbId, nextCollId, 'query'))
                        }
                      }}
                      placeholder={scopeDbId ? 'Select a container' : 'Select a database first'}
                      selectedOptions={scopeCollId ? [scopeCollId] : []}
                      value={scopeCollId}
                    >
                      {(containersQuery.data?.items ?? []).map((container: CosmosContainer) => (
                        <Option key={container.id} value={container.id}>
                          {container.id}
                        </Option>
                      ))}
                    </Combobox>
                  </Field>
                  {databasesQuery.isError && (
                    <InlineStatus title="Could not load databases">{toErrorMessage(databasesQuery.error)}</InlineStatus>
                  )}
                  {containersQuery.isError && (
                    <InlineStatus title="Could not load containers">{toErrorMessage(containersQuery.error)}</InlineStatus>
                  )}
                  {scopeDbId && containersQuery.isSuccess && (containersQuery.data?.items.length ?? 0) === 0 && (
                    <Text block className={styles.subtleText} size={200}>
                      No containers found for the selected database.
                    </Text>
                  )}
                </div>
              </>
            )}
          </div>
        ) : (
          <ToolbarButton disabled icon={<ArrowSyncRegular />}>
            Re-scope <span className={styles.scopeLabel}>{scopeLabel}</span>
          </ToolbarButton>
        )}
      </Toolbar>

      <Dialog
        open={isCreateDatabaseOpen}
        onOpenChange={(_, data) => {
          setIsCreateDatabaseOpen(data.open)
          if (!data.open) {
            resetCreateDatabaseDialog()
          }
        }}
      >
        <DialogSurface>
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
                  <InlineStatus title="Create failed">{toErrorMessage(createDatabaseMutation.error)}</InlineStatus>
                )}
              </div>
            </DialogContent>
            <DialogActions>
              <Button
                appearance="secondary"
                onClick={() => {
                  resetCreateDatabaseDialog()
                  setIsCreateDatabaseOpen(false)
                }}
              >
                Cancel
              </Button>
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

      <Dialog
        open={isCreateContainerOpen}
        onOpenChange={(_, data) => {
          setIsCreateContainerOpen(data.open)
          if (!data.open) {
            resetCreateContainerDialog()
          }
        }}
      >
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Create container</DialogTitle>
            <DialogContent>
              <div className={styles.dialogFields}>
                <Field label="Database id">
                  <Input readOnly value={selection.dbId ?? ''} />
                </Field>
                <Field label="Container id">
                  <Input
                    autoFocus
                    onChange={(_, data) => setContainerId(data.value)}
                    placeholder={selection.dbId ? `Container id for ${selection.dbId}` : 'Select a database'}
                    value={containerId}
                  />
                </Field>
                <Field label="Partition key path">
                  <Input onChange={(_, data) => setPartitionKeyPath(data.value)} placeholder="/id" value={partitionKeyPath} />
                </Field>
                {createContainerMutation.isError && (
                  <InlineStatus title="Create failed">{toErrorMessage(createContainerMutation.error)}</InlineStatus>
                )}
              </div>
            </DialogContent>
            <DialogActions>
              <Button
                appearance="secondary"
                onClick={() => {
                  resetCreateContainerDialog()
                  setIsCreateContainerOpen(false)
                }}
              >
                Cancel
              </Button>
              <Button
                appearance="primary"
                disabled={!selection.dbId || !containerId.trim() || createContainerMutation.isPending}
                onClick={submitCreateContainer}
              >
                {createContainerMutation.isPending ? 'Creating…' : 'Create'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </>
  )
}

function DatabaseLanding() {
  const { dbId } = useParams<{ dbId: string }>()

  if (!dbId) {
    return <Navigate replace to="/" />
  }

  return (
    <WorkspacePanel dbId={dbId} subtitle="Database selected" title={dbId}>
      <WorkspaceMessage
        description="Pick a container from the tree to browse documents or run SQL queries."
        title="Choose a container"
      />
    </WorkspacePanel>
  )
}

function ContainerView() {
  const styles = useStyles()
  const navigate = useNavigate()
  const { collId, dbId } = useParams<{ dbId: string; collId: string }>()
  const location = useLocation()
  const queryExecuteRef = useContext(QueryExecuteContext)

  if (!dbId || !collId) {
    return <Navigate replace to="/" />
  }

  const activeTab = parseSelection(location.pathname).section ?? 'query'
  const basePath = `/db/${encodeURIComponent(dbId)}/container/${encodeURIComponent(collId)}`

  return (
    <WorkspacePanel dbId={dbId} subtitle="Container" title={collId}>
      <div className={styles.tabbedContent}>
        <TabList
          selectedValue={activeTab}
          onTabSelect={(_, data) => navigate(`${basePath}/${String(data.value)}`)}
        >
          <Tab value="query">Query</Tab>
          <Tab value="sprocs">Stored Procedures</Tab>
          <Tab value="triggers">Triggers</Tab>
          <Tab value="udfs">UDFs</Tab>
        </TabList>

        <div className={styles.tabPanel}>
          {activeTab === 'query' && (
            <QueryEditor collId={collId} dbId={dbId} executeRef={queryExecuteRef ?? undefined} />
          )}
          {activeTab === 'sprocs' && (
            <ProgrammabilityEditor
              collId={collId}
              dbId={dbId}
              key={`${dbId}:${collId}:sprocs`}
              label="Stored procedures"
              resourceType="sprocs"
            />
          )}
          {activeTab === 'triggers' && (
            <ProgrammabilityEditor
              collId={collId}
              dbId={dbId}
              key={`${dbId}:${collId}:triggers`}
              label="Triggers"
              resourceType="triggers"
            />
          )}
          {activeTab === 'udfs' && (
            <ProgrammabilityEditor
              collId={collId}
              dbId={dbId}
              key={`${dbId}:${collId}:udfs`}
              label="User-defined functions"
              resourceType="udfs"
            />
          )}
        </div>
      </div>
    </WorkspacePanel>
  )
}

function DocumentRoute() {
  const navigate = useNavigate()
  const { collId, dbId, docId } = useParams<{ dbId: string; collId: string; docId: string }>()
  const [searchParams] = useSearchParams()

  if (!dbId || !collId || !docId) {
    return <Navigate replace to="/" />
  }

  const rawPartitionKey = searchParams.get('pk')
  if (rawPartitionKey === null) {
    return (
      <WorkspacePanel dbId={dbId} subtitle="Document selected" title={docId}>
        <WorkspaceMessage
          description="The document route is missing the partition key information required to load the item. Re-open the document from the tree."
          title="Partition key required"
          tone="error"
        />
      </WorkspacePanel>
    )
  }

  return (
    <WorkspacePanel
      dbId={dbId}
      subtitle="Document selected"
      title={docId}
      toolbar={
        <Button
          appearance="secondary"
          onClick={() =>
            navigate(`/db/${encodeURIComponent(dbId)}/container/${encodeURIComponent(collId)}/query`)
          }
        >
          Open query view
        </Button>
      }
    >
      <DocumentEditor
        key={`${docId}:${rawPartitionKey}`}
        collId={collId}
        dbId={dbId}
        docId={docId}
        onDeleted={() =>
          navigate(`/db/${encodeURIComponent(dbId)}/container/${encodeURIComponent(collId)}/query`)
        }
        partitionKey={parsePartitionKey(rawPartitionKey)}
      />
    </WorkspacePanel>
  )
}

function WorkspacePanel({
  children,
  dbId,
  subtitle,
  title,
  toolbar,
}: {
  children: ReactNode
  dbId: string
  subtitle: string
  title: string
  toolbar?: ReactNode
}) {
  const styles = useStyles()

  return (
    <section className={styles.workspaceSection}>
      <div className={styles.statusBar}>
        <div className={styles.statusBarLeft}>
          <Text className={styles.subtleText} size={200}>
            {subtitle}
          </Text>
          <div className={styles.statusBarDivider} />
          <Text size={300} weight="semibold">
            {title}
          </Text>
          <div className={styles.statusBarDivider} />
          <Text className={styles.subtleText} size={200}>
            Database: {dbId}
          </Text>
        </div>
        {toolbar ? <Toolbar className={styles.toolbar}>{toolbar}</Toolbar> : null}
      </div>
      <div className={styles.workspaceContent}>{children}</div>
    </section>
  )
}

function WorkspaceMessage({
  title,
  description,
  tone = 'neutral',
}: {
  title: string
  description: string
  tone?: 'error' | 'neutral'
}) {
  const styles = useStyles()

  return (
    <Card className={`${styles.messageCard} ${tone === 'error' ? styles.errorMessage : styles.neutralMessage}`}>
      <Text as="h2" block size={700} weight="bold">
        {title}
      </Text>
      <Text block className={styles.subtleText} size={400}>
        {description}
      </Text>
    </Card>
  )
}

function InlineStatus({ children, title }: { children: ReactNode; title: string }) {
  return (
    <MessageBar intent="error" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>{title}</MessageBarTitle>
        {children}
      </MessageBarBody>
    </MessageBar>
  )
}

function parseSelection(pathname: string): ExplorerSelection {
  const segments = pathname.split('/').filter(Boolean).map(decodeURIComponent)
  if (segments[0] !== 'db') {
    return {}
  }

  const sectionSegment = segments[4]
  const section: ContainerTab | undefined =
    segments[2] === 'container' && segments[3]
      ? sectionSegment === 'sprocs' || sectionSegment === 'triggers' || sectionSegment === 'udfs'
        ? sectionSegment
        : sectionSegment === 'query' || sectionSegment === undefined
          ? 'query'
          : undefined
      : undefined

  return {
    dbId: segments[1],
    collId: segments[3],
    section,
  }
}

function buildContainerSectionPath(dbId: string, collId: string, section: ContainerTab): string {
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

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Unexpected error'
}

function parsePartitionKey(rawValue: string): unknown {
  try {
    return JSON.parse(rawValue)
  } catch {
    return rawValue
  }
}

export default App
