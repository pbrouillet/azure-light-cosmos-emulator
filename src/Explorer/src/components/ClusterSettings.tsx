import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Badge,
  Button,
  Card,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Spinner,
  Switch,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components'
import { CopyRegular, EyeOffRegular, EyeRegular, SaveRegular, WarningRegular } from '@fluentui/react-icons'
import { cosmosClient } from '../api/cosmosClient'
import { useTheme } from '../theme'
import { KqlQueryEditor } from './KqlQueryEditor'
import type { ActivityLogEntry, EmulatorConfig, EmulatorInfo, EmulatorStats } from '../types/cosmos'

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
  grid: {
    display: 'grid',
    gap: tokens.spacingHorizontalL,
    alignItems: 'start',
    '@media (min-width: 960px)': {
      gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    },
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    paddingBottom: tokens.spacingVerticalL,
    paddingLeft: tokens.spacingHorizontalL,
    paddingRight: tokens.spacingHorizontalL,
    paddingTop: tokens.spacingVerticalL,
  },
  wideCard: {
    '@media (min-width: 960px)': {
      gridColumn: '1 / -1',
    },
  },
  sectionHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  infoList: {
    display: 'grid',
    gap: tokens.spacingVerticalM,
  },
  row: {
    display: 'grid',
    gap: tokens.spacingVerticalXS,
  },
  statsRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  label: {
    color: tokens.colorNeutralForeground2,
  },
  value: {
    wordBreak: 'break-word',
  },
  codeValue: {
    fontFamily: 'Consolas, "Courier New", monospace',
    wordBreak: 'break-word',
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  fieldGrid: {
    display: 'grid',
    gap: tokens.spacingHorizontalL,
    '@media (min-width: 720px)': {
      gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    },
  },
  switchRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  buttonRow: {
    display: 'flex',
    justifyContent: 'flex-end',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  spinnerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  messageColumn: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  activityTableContainer: {
    maxHeight: '400px',
    overflow: 'auto',
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  activityTable: {
    width: '100%',
    borderCollapse: 'collapse',
  },
  activityHeaderCell: {
    position: 'sticky',
    top: '0',
    backgroundColor: tokens.colorNeutralBackground1,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    textAlign: 'left',
    borderBottom: `1px solid ${tokens.colorNeutralStroke1}`,
    whiteSpace: 'nowrap',
    fontWeight: '600',
  },
  activityCell: {
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    verticalAlign: 'top',
    whiteSpace: 'nowrap',
  },
  activityPathCell: {
    wordBreak: 'break-all',
    whiteSpace: 'normal',
  },
  activityEmpty: {
    padding: tokens.spacingVerticalXL,
    textAlign: 'center',
    color: tokens.colorNeutralForeground3,
  },
})

const copyToClipboard = (text: string) => navigator.clipboard.writeText(text)

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) {
    return '0 B'
  }

  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1)
  const value = bytes / 1024 ** exponent
  const formatted = value >= 10 || exponent === 0 ? value.toFixed(0) : value.toFixed(1)
  return `${formatted} ${units[exponent]}`
}

function formatUptime(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(totalSeconds))
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const remainingSeconds = seconds % 60

  const parts: string[] = []
  if (days > 0) {
    parts.push(`${days}d`)
  }

  if (hours > 0 || parts.length > 0) {
    parts.push(`${hours}h`)
  }

  if (minutes > 0 || parts.length > 0) {
    parts.push(`${minutes}m`)
  }

  parts.push(`${remainingSeconds}s`)
  return parts.join(' ')
}

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'An unexpected error occurred.'
}

function formatActivityTime(timestamp: string): string {
  const value = new Date(timestamp)
  if (Number.isNaN(value.getTime())) {
    return timestamp
  }

  return value.toLocaleTimeString('en-GB', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  })
}

function getMethodColor(method: string): string {
  switch ((method ?? '').toUpperCase()) {
    case 'GET':
      return tokens.colorPaletteBlueForeground2
    case 'POST':
      return tokens.colorPaletteGreenForeground1
    case 'PUT':
      return tokens.colorPaletteDarkOrangeForeground1
    case 'DELETE':
      return tokens.colorPaletteRedForeground1
    default:
      return tokens.colorNeutralForeground1
  }
}

