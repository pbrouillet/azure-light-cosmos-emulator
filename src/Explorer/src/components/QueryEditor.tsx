import { useCallback, useImperativeHandle, useMemo, useState } from 'react'
import type { Ref } from 'react'
import Editor from '@monaco-editor/react'
import { useMutation } from '@tanstack/react-query'
import {
  Badge,
  Body1,
  Button,
  Card,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Subtitle2,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components'
import { PlayRegular } from '@fluentui/react-icons'
import { cosmosClient } from '../api/cosmosClient'
import { useTheme } from '../theme'
import type { CosmosDocument, CosmosQueryParameter, FeedResponse } from '../types/cosmos'

interface QueryEditorProps {
  dbId: string
  collId: string
  executeRef?: Ref<QueryEditorHandle>
}

export interface QueryEditorHandle {
  execute: () => void
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  grid: {
    display: 'grid',
    gap: tokens.spacingHorizontalL,
    minHeight: 0,
    '@media (min-width: 1200px)': {
      gridTemplateColumns: 'minmax(0, 1fr) 20rem',
    },
  },
  mainColumn: {
    display: 'flex',
    minHeight: 0,
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minHeight: 0,
    paddingBottom: tokens.spacingVerticalL,
    paddingLeft: tokens.spacingHorizontalL,
    paddingRight: tokens.spacingHorizontalL,
    paddingTop: tokens.spacingVerticalL,
  },
  cardHeader: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  subtleText: {
    color: tokens.colorNeutralForeground3,
  },
  statusRow: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
  },
  editorFrame: {
    borderRadius: tokens.borderRadiusMedium,
    overflow: 'hidden',
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  resultsWrapper: {
    maxHeight: '32rem',
    overflow: 'auto',
    borderRadius: tokens.borderRadiusMedium,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
  },
  headCell: {
    position: 'sticky',
    top: 0,
    backgroundColor: tokens.colorNeutralBackground1,
    textAlign: 'left',
    paddingBottom: tokens.spacingVerticalS,
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
    paddingTop: tokens.spacingVerticalS,
    color: tokens.colorNeutralForeground3,
    borderBottom: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  bodyCell: {
    paddingBottom: tokens.spacingVerticalS,
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
    paddingTop: tokens.spacingVerticalS,
    verticalAlign: 'top',
    borderBottom: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  asideCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    paddingBottom: tokens.spacingVerticalL,
    paddingLeft: tokens.spacingHorizontalL,
    paddingRight: tokens.spacingHorizontalL,
    paddingTop: tokens.spacingVerticalL,
  },
  textarea: {
    fontFamily: tokens.fontFamilyMonospace,
  },
  metricList: {
    display: 'grid',
    gap: tokens.spacingVerticalS,
  },
  metricCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    paddingBottom: tokens.spacingVerticalS,
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
    paddingTop: tokens.spacingVerticalS,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
})

export function QueryEditor({ dbId, collId, executeRef }: QueryEditorProps) {
  const styles = useStyles()
  const { isDark } = useTheme()
  const [query, setQuery] = useState('SELECT * FROM c')
  const [parametersJson, setParametersJson] = useState('[]')

  const queryMutation = useMutation<FeedResponse<CosmosDocument>, Error>({
    mutationFn: async () => {
      const parameters = parseParameters(parametersJson)
      return cosmosClient.executeQuery(dbId, collId, query, parameters)
    },
  })

  const executeQuery = useCallback(() => {
    queryMutation.mutate()
  }, [queryMutation])

  useImperativeHandle(
    executeRef,
    () => ({
      execute: executeQuery,
    }),
    [executeQuery],
  )

  const columns = useMemo(() => {
    const items = queryMutation.data?.items ?? []
    return Array.from(new Set(items.flatMap((item) => Object.keys(item))))
  }, [queryMutation.data])

  return (
    <section className={styles.root}>
      <div className={styles.grid}>
        <div className={styles.mainColumn}>
          <Card className={styles.card}>
            <div className={styles.cardHeader}>
              <div>
                <Subtitle2>SQL query</Subtitle2>
                <Body1 className={styles.subtleText}>
                  Run SQL queries against the selected container and inspect the returned items.
                </Body1>
              </div>
              <Button appearance="primary" icon={<PlayRegular />} onClick={executeQuery}>
                {queryMutation.isPending ? 'Executing…' : 'Execute query'}
              </Button>
            </div>
            <div className={styles.editorFrame}>
              <Editor
                defaultLanguage="sql"
                height="260px"
                onChange={(value) => setQuery(value ?? '')}
                options={{
                  automaticLayout: true,
                  fontSize: 14,
                  minimap: { enabled: false },
                  scrollBeyondLastLine: false,
                  wordWrap: 'on',
                }}
                theme={isDark ? 'vs-dark' : 'vs'}
                value={query}
              />
            </div>
          </Card>

          <Card className={styles.card}>
            <div className={styles.cardHeader}>
              <div>
                <Subtitle2>Results</Subtitle2>
                <Body1 className={styles.subtleText}>
                  Review returned items, request charge, and the response metadata from the emulator.
                </Body1>
              </div>
              <div className={styles.statusRow}>
                <Badge>Request charge: {queryMutation.data?.requestCharge ?? 0}</Badge>
                <Badge>Item count: {queryMutation.data?.itemCount ?? 0}</Badge>
              </div>
            </div>

            {queryMutation.isError && (
              <MessageBar intent="error" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>Query failed</MessageBarTitle>
                  {queryMutation.error.message}
                </MessageBarBody>
              </MessageBar>
            )}

            {queryMutation.isSuccess && (queryMutation.data?.items.length ?? 0) === 0 && (
              <Body1 className={styles.subtleText}>No items returned.</Body1>
            )}

            {(queryMutation.data?.items.length ?? 0) > 0 && (
              <div className={styles.resultsWrapper}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      {columns.map((column) => (
                        <th className={styles.headCell} key={column}>
                          <Text as="span" size={200} weight="semibold">
                            {column}
                          </Text>
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {queryMutation.data?.items.map((item, index) => (
                      <tr key={`${item.id}-${index}`}>
                        {columns.map((column) => (
                          <td className={styles.bodyCell} key={`${item.id}-${column}`}>
                            {formatCellValue(item[column])}
                          </td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {!queryMutation.isSuccess && !queryMutation.isError && (
              <Body1 className={styles.subtleText}>
                Execute a query to see matching documents here.
              </Body1>
            )}
          </Card>
        </div>

        <Card className={styles.asideCard}>
          <div>
            <Subtitle2>Parameters</Subtitle2>
            <Body1 className={styles.subtleText}>
              Provide a JSON array of query parameters when your statement uses placeholders.
            </Body1>
          </div>

          <Textarea
            className={styles.textarea}
            onChange={(_, data) => setParametersJson(data.value)}
            resize="vertical"
            rows={10}
            value={parametersJson}
          />

          <Button appearance="primary" icon={<PlayRegular />} onClick={executeQuery}>
            {queryMutation.isPending ? 'Executing…' : 'Execute query'}
          </Button>

          <div className={styles.metricList}>
            <MetricCard label="Database" value={dbId} />
            <MetricCard label="Container" value={collId} />
            <MetricCard label="Activity ID" value={queryMutation.data?.activityId ?? 'Not available yet'} />
          </div>
        </Card>
      </div>
    </section>
  )
}

function MetricCard({ label, value }: { label: string; value: string }) {
  const styles = useStyles()

  return (
    <div className={styles.metricCard}>
      <Text size={200} weight="semibold">
        {label}
      </Text>
      <Text font="monospace" size={200} wrap>
        {value}
      </Text>
    </div>
  )
}

function parseParameters(value: string): CosmosQueryParameter[] {
  const parsed = JSON.parse(value) as unknown
  if (!Array.isArray(parsed)) {
    throw new Error('Parameters must be a JSON array.')
  }

  return parsed.map((entry) => {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) {
      throw new Error('Each parameter must be an object with name and value properties.')
    }

    const candidate = entry as Partial<CosmosQueryParameter>
    if (typeof candidate.name !== 'string') {
      throw new Error('Each parameter must include a string name property.')
    }

    return {
      name: candidate.name,
      value: candidate.value,
    }
  })
}

function formatCellValue(value: unknown): string {
  if (value === null) {
    return 'null'
  }

  if (value === undefined) {
    return '—'
  }

  return typeof value === 'string' ? value : JSON.stringify(value)
}
