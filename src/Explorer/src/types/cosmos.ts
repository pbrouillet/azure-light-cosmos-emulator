export interface PartitionKeyDefinition {
  paths: string[]
  kind: 'Hash' | 'Range' | 'MultiHash' | string
  version: number
}

export interface CosmosDatabase {
  id: string
  _rid: string
  _self: string
  _etag: string
  _ts: number
  _colls: string
  _users: string
  maxThroughput?: number | null
}

export interface CosmosContainer {
  id: string
  _rid: string
  _self: string
  _etag: string
  _ts: number
  partitionKey: PartitionKeyDefinition
  indexingPolicy?: unknown
  defaultTtl?: number | null
  maxThroughput?: number | null
}

export interface CosmosDocument {
  id: string
  _rid?: string
  _self?: string
  _etag?: string
  _ts?: number
  _attachments?: string
  [key: string]: unknown
}

export interface StoredProcedure {
  id: string
  _rid?: string
  _self?: string
  _etag?: string
  _ts?: number
  body: string
}

export interface CosmosTrigger {
  id: string
  _rid?: string
  _self?: string
  _etag?: string
  _ts?: number
  body: string
  triggerType: 'Pre' | 'Post'
  triggerOperation: 'All' | 'Create' | 'Replace' | 'Delete'
}

export interface UserDefinedFunction {
  id: string
  _rid?: string
  _self?: string
  _etag?: string
  _ts?: number
  body: string
}

export interface FeedResponse<T> {
  _rid: string
  _count: number
  items: T[]
  requestCharge: number
  activityId: string
  continuationToken?: string | null
  itemCount: number
}

export interface CosmosQueryParameter {
  name: string
  value: unknown
}

export interface QueryExplainResult {
  query: string
  queryPlan: Record<string, unknown>
  estimatedRuCharge: {
    base: number
    filterCost: number
    joinCost: number
    aggregateCost: number
    orderByCost: number
    crossPartitionMultiplier: number
    total: number
  }
  indexAnalysis: {
    usedIndexes: string[]
    recommendations: string[]
    indexingPolicyPaths: { included: string[]; excluded: string[] }
  }
  warnings: string[]
  educationalNotes: string[]
}

export interface EmulatorInfo {
  name: string
  version: string
  endpoints: {
    noSql: string
    mongoDb: string
    explorer: string
  }
  connectionString: string
  masterKey: string
  configuration: {
    port: number
    mongoPort: number
    dataDirectory: string
    consistencyLevel: string
    enableSsl: boolean
    enableExplorer: boolean
    enableEntraId: boolean
    tenantId: string | null
    clientId: string | null
  }
}

export interface EmulatorStats {
  totalRequestUnits: number
  totalRequests: number
  databaseCount: number
  containerCount: number
  documentCount: number
  dataDirectory: string
  dataSizeBytes: number
  uptimeSeconds: number
}

export interface ActivityLogEntry {
  timestamp: string
  method: string
  path: string
  statusCode: number
  requestCharge: number
  latencyMs: number
  databaseId: string | null
  containerId: string | null
}
