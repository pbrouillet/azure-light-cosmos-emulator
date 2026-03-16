import { useCallback, useEffect, useMemo, useState } from 'react'
import Editor from '@monaco-editor/react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Body1,
  Button,
  Card,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Dropdown,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Option,
  Subtitle2,
  Textarea,
  makeStyles,
  tokens,
} from '@fluentui/react-components'
import { AddRegular, DeleteRegular, PlayRegular, SaveRegular } from '@fluentui/react-icons'
import { cosmosClient } from '../api/cosmosClient'
import { useTheme } from '../theme'
import type {
  CosmosTrigger,
  FeedResponse,
  StoredProcedure,
  UserDefinedFunction,
} from '../types/cosmos'

interface ProgrammabilityEditorProps {
  dbId: string
  collId: string
  resourceType: 'sprocs' | 'triggers' | 'udfs'
  label: string
}

type ProgrammabilityResource = StoredProcedure | CosmosTrigger | UserDefinedFunction

const triggerTypes = ['Pre', 'Post'] as const
const triggerOperations = ['All', 'Create', 'Replace', 'Delete'] as const

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
  actions: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
  },
  grid: {
    display: 'grid',
    gap: tokens.spacingHorizontalL,
    minHeight: 0,
    '@media (min-width: 1200px)': {
      gridTemplateColumns: '18rem minmax(0, 1fr)',
    },
  },
  listCard: {
    display: 'flex',
    minHeight: 0,
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    paddingBottom: tokens.spacingVerticalL,
    paddingLeft: tokens.spacingHorizontalL,
    paddingRight: tokens.spacingHorizontalL,
    paddingTop: tokens.spacingVerticalL,
  },
  listActions: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
  },
  list: {
    display: 'flex',
    minHeight: 0,
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    overflow: 'auto',
  },
  resourceButton: {
    justifyContent: 'flex-start',
    textAlign: 'left',
  },
  mainColumn: {
    display: 'flex',
    minHeight: 0,
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  editorCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minHeight: 0,
    paddingBottom: tokens.spacingVerticalL,
    paddingLeft: tokens.spacingHorizontalL,
    paddingRight: tokens.spacingHorizontalL,
    paddingTop: tokens.spacingVerticalL,
  },
  formGrid: {
    display: 'grid',
    gap: tokens.spacingHorizontalL,
    '@media (min-width: 768px)': {
      gridTemplateColumns: 'minmax(0, 1fr) 15rem 15rem',
    },
  },
  editorFrame: {
    minHeight: '28rem',
    overflow: 'hidden',
    borderRadius: tokens.borderRadiusMedium,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  bottomGrid: {
    display: 'grid',
    gap: tokens.spacingHorizontalL,
    '@media (min-width: 1200px)': {
      gridTemplateColumns: 'minmax(0, 1fr) 20rem',
    },
  },
  supportCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    paddingBottom: tokens.spacingVerticalL,
    paddingLeft: tokens.spacingHorizontalL,
    paddingRight: tokens.spacingHorizontalL,
    paddingTop: tokens.spacingVerticalL,
  },
  output: {
    margin: 0,
    paddingBottom: tokens.spacingVerticalM,
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
    paddingTop: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  metadataList: {
    display: 'grid',
    gap: tokens.spacingVerticalS,
  },
  dialogFields: {
    display: 'grid',
    gap: tokens.spacingVerticalS,
  },
})

