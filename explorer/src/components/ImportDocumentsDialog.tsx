import { useCallback, useRef, useState } from 'react'
import type { ChangeEvent } from 'react'
import Editor from '@monaco-editor/react'
import {
  Body1,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  ProgressBar,
  Spinner,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components'
import { ArrowUploadRegular, DocumentRegular } from '@fluentui/react-icons'
import { cosmosClient } from '../api/cosmosClient'
import { useTheme } from '../theme'
import type { CosmosDocument } from '../types/cosmos'

interface ImportDocumentsDialogProps {
  open: boolean
  dbId: string
  collId: string
  onClose: () => void
  onImportComplete: () => void
}

interface ImportProgress {
  status: 'importing' | 'done'
  total: number
  completed: number
  failed: number
  errors: string[]
}

const editorTemplate = `[
  {
    "id": "example-1",
    "key": "value"
  }
]`

const useStyles = makeStyles({
  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: '560px',
  },
  editorFrame: {
    height: '400px',
    overflow: 'hidden',
    borderRadius: tokens.borderRadiusMedium,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  fileZone: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: tokens.spacingVerticalM,
    height: '200px',
    borderRadius: tokens.borderRadiusMedium,
    border: `${tokens.strokeWidthThick} dashed ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground3,
    cursor: 'pointer',
  },
  fileInfo: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalXS,
  },
  progressSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  errorList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    maxHeight: '120px',
    overflowY: 'auto',
  },
})

type InputMode = 'editor' | 'file'

export function ImportDocumentsDialog({
  open,
  dbId,
  collId,
  onClose,
  onImportComplete,
}: ImportDocumentsDialogProps) {
  const styles = useStyles()
  const { isDark } = useTheme()
  const fileInputRef = useRef<HTMLInputElement>(null)

  const [mode, setMode] = useState<InputMode>('editor')
  const [editorValue, setEditorValue] = useState(editorTemplate)
  const [fileDocuments, setFileDocuments] = useState<CosmosDocument[] | null>(null)
  const [fileName, setFileName] = useState<string | null>(null)
  const [parseError, setParseError] = useState<string | null>(null)
  const [progress, setProgress] = useState<ImportProgress | null>(null)

  const resetState = useCallback(() => {
    setMode('editor')
    setEditorValue(editorTemplate)
    setFileDocuments(null)
    setFileName(null)
    setParseError(null)
    setProgress(null)
  }, [])

  const handleClose = useCallback(() => {
    if (progress?.status === 'importing') return
    resetState()
    onClose()
  }, [progress, resetState, onClose])

  const handleFileSelected = useCallback((event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    if (!file) return

    setParseError(null)
    setFileName(file.name)

    file.text().then((text) => {
      try {
        const documents = parseDocumentArray(text)
        setFileDocuments(documents)
      } catch (err) {
        setFileDocuments(null)
        setParseError(err instanceof Error ? err.message : 'Failed to parse JSON file.')
      }
    }).catch(() => {
      setFileDocuments(null)
      setParseError('Failed to read the file.')
    })
  }, [])

  const handleImport = useCallback(async () => {
    setParseError(null)

    let documents: CosmosDocument[]
    try {
      if (mode === 'editor') {
        documents = parseDocumentArray(editorValue)
      } else {
        if (!fileDocuments || fileDocuments.length === 0) {
          setParseError('No documents to import. Select a valid JSON file.')
          return
        }
        documents = fileDocuments
      }
    } catch (err) {
      setParseError(err instanceof Error ? err.message : 'Failed to parse documents.')
      return
    }

    if (documents.length === 0) {
      setParseError('The JSON contains no documents to import.')
      return
    }

    let completed = 0
    let failed = 0
    const errors: string[] = []

    setProgress({ status: 'importing', total: documents.length, completed: 0, failed: 0, errors: [] })

    for (const doc of documents) {
      try {
        await cosmosClient.upsertDocument(dbId, collId, doc)
        completed++
      } catch (err) {
        failed++
        const docId = typeof doc?.id === 'string' ? doc.id : '(unknown id)'
        errors.push(`${docId}: ${err instanceof Error ? err.message : 'Unknown error'}`)
      }

      setProgress({
        status: 'importing',
        total: documents.length,
        completed,
        failed,
        errors: errors.slice(),
      })
    }

    setProgress({
      status: 'done',
      total: documents.length,
      completed,
      failed,
      errors: errors.slice(),
    })

    onImportComplete()
  }, [mode, editorValue, fileDocuments, dbId, collId, onImportComplete])

  const documentCount = mode === 'editor' ? tryCountDocuments(editorValue) : (fileDocuments?.length ?? 0)
  const hasDocuments = documentCount > 0
  const isImporting = progress?.status === 'importing'
  const isDone = progress?.status === 'done'

  return (
    <Dialog
      modalType="non-modal"
      open={open}
      onOpenChange={(_, data) => {
        if (!data.open) handleClose()
      }}
    >
      <DialogSurface backdrop={!isImporting ? { onClick: handleClose } : undefined}>
        <DialogBody>
          <DialogTitle>Import Documents</DialogTitle>
          <DialogContent>
            <div className={styles.content}>
              <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                {dbId} / {collId}
              </Text>

              {!isDone && (
                <>
                  <TabList
                    selectedValue={mode}
                    onTabSelect={(_, data) => {
                      setMode(data.value as InputMode)
                      setParseError(null)
                    }}
                    disabled={isImporting}
                  >
                    <Tab value="editor">JSON Editor</Tab>
                    <Tab value="file">File Upload</Tab>
                  </TabList>

                  {mode === 'editor' && (
                    <div className={styles.editorFrame}>
                      <Editor
                        defaultLanguage="json"
                        height="100%"
                        onChange={(value) => {
                          setEditorValue(value ?? '[]')
                          setParseError(null)
                        }}
                        options={{
                          automaticLayout: true,
                          fontSize: 14,
                          formatOnPaste: true,
                          minimap: { enabled: false },
                          scrollBeyondLastLine: false,
                          wordWrap: 'on',
                          readOnly: isImporting,
                        }}
                        theme={isDark ? 'vs-dark' : 'vs'}
                        value={editorValue}
                      />
                    </div>
                  )}

                  {mode === 'file' && (
                    <>
                      <div
                        className={styles.fileZone}
                        onClick={() => !isImporting && fileInputRef.current?.click()}
                      >
                        {fileName ? (
                          <div className={styles.fileInfo}>
                            <DocumentRegular fontSize={32} />
                            <Text weight="semibold">{fileName}</Text>
                            {fileDocuments && (
                              <Text size={200}>
                                {fileDocuments.length} document{fileDocuments.length !== 1 ? 's' : ''} found
                              </Text>
                            )}
                          </div>
                        ) : (
                          <>
                            <ArrowUploadRegular fontSize={32} />
                            <Body1>Click to select a JSON file</Body1>
                            <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                              Accepts a JSON array or an object with a "documents" array
                            </Text>
                          </>
                        )}
                      </div>
                      <input
                        ref={fileInputRef}
                        type="file"
                        accept=".json"
                        style={{ display: 'none' }}
                        onChange={handleFileSelected}
                      />
                    </>
                  )}

                  {hasDocuments && !isImporting && (
                    <Text size={200}>
                      {documentCount} document{documentCount !== 1 ? 's' : ''} ready to import
                    </Text>
                  )}
                </>
              )}

              {parseError && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Parse error</MessageBarTitle>
                    {parseError}
                  </MessageBarBody>
                </MessageBar>
              )}

              {progress && (
                <div className={styles.progressSection}>
                  {isImporting && (
                    <>
                      <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS }}>
                        <Spinner size="tiny" />
                        <Text>
                          Importing {progress.completed + progress.failed} / {progress.total}…
                        </Text>
                      </div>
                      <ProgressBar
                        value={(progress.completed + progress.failed) / progress.total}
                      />
                    </>
                  )}

                  {isDone && (
                    <>
                      <Text block>
                        {progress.completed} of {progress.total} document
                        {progress.total !== 1 ? 's' : ''} imported successfully.
                      </Text>
                      {progress.failed > 0 && (
                        <MessageBar intent="warning" layout="multiline">
                          <MessageBarBody>
                            <MessageBarTitle>
                              {progress.failed} document{progress.failed !== 1 ? 's' : ''} failed
                            </MessageBarTitle>
                            <div className={styles.errorList}>
                              {progress.errors.slice(0, 10).map((err, i) => (
                                <Text key={i} block size={200}>
                                  {err}
                                </Text>
                              ))}
                              {progress.errors.length > 10 && (
                                <Text block size={200}>
                                  …and {progress.errors.length - 10} more
                                </Text>
                              )}
                            </div>
                          </MessageBarBody>
                        </MessageBar>
                      )}
                      {progress.failed === 0 && progress.total > 0 && (
                        <MessageBar intent="success">
                          <MessageBarBody>
                            <MessageBarTitle>Success</MessageBarTitle>
                            All documents were imported successfully.
                          </MessageBarBody>
                        </MessageBar>
                      )}
                    </>
                  )}
                </div>
              )}
            </div>
          </DialogContent>

          <DialogActions>
            {isDone ? (
              <Button appearance="primary" onClick={handleClose}>
                Close
              </Button>
            ) : (
              <>
                <Button appearance="secondary" onClick={handleClose} disabled={isImporting}>
                  Cancel
                </Button>
                <Button
                  appearance="primary"
                  disabled={!hasDocuments || isImporting}
                  icon={isImporting ? undefined : <ArrowUploadRegular />}
                  onClick={() => void handleImport()}
                >
                  {isImporting ? 'Importing…' : 'Import'}
                </Button>
              </>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

function parseDocumentArray(text: string): CosmosDocument[] {
  const data: unknown = JSON.parse(text)

  if (Array.isArray(data)) {
    return data as CosmosDocument[]
  }

  if (data !== null && typeof data === 'object' && Array.isArray((data as Record<string, unknown>).documents)) {
    return (data as Record<string, unknown>).documents as CosmosDocument[]
  }

  throw new Error(
    'Expected a JSON array of documents, or an object with a "documents" array property.',
  )
}

function tryCountDocuments(text: string): number {
  try {
    return parseDocumentArray(text).length
  } catch {
    return 0
  }
}
