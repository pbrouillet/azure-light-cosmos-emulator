import { useCallback, useRef, useState } from 'react'
import Editor from '@monaco-editor/react'
import type { Monaco } from '@monaco-editor/react'
import { useMutation } from '@tanstack/react-query'
import {
  Badge,
  Card,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
} from '@fluentui/react-components'
import { PlayRegular, DismissRegular } from '@fluentui/react-icons'
import { cosmosClient } from '../api/cosmosClient'
import { useTheme } from '../theme'
import type { KqlQueryResult } from '../types/cosmos'

const defaultPresets = [
  { label: 'Recent errors', query: 'activity\n| where statusCode >= 400\n| sort by timestamp desc\n| take 50' },
  { label: 'Requests by method', query: 'activity\n| summarize count() by method' },
  { label: 'Top RU consumers', query: 'activity\n| summarize totalRU = sum(requestCharge) by path\n| sort by totalRU desc\n| take 20' },
  { label: 'Slow queries', query: 'telemetry\n| where latencyMs > 100\n| project sqlText, latencyMs, requestCharge, databaseId, containerId\n| sort by latencyMs desc\n| take 20' },
  { label: 'Request count', query: 'activity\n| count' },
  { label: 'Avg latency by path', query: 'activity\n| summarize avgLatency = avg(latencyMs), requests = count() by path\n| sort by avgLatency desc' },
]

const telemetryPresets = [
  { label: 'All queries', query: 'telemetry\n| sort by timestamp desc\n| take 200' },
  { label: 'Slow queries', query: 'telemetry\n| where latencyMs > 100\n| sort by latencyMs desc\n| take 50' },
  { label: 'Errors', query: 'telemetry\n| where statusCode >= 400\n| sort by timestamp desc\n| take 50' },
  { label: 'High RU', query: 'telemetry\n| where requestCharge > 10\n| sort by requestCharge desc\n| take 50' },
  { label: 'By database', query: 'telemetry\n| summarize [\'queries\'] = count(), [\'avgRU\'] = avg(requestCharge), [\'avgLatency\'] = avg(latencyMs) by databaseId' },
  { label: 'Cross-partition', query: 'telemetry\n| where isCrossPartition == true\n| sort by timestamp desc\n| take 50' },
]

export interface KqlQueryEditorProps {
  onResults?: (result: KqlQueryResult | null) => void
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    paddingBottom: tokens.spacingVerticalL,
    paddingLeft: tokens.spacingHorizontalL,
    paddingRight: tokens.spacingHorizontalL,
    paddingTop: tokens.spacingVerticalL,
  },
  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  editorContainer: {
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
    overflow: 'hidden',
  },
  presets: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  resultsContainer: {
    maxHeight: '400px',
    overflow: 'auto',
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
    fontSize: '13px',
  },
  th: {
    position: 'sticky',
    top: 0,
    backgroundColor: tokens.colorNeutralBackground3,
    padding: '8px 12px',
    textAlign: 'left',
    fontWeight: 600,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    whiteSpace: 'nowrap',
    userSelect: 'none',
  },
  td: {
    padding: '6px 12px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    verticalAlign: 'top',
    whiteSpace: 'nowrap',
  },
  stats: {
    display: 'flex',
    gap: tokens.spacingHorizontalL,
    alignItems: 'center',
  },
  emptyResults: {
    padding: tokens.spacingVerticalXL,
    textAlign: 'center',
    color: tokens.colorNeutralForeground3,
  },
})

function registerKqlLanguage(monaco: Monaco) {
  if (monaco.languages.getLanguages().some((l: { id: string }) => l.id === 'kql')) return

  monaco.languages.register({ id: 'kql' })
  monaco.languages.setMonarchTokensProvider('kql', {
    ignoreCase: true,
    keywords: [
      'where', 'project', 'extend', 'summarize', 'sort', 'order', 'by',
      'take', 'limit', 'top', 'count', 'distinct', 'join', 'union',
      'let', 'set', 'render', 'as', 'on', 'asc', 'desc', 'nulls',
      'first', 'last', 'and', 'or', 'not', 'in', 'has', 'contains',
      'startswith', 'endswith', 'between', 'matches', 'regex',
      'project-away', 'project-rename', 'project-reorder',
    ],
    builtinFunctions: [
      'count', 'sum', 'avg', 'min', 'max', 'dcount', 'countif', 'sumif',
      'avgif', 'percentile', 'stdev', 'variance', 'make_list', 'make_set',
      'ago', 'now', 'bin', 'floor', 'round', 'strlen', 'toupper', 'tolower',
      'trim', 'substring', 'strcat', 'tostring', 'toint', 'tolong',
      'todouble', 'toreal', 'todatetime', 'isnull', 'isnotnull', 'isempty',
      'isnotempty', 'iff', 'iif', 'coalesce', 'format_datetime',
      'datetime_diff',
    ],
    tokenizer: {
      root: [
        [/\/\/.*$/, 'comment'],
        [/"[^"]*"/, 'string'],
        [/'[^']*'/, 'string'],
        [/\b\d+(\.\d+)?\b/, 'number'],
        [/\b(1[hdms]|[0-9]+[hdms])\b/, 'number'],
        [/\|/, 'delimiter.pipe'],
        [/[a-zA-Z_]\w*/, {
          cases: {
            '@keywords': 'keyword',
            '@builtinFunctions': 'predefined',
            '@default': 'identifier',
          },
        }],
        [/[{}()[\]]/, 'bracket'],
        [/[<>!=]=?/, 'operator'],
        [/[+\-*/%]/, 'operator'],
        [/,/, 'delimiter'],
      ],
    },
  })
}