function getStatusColor(statusCode: number): string {
  if (statusCode >= 200 && statusCode < 300) {
    return tokens.colorPaletteGreenForeground1
  }

  if (statusCode >= 400 && statusCode < 500) {
    return tokens.colorPaletteDarkOrangeForeground1
  }

  if (statusCode >= 500) {
    return tokens.colorPaletteRedForeground1
  }

  return tokens.colorNeutralForeground1
}

function InfoRow({ label, value, isCode = false }: { label: string; value: string; isCode?: boolean }) {
  const styles = useStyles()

  return (
    <div className={styles.row}>
      <Text block className={styles.label} size={200} weight="semibold">
        {label}
      </Text>
      <Text block className={isCode ? styles.codeValue : styles.value}>
        {value}
      </Text>
    </div>
  )
}

function StatBadge({ label, value }: { label: string; value: string }) {
  const styles = useStyles()

  return (
    <div className={styles.statsRow}>
      <Text className={styles.label} weight="semibold">
        {label}
      </Text>
      <Badge appearance="filled">{value}</Badge>
    </div>
  )
}

function ConnectionInfoCard({ info }: { info: EmulatorInfo }) {
  const styles = useStyles()

  return (
    <Card className={styles.card}>
      <div className={styles.sectionHeader}>
        <Text as="h2" size={500} weight="semibold">
          Connection Info
        </Text>
        <Button
          appearance="subtle"
          icon={<CopyRegular />}
          onClick={() => {
            void copyToClipboard(info.connectionString)
          }}
        >
          Copy connection string
        </Button>
      </div>

      <div className={styles.infoList}>
        <InfoRow isCode label="NoSQL endpoint" value={info.endpoints.noSql} />
        <InfoRow isCode label="MongoDB endpoint" value={info.endpoints.mongoDb} />
        <InfoRow isCode label="Explorer endpoint" value={info.endpoints.explorer} />
        <InfoRow isCode label="Connection string" value={info.connectionString} />
      </div>
    </Card>
  )
}

function AccessKeysCard({ info }: { info: EmulatorInfo }) {
  const styles = useStyles()
  const [showMasterKey, setShowMasterKey] = useState(false)

  return (
    <Card className={styles.card}>
      <div className={styles.sectionHeader}>
        <Text as="h2" size={500} weight="semibold">
          Access Keys
        </Text>
        <div className={styles.actions}>
          <Button
            appearance="subtle"
            icon={showMasterKey ? <EyeOffRegular /> : <EyeRegular />}
            onClick={() => setShowMasterKey((current) => !current)}
          >
            {showMasterKey ? 'Hide key' : 'Show key'}
          </Button>
          <Button
            appearance="subtle"
            icon={<CopyRegular />}
            onClick={() => {
              void copyToClipboard(info.masterKey)
            }}
          >
            Copy key
          </Button>
        </div>
      </div>

      <div className={styles.infoList}>
        <InfoRow
          isCode
          label="Master key"
          value={showMasterKey ? info.masterKey : '•'.repeat(Math.min(info.masterKey.length, 64))}
        />
      </div>
    </Card>
  )
}

function ClusterStatsCard({ stats }: { stats: EmulatorStats }) {
  const styles = useStyles()

  return (
    <Card className={styles.card}>
      <Text as="h2" size={500} weight="semibold">
        Cluster Stats
      </Text>

      <div className={styles.infoList}>
        <StatBadge label="Total RU" value={stats.totalRequestUnits.toLocaleString()} />
        <StatBadge label="Total requests" value={stats.totalRequests.toLocaleString()} />
        <StatBadge label="Database count" value={stats.databaseCount.toLocaleString()} />
        <StatBadge label="Container count" value={stats.containerCount.toLocaleString()} />
        <StatBadge label="Document count" value={stats.documentCount.toLocaleString()} />
        <StatBadge label="Data size" value={formatBytes(stats.dataSizeBytes)} />
        <StatBadge label="Data path" value={stats.dataDirectory} />
        <StatBadge label="Uptime" value={formatUptime(stats.uptimeSeconds)} />
      </div>
    </Card>
  )
}

