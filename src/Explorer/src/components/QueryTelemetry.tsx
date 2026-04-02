import { Fragment, useCallback, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Card,
  Combobox,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Option,
  Spinner,
  Text,
  tokens,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
} from '@fluentui/react-components'
import { ArrowSyncRegular, DeleteRegular } from '@fluentui/react-icons'
import { cosmosClient } from '../api/cosmosClient'
import type { KqlQueryResult, QueryExplainResult, QueryTelemetryEntry } from '../types/cosmos'
import { KqlQueryEditor } from './KqlQueryEditor'

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    padding: '24px',
    gap: '16px',
    overflow: 'auto',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  filters: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
  },
  tableWrapper: {
    flex: 1,
    overflow: 'auto',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
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
  },
  row: {
    cursor: 'pointer',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  rowExpanded: {
    backgroundColor: tokens.colorNeutralBackground1Selected,
  },
  sqlCell: {
    maxWidth: '300px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    fontFamily: 'monospace',
    fontSize: '12px',
  },
  sqlExpanded: {
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-all',
    fontFamily: 'monospace',
    fontSize: '12px',
    padding: '12px',
    backgroundColor: tokens.colorNeutralBackground3,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  statusOk: { color: tokens.colorPaletteGreenForeground1 },
  statusErr: { color: tokens.colorPaletteRedForeground1 },
  mono: { fontFamily: 'monospace', fontSize: '12px' },
  empty: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    height: '200px',
    color: tokens.colorNeutralForeground3,
  },
  planSection: {
    marginTop: '8px',
    padding: '8px',
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  planGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))',
    gap: '8px',
    marginTop: '4px',
  },
  planStat: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: '2px',
  },
  planLabel: {
    fontSize: '11px',
    color: tokens.colorNeutralForeground3,
    fontWeight: 600,
    textTransform: 'uppercase' as const,
  },
  planValue: {
    fontSize: '13px',
    fontFamily: 'monospace',
  },
  tagList: {
    display: 'flex',
    flexWrap: 'wrap' as const,
    gap: '4px',
    marginTop: '4px',
  },
  tag: {
    fontSize: '11px',
    padding: '2px 6px',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground4,
  },
  tagWarn: {
    fontSize: '11px',
    padding: '2px 6px',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorPaletteYellowBackground2,
  },
})