export function ProgrammabilityEditor({
  dbId,
  collId,
  resourceType,
  label,
}: ProgrammabilityEditorProps) {
  const styles = useStyles()
  const { isDark } = useTheme()
  const queryClient = useQueryClient()
  const singularLabel = label.toLowerCase().slice(0, -1)
  const isStoredProcedure = resourceType === 'sprocs'
  const isTrigger = resourceType === 'triggers'
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [isCreatingNew, setIsCreatingNew] = useState(false)
  const [resourceId, setResourceId] = useState('')
  const [editorValue, setEditorValue] = useState(defaultBody(resourceType))
  const [triggerType, setTriggerType] = useState<CosmosTrigger['triggerType']>('Pre')
  const [triggerOperation, setTriggerOperation] =
    useState<CosmosTrigger['triggerOperation']>('All')
  const [executionResult, setExecutionResult] = useState<string | null>(null)
  const [successMessage, setSuccessMessage] = useState<string | null>(null)
  const [createDialogOpen, setCreateDialogOpen] = useState(false)
  const [draftResourceId, setDraftResourceId] = useState('')
  const [createDialogError, setCreateDialogError] = useState<string | null>(null)
  const [deleteDialogOpen, setDeleteDialogOpen] = useState(false)
  const [executeDialogOpen, setExecuteDialogOpen] = useState(false)
  const [executeArgs, setExecuteArgs] = useState('[]')
  const [executePartitionKey, setExecutePartitionKey] = useState('')

  const resourcesQuery = useQuery<FeedResponse<ProgrammabilityResource>, Error>({
    queryKey: ['programmability', resourceType, dbId, collId],
    queryFn: () => listResources(resourceType, dbId, collId),
  })

  const resources = useMemo(() => resourcesQuery.data?.items ?? [], [resourcesQuery.data])
  const selectedResource = useMemo(
    () => (isCreatingNew ? undefined : resources.find((resource) => resource.id === selectedId)),
    [isCreatingNew, resources, selectedId],
  )

  const resetDraft = useCallback(
    (nextId = '') => {
      setSelectedId(null)
      setResourceId(nextId)
      setEditorValue(defaultBody(resourceType))
      setTriggerType('Pre')
      setTriggerOperation('All')
      setExecutionResult(null)
    },
    [resourceType],
  )

  const loadResource = useCallback((resource: ProgrammabilityResource) => {
    setIsCreatingNew(false)
    setSelectedId(resource.id)
    setResourceId(resource.id)
    setEditorValue(resource.body)
    setExecutionResult(null)
    setSuccessMessage(null)

    if (isTriggerResource(resource)) {
      setTriggerType(resource.triggerType)
      setTriggerOperation(resource.triggerOperation)
      return
    }

    setTriggerType('Pre')
    setTriggerOperation('All')
  }, [])

  const beginCreateDraft = useCallback(
    (nextId = '') => {
      setIsCreatingNew(true)
      setSuccessMessage(null)
      resetDraft(nextId)
    },
    [resetDraft],
  )

  useEffect(() => {
    if (isCreatingNew) {
      return
    }

    if (resources.length === 0) {
      return
    }

    const resource = resources.find((entry) => entry.id === selectedId) ?? resources[0]
    if (!selectedResource || selectedResource.id !== resource.id) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- form state must follow the fetched selection
      loadResource(resource)
    }
  }, [isCreatingNew, loadResource, resourceType, resources, selectedId, selectedResource])

  const isDraftMode = isCreatingNew || !selectedResource

  const saveMutation = useMutation<ProgrammabilityResource, Error>({
    mutationFn: async () => {
      const id = resourceId.trim()
      if (!id) {
        throw new Error('Resource id is required.')
      }

      const body = editorValue.trim()
      if (!body) {
        throw new Error('Resource body is required.')
      }

      if (isDraftMode) {
        return createResource(resourceType, dbId, collId, id, body, {
          triggerOperation,
          triggerType,
        })
      }

      return replaceResource(resourceType, dbId, collId, selectedResource.id, body, {
        triggerOperation,
        triggerType,
      })
    },
    onSuccess: async (resource) => {
      await queryClient.invalidateQueries({ queryKey: ['programmability', resourceType, dbId, collId] })
      loadResource(resource)
      setSuccessMessage(`${singularLabel} ${isDraftMode ? 'created' : 'saved'} successfully.`)
    },
  })

  const deleteMutation = useMutation<void, Error>({
    mutationFn: async () => {
      if (!selectedResource) {
        throw new Error(`Select a ${singularLabel} to delete.`)
      }

      return deleteResource(resourceType, dbId, collId, selectedResource.id)
    },
    onSuccess: async () => {
      setDeleteDialogOpen(false)
      setSuccessMessage(`${singularLabel} deleted successfully.`)
      setIsCreatingNew(false)
      resetDraft('')
      await queryClient.invalidateQueries({ queryKey: ['programmability', resourceType, dbId, collId] })
    },
  })

  const executeMutation = useMutation<unknown, Error, { rawArgs: string; rawPartitionKey: string }>({
    mutationFn: ({ rawArgs, rawPartitionKey }) => {
      if (!selectedResource) {
        throw new Error('Select a stored procedure to execute.')
      }

      const args = parseJsonArray(rawArgs)
      const partitionKey = rawPartitionKey.trim() ? parseJsonValue(rawPartitionKey) : undefined

      return cosmosClient.executeStoredProcedure(dbId, collId, selectedResource.id, args, partitionKey)
    },
    onSuccess: (result) => {
      setExecuteDialogOpen(false)
      setExecutionResult(formatResult(result))
      setSuccessMessage('Stored procedure executed successfully.')
    },
  })

  const metadata = [
    { label: '_rid', value: selectedResource?._rid },
    { label: '_etag', value: selectedResource?._etag },
    { label: '_ts', value: selectedResource?._ts },
  ]

  const activeError =
    resourcesQuery.error ?? saveMutation.error ?? deleteMutation.error ?? executeMutation.error

  const submitCreateDraft = () => {
    const id = draftResourceId.trim()
    if (!id) {
      setCreateDialogError('Resource id is required.')
      return
    }

    setCreateDialogError(null)
    setDraftResourceId('')
    setCreateDialogOpen(false)
    beginCreateDraft(id)
  }

  const submitExecute = () => {
    setExecutionResult(null)
    executeMutation.mutate({ rawArgs: executeArgs, rawPartitionKey: executePartitionKey })
  }

  return (
    <section className={styles.root}>
      <div className={styles.header}>
        <div>
          <Subtitle2>{label}</Subtitle2>
          <Body1 className={styles.subtleText}>
            Create, edit, and manage {label.toLowerCase()} for the selected container with Monaco in
            JavaScript mode.
          </Body1>
        </div>

        <div className={styles.actions}>
          {isStoredProcedure && (
            <Dialog modalType="non-modal" open={executeDialogOpen} onOpenChange={(_, data) => setExecuteDialogOpen(data.open)}>
              <DialogTrigger>
                <Button
                  appearance="secondary"
                  disabled={!selectedResource || isCreatingNew || executeMutation.isPending}
                  icon={<PlayRegular />}
                >
                  Execute
                </Button>
              </DialogTrigger>
              <DialogSurface backdrop={{ onClick: () => setExecuteDialogOpen(false) }}>
                <DialogBody>
                  <DialogTitle>Execute stored procedure</DialogTitle>
                  <DialogContent>
                    <div className={styles.dialogFields}>
                      <Field label="Arguments (JSON array)">
                        <Textarea
                          onChange={(_, data) => setExecuteArgs(data.value)}
                          resize="vertical"
                          rows={6}
                          value={executeArgs}
                        />
                      </Field>
                      <Field label="Partition key (optional JSON value)">
                        <Input
                          onChange={(_, data) => setExecutePartitionKey(data.value)}
                          value={executePartitionKey}
                        />
                      </Field>
                    </div>
                  </DialogContent>
                  <DialogActions>
                    <DialogTrigger>
                      <Button appearance="secondary">Cancel</Button>
                    </DialogTrigger>
                    <Button appearance="primary" icon={<PlayRegular />} onClick={submitExecute}>
                      {executeMutation.isPending ? 'Executing…' : 'Execute'}
                    </Button>
                  </DialogActions>
                </DialogBody>
              </DialogSurface>
            </Dialog>
          )}

          <Button appearance="primary" icon={<SaveRegular />} onClick={() => saveMutation.mutate()}>
            {saveMutation.isPending ? 'Saving…' : isDraftMode ? 'Create' : 'Save'}
          </Button>
        </div>
      </div>

      {successMessage && (
        <MessageBar intent="success">
          <MessageBarBody>
            <MessageBarTitle>Success</MessageBarTitle>
            {successMessage}
          </MessageBarBody>
        </MessageBar>
      )}

      {activeError && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Operation failed</MessageBarTitle>
            {toErrorMessage(activeError)}
          </MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.grid}>
        <Card className={styles.listCard}>
          <div>
            <Subtitle2>{label}</Subtitle2>
            <Body1 className={styles.subtleText}>Select a resource or create a new draft.</Body1>
          </div>

          <div className={styles.listActions}>
            <Dialog
              modalType="non-modal"
              open={createDialogOpen}
              onOpenChange={(_, data) => {
                setCreateDialogOpen(data.open)
                if (!data.open) {
                  setDraftResourceId('')
                  setCreateDialogError(null)
                }
              }}
            >
              <DialogTrigger>
                <Button appearance="secondary" icon={<AddRegular />}>
                  New
                </Button>
              </DialogTrigger>
              <DialogSurface backdrop={{ onClick: () => { setCreateDialogOpen(false); setCreateDialogError(null) } }}>
                <DialogBody>
                  <DialogTitle>Create {singularLabel} draft</DialogTitle>
                  <DialogContent>
                    <div className={styles.dialogFields}>
                      <Field label="Resource id">
                        <Input
                          onChange={(_, data) => setDraftResourceId(data.value)}
                          placeholder="Enter a resource id"
                          value={draftResourceId}
                        />
                      </Field>
                      {createDialogError && (
                        <MessageBar intent="error">
                          <MessageBarBody>
                            <MessageBarTitle>Draft details required</MessageBarTitle>
                            {createDialogError}
                          </MessageBarBody>
                        </MessageBar>
                      )}
                    </div>
                  </DialogContent>
                  <DialogActions>
                    <DialogTrigger>
                      <Button appearance="secondary">Cancel</Button>
                    </DialogTrigger>
                    <Button appearance="primary" icon={<AddRegular />} onClick={submitCreateDraft}>
                      Open draft
                    </Button>
                  </DialogActions>
                </DialogBody>
              </DialogSurface>
            </Dialog>

            <Dialog modalType="non-modal" open={deleteDialogOpen} onOpenChange={(_, data) => setDeleteDialogOpen(data.open)}>
              <DialogTrigger>
                <Button
                  appearance="secondary"
                  disabled={!selectedResource || isCreatingNew || deleteMutation.isPending}
                  icon={<DeleteRegular />}
                >
                  Delete
                </Button>
              </DialogTrigger>
              <DialogSurface backdrop={{ onClick: () => setDeleteDialogOpen(false) }}>
                <DialogBody>
                  <DialogTitle>Delete {singularLabel}</DialogTitle>
                  <DialogContent>
                    <Body1>
                      {selectedResource
                        ? `Delete ${singularLabel} “${selectedResource.id}”?`
                        : `Select a ${singularLabel} to delete.`}
                    </Body1>
                  </DialogContent>
                  <DialogActions>
                    <DialogTrigger>
                      <Button appearance="secondary">Cancel</Button>
                    </DialogTrigger>
                    <Button appearance="primary" icon={<DeleteRegular />} onClick={() => deleteMutation.mutate()}>
                      {deleteMutation.isPending ? 'Deleting…' : 'Delete'}
                    </Button>
                  </DialogActions>
                </DialogBody>
              </DialogSurface>
            </Dialog>
          </div>

          <div className={styles.list}>
            {resources.length === 0 ? (
              <Body1 className={styles.subtleText}>No {label.toLowerCase()} found yet.</Body1>
            ) : (
              resources.map((resource) => {
                const isSelected = !isCreatingNew && selectedResource?.id === resource.id
                return (
                  <Button
                    appearance={isSelected ? 'primary' : 'subtle'}
                    className={styles.resourceButton}
                    key={resource.id}
                    onClick={() => loadResource(resource)}
                  >
                    {resource.id}
                  </Button>
                )
              })
            )}
          </div>
        </Card>

        <div className={styles.mainColumn}>
          <Card className={styles.editorCard}>
            <div>
              <Subtitle2>Editor</Subtitle2>
              <Body1 className={styles.subtleText}>Use the resource form and Monaco editor to manage the selected JavaScript body.</Body1>
            </div>

            <div className={styles.formGrid}>
              <Field label="Resource id">
                <Input
                  disabled={!isDraftMode}
                  onChange={(_, data) => setResourceId(data.value)}
                  placeholder="Enter a resource id"
                  value={resourceId}
                />
              </Field>

              {isTrigger && (
                <Field label="Trigger type">
                  <Dropdown
                    onOptionSelect={(_, data) => {
                      if (data.optionValue) {
                        setTriggerType(data.optionValue as CosmosTrigger['triggerType'])
                      }
                    }}
                    selectedOptions={[triggerType]}
                    value={triggerType}
                  >
                    {triggerTypes.map((value) => (
                      <Option key={value} value={value}>
                        {value}
                      </Option>
                    ))}
                  </Dropdown>
                </Field>
              )}

              {isTrigger && (
                <Field label="Trigger operation">
                  <Dropdown
                    onOptionSelect={(_, data) => {
                      if (data.optionValue) {
                        setTriggerOperation(data.optionValue as CosmosTrigger['triggerOperation'])
                      }
                    }}
                    selectedOptions={[triggerOperation]}
                    value={triggerOperation}
                  >
                    {triggerOperations.map((value) => (
                      <Option key={value} value={value}>
                        {value}
                      </Option>
                    ))}
                  </Dropdown>
                </Field>
              )}
            </div>

            <div className={styles.editorFrame}>
              <Editor
                defaultLanguage="javascript"
                height="100%"
                onChange={(value) => setEditorValue(value ?? '')}
                options={{
                  automaticLayout: true,
                  fontSize: 14,
                  minimap: { enabled: false },
                  scrollBeyondLastLine: false,
                  wordWrap: 'on',
                }}
                theme={isDark ? 'vs-dark' : 'vs'}
                value={editorValue}
              />
            </div>
          </Card>

          <div className={styles.bottomGrid}>
            <Card className={styles.supportCard}>
              <Subtitle2>{isStoredProcedure ? 'Execution output' : 'Editor notes'}</Subtitle2>
              {isStoredProcedure ? (
                executionResult ? (
                  <pre className={styles.output}>{executionResult}</pre>
                ) : (
                  <Body1 className={styles.subtleText}>
                    Execute the selected stored procedure to inspect its result here.
                  </Body1>
                )
              ) : (
                <Body1 className={styles.subtleText}>
                  Bodies are saved as JavaScript resources in the selected container.
                </Body1>
              )}
            </Card>

            <Card className={styles.supportCard}>
              <Subtitle2>Metadata</Subtitle2>
              <div className={styles.metadataList}>
                {metadata.map((entry) => (
                  <Field key={entry.label} label={entry.label}>
                    <Input readOnly value={formatMetadataValue(entry.value)} />
                  </Field>
                ))}
                <Field label="Database">
                  <Input readOnly value={dbId} />
                </Field>
                <Field label="Container">
                  <Input readOnly value={collId} />
                </Field>
                <Field label="Mode">
                  <Input readOnly value={isDraftMode ? 'Creating new' : 'Editing existing'} />
                </Field>
              </div>
            </Card>
          </div>
        </div>
      </div>
    </section>
  )
}

