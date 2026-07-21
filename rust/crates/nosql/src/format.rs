//! Resource → JSON response formatters. Port the controllers' `FormatX`
//! helpers.
//!
//! The .NET host configures `PropertyNamingPolicy = null` with no global enum
//! converter, so nested policy objects serialize with **PascalCase** property
//! names and enums as **integers**. These helpers reproduce that exact shape
//! for wire parity with the emulator.

use cosmos_core::models::policies::{IndexingMode, IndexingPolicy, SortOrder, SpatialType};
use cosmos_core::models::{
    CosmosContainer, CosmosDatabase, CosmosOffer, CosmosPermission, CosmosUser, PartitionKeyKind,
    PermissionMode, UniqueKeyPolicy, VectorEmbeddingPolicy,
};
use serde_json::{json, Value};

pub fn format_database(db: &CosmosDatabase) -> Value {
    json!({
        "id": db.id,
        "_rid": db.rid,
        "_self": db.self_link(),
        "_etag": db.etag,
        "_ts": db.timestamp,
        "_colls": "colls/",
        "_users": "users/",
        "maxThroughput": db.max_throughput,
    })
}

pub fn format_container(c: &CosmosContainer) -> Value {
    json!({
        "id": c.id,
        "_rid": c.rid,
        "_self": container_self(c),
        "_etag": c.etag,
        "_ts": c.timestamp,
        "_docs": "docs/",
        "_sprocs": "sprocs/",
        "_triggers": "triggers/",
        "_udfs": "udfs/",
        "_conflicts": "conflicts/",
        "partitionKey": {
            "paths": c.partition_key.paths,
            "kind": partition_key_kind_str(c.partition_key.kind),
            "version": c.partition_key.version,
        },
        "indexingPolicy": indexing_policy_json(&c.indexing_policy),
        "uniqueKeyPolicy": c.unique_key_policy.as_ref().map(unique_key_policy_json),
        "vectorEmbeddingPolicy": c.vector_embedding_policy.as_ref().map(vector_embedding_policy_json),
        "defaultTtl": c.default_time_to_live,
        "maxThroughput": c.max_throughput,
    })
}

pub fn format_user(u: &CosmosUser) -> Value {
    json!({
        "id": u.id,
        "_rid": u.rid,
        "_self": format!("dbs/{}/users/{}/", u.database_id, u.id),
        "_etag": u.etag,
        "_ts": u.timestamp,
        "_permissions": "permissions/",
    })
}

pub fn format_permission(p: &CosmosPermission) -> Value {
    json!({
        "id": p.id,
        "_rid": p.rid,
        "_self": format!("dbs/{}/users/{}/permissions/{}/", p.database_id, p.user_id, p.id),
        "_etag": p.etag,
        "_ts": p.timestamp,
        "permissionMode": permission_mode_str(p.permission_mode),
        "resource": p.resource,
        "_token": p.token,
    })
}

pub fn format_offer(o: &CosmosOffer) -> Value {
    json!({
        "offerVersion": o.offer_version,
        "offerType": o.offer_type,
        "content": { "offerThroughput": o.content.offer_throughput },
        "resource": o.resource,
        "offerResourceId": o.offer_resource_id,
        "id": o.id,
        "_rid": o.rid,
        "_self": o.self_link(),
        "_etag": o.etag,
        "_ts": o.timestamp,
    })
}

fn container_self(c: &CosmosContainer) -> String {
    if c.self_link.is_empty() {
        format!("dbs/{}/colls/{}/", c.database_id, c.id)
    } else {
        c.self_link.clone()
    }
}

fn partition_key_kind_str(kind: PartitionKeyKind) -> &'static str {
    match kind {
        PartitionKeyKind::Hash => "Hash",
        PartitionKeyKind::Range => "Range",
        PartitionKeyKind::MultiHash => "MultiHash",
    }
}

fn permission_mode_str(mode: PermissionMode) -> &'static str {
    match mode {
        PermissionMode::Read => "Read",
        PermissionMode::All => "All",
    }
}

fn indexing_policy_json(p: &IndexingPolicy) -> Value {
    json!({
        "Automatic": p.automatic,
        "IndexingMode": indexing_mode_int(p.indexing_mode),
        "IncludedPaths": p.included_paths.iter().map(|ip| json!({ "Path": ip.path })).collect::<Vec<_>>(),
        "ExcludedPaths": p.excluded_paths.iter().map(|ep| json!({ "Path": ep.path })).collect::<Vec<_>>(),
        "CompositeIndexes": p.composite_indexes.as_ref().map(|indexes| {
            indexes
                .iter()
                .map(|ci| {
                    json!({
                        "Paths": ci.paths.iter().map(|cp| json!({
                            "Path": cp.path,
                            "Order": sort_order_int(cp.order),
                        })).collect::<Vec<_>>()
                    })
                })
                .collect::<Vec<_>>()
        }),
        "SpatialIndexes": p.spatial_indexes.as_ref().map(|indexes| {
            indexes
                .iter()
                .map(|si| {
                    json!({
                        "Path": si.path,
                        "Types": si.types.iter().map(|t| spatial_type_int(*t)).collect::<Vec<_>>(),
                    })
                })
                .collect::<Vec<_>>()
        }),
        "VectorIndexes": p.vector_indexes.as_ref().map(|indexes| {
            indexes
                .iter()
                .map(|vi| json!({ "Path": vi.path, "Type": vi.index_type }))
                .collect::<Vec<_>>()
        }),
    })
}

fn unique_key_policy_json(p: &UniqueKeyPolicy) -> Value {
    json!({
        "UniqueKeys": p.unique_keys.iter().map(|uk| json!({ "Paths": uk.paths })).collect::<Vec<_>>(),
    })
}

fn vector_embedding_policy_json(p: &VectorEmbeddingPolicy) -> Value {
    json!({
        "VectorEmbeddings": p.vector_embeddings.iter().map(|ve| json!({
            "Path": ve.path,
            "DataType": ve.data_type,
            "DistanceFunction": ve.distance_function,
            "Dimensions": ve.dimensions,
        })).collect::<Vec<_>>(),
    })
}

fn indexing_mode_int(mode: IndexingMode) -> i32 {
    match mode {
        IndexingMode::Consistent => 0,
        IndexingMode::Lazy => 1,
        IndexingMode::None => 2,
    }
}

fn sort_order_int(order: SortOrder) -> i32 {
    match order {
        SortOrder::Ascending => 0,
        SortOrder::Descending => 1,
    }
}

fn spatial_type_int(t: SpatialType) -> i32 {
    match t {
        SpatialType::Point => 0,
        SpatialType::Polygon => 1,
        SpatialType::MultiPolygon => 2,
        SpatialType::LineString => 3,
    }
}
