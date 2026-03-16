import type {
  ActivityLogEntry,
  CosmosContainer,
  CosmosDatabase,
  CosmosDocument,
  CosmosQueryParameter,
  CosmosTrigger,
  EmulatorInfo,
  QueryExplainResult,
  EmulatorStats,
  FeedResponse,
  StoredProcedure,
  UserDefinedFunction,
} from '../types/cosmos'

const cosmosHeaders = {
  activityId: 'x-ms-activity-id',
  continuation: 'x-ms-continuation',
  enableCrossPartition: 'x-ms-documentdb-query-enablecrosspartition',
  ifMatch: 'if-match',
  isQuery: 'x-ms-documentdb-isquery',
  itemCount: 'x-ms-item-count',
  maxItemCount: 'x-ms-max-item-count',
  partitionKey: 'x-ms-documentdb-partitionkey',
  requestCharge: 'x-ms-request-charge',
  version: 'x-ms-version',
} as const

const systemProperties = new Set(['_attachments', '_etag', '_rid', '_self', '_ts'])

export class CosmosClient {
  private readonly baseUrl: string

  constructor(baseUrl = '') {
    this.baseUrl = baseUrl
  }

  async listDatabases(): Promise<FeedResponse<CosmosDatabase>> {
    const { body, headers } = await this.request<Record<string, unknown>>('/dbs')
    return this.toFeed<CosmosDatabase>(body, 'Databases', headers)
  }

  async getEmulatorInfo(): Promise<EmulatorInfo> {
    const { body } = await this.request<EmulatorInfo>('/api/emulator/info')
    return body
  }

  async getEmulatorStats(): Promise<EmulatorStats> {
    const { body } = await this.request<EmulatorStats>('/api/emulator/stats')
    return body
  }

  async getEmulatorActivity(): Promise<ActivityLogEntry[]> {
    const { body } = await this.request<ActivityLogEntry[]>('/api/emulator/activity')
    return body
  }

  async updateEmulatorSettings(settings: {
    enableEntraId?: boolean
    tenantId?: string | null
    clientId?: string | null
  }): Promise<EmulatorInfo> {
    const { body } = await this.request<EmulatorInfo>('/api/emulator/settings', {
      method: 'PUT',
      body: JSON.stringify(settings),
    })
    return body
  }

  async createDatabase(id: string): Promise<CosmosDatabase> {
    const { body } = await this.request<CosmosDatabase>('/dbs', {
      method: 'POST',
      body: JSON.stringify({ id }),
    })

    return body
  }

  async deleteDatabase(id: string): Promise<void> {
    await this.request(`/dbs/${encodeURIComponent(id)}`, { method: 'DELETE' })
  }

  async listContainers(dbId: string): Promise<FeedResponse<CosmosContainer>> {
    const { body, headers } = await this.request<Record<string, unknown>>(
      `/dbs/${encodeURIComponent(dbId)}/colls`,
    )

    return this.toFeed<CosmosContainer>(body, 'DocumentCollections', headers)
  }

  async createContainer(
    dbId: string,
    id: string,
    partitionKeyPaths: string[],
  ): Promise<CosmosContainer> {
    const { body } = await this.request<CosmosContainer>(
      `/dbs/${encodeURIComponent(dbId)}/colls`,
      {
        method: 'POST',
        body: JSON.stringify({
          id,
          partitionKey: {
            paths: partitionKeyPaths,
            kind: 'Hash',
            version: 2,
          },
        }),
      },
    )

    return body
  }

  async deleteContainer(dbId: string, collId: string): Promise<void> {
    await this.request(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}`,
      { method: 'DELETE' },
    )
  }

  async listDocuments(
    dbId: string,
    collId: string,
  ): Promise<FeedResponse<CosmosDocument>> {
    return this.executeQuery(dbId, collId, 'SELECT * FROM c')
  }

  async getDocument(
    dbId: string,
    collId: string,
    docId: string,
    partitionKey: unknown,
  ): Promise<CosmosDocument> {
    const { body } = await this.request<CosmosDocument>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/docs/${encodeURIComponent(docId)}`,
      {
        headers: {
          [cosmosHeaders.partitionKey]: this.toPartitionKeyHeader(partitionKey),
        },
      },
    )