async function listResources(
  resourceType: ProgrammabilityEditorProps['resourceType'],
  dbId: string,
  collId: string,
): Promise<FeedResponse<ProgrammabilityResource>> {
  switch (resourceType) {
    case 'sprocs':
      return cosmosClient.listStoredProcedures(dbId, collId)
    case 'triggers':
      return cosmosClient.listTriggers(dbId, collId)
    case 'udfs':
      return cosmosClient.listUdfs(dbId, collId)
  }
}

async function createResource(
  resourceType: ProgrammabilityEditorProps['resourceType'],
  dbId: string,
  collId: string,
  id: string,
  body: string,
  triggerOptions: Pick<CosmosTrigger, 'triggerOperation' | 'triggerType'>,
): Promise<ProgrammabilityResource> {
  switch (resourceType) {
    case 'sprocs':
      return cosmosClient.createStoredProcedure(dbId, collId, id, body)
    case 'triggers':
      return cosmosClient.createTrigger(dbId, collId, {
        id,
        body,
        triggerOperation: triggerOptions.triggerOperation,
        triggerType: triggerOptions.triggerType,
      })
    case 'udfs':
      return cosmosClient.createUdf(dbId, collId, id, body)
  }
}

async function replaceResource(
  resourceType: ProgrammabilityEditorProps['resourceType'],
  dbId: string,
  collId: string,
  id: string,
  body: string,
  triggerOptions: Pick<CosmosTrigger, 'triggerOperation' | 'triggerType'>,
): Promise<ProgrammabilityResource> {
  switch (resourceType) {
    case 'sprocs':
      return cosmosClient.replaceStoredProcedure(dbId, collId, id, body)
    case 'triggers':
      return cosmosClient.replaceTrigger(dbId, collId, id, {
        body,
        triggerOperation: triggerOptions.triggerOperation,
        triggerType: triggerOptions.triggerType,
      })
    case 'udfs':
      return cosmosClient.replaceUdf(dbId, collId, id, body)
  }
}