export function QueryTelemetry() {
  const styles = useStyles()
  const queryClient = useQueryClient()
  const [dbFilter, setDbFilter] = useState<string>('')
  const [containerFilter, setContainerFilter] = useState<string>('')
  const [expandedId, setExpandedId] = useState<string | null>(null)
  const [kqlResult, setKqlResult] = useState<KqlQueryResult | null>(null)

  const { data: entries, isLoading } = useQuery({
    queryKey: ['queryTelemetry', dbFilter, containerFilter],
    queryFn: () =>
      cosmosClient.getQueryTelemetry(
        dbFilter || undefined,
        containerFilter || undefined,
        200,
      ),
    refetchInterval: 5000,
  })

  const { data: databases } = useQuery({
    queryKey: ['databases'],
    queryFn: () => cosmosClient.listDatabases(),
  })

  const clearMutation = useMutation({
    mutationFn: () => cosmosClient.clearQueryTelemetry(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['queryTelemetry'] })
    },
  })

  const formatTimestamp = useCallback((ts: string) => {
    const d = new Date(ts)
    return d.toLocaleTimeString(undefined, {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      fractionalSecondDigits: 3,
    })
  }, [])

  const parsePlan = useCallback((planJson: string | null): QueryExplainResult | null => {
    if (!planJson) return null
    try {
      return JSON.parse(planJson) as QueryExplainResult
    } catch {
      return null
    }
  }, [])

  const dbOptions = databases?.items.map((db) => db.id) ?? []

  const handleKqlResults = useCallback((result: KqlQueryResult | null) => {
    setKqlResult(result)
    setExpandedId(null)
  }, [])

  const formatKqlCell = useCallback((value: unknown): string => {
    if (value === null || value === undefined) return ''
    if (typeof value === 'object') return JSON.stringify(value)
    return String(value)
  }, [])

  return (
    <div className={styles.root}>
      <div className={styles.header}>
        <Text size={500} weight="semibold">
          Query Telemetry
        </Text>
        <Toolbar>
          <ToolbarButton
            icon={<ArrowSyncRegular />}
            onClick={() =>
              queryClient.invalidateQueries({ queryKey: ['queryTelemetry'] })
            }
          >
            Refresh
          </ToolbarButton>
          <ToolbarDivider />
          <ToolbarButton
            icon={<DeleteRegular />}
            onClick={() => clearMutation.mutate()}
          >
            Clear
          </ToolbarButton>
        </Toolbar>
      </div>

      <div className={styles.filters}>
        <Combobox
          clearable
          onOptionSelect={(_e, data) => setDbFilter(data.optionValue ?? '')}
          placeholder="Filter by database..."
          value={dbFilter}
        >
          {dbOptions.map((db) => (
            <Option key={db} value={db}>
              {db}
            </Option>
          ))}
        </Combobox>
        <Combobox
          clearable
          freeform
          onOptionSelect={(_e, data) =>
            setContainerFilter(data.optionValue ?? '')
          }
          placeholder="Filter by container..."
          value={containerFilter}
        />
      </div>

      <KqlQueryEditor onResults={handleKqlResults} />

      {kqlResult ? (
        <>
          <MessageBar>
            <MessageBarBody>
              KQL query returned {kqlResult.rows.length} row{kqlResult.rows.length !== 1 ? 's' : ''} with {kqlResult.columns.length} column{kqlResult.columns.length !== 1 ? 's' : ''}.
            </MessageBarBody>
          </MessageBar>
          {kqlResult.rows.length === 0 ? (
            <Card className={styles.empty}>
              <Text>No results match the KQL query.</Text>
            </Card>
          ) : (
            <div className={styles.tableWrapper}>
              <table className={styles.table}>
                <thead>
                  <tr>
                    {kqlResult.columns.map((col) => (
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
                  {kqlResult.rows.map((row, rowIdx) => (
                    <tr key={rowIdx} className={styles.row}>
                      {row.map((cell, colIdx) => (
                        <td key={colIdx} className={styles.td}>
                          <span className={styles.mono}>{formatKqlCell(cell)}</span>
                        </td>
                      ))}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      ) : isLoading ? (
        <Spinner label="Loading telemetry..." />
      ) : !entries || entries.length === 0 ? (
        <Card className={styles.empty}>
          <Text>No query telemetry recorded yet. Execute queries to see data here.</Text>
        </Card>
      ) : (
        <>
          <MessageBar>
            <MessageBarBody>
              Showing {entries.length} most recent queries.
              {dbFilter && ` Database: ${dbFilter}.`}
              {containerFilter && ` Container: ${containerFilter}.`}
            </MessageBarBody>
          </MessageBar>
          <div className={styles.tableWrapper}>
            <table className={styles.table}>
              <thead>
                <tr>
                  <th className={styles.th}>Time</th>
                  <th className={styles.th}>Database</th>
                  <th className={styles.th}>Container</th>
                  <th className={styles.th}>SQL</th>
                  <th className={styles.th}>Consistency</th>
                  <th className={styles.th}>RU</th>
                  <th className={styles.th}>Latency</th>
                  <th className={styles.th}>Items</th>
                  <th className={styles.th}>Status</th>
                </tr>
              </thead>
              <tbody>
                {entries.map((entry: QueryTelemetryEntry) => (
                  <Fragment key={entry.id}>
                    <tr
                      className={`${styles.row} ${expandedId === entry.id ? styles.rowExpanded : ''}`}
                      onClick={() =>
                        setExpandedId(expandedId === entry.id ? null : entry.id)
                      }
                    >
                      <td className={styles.td}>
                        <span className={styles.mono}>
                          {formatTimestamp(entry.timestamp)}
                        </span>
                      </td>
                      <td className={styles.td}>{entry.databaseId}</td>
                      <td className={styles.td}>{entry.containerId}</td>
                      <td className={`${styles.td} ${styles.sqlCell}`}>
                        {entry.sqlText}
                      </td>
                      <td className={styles.td}>{entry.consistencyLevel}</td>
                      <td className={styles.td}>
                        <span className={styles.mono}>
                          {(entry.requestCharge ?? 0).toFixed(2)}
                        </span>
                      </td>
                      <td className={styles.td}>
                        <span className={styles.mono}>
                          {entry.latencyMs ?? 0}ms
                        </span>
                      </td>
                      <td className={styles.td}>{entry.itemCount ?? 0}</td>
                      <td className={styles.td}>
                        <span
                          className={
                            entry.statusCode < 400
                              ? styles.statusOk
                              : styles.statusErr
                          }
                        >
                          {entry.statusCode}
                        </span>
                      </td>
                    </tr>
                    {expandedId === entry.id && (
                      <tr key={`${entry.id}-detail`}>
                        <td colSpan={9} className={styles.sqlExpanded}>
                          <strong>SQL:</strong> {entry.sqlText}
                          {'\n'}
                          <strong>Activity ID:</strong> {entry.activityId}
                          {'\n'}
                          <strong>Partition Key:</strong>{' '}
                          {entry.partitionKey ?? '(cross-partition)'}
                          {'\n'}
                          <strong>Cross-partition:</strong>{' '}
                          {entry.isCrossPartition ? 'Yes' : 'No'}
                          {entry.continuationToken && (
                            <>
                              {'\n'}
                              <strong>Continuation:</strong>{' '}
                              {entry.continuationToken}
                            </>
                          )}
                          {(() => {
                            const plan = parsePlan(entry.queryPlan)
                            if (!plan) return null
                            return (
                              <div className={styles.planSection}>
                                <strong>Query Plan</strong>
                                <div className={styles.planGrid}>
                                  <div className={styles.planStat}>
                                    <span className={styles.planLabel}>Base RU</span>
                                    <span className={styles.planValue}>{plan.estimatedRuCharge.base.toFixed(2)}</span>
                                  </div>
                                  <div className={styles.planStat}>
                                    <span className={styles.planLabel}>Filter</span>
                                    <span className={styles.planValue}>{plan.estimatedRuCharge.filterCost.toFixed(2)}</span>
                                  </div>
                                  <div className={styles.planStat}>
                                    <span className={styles.planLabel}>Join</span>
                                    <span className={styles.planValue}>{plan.estimatedRuCharge.joinCost.toFixed(2)}</span>
                                  </div>
                                  <div className={styles.planStat}>
                                    <span className={styles.planLabel}>Aggregate</span>
                                    <span className={styles.planValue}>{plan.estimatedRuCharge.aggregateCost.toFixed(2)}</span>
                                  </div>
                                  <div className={styles.planStat}>
                                    <span className={styles.planLabel}>Order By</span>
                                    <span className={styles.planValue}>{plan.estimatedRuCharge.orderByCost.toFixed(2)}</span>
                                  </div>
                                  <div className={styles.planStat}>
                                    <span className={styles.planLabel}>Total Est.</span>
                                    <span className={styles.planValue}>{plan.estimatedRuCharge.total.toFixed(2)}</span>
                                  </div>
                                </div>

                                {plan.indexAnalysis.usedIndexes.length > 0 && (
                                  <>
                                    {'\n'}
                                    <strong>Used Indexes</strong>
                                    <div className={styles.tagList}>
                                      {plan.indexAnalysis.usedIndexes.map((idx, i) => (
                                        <span className={styles.tag} key={i}>{idx}</span>
                                      ))}
                                    </div>
                                  </>
                                )}

                                {plan.indexAnalysis.recommendations.length > 0 && (
                                  <>
                                    {'\n'}
                                    <strong>Recommendations</strong>
                                    <div className={styles.tagList}>
                                      {plan.indexAnalysis.recommendations.map((rec, i) => (
                                        <span className={styles.tagWarn} key={i}>{rec}</span>
                                      ))}
                                    </div>
                                  </>
                                )}

                                {plan.warnings.length > 0 && (
                                  <>
                                    {'\n'}
                                    <strong>Warnings</strong>
                                    <div className={styles.tagList}>
                                      {plan.warnings.map((w, i) => (
                                        <span className={styles.tagWarn} key={i}>{w}</span>
                                      ))}
                                    </div>
                                  </>
                                )}
                              </div>
                            )
                          })()}
                        </td>
                      </tr>
                    )}
                  </Fragment>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  )
}
