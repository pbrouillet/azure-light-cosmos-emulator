import { useCallback, useEffect, useRef, useState } from 'react'
import type { MutableRefObject, ReactNode } from 'react'
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
  Popover,
  PopoverSurface,
  PopoverTrigger,
  makeStyles,
  Text,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
  tokens,
} from '@fluentui/react-components'
import {
  ArrowSyncRegular,
  DatabaseRegular,
  DismissRegular,
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
type WorkspaceTabType = 'query' | 'container' | 'document'

interface ExplorerSelection {
  dbId?: string
  collId?: string
  docId?: string
  section?: ContainerTab
}

interface WorkspaceTab {
  id: string
  type: WorkspaceTabType
  label: string
  dbId: string
  collId: string
  section?: ContainerTab
  docId?: string
  partitionKey?: unknown
}

interface OpenTabOptions {
  replace?: boolean
  syncLocation?: boolean
}

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
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    overflow: 'hidden',
    paddingBottom: tokens.spacingVerticalXL,
    paddingLeft: tokens.spacingHorizontalXL,
    paddingRight: tokens.spacingHorizontalXL,
    paddingTop: tokens.spacingVerticalXL,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  tabBar: {
    display: 'flex',
    alignItems: 'center',
    gap: '1px',
    overflowX: 'auto',
    flexShrink: 0,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: `${tokens.borderRadiusMedium} ${tokens.borderRadiusMedium} 0 0`,
  },
  workspaceTab: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalXS,
    paddingTop: tokens.spacingVerticalS,
    paddingBottom: tokens.spacingVerticalS,
    cursor: 'pointer',
    backgroundColor: tokens.colorNeutralBackground3,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    whiteSpace: 'nowrap',
    maxWidth: '200px',
    flexShrink: 0,
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  workspaceTabActive: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `2px solid ${tokens.colorBrandStroke1}`,
  },
  tabCloseButton: {
    minWidth: 'auto',
    padding: '2px',
  },
  tabContent: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
    overflow: 'hidden',
  },
  hiddenTabPanel: {
    display: 'none',
  },
  visibleTabPanel: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
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
})