function formatCellValue(value: unknown): string {
  if (value === null || value === undefined) return ''
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

export function KqlQueryEditor({ onResults }: KqlQueryEditorProps = {}) {
  const styles = useStyles()
  const { isDark } = useTheme()
  const presetQueries = onResults ? telemetryPresets : defaultPresets
  const [query, setQuery] = useState(presetQueries[0].query)
  const editorRef = useRef<{ getValue: () => string } | null>(null)

  const kqlMutation = useMutation({
    mutationFn: (q: string) => cosmosClient.executeKqlQuery(q),
    onSuccess: (data) => {
      onResults?.(data)
    },
  })

  const executeQuery = useCallback(() => {
    const currentQuery = editorRef.current?.getValue() ?? query
    if (currentQuery.trim()) {
      kqlMutation.mutate(currentQuery.trim())
    }
  }, [query, kqlMutation])

  const handleClearResults = useCallback(() => {
    onResults?.(null)
    kqlMutation.reset()
  }, [onResults, kqlMutation])

  const handleEditorMount = useCallback((editor: { getValue: () => string }) => {
    editorRef.current = editor
    // Ctrl+Enter to execute
    const monacoEditor = editor as unknown as { addCommand: (keybinding: number, handler: () => void) => void }
    // monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter = 2048 | 3
    monacoEditor.addCommand(2048 | 3, () => {
      executeQuery()
    })
  }, [executeQuery])

  const handleEditorBeforeMount = useCallback((monaco: Monaco) => {
    registerKqlLanguage(monaco)
  }, [])

  const result: KqlQueryResult | undefined = kqlMutation.data

  const showInlineResults = !onResults && result

  return (
    <Card className={styles.root} style={{ gridColumn: '1 / -1' }}>
      <div className={styles.header}>
        <Text as="h2" size={500} weight="semibold">
          KQL Query
        </Text>
        <Toolbar>
          <ToolbarButton
            appearance="primary"
            icon={<PlayRegular />}
            disabled={kqlMutation.isPending}
            onClick={executeQuery}
          >
            {kqlMutation.isPending ? 'Running…' : 'Run'}
          </ToolbarButton>
          {onResults && kqlMutation.data && (
            <>
              <ToolbarDivider />
              <ToolbarButton
                icon={<DismissRegular />}
                onClick={handleClearResults}
              >
                Clear filter
              </ToolbarButton>
            </>
          )}
          <ToolbarDivider />
          {presetQueries.map((preset) => (
            <ToolbarButton
              key={preset.label}
              onClick={() => {
                setQuery(preset.query)
                kqlMutation.mutate(preset.query)
              }}
            >
              {preset.label}
            </ToolbarButton>
          ))}
        </Toolbar>
      </div>

      <div className={styles.editorContainer}>
        <Editor
          height="160px"
          language="kql"
          theme={isDark ? 'vs-dark' : 'light'}
          value={query}
          onChange={(value) => setQuery(value ?? '')}
          onMount={handleEditorMount}
          beforeMount={handleEditorBeforeMount}
          options={{
            minimap: { enabled: false },
            lineNumbers: 'on',
            scrollBeyondLastLine: false,
            fontSize: 14,
            wordWrap: 'on',
            automaticLayout: true,
            padding: { top: 8, bottom: 8 },
          }}
        />
      </div>

      {kqlMutation.isError && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Query error</MessageBarTitle>
            {kqlMutation.error instanceof Error ? kqlMutation.error.message : 'Unknown error'}
          </MessageBarBody>
        </MessageBar>
      )}

      {showInlineResults && (
        <>
          <div className={styles.stats}>
            <Badge appearance="outline" color="informative">
              {result.rows.length} row{result.rows.length !== 1 ? 's' : ''}
            </Badge>
            <Badge appearance="outline" color="subtle">
              {result.columns.length} column{result.columns.length !== 1 ? 's' : ''}
            </Badge>
          </div>

          {result.rows.length === 0 ? (
            <div className={styles.emptyResults}>No results</div>
          ) : (
            <div className={styles.resultsContainer}>
              <table className={styles.table}>
                <thead>
                  <tr>
                    {result.columns.map((col) => (
                      <th key={col.name} className={styles.th}>
                        {col.name}
                        <Text size={100} style={{ marginLeft: '4px', opacity: 0.6 }}>
                          ({col.type})
                        </Text>
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {result.rows.map((row, rowIdx) => (
                    <tr key={rowIdx}>
                      {row.map((cell, colIdx) => (
                        <td key={colIdx} className={styles.td}>
                          {formatCellValue(cell)}
                        </td>
                      ))}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </Card>
  )
}
