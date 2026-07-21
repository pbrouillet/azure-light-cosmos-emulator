//! Lenient parsers for container policy sub-documents from request bodies.
//! Ports the `JsonSerializer.Deserialize<...>` calls in `ContainersController`
//! (which use case-insensitive, camelCase-enum options).

use cosmos_core::models::policies::{
    CompositeIndex, CompositeIndexPath, ExcludedPath, IncludedPath, IndexingMode, IndexingPolicy,
    SortOrder, SpatialIndex, SpatialType, UniqueKey, UniqueKeyPolicy, VectorEmbedding,
    VectorEmbeddingPolicy, VectorIndex,
};
use cosmos_core::models::{PartitionKeyDefinition, PartitionKeyKind};
use serde_json::Value;

pub fn parse_partition_key(node: &Value) -> PartitionKeyDefinition {
    let obj = match node.as_object() {
        Some(o) => o,
        None => return PartitionKeyDefinition::new(vec!["/id".to_string()]),
    };
    let paths: Vec<String> = obj
        .get("paths")
        .and_then(|v| v.as_array())
        .map(|arr| {
            arr.iter()
                .filter_map(|p| p.as_str())
                .filter(|p| !p.is_empty())
                .map(|p| p.to_string())
                .collect()
        })
        .unwrap_or_else(|| vec!["/id".to_string()]);
    let paths = if paths.is_empty() {
        vec!["/id".to_string()]
    } else {
        paths
    };
    let kind = match obj.get("kind").and_then(|v| v.as_str()) {
        Some("MultiHash") => PartitionKeyKind::MultiHash,
        Some("Range") => PartitionKeyKind::Range,
        _ => PartitionKeyKind::Hash,
    };
    let version = obj.get("version").and_then(|v| v.as_i64()).unwrap_or(2) as i32;
    PartitionKeyDefinition {
        paths,
        kind,
        version,
    }
}

pub fn parse_indexing_policy(node: &Value) -> IndexingPolicy {
    let mut policy = IndexingPolicy::default();
    let obj = match node.as_object() {
        Some(o) => o,
        None => return policy,
    };
    if let Some(a) = obj.get("automatic").and_then(|v| v.as_bool()) {
        policy.automatic = a;
    }
    if let Some(mode) = obj.get("indexingMode").and_then(|v| v.as_str()) {
        policy.indexing_mode = match mode.to_ascii_lowercase().as_str() {
            "lazy" => IndexingMode::Lazy,
            "none" => IndexingMode::None,
            _ => IndexingMode::Consistent,
        };
    }
    if let Some(paths) = obj.get("includedPaths").and_then(|v| v.as_array()) {
        policy.included_paths = paths
            .iter()
            .filter_map(|p| p.get("path").and_then(|v| v.as_str()))
            .map(|p| IncludedPath {
                path: p.to_string(),
            })
            .collect();
    }
    if let Some(paths) = obj.get("excludedPaths").and_then(|v| v.as_array()) {
        policy.excluded_paths = paths
            .iter()
            .filter_map(|p| p.get("path").and_then(|v| v.as_str()))
            .map(|p| ExcludedPath {
                path: p.to_string(),
            })
            .collect();
    }
    if let Some(indexes) = obj.get("compositeIndexes").and_then(|v| v.as_array()) {
        policy.composite_indexes = Some(
            indexes
                .iter()
                .map(|ci| CompositeIndex {
                    paths: ci
                        .get("paths")
                        .and_then(|v| v.as_array())
                        .map(|arr| {
                            arr.iter()
                                .filter_map(|cp| {
                                    cp.get("path").and_then(|v| v.as_str()).map(|path| {
                                        let order = match cp
                                            .get("order")
                                            .and_then(|v| v.as_str())
                                            .map(|s| s.to_ascii_lowercase())
                                            .as_deref()
                                        {
                                            Some("descending") => SortOrder::Descending,
                                            _ => SortOrder::Ascending,
                                        };
                                        CompositeIndexPath {
                                            path: path.to_string(),
                                            order,
                                        }
                                    })
                                })
                                .collect()
                        })
                        .unwrap_or_default(),
                })
                .collect(),
        );
    }
    if let Some(indexes) = obj.get("spatialIndexes").and_then(|v| v.as_array()) {
        policy.spatial_indexes = Some(
            indexes
                .iter()
                .filter_map(|si| {
                    si.get("path")
                        .and_then(|v| v.as_str())
                        .map(|path| SpatialIndex {
                            path: path.to_string(),
                            types: si
                                .get("types")
                                .and_then(|v| v.as_array())
                                .map(|arr| {
                                    arr.iter()
                                        .filter_map(|t| t.as_str())
                                        .filter_map(parse_spatial_type)
                                        .collect()
                                })
                                .unwrap_or_default(),
                        })
                })
                .collect(),
        );
    }
    if let Some(indexes) = obj.get("vectorIndexes").and_then(|v| v.as_array()) {
        policy.vector_indexes = Some(
            indexes
                .iter()
                .filter_map(|vi| {
                    vi.get("path")
                        .and_then(|v| v.as_str())
                        .map(|path| VectorIndex {
                            path: path.to_string(),
                            index_type: vi
                                .get("type")
                                .and_then(|v| v.as_str())
                                .unwrap_or("flat")
                                .to_string(),
                        })
                })
                .collect(),
        );
    }
    policy
}

pub fn parse_unique_key_policy(node: &Value) -> UniqueKeyPolicy {
    let unique_keys = node
        .get("uniqueKeys")
        .and_then(|v| v.as_array())
        .map(|arr| {
            arr.iter()
                .map(|uk| UniqueKey {
                    paths: uk
                        .get("paths")
                        .and_then(|v| v.as_array())
                        .map(|paths| {
                            paths
                                .iter()
                                .filter_map(|p| p.as_str())
                                .map(|p| p.to_string())
                                .collect()
                        })
                        .unwrap_or_default(),
                })
                .collect()
        })
        .unwrap_or_default();
    UniqueKeyPolicy { unique_keys }
}

pub fn parse_vector_embedding_policy(node: &Value) -> VectorEmbeddingPolicy {
    let vector_embeddings = node
        .get("vectorEmbeddings")
        .and_then(|v| v.as_array())
        .map(|arr| {
            arr.iter()
                .filter_map(|ve| {
                    ve.get("path")
                        .and_then(|v| v.as_str())
                        .map(|path| VectorEmbedding {
                            path: path.to_string(),
                            data_type: ve
                                .get("dataType")
                                .and_then(|v| v.as_str())
                                .unwrap_or("float32")
                                .to_string(),
                            distance_function: ve
                                .get("distanceFunction")
                                .and_then(|v| v.as_str())
                                .unwrap_or("cosine")
                                .to_string(),
                            dimensions: ve
                                .get("dimensions")
                                .and_then(|v| v.as_i64())
                                .unwrap_or(1536) as i32,
                        })
                })
                .collect()
        })
        .unwrap_or_default();
    VectorEmbeddingPolicy { vector_embeddings }
}

fn parse_spatial_type(s: &str) -> Option<SpatialType> {
    match s.to_ascii_lowercase().as_str() {
        "point" => Some(SpatialType::Point),
        "polygon" => Some(SpatialType::Polygon),
        "multipolygon" => Some(SpatialType::MultiPolygon),
        "linestring" => Some(SpatialType::LineString),
        _ => None,
    }
}