function ConfigurationCard({ info }: { info: EmulatorInfo }) {
  const styles = useStyles()

  return (
    <Card className={styles.card}>
      <Text as="h2" size={500} weight="semibold">
        Configuration
      </Text>

      <div className={styles.infoList}>
        <InfoRow label="Port" value={info.configuration.port.toString()} />
        <InfoRow label="MongoDB port" value={info.configuration.mongoPort.toString()} />
        <InfoRow label="Storage backend" value={info.configuration.storage ?? 'Sqlite'} />
        <InfoRow label="Consistency level" value={info.configuration.consistencyLevel} />
        <InfoRow label="SSL enabled" value={info.configuration.enableSsl ? 'Yes' : 'No'} />
        <InfoRow label="Explorer enabled" value={info.configuration.enableExplorer ? 'Yes' : 'No'} />
        <InfoRow label="Data directory" value={info.configuration.dataDirectory} />
      </div>
    </Card>
  )
}

const storageOptions = ['Sqlite', 'SurrealDb', 'InMemory'] as const

function StorageConfigCard() {
  const styles = useStyles()
  const queryClient = useQueryClient()
  const [restartNeeded, setRestartNeeded] = useState(false)

  const configQuery = useQuery({
    queryKey: ['emulatorConfig'],
    queryFn: () => cosmosClient.getEmulatorConfig(),
  })

  const [draft, setDraft] = useState<{ storage: string; dataDirectory: string } | null>(null)

  const config = configQuery.data
  const current = draft ?? {
    storage: config?.storage ?? 'Sqlite',
    dataDirectory: config?.dataDirectory ?? '',
  }

  const hasChanges = useMemo(() => {
    if (!config) return false
    return current.storage !== config.storage || current.dataDirectory !== config.dataDirectory
  }, [config, current])

  const saveMutation = useMutation({
    mutationFn: () => cosmosClient.updateEmulatorConfig({
      storage: current.storage,
      dataDirectory: current.dataDirectory,
    }),
    onSuccess: (result: EmulatorConfig) => {
      setDraft(null)
      setRestartNeeded(result.restartRequired)
      queryClient.setQueryData(['emulatorConfig'], result)
      void queryClient.invalidateQueries({ queryKey: ['emulatorInfo'] })
    },
  })

  return (
    <Card className={`${styles.card} ${styles.wideCard}`}>
      <Text as="h2" size={500} weight="semibold">
        Storage Configuration
      </Text>

      {restartNeeded && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Restart required</MessageBarTitle>
            Storage settings have been saved to <code>emulator-config.json</code>. Restart the emulator for changes to take effect.
          </MessageBarBody>
        </MessageBar>
      )}

      {saveMutation.isError && (
        <MessageBar intent="error">
          <MessageBarBody>
            <MessageBarTitle>Save failed</MessageBarTitle>
            {saveMutation.error instanceof Error ? saveMutation.error.message : 'An unexpected error occurred.'}
          </MessageBarBody>
        </MessageBar>
      )}

      {configQuery.isPending ? (
        <div className={styles.spinnerRow}>
          <Spinner size="tiny" />
          <Text>Loading storage config…</Text>
        </div>
      ) : (
        <>
          <div className={styles.fieldGrid}>
            <Field label="Storage backend" hint="Engine used to persist emulator data.">
              <Select
                value={current.storage}
                onChange={(_, data) =>
                  setDraft((prev) => ({
                    ...(prev ?? { storage: config?.storage ?? 'SurrealDb', dataDirectory: config?.dataDirectory ?? '' }),
                    storage: data.value,
                  }))
                }
              >
                {storageOptions.map((opt) => (
                  <option key={opt} value={opt}>
                    {opt === 'SurrealDb' ? 'SurrealDb (RocksDB)' : opt === 'Sqlite' ? 'SQLite' : 'In-Memory (ephemeral)'}
                  </option>
                ))}
              </Select>
            </Field>

            <Field label="Data directory" hint="Disk path for persistent storage.">
              <Input
                placeholder="Leave empty for default"
                value={current.dataDirectory}
                onChange={(e) =>
                  setDraft((prev) => ({
                    ...(prev ?? { storage: config?.storage ?? 'SurrealDb', dataDirectory: config?.dataDirectory ?? '' }),
                    dataDirectory: e.target.value,
                  }))
                }
              />
            </Field>
          </div>

          <div className={styles.buttonRow}>
            {hasChanges && (
              <Text className={styles.subtleText} size={200}>
                <WarningRegular /> Changes require an emulator restart
              </Text>
            )}
            <Button
              appearance="primary"
              disabled={!hasChanges || saveMutation.isPending}
              icon={<SaveRegular />}
              onClick={() => saveMutation.mutate()}
            >
              {saveMutation.isPending ? 'Saving…' : 'Save'}
            </Button>
          </div>
        </>
      )}
    </Card>
  )
}

