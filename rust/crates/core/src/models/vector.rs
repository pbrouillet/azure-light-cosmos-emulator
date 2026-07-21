//! Vector search models. Ports `VectorDistanceFunction.cs`, `VectorIndexOptions.cs`,
//! `VectorSearch.cs`, and `VectorMath.cs`.

use crate::models::partition_key::PartitionKeyValue;

/// The distance/similarity function used for vector comparisons, matching the
/// Cosmos DB `distanceFunction` options.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum VectorDistanceFunction {
    /// Cosine similarity (higher is more similar).
    #[default]
    Cosine,
    /// Dot product similarity (higher is more similar).
    DotProduct,
    /// Euclidean (L2) distance (lower is closer).
    Euclidean,
}

impl VectorDistanceFunction {
    /// Parses a Cosmos DB distance-function string (case-insensitive). Accepts
    /// `cosine`, `dotproduct`/`dot product`, and `euclidean`; defaults to
    /// [`VectorDistanceFunction::Cosine`] for null/empty/unknown values.
    pub fn parse(value: Option<&str>) -> Self {
        match value.map(|v| v.trim().to_ascii_lowercase()).as_deref() {
            Some("cosine") => Self::Cosine,
            Some("dotproduct") | Some("dot product") => Self::DotProduct,
            Some("euclidean") => Self::Euclidean,
            _ => Self::Cosine,
        }
    }
}

/// Runtime tuning options for the vector index. Ports `VectorIndexOptions`.
#[derive(Debug, Clone)]
pub struct VectorIndexOptions {
    /// Master switch for index-accelerated vector search.
    pub enabled: bool,
    /// When true, embedding paths used in a `VectorDistance` ORDER BY are
    /// auto-indexed even if the container declares no vector policy.
    pub implicit_indexing: bool,
    /// HNSW graph connectivity (reserved for a future approximate index).
    pub m: usize,
    /// HNSW construction-time search width (reserved).
    pub ef_construction: usize,
    /// HNSW query-time search width (reserved).
    pub ef_search: usize,
    /// Tombstone ratio above which an HNSW graph is rebuilt (reserved).
    pub rebuild_tombstone_ratio: f64,
    /// Partition size at or below which a partition-scoped query is exact-scanned.
    pub partition_exact_scan_threshold: usize,
    /// Whether the HNSW graph is built on a background thread (reserved).
    pub background_build: bool,
}

impl Default for VectorIndexOptions {
    fn default() -> Self {
        Self {
            enabled: true,
            implicit_indexing: true,
            m: 16,
            ef_construction: 200,
            ef_search: 100,
            rebuild_tombstone_ratio: 0.25,
            partition_exact_scan_threshold: 4096,
            background_build: true,
        }
    }
}

/// A request to find the nearest neighbours of a query vector within a container.
/// Ports `VectorSearchRequest`.
#[derive(Debug, Clone)]
pub struct VectorSearchRequest {
    pub database_id: String,
    pub container_id: String,
    /// The document property path holding the embedding, e.g. `/embedding`.
    pub path: String,
    pub query_vector: Vec<f32>,
    pub distance_function: VectorDistanceFunction,
    pub top_k: usize,
    /// Optional partition-key scope; when set, only that partition is searched.
    pub partition_key: Option<PartitionKeyValue>,
    /// The vector index type: `flat` (exact), `quantizedFlat`, or `diskANN`.
    pub index_type: String,
}

/// A single nearest-neighbour result. Ports `VectorHit`.
#[derive(Debug, Clone, PartialEq)]
pub struct VectorHit {
    pub document_id: String,
    pub partition_key: PartitionKeyValue,
    /// Nearest-first distance (lower is closer).
    pub distance: f64,
    /// Cosmos `VectorDistance` score (similarity for cosine/dot, raw distance for L2).
    pub score: f64,
}

/// Vector similarity/distance computations. Ports `VectorMath`. All functions
/// assume equal-length vectors.
pub mod vector_math {
    use super::VectorDistanceFunction;

    /// Cosine similarity in [-1, 1] (higher is more similar).
    pub fn cosine_similarity(a: &[f32], b: &[f32]) -> f64 {
        let (mut dot, mut mag_a, mut mag_b) = (0.0f64, 0.0f64, 0.0f64);
        for i in 0..a.len() {
            dot += a[i] as f64 * b[i] as f64;
            mag_a += a[i] as f64 * a[i] as f64;
            mag_b += b[i] as f64 * b[i] as f64;
        }
        if mag_a == 0.0 || mag_b == 0.0 {
            return 0.0;
        }
        dot / (mag_a.sqrt() * mag_b.sqrt())
    }

    /// Dot product (higher is more similar).
    pub fn dot_product(a: &[f32], b: &[f32]) -> f64 {
        let mut dot = 0.0f64;
        for i in 0..a.len() {
            dot += a[i] as f64 * b[i] as f64;
        }
        dot
    }

    /// Euclidean (L2) distance (lower is closer).
    pub fn euclidean_distance(a: &[f32], b: &[f32]) -> f64 {
        let mut sum = 0.0f64;
        for i in 0..a.len() {
            let d = a[i] as f64 - b[i] as f64;
            sum += d * d;
        }
        sum.sqrt()
    }

    /// The Cosmos `VectorDistance` score: similarity for cosine/dot product
    /// (higher is closer), raw distance for Euclidean (lower is closer).
    pub fn score(a: &[f32], b: &[f32], fto: VectorDistanceFunction) -> f64 {
        match fto {
            VectorDistanceFunction::Cosine => cosine_similarity(a, b),
            VectorDistanceFunction::DotProduct => dot_product(a, b),
            VectorDistanceFunction::Euclidean => euclidean_distance(a, b),
        }
    }

    /// A monotonic distance where **lower always means closer**, suitable for
    /// nearest-first ordering regardless of the underlying similarity function.
    pub fn nearest_first_distance(a: &[f32], b: &[f32], fto: VectorDistanceFunction) -> f64 {
        match fto {
            VectorDistanceFunction::Cosine => 1.0 - cosine_similarity(a, b),
            VectorDistanceFunction::DotProduct => -dot_product(a, b),
            VectorDistanceFunction::Euclidean => euclidean_distance(a, b),
        }
    }
}
