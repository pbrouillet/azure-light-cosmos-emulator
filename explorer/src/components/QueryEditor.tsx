import { useCallback, useImperativeHandle, useMemo, useRef, useState } from 'react'
import type { Ref } from 'react'
import Editor from '@monaco-editor/react'
import type { Monaco } from '@monaco-editor/react'
import type { IDisposable, editor, Position } from 'monaco-editor'
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
import type {
  CosmosDocument,
  CosmosQueryParameter,
  FeedResponse,
  QueryExplainResult,
} from '../types/cosmos'

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
  actionRow: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
    justifyContent: 'flex-end',
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
  explainGrid: {
    display: 'grid',
    gap: tokens.spacingVerticalM,
    '@media (min-width: 960px)': {
      gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)',
    },
  },
  sectionCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    paddingBottom: tokens.spacingVerticalM,
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
    paddingTop: tokens.spacingVerticalM,
  },
  monoBlock: {
    margin: 0,
    paddingBottom: tokens.spacingVerticalM,
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
    paddingTop: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase300,
    overflowX: 'auto',
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  list: {
    margin: 0,
    paddingLeft: '1.25rem',
    display: 'grid',
    gap: tokens.spacingVerticalXS,
  },
  infoPanel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    paddingBottom: tokens.spacingVerticalM,
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
    paddingTop: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorBrandBackground2,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorBrandStroke1}`,
  },
})

function registerCosmosSqlCompletionProvider(monaco: Monaco): IDisposable {
  return monaco.languages.registerCompletionItemProvider('sql', {
    triggerCharacters: ['.', ' '],
    provideCompletionItems(model: editor.ITextModel, position: Position) {
      const word = model.getWordUntilPosition(position)
      const range = {
        startLineNumber: position.lineNumber,
        endLineNumber: position.lineNumber,
        startColumn: word.startColumn,
        endColumn: word.endColumn,
      }

      const lineContent = model.getLineContent(position.lineNumber)
      const textBeforeCursor = lineContent.substring(0, position.column - 1)

      // When the user types "c.", suggest system properties
      if (/\bc\.\s*$/.test(textBeforeCursor)) {
        const properties = ['id', '_ts', '_etag', '_rid', '_self', '_attachments']
        return {
          suggestions: properties.map((prop) => ({
            label: prop,
            kind: monaco.languages.CompletionItemKind.Property,
            insertText: prop,
            range,
            detail: 'System property',
          })),
        }
      }

      const keywords = [
        'SELECT', 'DISTINCT', 'VALUE', 'TOP', 'FROM', 'WHERE', 'JOIN', 'IN',
        'AND', 'OR', 'NOT', 'BETWEEN', 'LIKE', 'ORDER BY', 'GROUP BY',
        'OFFSET', 'LIMIT', 'ASC', 'DESC', 'AS', 'EXISTS',
      ]

      const functions = [
        'CONTAINS', 'STARTSWITH', 'ENDSWITH', 'UPPER', 'LOWER', 'CONCAT',
        'LENGTH', 'SUBSTRING', 'REPLACE', 'TRIM', 'LEFT', 'RIGHT', 'REVERSE',
        'LTRIM', 'RTRIM', 'REPLICATE', 'INDEX_OF', 'StringEquals', 'StringToArray',
        'StringToBoolean', 'StringToNull', 'StringToNumber', 'StringToObject',
        'IIF',
        'ARRAY_CONTAINS', 'ARRAY_CONTAINS_ALL', 'ARRAY_CONTAINS_ANY', 'ARRAY_LENGTH',
        'ARRAY_CONCAT', 'ARRAY_SLICE', 'SetIntersect', 'SetUnion',
        'IS_STRING', 'IS_NUMBER', 'IS_BOOL',
        'IS_NULL', 'IS_ARRAY', 'IS_OBJECT', 'IS_PRIMITIVE', 'IS_DEFINED',
        'IS_INTEGER', 'IS_FINITE', 'IS_NAN',
        'COUNT', 'SUM', 'AVG', 'MIN', 'MAX', 'ABS', 'CEILING', 'FLOOR',
        'ROUND', 'POWER', 'SQRT', 'LOG', 'LOG10', 'EXP', 'SIN', 'COS',
        'TAN', 'ACOS', 'ASIN', 'ATAN', 'ATN2', 'COT', 'SQUARE', 'RAND',
        'NumberBin', 'PI', 'SIGN', 'TRUNC', 'DEGREES', 'RADIANS',
        'IntAdd', 'IntSub', 'IntMul', 'IntDiv', 'IntMod',
        'IntBitAnd', 'IntBitOr', 'IntBitXor', 'IntBitNot',
        'IntBitLeftShift', 'IntBitRightShift',
        'GETCURRENTDATETIME', 'GETCURRENTTIMESTAMP', 'GETCURRENTTICKS',
        'DATETIMEADD', 'DATETIMEDIFF', 'DATETIMEPART',
        'DATETIMETOTICKS', 'TICKSTODATETIME',
        'DateTimeBin', 'DateTimeFromParts', 'DateTimeToTimestamp', 'TimestampToDateTime',
        'REGEXMATCH',
        'FullTextContains', 'FullTextContainsAll', 'FullTextContainsAny', 'FullTextScore',
      ]

      const suggestions = [
        ...keywords.map((kw) => ({
          label: kw,
          kind: monaco.languages.CompletionItemKind.Keyword,
          insertText: kw,
          range,
          detail: 'Keyword',
        })),
        ...functions.map((fn) => ({
          label: fn,
          kind: monaco.languages.CompletionItemKind.Function,
          insertText: fn + '($0)',
          insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
          range,
          detail: 'Built-in function',
        })),
        {
          label: 'c',
          kind: monaco.languages.CompletionItemKind.Variable,
          insertText: 'c',
          range,
          detail: 'Default collection alias',
        },
      ]

      return { suggestions }
    },
  })
}

export function QueryEditor({ dbId, collId, executeRef }: QueryEditorProps) {
  const styles = useStyles()
  const { isDark } = useTheme()
  const [query, setQuery] = useState('SELECT * FROM c')
  const [parametersJson, setParametersJson] = useState('[]')
  const [isExplainOpen, setIsExplainOpen] = useState(false)
  const completionDisposable = useRef<IDisposable | null>(null)

  const handleEditorBeforeMount = useCallback((monaco: Monaco) => {
    completionDisposable.current?.dispose()
    completionDisposable.current = registerCosmosSqlCompletionProvider(monaco)
  }, [])

  const queryMutation = useMutation<FeedResponse<CosmosDocument>, Error>({
    mutationFn: async () => {
      const parameters = parseParameters(parametersJson)
      return cosmosClient.executeQuery(dbId, collId, query, parameters)
    },
  })

  const explainMutation = useMutation<QueryExplainResult, Error>({
    mutationFn: async () => cosmosClient.explainQuery(dbId, collId, query),
  })

  const executeQuery = useCallback(() => {
    queryMutation.mutate()
  }, [queryMutation])

  const explainQuery = useCallback(() => {
    setIsExplainOpen(true)
    explainMutation.mutate()
  }, [explainMutation])

  useImperativeHandle(
    executeRef,
    () => ({
      execute: executeQuery,
    }),
    [executeQuery],
  )

  const resultsJson = useMemo(() => {
    const items = queryMutation.data?.items
    if (!items || items.length === 0) return ''
    return JSON.stringify(items, null, 2)
  }, [queryMutation.data])

  const explainPlanJson = useMemo(
    () => (explainMutation.data ? JSON.stringify(explainMutation.data.queryPlan, null, 2) : ''),
    [explainMutation.data],
  )

  const ruRows = useMemo(
    () =>
      explainMutation.data
        ? [
            ['Base', explainMutation.data.estimatedRuCharge.base],
            ['Filter cost', explainMutation.data.estimatedRuCharge.filterCost],
            ['Join cost', explainMutation.data.estimatedRuCharge.joinCost],
            ['Aggregate cost', explainMutation.data.estimatedRuCharge.aggregateCost],
            ['Order by cost', explainMutation.data.estimatedRuCharge.orderByCost],
            ['Cross-partition multiplier', explainMutation.data.estimatedRuCharge.crossPartitionMultiplier],
            ['Total', explainMutation.data.estimatedRuCharge.total],
          ]
        : [],
    [explainMutation.data],
  )

  const hasExplainPanel =
    isExplainOpen || explainMutation.isPending || explainMutation.isError || explainMutation.isSuccess

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
              <div className={styles.actionRow}>
                <Button appearance="secondary" onClick={explainQuery}>
                  {explainMutation.isPending ? 'Explaining…' : 'Explain'}
                </Button>
                <Button appearance="primary" icon={<PlayRegular />} onClick={executeQuery}>
                  {queryMutation.isPending ? 'Executing…' : 'Execute query'}
                </Button>
              </div>
            </div>
            <div className={styles.editorFrame}>
              <Editor
                beforeMount={handleEditorBeforeMount}
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

          {hasExplainPanel && (
            <Card className={styles.card}>
              <div className={styles.cardHeader}>
                <div>
                  <Subtitle2>Educational explain</Subtitle2>
                  <Body1 className={styles.subtleText}>
                    Inspect the estimated RU breakdown, likely index usage, and query plan without executing the statement.
                  </Body1>
                </div>
                <div className={styles.actionRow}>
                  {explainMutation.data && <Badge>Total RU: {formatNumber(explainMutation.data.estimatedRuCharge.total)}</Badge>}
                  <Button appearance="secondary" onClick={() => setIsExplainOpen((open) => !open)}>
                    {isExplainOpen ? 'Collapse' : 'Expand'}
                  </Button>
                </div>
              </div>

              {isExplainOpen && explainMutation.isError && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Explain failed</MessageBarTitle>
                    {explainMutation.error.message}
                  </MessageBarBody>
                </MessageBar>
              )}

              {isExplainOpen && explainMutation.isPending && (
                <Body1 className={styles.subtleText}>Analyzing query structure and index hints…</Body1>
              )}

              {isExplainOpen && explainMutation.data && (
                <>
                  <div className={styles.explainGrid}>
                    <Card className={styles.sectionCard}>
                      <Subtitle2>Query plan</Subtitle2>
                      <Text size={200} className={styles.subtleText}>
                        Structured view of the parsed query shape.
                      </Text>
                      <pre className={styles.monoBlock}>{explainPlanJson}</pre>
                    </Card>

                    <Card className={styles.sectionCard}>
                      <Subtitle2>Estimated RU breakdown</Subtitle2>
                      <table className={styles.table}>
                        <thead>
                          <tr>
                            <th className={styles.headCell}>
                              <Text as="span" size={200} weight="semibold">
                                Metric
                              </Text>
                            </th>
                            <th className={styles.headCell}>
                              <Text as="span" size={200} weight="semibold">
                                Value
                              </Text>
                            </th>
                          </tr>
                        </thead>
                        <tbody>
                          {ruRows.map(([label, value]) => (
                            <tr key={label}>
                              <td className={styles.bodyCell}>{label}</td>
                              <td className={styles.bodyCell}>{formatNumber(value)}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </Card>
                  </div>

                  <div className={styles.explainGrid}>
                    <Card className={styles.sectionCard}>
                      <div className={styles.cardHeader}>
                        <div>
                          <Subtitle2>Index recommendations</Subtitle2>
                          <Text size={200} className={styles.subtleText}>
                            Suggested paths and patterns that would usually improve query efficiency.
                          </Text>
                        </div>
                        <div className={styles.statusRow}>
                          {explainMutation.data.indexAnalysis.usedIndexes.length > 0 ? (
                            explainMutation.data.indexAnalysis.usedIndexes.map((indexPath) => (
                              <Badge key={indexPath}>{indexPath}</Badge>
                            ))
                          ) : (
                            <Badge>No definite index usage detected</Badge>
                          )}
                        </div>
                      </div>
                      <ul className={styles.list}>
                        {explainMutation.data.indexAnalysis.recommendations.map((recommendation) => (
                          <li key={recommendation}>
                            <Text>{recommendation}</Text>
                          </li>
                        ))}
                      </ul>
                    </Card>

                    <Card className={styles.sectionCard}>
                      <Subtitle2>Indexing policy paths</Subtitle2>
                      <Text size={200} className={styles.subtleText}>
                        Current included and excluded paths from the selected container.
                      </Text>
                      <Text weight="semibold">Included</Text>
                      <div className={styles.statusRow}>
                        {explainMutation.data.indexAnalysis.indexingPolicyPaths.included.map((path) => (
                          <Badge key={`included-${path}`}>{path}</Badge>
                        ))}
                      </div>
                      <Text weight="semibold">Excluded</Text>
                      <div className={styles.statusRow}>
                        {explainMutation.data.indexAnalysis.indexingPolicyPaths.excluded.map((path) => (
                          <Badge key={`excluded-${path}`}>{path}</Badge>
                        ))}
                      </div>
                    </Card>
                  </div>

                  {explainMutation.data.warnings.map((warning) => (
                    <MessageBar intent="warning" key={warning} layout="multiline">
                      <MessageBarBody>
                        <MessageBarTitle>Warning</MessageBarTitle>
                        {warning}
                      </MessageBarBody>
                    </MessageBar>
                  ))}

                  <Card className={styles.infoPanel}>
                    <Subtitle2>Educational notes</Subtitle2>
                    <ul className={styles.list}>
                      {explainMutation.data.educationalNotes.map((note) => (
                        <li key={note}>
                          <Text>{note}</Text>
                        </li>
                      ))}
                    </ul>
                  </Card>
                </>
              )}
            </Card>
          )}

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

            {resultsJson.length > 0 && (
              <div className={styles.resultsWrapper}>
                <Editor
                  defaultLanguage="json"
                  height={`${Math.min(Math.max(resultsJson.split('\n').length * 19, 120), 600)}px`}
                  onChange={() => {}}
                  options={{
                    automaticLayout: true,
                    folding: true,
                    fontSize: 13,
                    lineNumbers: 'on',
                    minimap: { enabled: false },
                    readOnly: true,
                    scrollBeyondLastLine: false,
                    wordWrap: 'on',
                  }}
                  theme={isDark ? 'vs-dark' : 'vs'}
                  value={resultsJson}
                />
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
            placeholder='[{"name": "@param", "value": "..."}]'
            resize="vertical"
            rows={10}
            value={parametersJson}
          />

          <div className={styles.actionRow}>
            <Button appearance="secondary" onClick={explainQuery}>
              {explainMutation.isPending ? 'Explaining…' : 'Explain'}
            </Button>
            <Button appearance="primary" icon={<PlayRegular />} onClick={executeQuery}>
              {queryMutation.isPending ? 'Executing…' : 'Execute query'}
            </Button>
          </div>

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
  let parsed: unknown
  try {
    parsed = JSON.parse(value)
  } catch {
    // Retry with single quotes replaced by double quotes (common copy-paste mistake)
    try {
      parsed = JSON.parse(value.replace(/'/g, '"'))
    } catch {
      throw new Error('Invalid JSON parameters. Use double quotes for property names and string values.')
    }
  }
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

function formatNumber(value: number | string): string {
  if (typeof value === 'string') {
    return value
  }

  return Number.isInteger(value) ? value.toString() : value.toFixed(1)
}