    return body
  }

  async createDocument(
    dbId: string,
    collId: string,
    doc: CosmosDocument,
  ): Promise<CosmosDocument> {
    const { body } = await this.request<CosmosDocument>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/docs`,
      {
        method: 'POST',
        body: JSON.stringify(this.stripSystemProperties(doc)),
      },
    )

    return body
  }

  async replaceDocument(
    dbId: string,
    collId: string,
    docId: string,
    doc: CosmosDocument,
  ): Promise<CosmosDocument> {
    const headers: Record<string, string> = {}
    if (typeof doc._etag === 'string' && doc._etag.length > 0) {
      headers[cosmosHeaders.ifMatch] = doc._etag
    }

    const { body } = await this.request<CosmosDocument>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/docs/${encodeURIComponent(docId)}`,
      {
        method: 'PUT',
        headers,
        body: JSON.stringify(this.stripSystemProperties(doc)),
      },
    )

    return body
  }

  async deleteDocument(
    dbId: string,
    collId: string,
    docId: string,
    partitionKey: unknown,
  ): Promise<void> {
    await this.request(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/docs/${encodeURIComponent(docId)}`,
      {
        method: 'DELETE',
        headers: {
          [cosmosHeaders.partitionKey]: this.toPartitionKeyHeader(partitionKey),
        },
      },
    )
  }

  async executeQuery(
    dbId: string,
    collId: string,
    query: string,
    parameters: CosmosQueryParameter[] = [],
  ): Promise<FeedResponse<CosmosDocument>> {
    const { body, headers } = await this.request<Record<string, unknown>>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/docs`,
      {
        method: 'POST',
        headers: {
          [cosmosHeaders.enableCrossPartition]: 'true',
          [cosmosHeaders.isQuery]: 'true',
          [cosmosHeaders.maxItemCount]: '100',
        },
        body: JSON.stringify({ query, parameters }),
      },
    )

    return this.toFeed<CosmosDocument>(body, 'Documents', headers)
  }

  async explainQuery(dbId: string, collId: string, query: string): Promise<QueryExplainResult> {
    const { body } = await this.request<QueryExplainResult>('/api/emulator/explain', {
      method: 'POST',
      body: JSON.stringify({ databaseId: dbId, containerId: collId, query }),
    })
    return body
  }

  async listStoredProcedures(
    dbId: string,
    collId: string,
  ): Promise<FeedResponse<StoredProcedure>> {
    const { body, headers } = await this.request<Record<string, unknown>>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/sprocs`,
    )

    return this.toFeed<StoredProcedure>(body, 'StoredProcedures', headers)
  }

  async createStoredProcedure(
    dbId: string,
    collId: string,
    id: string,
    body: string,
  ): Promise<StoredProcedure> {
    const { body: responseBody } = await this.request<StoredProcedure>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/sprocs`,
      {
        method: 'POST',
        body: JSON.stringify({ id, body }),
      },
    )

    return responseBody
  }

  async replaceStoredProcedure(
    dbId: string,
    collId: string,
    id: string,
    body: string,
  ): Promise<StoredProcedure> {
    const { body: responseBody } = await this.request<StoredProcedure>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/sprocs/${encodeURIComponent(id)}`,
      {
        method: 'PUT',
        body: JSON.stringify({ id, body }),
      },
    )

    return responseBody
  }

  async deleteStoredProcedure(dbId: string, collId: string, sprocId: string): Promise<void> {
    await this.request(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/sprocs/${encodeURIComponent(sprocId)}`,
      { method: 'DELETE' },
    )
  }

  async executeStoredProcedure(
    dbId: string,
    collId: string,
    sprocId: string,
    args: unknown[],
    partitionKey?: unknown,
  ): Promise<unknown> {
    const headers: Record<string, string> = {}
    if (partitionKey !== undefined) {
      headers[cosmosHeaders.partitionKey] = this.toPartitionKeyHeader(partitionKey)
    }

    const { body } = await this.request<unknown>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/sprocs/${encodeURIComponent(sprocId)}`,
      {
        method: 'POST',
        headers,
        body: JSON.stringify(args),
      },
    )

    return body
  }

  async listTriggers(dbId: string, collId: string): Promise<FeedResponse<CosmosTrigger>> {
    const { body, headers } = await this.request<Record<string, unknown>>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/triggers`,
    )

    return this.toFeed<CosmosTrigger>(body, 'Triggers', headers)
  }

  async createTrigger(
    dbId: string,
    collId: string,
    trigger: { id: string; body: string; triggerType: string; triggerOperation: string },
  ): Promise<CosmosTrigger> {
    const { body } = await this.request<CosmosTrigger>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/triggers`,
      {
        method: 'POST',
        body: JSON.stringify(trigger),
      },
    )

    return body
  }

  async replaceTrigger(
    dbId: string,
    collId: string,
    id: string,
    trigger: { body: string; triggerType: string; triggerOperation: string },
  ): Promise<CosmosTrigger> {
    const { body } = await this.request<CosmosTrigger>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/triggers/${encodeURIComponent(id)}`,
      {
        method: 'PUT',
        body: JSON.stringify({ id, ...trigger }),
      },
    )

    return body
  }

  async deleteTrigger(dbId: string, collId: string, triggerId: string): Promise<void> {
    await this.request(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/triggers/${encodeURIComponent(triggerId)}`,
      { method: 'DELETE' },
    )
  }

  async listUdfs(dbId: string, collId: string): Promise<FeedResponse<UserDefinedFunction>> {
    const { body, headers } = await this.request<Record<string, unknown>>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/udfs`,
    )

    return this.toFeed<UserDefinedFunction>(body, 'UserDefinedFunctions', headers)
  }

  async createUdf(
    dbId: string,
    collId: string,
    id: string,
    body: string,
  ): Promise<UserDefinedFunction> {
    const { body: responseBody } = await this.request<UserDefinedFunction>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/udfs`,
      {
        method: 'POST',
        body: JSON.stringify({ id, body }),
      },
    )

    return responseBody
  }

  async replaceUdf(
    dbId: string,
    collId: string,
    id: string,
    body: string,
  ): Promise<UserDefinedFunction> {
    const { body: responseBody } = await this.request<UserDefinedFunction>(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/udfs/${encodeURIComponent(id)}`,
      {
        method: 'PUT',
        body: JSON.stringify({ id, body }),
      },
    )

    return responseBody
  }

  async deleteUdf(dbId: string, collId: string, udfId: string): Promise<void> {
    await this.request(
      `/dbs/${encodeURIComponent(dbId)}/colls/${encodeURIComponent(collId)}/udfs/${encodeURIComponent(udfId)}`,
      { method: 'DELETE' },
    )
  }

  async getDatabaseThroughput(dbId: string): Promise<{ id: string; maxThroughput: number | null }> {
    const { body } = await this.request<{ id: string; maxThroughput: number | null }>(
      `/api/emulator/throughput/database/${encodeURIComponent(dbId)}`,
    )
    return body
  }

  async updateDatabaseThroughput(
    dbId: string,
    maxThroughput: number | null,
  ): Promise<{ id: string; maxThroughput: number | null }> {
    const { body } = await this.request<{ id: string; maxThroughput: number | null }>(
      `/api/emulator/throughput/database/${encodeURIComponent(dbId)}`,
      {
        method: 'PUT',
        body: JSON.stringify({ maxThroughput }),
      },
    )
    return body
  }

  async getContainerThroughput(
    dbId: string,
    collId: string,
  ): Promise<{ id: string; databaseId: string; maxThroughput: number }> {
    const { body } = await this.request<{ id: string; databaseId: string; maxThroughput: number }>(
      `/api/emulator/throughput/container/${encodeURIComponent(dbId)}/${encodeURIComponent(collId)}`,
    )
    return body
  }

  async updateContainerThroughput(
    dbId: string,
    collId: string,
    maxThroughput: number | null,
  ): Promise<{ id: string; databaseId: string; maxThroughput: number }> {
    const { body } = await this.request<{ id: string; databaseId: string; maxThroughput: number }>(
      `/api/emulator/throughput/container/${encodeURIComponent(dbId)}/${encodeURIComponent(collId)}`,
      {
        method: 'PUT',
        body: JSON.stringify({ maxThroughput }),
      },
    )
    return body
  }

  private async request<T = undefined>(
    path: string,
    init: RequestInit = {},
  ): Promise<{ body: T; headers: Headers }> {
    const headers = new Headers(init.headers)
    headers.set('accept', 'application/json')
    headers.set(cosmosHeaders.version, '2024-11-30')
    headers.set('x-ms-cosmos-explorer', 'true')

    if (init.body !== undefined && !headers.has('content-type')) {
      headers.set('content-type', 'application/json')
    }

    const response = await fetch(`${this.baseUrl}${path}`, {
      ...init,
      credentials: 'same-origin',
      headers,
    })

    const text = await response.text()

    if (!response.ok) {
      throw new Error(this.toErrorMessage(response.status, response.statusText, text))
    }

    const body = (text ? JSON.parse(text) : undefined) as T
    return { body, headers: response.headers }
  }

  private stripSystemProperties(doc: CosmosDocument): Record<string, unknown> {
    return Object.entries(doc).reduce<Record<string, unknown>>((clean, [key, value]) => {
      if (!systemProperties.has(key)) {
        clean[key] = value
      }

      return clean
    }, {})
  }

  private toFeed<T>(
    body: Record<string, unknown>,
    resourceProperty: string,
    headers: Headers,
  ): FeedResponse<T> {
    const rawItems = body[resourceProperty]
    const items = Array.isArray(rawItems) ? (rawItems as T[]) : []
    const count = typeof body._count === 'number' ? body._count : items.length
    const headerItemCount = headers.get(cosmosHeaders.itemCount)

    return {
      _rid: typeof body._rid === 'string' ? body._rid : '',
      _count: count,
      items,
      requestCharge: Number(headers.get(cosmosHeaders.requestCharge) ?? '0'),
      activityId: headers.get(cosmosHeaders.activityId) ?? '',
      continuationToken: headers.get(cosmosHeaders.continuation),
      itemCount: headerItemCount ? Number(headerItemCount) : count,
    }
  }

  private toPartitionKeyHeader(partitionKey: unknown): string {
    if (Array.isArray(partitionKey)) {
      return JSON.stringify(partitionKey)
    }

    return JSON.stringify([partitionKey])
  }

  private toErrorMessage(status: number, statusText: string, payload: string): string {
    if (!payload) {
      return `${status} ${statusText}`
    }

    try {
      const error = JSON.parse(payload) as { code?: string; message?: string }
      return error.message ?? error.code ?? payload
    } catch {
      return payload
    }
  }
}

export const cosmosClient = new CosmosClient()
