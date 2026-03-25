# API Compatibility

## Cosmos DB REST API Coverage

### Databases
| Operation | Status | Notes |
|---|---|---|
| Create Database | ✅ | POST /dbs |
| List Databases | ✅ | GET /dbs |
| Get Database | ✅ | GET /dbs/{id} |
| Delete Database | ✅ | Cascade deletes containers/docs |

### Containers (Collections)
| Operation | Status | Notes |
|---|---|---|
| Create Container | ✅ | Partition key definition required |
| List Containers | ✅ | |
| Get Container | ✅ | |
| Replace Container | ✅ | Indexing policy, TTL |
| Delete Container | ✅ | Cascade deletes documents |

### Documents
| Operation | Status | Notes |
|---|---|---|
| Create Document | ✅ | Auto partition key extraction |
| Read Document | ✅ | Partition key header required |
| Replace Document | ✅ | ETag / If-Match support |
| Upsert Document | ✅ | x-ms-documentdb-is-upsert header |
| Delete Document | ✅ | |
| Query Documents | ✅ | Cosmos SQL subset |

### Stored Procedures
| Operation | Status | Notes |
|---|---|---|
| Create | ✅ | |
| List | ✅ | |
| Execute | ⚠️ | Basic Jint execution, limited context API |
| Replace | ✅ | |
| Delete | ✅ | |

### Triggers
| Operation | Status | Notes |
|---|---|---|
| Create | ✅ | Pre/Post, All/Create/Replace/Delete |
| List | ✅ | |
| Replace | ✅ | |
| Delete | ✅ | |

### User-Defined Functions
| Operation | Status | Notes |
|---|---|---|
| Create | ✅ | |
| List | ✅ | |
| Replace | ✅ | |
| Delete | ✅ | |

### Change Feed
| Operation | Status | Notes |
|---|---|---|
| Read Change Feed | ✅ | A-IM: Incremental feed header |
| Continuation tokens | ✅ | LSN-based |
| Full fidelity mode | ✅ | All versions and deletes |

### Headers
| Header | Status |
|---|---|
| x-ms-request-charge | ✅ (fixed value) |
| x-ms-activity-id | ✅ |
| x-ms-session-token | ✅ |
| x-ms-continuation | ✅ |
| x-ms-item-count | ✅ |
| ETag / If-Match | ✅ |

### Spatial Functions
| Function | Status | Notes |
|---|---|---|
| ST_DISTANCE | ✅ | Returns distance in meters (Haversine/WGS84) |
| ST_WITHIN | ✅ | Point/LineString/Polygon within geometry |
| ST_INTERSECTS | ✅ | Geometry intersection check |
| ST_ISVALID | ✅ | GeoJSON validation with coordinate range checks |
| ST_ISVALIDDETAILED | ✅ | Returns { valid, reason } object |
| ST_AREA | ✅ | Returns area in square meters (spherical) |

## Known Limitations

- **Request Units**: Always returns a fixed charge (not metered)
- **Stored Procedure Context**: Limited `__` context object API
- **Spatial Indexes**: Spatial index metadata is stored but not used for query optimization (spatial functions work via full scan)
- **Vector Search**: VectorDistance (brute-force), vector embedding policy, vector indexes (flat/quantizedFlat/diskANN)
- **Cross-region replication**: Not applicable (single-node)
- **Conflict resolution**: Simplified (single-node has no conflicts)