async function deleteResource(
  resourceType: ProgrammabilityEditorProps['resourceType'],
  dbId: string,
  collId: string,
  id: string,
): Promise<void> {
  switch (resourceType) {
    case 'sprocs':
      return cosmosClient.deleteStoredProcedure(dbId, collId, id)
    case 'triggers':
      return cosmosClient.deleteTrigger(dbId, collId, id)
    case 'udfs':
      return cosmosClient.deleteUdf(dbId, collId, id)
  }
}

function defaultBody(resourceType: ProgrammabilityEditorProps['resourceType']): string {
  switch (resourceType) {
    case 'sprocs':
      return [
        'function main() {',
        '  const context = getContext();',
        "  context.getResponse().setBody('Stored procedure result');",
        '}',
      ].join('\n')
    case 'triggers':
      return ['function trigger() {', '  // Add trigger logic here.', '}'].join('\n')
    case 'udfs':
      return ['function udf() {', '  return true;', '}'].join('\n')
  }
}

function isTriggerResource(resource: ProgrammabilityResource): resource is CosmosTrigger {
  return 'triggerType' in resource && 'triggerOperation' in resource
}

function parseJsonArray(value: string): unknown[] {
  const parsed = JSON.parse(value) as unknown
  if (!Array.isArray(parsed)) {
    throw new Error('Stored procedure arguments must be a JSON array.')
  }

  return parsed
}

function parseJsonValue(value: string): unknown {
  return JSON.parse(value)
}

function formatResult(value: unknown): string {
  if (typeof value === 'string') {
    return value
  }

  if (value === undefined) {
    return 'undefined'
  }

  return JSON.stringify(value, null, 2)
}

function formatMetadataValue(value: unknown): string {
  if (value === undefined) {
    return '—'
  }

  return typeof value === 'string' ? value : JSON.stringify(value)
}

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Unexpected error'
}