function App() {
  const styles = useStyles()
  const location = useLocation()
  const navigate = useNavigate()
  const { isDark, toggle } = useTheme()
  const selection = parseSelection(location.pathname)
  const queryCounter = useRef(0)
  const tabsRef = useRef<WorkspaceTab[]>([])
  const queryExecuteRefs = useRef<Map<string, QueryEditorHandle>>(new Map())

  const [sidebarWidth, setSidebarWidth] = useState(384) // 24rem
  const [isDragging, setIsDragging] = useState(false)
  const [activeView, setActiveView] = useState<'explorer' | 'settings'>('explorer')
  const [tabs, setTabs] = useState<WorkspaceTab[]>([])
  const [activeTabId, setActiveTabId] = useState<string | null>(null)

  const activeTab = tabs.find((tab) => tab.id === activeTabId) ?? null
  const activeQueryTab = activeTab?.type === 'query' ? activeTab : null

  const startResize = useCallback((e: React.MouseEvent) => {
    e.preventDefault()
    setIsDragging(true)
  }, [])

  const navigateToTab = useCallback(
    (tab: WorkspaceTab, replace = false) => {
      navigate(buildWorkspaceTabPath(tab), { replace })
    },
    [navigate],
  )

  const openTab = useCallback(
    (tab: WorkspaceTab, options?: OpenTabOptions) => {
      const existing = tabsRef.current.find((candidate) => candidate.id === tab.id)
      const nextTab = existing ?? tab

      if (!existing) {
        const nextTabs = [...tabsRef.current, tab]
        tabsRef.current = nextTabs
        setTabs(nextTabs)
      }

      setActiveTabId(nextTab.id)

      if (options?.syncLocation) {
        navigateToTab(nextTab, options.replace)
      }

      return nextTab
    },
    [navigateToTab],
  )

  const openNewQuery = useCallback(
    (dbId: string, collId: string, options?: OpenTabOptions & { tabId?: string }) => {
      const nextTab = createQueryWorkspaceTab(queryCounter, dbId, collId, options?.tabId)
      return openTab(nextTab, options)
    },
    [openTab],
  )

  const closeTab = useCallback(
    (tabId: string) => {
      const currentTabs = tabsRef.current
      const closingIndex = currentTabs.findIndex((tab) => tab.id === tabId)
      if (closingIndex === -1) {
        return
      }

      const filteredTabs = currentTabs.filter((tab) => tab.id !== tabId)
      tabsRef.current = filteredTabs
      setTabs(filteredTabs)
      queryExecuteRefs.current.delete(tabId)

      const wasActive = activeTabId === tabId
      const nextActiveId = wasActive
        ? filteredTabs.length > 0
          ? filteredTabs[filteredTabs.length - 1].id
          : null
        : activeTabId

      setActiveTabId(nextActiveId)

      if (wasActive) {
        const nextActiveTab = filteredTabs.find((tab) => tab.id === nextActiveId) ?? null
        if (nextActiveTab) {
          navigateToTab(nextActiveTab, true)
        } else {
          navigate(buildWorkspaceLandingPath(currentTabs[closingIndex].dbId), { replace: true })
        }
      }
    },
    [activeTabId, navigate, navigateToTab],
  )

  const registerQueryExecuteRef = useCallback((tabId: string, handle: QueryEditorHandle | null) => {
    if (handle) {
      queryExecuteRefs.current.set(tabId, handle)
      return
    }

    queryExecuteRefs.current.delete(tabId)
  }, [])

  const executeActiveQuery = useCallback(() => {
    if (!activeQueryTab) {
      return
    }

    queryExecuteRefs.current.get(activeQueryTab.id)?.execute()
  }, [activeQueryTab])

  useEffect(() => {
    tabsRef.current = tabs
  }, [tabs])

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

  useEffect(() => {
    const routeSelection = parseSelection(location.pathname)
    if (!routeSelection.dbId || !routeSelection.collId) {
      return
    }

    const searchParams = new URLSearchParams(location.search)
    const requestedTabId = searchParams.get('tab') ?? undefined

    if (routeSelection.docId) {
      const rawPartitionKey = searchParams.get('pk')
      if (rawPartitionKey === null) {
        return
      }

      const openedTab = openTab({
        id: requestedTabId ?? buildDocumentTabId(routeSelection.dbId, routeSelection.collId, routeSelection.docId, rawPartitionKey),
        type: 'document',
        label: routeSelection.docId,
        dbId: routeSelection.dbId,
        collId: routeSelection.collId,
        docId: routeSelection.docId,
        partitionKey: parsePartitionKey(rawPartitionKey),
      })

      if (!requestedTabId) {
        navigateToTab(openedTab, true)
      }

      return
    }

    if (!routeSelection.section) {
      return
    }

    if (routeSelection.section === 'query') {
      const existingQueryTab = requestedTabId
        ? tabsRef.current.find((tab) => tab.id === requestedTabId && tab.type === 'query')
        : undefined

      if (existingQueryTab) {
        openTab(existingQueryTab)
        return
      }

      const openedTab = openNewQuery(routeSelection.dbId, routeSelection.collId, { tabId: requestedTabId })
      if (!requestedTabId) {
        navigateToTab(openedTab, true)
      }

      return
    }

    const openedTab = openTab({
      id: requestedTabId ?? buildContainerTabId(routeSelection.dbId, routeSelection.collId, routeSelection.section),
      type: 'container',
      label: `${routeSelection.collId} – ${sectionLabel(routeSelection.section)}`,
      dbId: routeSelection.dbId,
      collId: routeSelection.collId,
      section: routeSelection.section,
    })

    if (!requestedTabId) {
      navigateToTab(openedTab, true)
    }
  }, [location.key, location.pathname, location.search, navigateToTab, openNewQuery, openTab])

  const renderTabContent = (tab: WorkspaceTab) => {
    if (tab.type === 'query') {
      return (
        <WorkspacePanel dbId={tab.dbId} subtitle={`Container: ${tab.collId}`} title={tab.label}>
          <QueryEditor
            key={tab.id}
            collId={tab.collId}
            dbId={tab.dbId}
            executeRef={(handle) => registerQueryExecuteRef(tab.id, handle)}
          />
        </WorkspacePanel>
      )
    }

    if (tab.type === 'container') {
      const section: Exclude<ContainerTab, 'query'> =
        tab.section === 'triggers' || tab.section === 'udfs' ? tab.section : 'sprocs'
      const label = sectionEditorLabel(section)

      return (
        <WorkspacePanel dbId={tab.dbId} subtitle={`Container: ${tab.collId}`} title={tab.label}>
          <ProgrammabilityEditor
            key={tab.id}
            collId={tab.collId}
            dbId={tab.dbId}
            label={label}
            resourceType={section}
          />
        </WorkspacePanel>
      )
    }

    if (tab.type === 'document' && tab.docId) {
      return (
        <WorkspacePanel
          dbId={tab.dbId}
          subtitle={`Document · ${tab.collId}`}
          title={tab.label}
          toolbar={
            <Button appearance="secondary" onClick={() => openNewQuery(tab.dbId, tab.collId, { syncLocation: true })}>
              Open query view
            </Button>
          }
        >
          <DocumentEditor
            key={tab.id}
            collId={tab.collId}
            dbId={tab.dbId}
            docId={tab.docId}
            onDeleted={() => closeTab(tab.id)}
            partitionKey={tab.partitionKey}
          />
        </WorkspacePanel>
      )
    }

    return null
  }

  return (
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

      <GlobalToolbar
        activeQueryTab={activeQueryTab}
        onExecuteQuery={executeActiveQuery}
        onOpenNewQuery={(dbId, collId) => {
          openNewQuery(dbId, collId, { syncLocation: true })
        }}
        selection={selection}
      />

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
              {tabs.length > 0 ? (
                <>
                  <div className={styles.tabBar}>
                    {tabs.map((tab) => (
                      <div
                        className={`${styles.workspaceTab} ${tab.id === activeTabId ? styles.workspaceTabActive : ''}`}
                        key={tab.id}
                        onClick={() => {
                          setActiveTabId(tab.id)
                          navigateToTab(tab)
                        }}
                      >
                        <Text size={200} truncate>
                          {tab.label}
                        </Text>
                        <Button
                          appearance="transparent"
                          aria-label={`Close ${tab.label}`}
                          className={styles.tabCloseButton}
                          icon={<DismissRegular />}
                          onClick={(event) => {
                            event.stopPropagation()
                            closeTab(tab.id)
                          }}
                          size="small"
                        />
                      </div>
                    ))}
                  </div>
                  <div className={styles.tabContent}>
                    {tabs.map((tab) => (
                      <div
                        className={tab.id === activeTabId ? styles.visibleTabPanel : styles.hiddenTabPanel}
                        key={tab.id}
                      >
                        {renderTabContent(tab)}
                      </div>
                    ))}
                  </div>
                </>
              ) : (
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
                  <Route element={<WorkspaceRoutePlaceholder />} path="/db/:dbId/container/:collId" />
                  <Route element={<WorkspaceRoutePlaceholder />} path="/db/:dbId/container/:collId/query" />
                  <Route element={<WorkspaceRoutePlaceholder />} path="/db/:dbId/container/:collId/sprocs" />
                  <Route element={<WorkspaceRoutePlaceholder />} path="/db/:dbId/container/:collId/triggers" />
                  <Route element={<WorkspaceRoutePlaceholder />} path="/db/:dbId/container/:collId/udfs" />
                  <Route element={<DocumentRoutePlaceholder />} path="/db/:dbId/container/:collId/doc/:docId" />
                  <Route element={<Navigate replace to="/" />} path="*" />
                </Routes>
              )}
            </main>
          </>
        ) : (
          <main className={styles.main}>
            <ClusterSettings />
          </main>
        )}
      </div>
    </div>
  )
}

