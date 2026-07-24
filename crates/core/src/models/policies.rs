//! Container policies: indexing, unique key, conflict resolution, vector.
//! Ports `IndexingPolicy.cs`, `UniqueKeyPolicy.cs`, `ConflictResolutionPolicy.cs`,
//! `VectorEmbeddingPolicy.cs`.

use serde::{Deserialize, Serialize};

// ---------- Indexing ----------

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum IndexingMode {
    Consistent,
    Lazy,
    None,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum SortOrder {
    Ascending,
    Descending,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum SpatialType {
    Point,
    Polygon,
    MultiPolygon,
    LineString,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct IncludedPath {
    pub path: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ExcludedPath {
    pub path: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CompositeIndexPath {
    pub path: String,
    #[serde(default = "asc")]
    pub order: SortOrder,
}

fn asc() -> SortOrder {
    SortOrder::Ascending
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CompositeIndex {
    pub paths: Vec<CompositeIndexPath>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SpatialIndex {
    pub path: String,
    pub types: Vec<SpatialType>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VectorIndex {
    pub path: String,
    /// Index type: `flat`, `quantizedFlat`, or `diskANN`.
    #[serde(rename = "type")]
    pub index_type: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct IndexingPolicy {
    pub automatic: bool,
    pub indexing_mode: IndexingMode,
    pub included_paths: Vec<IncludedPath>,
    pub excluded_paths: Vec<ExcludedPath>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub composite_indexes: Option<Vec<CompositeIndex>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub spatial_indexes: Option<Vec<SpatialIndex>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub vector_indexes: Option<Vec<VectorIndex>>,
}

impl Default for IndexingPolicy {
    fn default() -> Self {
        Self {
            automatic: true,
            indexing_mode: IndexingMode::Consistent,
            included_paths: vec![IncludedPath {
                path: "/*".to_string(),
            }],
            excluded_paths: vec![ExcludedPath {
                path: "/\"_etag\"/?".to_string(),
            }],
            composite_indexes: None,
            spatial_indexes: None,
            vector_indexes: None,
        }
    }
}

// ---------- Unique key ----------

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UniqueKey {
    pub paths: Vec<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct UniqueKeyPolicy {
    #[serde(default)]
    pub unique_keys: Vec<UniqueKey>,
}

// ---------- Conflict resolution ----------

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ConflictResolutionMode {
    LastWriterWins,
    Custom,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ConflictResolutionPolicy {
    pub mode: ConflictResolutionMode,
    pub conflict_resolution_path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub conflict_resolution_procedure: Option<String>,
}

impl Default for ConflictResolutionPolicy {
    fn default() -> Self {
        Self {
            mode: ConflictResolutionMode::LastWriterWins,
            conflict_resolution_path: "/_ts".to_string(),
            conflict_resolution_procedure: None,
        }
    }
}

// ---------- Vector embedding ----------

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VectorEmbedding {
    pub path: String,
    #[serde(default = "float32")]
    pub data_type: String,
    #[serde(default = "cosine")]
    pub distance_function: String,
    #[serde(default = "default_dimensions")]
    pub dimensions: i32,
}

fn float32() -> String {
    "float32".to_string()
}

fn cosine() -> String {
    "cosine".to_string()
}

fn default_dimensions() -> i32 {
    1536
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct VectorEmbeddingPolicy {
    #[serde(default)]
    pub vector_embeddings: Vec<VectorEmbedding>,
}
