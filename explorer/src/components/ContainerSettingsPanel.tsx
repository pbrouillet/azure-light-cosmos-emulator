import { useState } from 'react'
import Editor from '@monaco-editor/react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Badge,
  Body1,
  Button,
  Card,
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
import { SaveRegular } from '@fluentui/react-icons'
import { cosmosClient } from '../api/cosmosClient'
import { useTheme } from '../theme'

interface ContainerSettingsPanelProps {
  dbId: string
  collId: string
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    overflowY: 'auto',
    paddingBottom: tokens.spacingVerticalL,
  },
  spinnerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    flexShrink: 0,
    gap: tokens.spacingVerticalM,
    paddingBottom: tokens.spacingVerticalL,
    paddingLeft: tokens.spacingHorizontalL,
    paddingRight: tokens.spacingHorizontalL,
    paddingTop: tokens.spacingVerticalL,
  },
  metadataList: {
    display: 'grid',
    gap: tokens.spacingVerticalS,
  },
  editorFrame: {
    height: '280px',
    flexShrink: 0,
    overflow: 'hidden',
    borderRadius: tokens.borderRadiusMedium,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
})

export function ContainerSettingsPanel({ dbId, collId }: ContainerSettingsPanelProps) {
  const styles = useStyles()
  const { isDark } = useTheme()
  const queryClient = useQueryClient()

  const containerQuery = useQuery({
    queryKey: ['container', dbId, collId],
    queryFn: () => cosmosClient.getContainer(dbId, collId),
  })

  const container = containerQuery.data

  const [indexingPolicyValue, setIndexingPolicyValue] = useState('')
  const [defaultTtl, setDefaultTtl] = useState('')
  const [maxThroughput, setMaxThroughput] = useState('')
  const [saveMessage, setSaveMessage] = useState<string | null>(null)

  // Sync local state when container data changes (key-based tracking pattern)
  const [containerKey, setContainerKey] = useState<string | null>(null)
  const currentContainerKey = container ? `${container.id}-${container._etag ?? ''}` : null
  if (currentContainerKey !== containerKey) {
    setContainerKey(currentContainerKey)
    if (container) {
      setIndexingPolicyValue(
        container.indexingPolicy != null
          ? JSON.stringify(container.indexingPolicy, null, 2)
          : '{}',
      )
      setDefaultTtl(container.defaultTtl != null ? String(container.defaultTtl) : '')
      setMaxThroughput(container.maxThroughput != null ? String(container.maxThroughput) : '')
    }
    setSaveMessage(null)
  }

  const saveMutation = useMutation({
    mutationFn: async () => {
      let parsedIndexingPolicy: unknown
      try {
        parsedIndexingPolicy = JSON.parse(indexingPolicyValue) as unknown
      } catch {
        throw new Error('Indexing policy is not valid JSON.')
      }

      const parsedDefaultTtl = defaultTtl === '' ? null : Number(defaultTtl)
      const parsedMaxThroughput = maxThroughput === '' ? null : Number(maxThroughput)

      return cosmosClient.replaceContainer(dbId, collId, {
        indexingPolicy: parsedIndexingPolicy,
        defaultTtl: parsedDefaultTtl,
        maxThroughput: parsedMaxThroughput,
      })
    },
    onSuccess: async () => {
      setSaveMessage('Container settings saved successfully.')
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['container', dbId, collId] }),
        queryClient.invalidateQueries({ queryKey: ['containers', dbId] }),
      ])
    },
  })

  if (containerQuery.isPending) {
    return (
      <div className={styles.spinnerRow}>
        <Spinner />
        <Body1>Loading container settings…</Body1>
      </div>
    )
  }

  if (containerQuery.isError) {
    return (
      <MessageBar intent="error" layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>Unable to load container</MessageBarTitle>
          {containerQuery.error instanceof Error ? containerQuery.error.message : 'Unknown error'}
        </MessageBarBody>
      </MessageBar>
    )
  }

  if (!container) {
    return <Body1>Container not found.</Body1>
  }

  return (
    <section className={styles.root}>
      <Card className={styles.card}>
        <Subtitle2>Partition Key</Subtitle2>
        <div className={styles.metadataList}>
          <Field label="Paths">
            <Input readOnly value={container.partitionKey?.paths?.join(', ') ?? ''} />
          </Field>
          <Field label="Kind">
            <Input readOnly value={container.partitionKey?.kind ?? ''} />
          </Field>
          <Field label="Version">
            <Input readOnly value={String(container.partitionKey?.version ?? '')} />
          </Field>
        </div>
        <Subtitle2>System Properties</Subtitle2>
        <div className={styles.metadataList}>
          <Field label="_rid">
            <Input readOnly value={container._rid ?? ''} />
          </Field>
          <Field label="_etag">
            <Input readOnly value={container._etag ?? ''} />
          </Field>
        </div>
      </Card>

      <Card className={styles.card}>
        <Subtitle2>Editable Settings</Subtitle2>

        <Field label="Indexing Policy">
          <div className={styles.editorFrame}>
            <Editor
              defaultLanguage="json"
              height="100%"
              onChange={(value) => setIndexingPolicyValue(value ?? '{}')}
              options={{
                automaticLayout: true,
                fontSize: 13,
                formatOnPaste: true,
                formatOnType: true,
                minimap: { enabled: false },
                scrollBeyondLastLine: false,
                wordWrap: 'on',
              }}
              theme={isDark ? 'vs-dark' : 'vs'}
              value={indexingPolicyValue}
            />
          </div>
        </Field>

        <Field label="Default TTL (-1 for off, empty for null)">
          <Input
            onChange={(_, data) => setDefaultTtl(data.value)}
            type="number"
            value={defaultTtl}
          />
        </Field>

        <Field label="Max Throughput">
          <Input
            onChange={(_, data) => setMaxThroughput(data.value)}
            type="number"
            value={maxThroughput}
          />
        </Field>

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
              {saveMutation.error instanceof Error ? saveMutation.error.message : 'Unknown error'}
            </MessageBarBody>
          </MessageBar>
        )}

        <div className={styles.actions}>
          <Button
            appearance="primary"
            disabled={saveMutation.isPending}
            icon={<SaveRegular />}
            onClick={() => {
              setSaveMessage(null)
              saveMutation.mutate()
            }}
          >
            {saveMutation.isPending ? 'Saving…' : 'Save'}
          </Button>
          {container._etag && (
            <Badge appearance="outline">ETag: {container._etag}</Badge>
          )}
        </div>
      </Card>
    </section>
  )
}