function GlobalToolbar({
  activeQueryTab,
  onExecuteQuery,
  onOpenNewQuery,
  selection,
}: {
  activeQueryTab: WorkspaceTab | null
  onExecuteQuery: () => void
  onOpenNewQuery: (dbId: string, collId: string) => void
  selection: ExplorerSelection
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
  const currentDbId = activeQueryTab?.dbId ?? selection.dbId ?? ''
  const currentCollId = activeQueryTab?.collId ?? selection.collId ?? ''
  const [scopeDbId, setScopeDbId] = useState(currentDbId)
  const [scopeCollId, setScopeCollId] = useState(currentCollId)

  const canCreateContainer = Boolean(selection.dbId)
  const canOpenQuery = Boolean(currentDbId && currentCollId)
  const isQueryView = activeQueryTab?.type === 'query'
  const scopeLabel = currentDbId && currentCollId ? `${currentDbId} / ${currentCollId}` : 'No scope'

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
      onOpenNewQuery(variables.dbId, container.id)
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
            if (currentDbId && currentCollId) {
              onOpenNewQuery(currentDbId, currentCollId)
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
          onClick={onExecuteQuery}
        >
          Execute
        </ToolbarButton>
        <ToolbarDivider />
        {isQueryView ? (
          <Popover
            onOpenChange={(_, data) => {
              setIsRescopeOpen(data.open)
              if (data.open) {
                setScopeDbId(currentDbId)
                setScopeCollId(currentCollId)
              }
            }}
            open={isRescopeOpen}
            positioning="below-start"
          >
            <PopoverTrigger disableButtonEnhancement>
              <ToolbarButton icon={<ArrowSyncRegular />}>
                Re-scope <span className={styles.scopeLabel}>{scopeLabel}</span>
              </ToolbarButton>
            </PopoverTrigger>
            <PopoverSurface className={styles.scopePopover}>
              <Text block size={300} weight="semibold">
                Select query scope
              </Text>
              <Field label="Database">
                <Combobox
                  onOptionSelect={(_, data) => {
                    const nextDbId = data.optionValue ?? ''
                    setScopeDbId(nextDbId)
                    setScopeCollId(nextDbId === currentDbId ? currentCollId : '')
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
                      onOpenNewQuery(scopeDbId, nextCollId)
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
            </PopoverSurface>
          </Popover>
        ) : (
          <ToolbarButton disabled icon={<ArrowSyncRegular />}>
            Re-scope <span className={styles.scopeLabel}>{scopeLabel}</span>
          </ToolbarButton>
        )}
      </Toolbar>

      {isCreateDatabaseOpen && (
        <Dialog
          modalType="non-modal"
          open
          onOpenChange={(_, data) => {
            if (!data.open) {
              resetCreateDatabaseDialog()
              setIsCreateDatabaseOpen(false)
            }
          }}
        >
          <DialogSurface backdrop={{ onClick: () => { resetCreateDatabaseDialog(); setIsCreateDatabaseOpen(false) } }}>
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
      )}

      {isCreateContainerOpen && (
        <Dialog
          modalType="non-modal"
          open
          onOpenChange={(_, data) => {
            if (!data.open) {
              resetCreateContainerDialog()
              setIsCreateContainerOpen(false)
            }
          }}
        >
          <DialogSurface backdrop={{ onClick: () => { resetCreateContainerDialog(); setIsCreateContainerOpen(false) } }}>
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
      )}
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

function WorkspaceRoutePlaceholder() {
  return null
}

function DocumentRoutePlaceholder() {
  const { collId, dbId, docId } = useParams<{ dbId: string; collId: string; docId: string }>()
  const [searchParams] = useSearchParams()

  if (!dbId || !collId || !docId) {
    return <Navigate replace to="/" />
  }

  if (searchParams.get('pk') !== null) {
    return null
  }

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

  const isContainerRoute = segments[2] === 'container' && Boolean(segments[3])
  const sectionSegment = segments[4]
  const docId = sectionSegment === 'doc' ? segments[5] : undefined
  const section: ContainerTab | undefined =
    isContainerRoute && sectionSegment !== 'doc'
      ? sectionSegment === 'sprocs' || sectionSegment === 'triggers' || sectionSegment === 'udfs'
        ? sectionSegment
        : sectionSegment === 'query' || sectionSegment === undefined
          ? 'query'
          : undefined
      : undefined

  return {
    dbId: segments[1],
    collId: segments[3],
    docId,
    section,
  }
}

function createQueryWorkspaceTab(
  queryCounter: MutableRefObject<number>,
  dbId: string,
  collId: string,
  requestedId?: string,
): WorkspaceTab {
  if (requestedId) {
    const match = /^query-(\d+)$/u.exec(requestedId)
    if (match) {
      const queryNumber = Number(match[1])
      queryCounter.current = Math.max(queryCounter.current, queryNumber)
      return {
        id: requestedId,
        type: 'query',
        label: `Query ${queryNumber}`,
        dbId,
        collId,
        section: 'query',
      }
    }

    return {
      id: requestedId,
      type: 'query',
      label: 'Query',
      dbId,
      collId,
      section: 'query',
    }
  }

  queryCounter.current += 1

  return {
    id: `query-${queryCounter.current}`,
    type: 'query',
    label: `Query ${queryCounter.current}`,
    dbId,
    collId,
    section: 'query',
  }
}

function buildWorkspaceLandingPath(dbId?: string): string {
  return dbId ? `/db/${encodeURIComponent(dbId)}` : '/'
}

function buildContainerTabId(dbId: string, collId: string, section: Exclude<ContainerTab, 'query'>): string {
  return `${section}-${dbId}-${collId}`
}

function buildDocumentTabId(dbId: string, collId: string, docId: string, rawPartitionKey: string): string {
  return `doc-${dbId}-${collId}-${docId}-${rawPartitionKey}`
}

function buildWorkspaceTabPath(tab: WorkspaceTab): string {
  const searchParams = new URLSearchParams()
  searchParams.set('tab', tab.id)

  if (tab.type === 'document' && tab.docId) {
    if (tab.partitionKey !== undefined) {
      searchParams.set('pk', JSON.stringify(tab.partitionKey))
    }

    return `/db/${encodeURIComponent(tab.dbId)}/container/${encodeURIComponent(tab.collId)}/doc/${encodeURIComponent(tab.docId)}?${searchParams.toString()}`
  }

  if (tab.type === 'container') {
    return `${buildContainerSectionPath(tab.dbId, tab.collId, tab.section ?? 'sprocs')}?${searchParams.toString()}`
  }

  return `${buildContainerSectionPath(tab.dbId, tab.collId, 'query')}?${searchParams.toString()}`
}

function sectionLabel(section: Exclude<ContainerTab, 'query'>): string {
  switch (section) {
    case 'sprocs':
      return 'Sprocs'
    case 'triggers':
      return 'Triggers'
    case 'udfs':
      return 'UDFs'
  }
}

function sectionEditorLabel(section: Exclude<ContainerTab, 'query'>): 'Stored procedures' | 'Triggers' | 'User-defined functions' {
  switch (section) {
    case 'sprocs':
      return 'Stored procedures'
    case 'triggers':
      return 'Triggers'
    case 'udfs':
      return 'User-defined functions'
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