function RecentActivityCard({ activity }: { activity: ActivityLogEntry[] }) {
  const styles = useStyles()

  return (
    <Card className={`${styles.card} ${styles.wideCard}`}>
      <Text as="h2" size={500} weight="semibold">
        Recent Activity
      </Text>

      {activity.length === 0 ? (
        <div className={styles.activityEmpty}>No activity recorded yet</div>
      ) : (
        <div className={styles.activityTableContainer}>
          <table className={styles.activityTable}>
            <thead>
              <tr>
                <th className={styles.activityHeaderCell}>Time</th>
                <th className={styles.activityHeaderCell}>Method</th>
                <th className={styles.activityHeaderCell}>Path</th>
                <th className={styles.activityHeaderCell}>Status</th>
                <th className={styles.activityHeaderCell}>RU</th>
                <th className={styles.activityHeaderCell}>Latency (ms)</th>
              </tr>
            </thead>
            <tbody>
              {activity.map((entry) => (
                <tr key={`${entry.timestamp}-${entry.method}-${entry.path}`}>
                  <td className={styles.activityCell}>{formatActivityTime(entry.timestamp)}</td>
                  <td className={styles.activityCell} style={{ color: getMethodColor(entry.method) }}>
                    {entry.method}
                  </td>
                  <td className={`${styles.activityCell} ${styles.activityPathCell}`}>{entry.path}</td>
                  <td className={styles.activityCell} style={{ color: getStatusColor(entry.statusCode) }}>
                    {entry.statusCode}
                  </td>
                  <td className={styles.activityCell}>{entry.requestCharge.toFixed(2)}</td>
                  <td className={styles.activityCell}>{entry.latencyMs.toFixed(2)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  )
}

export function ClusterSettings() {
  const styles = useStyles()
  useTheme()
  const queryClient = useQueryClient()
  const [settingsDraft, setSettingsDraft] = useState<{
    enableEntraId: boolean
    tenantId: string
    clientId: string
  } | null>(null)
  const [saveMessage, setSaveMessage] = useState<string | null>(null)

  const emulatorInfoQuery = useQuery({
    queryKey: ['emulatorInfo'],
    queryFn: () => cosmosClient.getEmulatorInfo(),
  })

  const emulatorStatsQuery = useQuery({
    queryKey: ['emulatorStats'],
    queryFn: () => cosmosClient.getEmulatorStats(),
    refetchInterval: 10000,
  })

  const emulatorActivityQuery = useQuery({
    queryKey: ['emulatorActivity'],
    queryFn: () => cosmosClient.getEmulatorActivity(),
    refetchInterval: 5000,
  })

  const emulatorInfo = emulatorInfoQuery.data
  const emulatorStats = emulatorStatsQuery.data
  const currentSettings = settingsDraft ?? createSettingsDraft(emulatorInfo)

  const normalizedSettings = useMemo(
    () => ({
      enableEntraId: currentSettings.enableEntraId,
      tenantId: currentSettings.tenantId.trim() || null,
      clientId: currentSettings.clientId.trim() || null,
    }),
    [currentSettings],
  )

  const hasChanges = useMemo(() => {
    if (!emulatorInfo) {
      return false
    }

    return (
      normalizedSettings.enableEntraId !== emulatorInfo.configuration.enableEntraId ||
      normalizedSettings.tenantId !== emulatorInfo.configuration.tenantId ||
      normalizedSettings.clientId !== emulatorInfo.configuration.clientId
    )
  }, [emulatorInfo, normalizedSettings])

  const saveSettingsMutation = useMutation({
    mutationFn: () => cosmosClient.updateEmulatorSettings(normalizedSettings),
    onSuccess: (updatedInfo) => {
      setSaveMessage('Entra ID settings saved successfully.')
      setSettingsDraft(null)
      queryClient.setQueryData(['emulatorInfo'], updatedInfo)
    },
  })

  const errorMessages = [
    emulatorInfoQuery.isError ? toErrorMessage(emulatorInfoQuery.error) : null,
    emulatorStatsQuery.isError ? toErrorMessage(emulatorStatsQuery.error) : null,
    emulatorActivityQuery.isError ? toErrorMessage(emulatorActivityQuery.error) : null,
    saveSettingsMutation.isError ? toErrorMessage(saveSettingsMutation.error) : null,
  ].filter((message): message is string => Boolean(message))

  if (emulatorInfoQuery.isPending && emulatorStatsQuery.isPending) {
    return (
      <div className={styles.spinnerRow}>
        <Spinner />
        <Text>Loading cluster settings…</Text>
      </div>
    )
  }

  return (
    <section className={styles.root}>
      <div className={styles.header}>
        <div className={styles.stack}>
          <Text as="h1" size={700} weight="bold">
            Cluster Settings
          </Text>
          <Text block className={styles.subtleText}>
            {emulatorInfo ? `${emulatorInfo.name} · v${emulatorInfo.version}` : 'Review emulator connection details, usage statistics, and authentication settings.'}
          </Text>
        </div>
      </div>

      {errorMessages.length > 0 && (
        <div className={styles.messageColumn}>
          {errorMessages.map((message) => (
            <MessageBar intent="error" key={message} layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Cluster settings error</MessageBarTitle>
                {message}
              </MessageBarBody>
            </MessageBar>
          ))}
        </div>
      )}

      {saveMessage && !saveSettingsMutation.isError && (
        <MessageBar intent="success">
          <MessageBarBody>
            <MessageBarTitle>Saved</MessageBarTitle>
            {saveMessage}
          </MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.grid}>
        {emulatorInfo ? <ConnectionInfoCard info={emulatorInfo} /> : <LoadingCard title="Connection Info" />}
        {emulatorInfo ? <AccessKeysCard info={emulatorInfo} /> : <LoadingCard title="Access Keys" />}
        {emulatorStats ? <ClusterStatsCard stats={emulatorStats} /> : <LoadingCard title="Cluster Stats" />}
        {emulatorInfo ? <ConfigurationCard info={emulatorInfo} /> : <LoadingCard title="Configuration" />}

        <StorageConfigCard />

        <Card className={`${styles.card} ${styles.wideCard}`}>
          <Text as="h2" size={500} weight="semibold">
            Entra ID Authentication
          </Text>

          <div className={styles.switchRow}>
            <Switch
              checked={currentSettings.enableEntraId}
              label="Enable Entra ID authentication"
              onChange={(_, data) =>
                setSettingsDraft((current) => ({
                  ...(current ?? createSettingsDraft(emulatorInfo)),
                  enableEntraId: data.checked,
                }))
              }
            />
            <Text className={styles.subtleText}>
              Configure the tenant and client application used by the emulator explorer.
            </Text>
          </div>

          <div className={styles.fieldGrid}>
            <Field label="Tenant ID">
              <Input
                placeholder="Contoso tenant ID"
                value={currentSettings.tenantId}
                onChange={(event) =>
                  setSettingsDraft((current) => ({
                    ...(current ?? createSettingsDraft(emulatorInfo)),
                    tenantId: event.target.value,
                  }))
                }
              />
            </Field>

            <Field label="Client ID">
              <Input
                placeholder="Explorer client ID"
                value={currentSettings.clientId}
                onChange={(event) =>
                  setSettingsDraft((current) => ({
                    ...(current ?? createSettingsDraft(emulatorInfo)),
                    clientId: event.target.value,
                  }))
                }
              />
            </Field>
          </div>

          <div className={styles.buttonRow}>
            <Button
              appearance="primary"
              disabled={!emulatorInfo || !hasChanges || saveSettingsMutation.isPending}
              icon={<SaveRegular />}
              onClick={() => {
                setSaveMessage(null)
                saveSettingsMutation.mutate()
              }}
            >
              {saveSettingsMutation.isPending ? 'Saving…' : 'Save'}
            </Button>
          </div>
        </Card>

        {emulatorActivityQuery.data ? (
          <RecentActivityCard activity={emulatorActivityQuery.data} />
        ) : (
          <LoadingCard title="Recent Activity" />
        )}

        <KqlQueryEditor />
      </div>
    </section>
  )
}

function LoadingCard({ title }: { title: string }) {
  const styles = useStyles()

  return (
    <Card className={styles.card}>
      <Text as="h2" size={500} weight="semibold">
        {title}
      </Text>
      <div className={styles.spinnerRow}>
        <Spinner size="tiny" />
        <Text>Loading…</Text>
      </div>
    </Card>
  )
}

function createSettingsDraft(info?: EmulatorInfo | null) {
  return {
    enableEntraId: info?.configuration.enableEntraId ?? false,
    tenantId: info?.configuration.tenantId ?? '',
    clientId: info?.configuration.clientId ?? '',
  }
}
