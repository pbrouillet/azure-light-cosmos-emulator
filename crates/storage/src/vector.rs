//! Vector index provider and the indexing `DocumentStore` decorator.
//!
//! Ports `HnswVectorIndexProvider` and `VectorIndexingDocumentStore`.
//!
//! The provider maintains one in-memory shard per container embedding path. For
//! declared `flat` indexes it uses exact scan. For `quantizedFlat`/`diskANN` (and
//! other non-flat types) it builds a real HNSW small-world graph and searches it,
//! falling back to exact scan for graph-less shards and small partition-scoped
//! queries, mirroring the .NET provider's public behaviour.

use std::cmp::Ordering;
use std::collections::{BinaryHeap, HashMap, HashSet};
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use cosmos_core::error::CosmosResult;
use cosmos_core::models::vector::vector_math;
use cosmos_core::models::*;
use cosmos_core::traits::{DocumentStore, VectorIndexProvider};
use serde_json::Value;

#[derive(Clone)]
struct Entry {
    doc_id: String,
    pk: PartitionKeyValue,
    pk_header: String,
    vector: Vec<f32>,
    deleted: bool,
}

struct Shard {
    path: String,
    distance_function: VectorDistanceFunction,
    index_type: String,
    dimensions: usize,
    entries: Vec<Entry>,
    key_to_id: HashMap<String, usize>,
    partition_entries: HashMap<String, Vec<usize>>,
    tombstones: usize,
    graph: Option<HnswGraph>,
}

impl Shard {
    fn live_count(&self) -> usize {
        self.entries.len().saturating_sub(self.tombstones)
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct ScoredId {
    distance: f64,
    id: usize,
}

impl Eq for ScoredId {}

impl Ord for ScoredId {
    fn cmp(&self, other: &Self) -> Ordering {
        self.distance
            .partial_cmp(&other.distance)
            .unwrap_or(Ordering::Equal)
            .then_with(|| self.id.cmp(&other.id))
    }
}

impl PartialOrd for ScoredId {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

/// A compact in-memory HNSW graph. Node ids are aligned with `Shard.entries`.
struct HnswGraph {
    layers: Vec<Vec<Vec<usize>>>,
    levels: Vec<usize>,
    entry_point: Option<usize>,
    max_level: usize,
    m: usize,
    ef_construction: usize,
    ef_search: usize,
    distance_function: VectorDistanceFunction,
}

impl HnswGraph {
    fn new(options: &VectorIndexOptions, distance_function: VectorDistanceFunction) -> Self {
        Self {
            layers: Vec::new(),
            levels: Vec::new(),
            entry_point: None,
            max_level: 0,
            m: options.m.max(2),
            ef_construction: options.ef_construction.max(options.m.max(2)),
            ef_search: options.ef_search.max(1),
            distance_function,
        }
    }

    fn build(
        entries: &[Entry],
        options: &VectorIndexOptions,
        distance_function: VectorDistanceFunction,
    ) -> Self {
        let mut graph = Self::new(options, distance_function);
        for id in 0..entries.len() {
            if !entries[id].deleted {
                graph.insert(id, entries);
            }
        }
        graph
    }

    fn insert(&mut self, id: usize, entries: &[Entry]) {
        let level = Self::random_level(id, self.m);
        self.ensure_node(id, level);

        let Some(mut current) = self.entry_point else {
            self.entry_point = Some(id);
            self.max_level = level;
            return;
        };
        if entries[current].deleted {
            current = entries
                .iter()
                .enumerate()
                .find(|(entry_id, entry)| *entry_id != id && !entry.deleted)
                .map(|(entry_id, _)| entry_id)
                .unwrap_or(id);
        }

        let query = &entries[id].vector;
        for layer in ((level + 1)..=self.max_level).rev() {
            current = self.greedy_search_layer(query, current, layer, entries);
        }

        let top = level.min(self.max_level);
        for layer in (0..=top).rev() {
            let candidates =
                self.search_layer(query, current, self.ef_construction, layer, entries);
            self.connect(id, candidates, layer, entries);
            if let Some(best) = self.nearest_in_layer(query, id, layer, entries) {
                current = best;
            }
        }

        if level > self.max_level {
            self.entry_point = Some(id);
            self.max_level = level;
        }
    }

    fn search(&self, query: &[f32], k: usize, entries: &[Entry]) -> Vec<ScoredId> {
        let Some(mut current) = self.entry_point else {
            return Vec::new();
        };
        if entries[current].deleted {
            let Some((live_id, _)) = entries.iter().enumerate().find(|(_, entry)| !entry.deleted)
            else {
                return Vec::new();
            };
            current = live_id;
        }
        if k == 0 {
            return Vec::new();
        }
        for layer in (1..=self.max_level).rev() {
            current = self.greedy_search_layer(query, current, layer, entries);
        }
        let ef = self.ef_search.max(k);
        let mut found = self.search_layer(query, current, ef, 0, entries);
        found.retain(|c| !entries[c.id].deleted);
        found.sort_by(|a, b| {
            a.distance
                .partial_cmp(&b.distance)
                .unwrap_or(Ordering::Equal)
        });
        found.truncate(k);
        found
    }

    fn ensure_node(&mut self, id: usize, level: usize) {
        while self.layers.len() <= level {
            self.layers.push(Vec::new());
        }
        for layer in &mut self.layers {
            while layer.len() <= id {
                layer.push(Vec::new());
            }
        }
        while self.levels.len() <= id {
            self.levels.push(0);
        }
        self.levels[id] = level;
    }

    fn greedy_search_layer(
        &self,
        query: &[f32],
        entry: usize,
        layer: usize,
        entries: &[Entry],
    ) -> usize {
        let mut current = entry;
        let mut current_dist = self.distance(query, &entries[current].vector);
        let mut changed = true;
        while changed {
            changed = false;
            for &neighbor in self.neighbors(current, layer) {
                if entries[neighbor].deleted {
                    continue;
                }
                let dist = self.distance(query, &entries[neighbor].vector);
                if dist < current_dist {
                    current = neighbor;
                    current_dist = dist;
                    changed = true;
                }
            }
        }
        current
    }

    fn search_layer(
        &self,
        query: &[f32],
        entry: usize,
        ef: usize,
        layer: usize,
        entries: &[Entry],
    ) -> Vec<ScoredId> {
        if entries[entry].deleted {
            return Vec::new();
        }

        let start = ScoredId {
            distance: self.distance(query, &entries[entry].vector),
            id: entry,
        };
        let mut visited = HashSet::from([entry]);
        let mut candidates = BinaryHeap::new();
        let mut results = BinaryHeap::new();
        candidates.push(ReverseScored(start));
        results.push(start);

        while let Some(ReverseScored(candidate)) = candidates.pop() {
            let worst = results.peek().map(|s| s.distance).unwrap_or(f64::INFINITY);
            if candidate.distance > worst && results.len() >= ef {
                break;
            }

            for &neighbor in self.neighbors(candidate.id, layer) {
                if !visited.insert(neighbor) || entries[neighbor].deleted {
                    continue;
                }
                let scored = ScoredId {
                    distance: self.distance(query, &entries[neighbor].vector),
                    id: neighbor,
                };
                let worst = results.peek().map(|s| s.distance).unwrap_or(f64::INFINITY);
                if results.len() < ef || scored.distance < worst {
                    candidates.push(ReverseScored(scored));
                    results.push(scored);
                    if results.len() > ef {
                        results.pop();
                    }
                }
            }
        }

        results.into_vec()
    }

    fn connect(&mut self, id: usize, candidates: Vec<ScoredId>, layer: usize, entries: &[Entry]) {
        let mut selected: Vec<usize> = candidates
            .into_iter()
            .filter(|c| c.id != id && !entries[c.id].deleted)
            .map(|c| c.id)
            .collect();
        selected.sort_by(|a, b| {
            self.distance(&entries[id].vector, &entries[*a].vector)
                .partial_cmp(&self.distance(&entries[id].vector, &entries[*b].vector))
                .unwrap_or(Ordering::Equal)
        });
        selected.dedup();
        selected.truncate(self.m);

        for &neighbor in &selected {
            self.add_edge(id, neighbor, layer, entries);
        }
        self.prune_neighbors(id, layer, entries);
    }

    fn add_edge(&mut self, a: usize, b: usize, layer: usize, entries: &[Entry]) {
        if !self.layers[layer][a].contains(&b) {
            self.layers[layer][a].push(b);
        }
        if !self.layers[layer][b].contains(&a) {
            self.layers[layer][b].push(a);
        }
        self.prune_neighbors(b, layer, entries);
    }

    fn prune_neighbors(&mut self, id: usize, layer: usize, entries: &[Entry]) {
        let dist_fn = self.distance_function;
        let neighbors = &mut self.layers[layer][id];
        neighbors.sort_by(|a, b| {
            vector_math::nearest_first_distance(&entries[id].vector, &entries[*a].vector, dist_fn)
                .partial_cmp(&vector_math::nearest_first_distance(
                    &entries[id].vector,
                    &entries[*b].vector,
                    dist_fn,
                ))
                .unwrap_or(Ordering::Equal)
        });
        neighbors.dedup();
        neighbors.truncate(self.m);
    }

    fn nearest_in_layer(
        &self,
        query: &[f32],
        id: usize,
        layer: usize,
        entries: &[Entry],
    ) -> Option<usize> {
        self.neighbors(id, layer)
            .iter()
            .copied()
            .filter(|n| !entries[*n].deleted)
            .min_by(|a, b| {
                self.distance(query, &entries[*a].vector)
                    .partial_cmp(&self.distance(query, &entries[*b].vector))
                    .unwrap_or(Ordering::Equal)
            })
    }

    fn neighbors(&self, id: usize, layer: usize) -> &[usize] {
        self.layers
            .get(layer)
            .and_then(|l| l.get(id))
            .map(Vec::as_slice)
            .unwrap_or(&[])
    }

    fn distance(&self, a: &[f32], b: &[f32]) -> f64 {
        vector_math::nearest_first_distance(a, b, self.distance_function)
    }

    fn random_level(id: usize, m: usize) -> usize {
        let mut x = splitmix64(id as u64 + 0x9E37_79B9_7F4A_7C15);
        let lambda = 1.0 / (m.max(2) as f64).ln();
        let unit = ((x >> 11) as f64 + 1.0) / ((1u64 << 53) as f64 + 1.0);
        let mut level = (-unit.ln() * lambda).floor() as usize;
        level = level.min(32);
        x = splitmix64(x);
        if x == 0 {
            level = level.saturating_add(1).min(32);
        }
        level
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct ReverseScored(ScoredId);

impl Ord for ReverseScored {
    fn cmp(&self, other: &Self) -> Ordering {
        other.0.cmp(&self.0)
    }
}

impl PartialOrd for ReverseScored {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

fn splitmix64(mut x: u64) -> u64 {
    x = x.wrapping_add(0x9E37_79B9_7F4A_7C15);
    let mut z = x;
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

/// In-memory HNSW vector index provider. Shards are built lazily from the
/// backing store on first use and kept current via the maintenance hooks invoked
/// by [`VectorIndexingDocumentStore`].
pub struct HnswVectorIndexProvider {
    store: Arc<dyn DocumentStore>,
    options: VectorIndexOptions,
    shards: Mutex<HashMap<String, Shard>>,
}

/// Backward-compatible name used by the host wiring. It now provides the real
/// HNSW implementation for non-flat index types.
pub type FlatVectorIndexProvider = HnswVectorIndexProvider;

impl HnswVectorIndexProvider {
    pub fn new(store: Arc<dyn DocumentStore>, options: VectorIndexOptions) -> Self {
        Self {
            store,
            options,
            shards: Mutex::new(HashMap::new()),
        }
    }

    fn normalize_path(path: &str) -> String {
        format!("/{}", path.trim().trim_start_matches('/'))
    }

    fn shard_key(database_id: &str, container_id: &str, path: &str) -> String {
        format!("{database_id}\0{container_id}\0{path}")
    }

    fn shard_key_prefix(database_id: &str, container_id: &str) -> String {
        format!("{database_id}\0{container_id}\0")
    }

    fn doc_key(doc_id: &str, pk: &PartitionKeyValue) -> String {
        format!("{}\0{doc_id}", pk.to_header_string())
    }

    fn is_flat(index_type: &str) -> bool {
        index_type.eq_ignore_ascii_case("flat")
    }

    fn paths_match(a: &str, b: &str) -> bool {
        Self::normalize_path(a) == Self::normalize_path(b)
    }

    fn extract_vector(body: &JsonObject, path: &str) -> Option<Vec<f32>> {
        let mut node = Value::Object(body.clone());
        for segment in path.split('/').filter(|s| !s.is_empty()) {
            node = node.get(segment)?.clone();
        }
        let array = node.as_array()?;
        if array.is_empty() {
            return None;
        }
        let mut vector = Vec::with_capacity(array.len());
        for item in array {
            vector.push(item.as_f64()? as f32);
        }
        Some(vector)
    }

    async fn build_shard(
        &self,
        database_id: &str,
        container_id: &str,
        path: &str,
        index_type: &str,
        distance_function: VectorDistanceFunction,
    ) -> CosmosResult<Option<Shard>> {
        let container = self.store.get_container(database_id, container_id).await?;

        let declared = container
            .indexing_policy
            .vector_indexes
            .as_ref()
            .and_then(|list| list.iter().find(|vi| Self::paths_match(&vi.path, path)));
        if !self.options.implicit_indexing && declared.is_none() {
            return Ok(None);
        }

        let effective_type = declared
            .map(|vi| vi.index_type.clone())
            .unwrap_or_else(|| index_type.to_string());
        let mut effective_function = distance_function;
        if let Some(policy) = container.vector_embedding_policy.as_ref() {
            if let Some(embedding) = policy
                .vector_embeddings
                .iter()
                .find(|e| Self::paths_match(&e.path, path))
            {
                effective_function =
                    VectorDistanceFunction::parse(Some(&embedding.distance_function));
            }
        }

        let mut shard = Shard {
            path: path.to_string(),
            distance_function: effective_function,
            index_type: effective_type,
            dimensions: 0,
            entries: Vec::new(),
            key_to_id: HashMap::new(),
            partition_entries: HashMap::new(),
            tombstones: 0,
            graph: None,
        };

        let docs = self.store.list_documents(database_id, container_id).await?;
        for doc in docs.resources {
            let Some(vector) = Self::extract_vector(&doc.body, path) else {
                continue;
            };
            if shard.dimensions == 0 {
                shard.dimensions = vector.len();
            } else if vector.len() != shard.dimensions {
                continue;
            }
            Self::append_entry_without_graph(&mut shard, doc.id, doc.partition_key, vector);
        }

        Self::rebuild_graph_if_needed(&mut shard, &self.options);
        Ok(Some(shard))
    }

    fn exact_rank<'a>(
        entries: impl Iterator<Item = (usize, &'a Entry)>,
        query: &[f32],
        distance_function: VectorDistanceFunction,
        top_k: usize,
    ) -> Vec<(usize, f64)> {
        let mut ranked: Vec<_> = entries
            .filter(|(_, e)| !e.deleted)
            .map(|(id, e)| {
                (
                    id,
                    vector_math::nearest_first_distance(&e.vector, query, distance_function),
                )
            })
            .collect();
        ranked.sort_by(|a, b| a.1.partial_cmp(&b.1).unwrap_or(Ordering::Equal));
        ranked.truncate(top_k);
        ranked
    }

    fn partition_filtered_graph_search(
        shard: &Shard,
        query: &[f32],
        pk_header: &str,
        top_k: usize,
    ) -> Vec<(usize, f64)> {
        let Some(graph) = shard.graph.as_ref() else {
            return Vec::new();
        };
        let total = shard.entries.len();
        let need = top_k.max(1);
        let mut k = total.min((need * 4).max(64));
        loop {
            let mut matches: Vec<_> = graph
                .search(query, k, &shard.entries)
                .into_iter()
                .filter(|s| shard.entries[s.id].pk_header == pk_header)
                .map(|s| (s.id, s.distance))
                .collect();
            matches.sort_by(|a, b| a.1.partial_cmp(&b.1).unwrap_or(Ordering::Equal));
            if matches.len() >= need || k >= total {
                matches.truncate(top_k);
                return matches;
            }
            k = total.min(k.saturating_mul(4));
        }
    }

    fn append_entry_without_graph(
        shard: &mut Shard,
        doc_id: String,
        pk: PartitionKeyValue,
        vector: Vec<f32>,
    ) {
        let id = shard.entries.len();
        let pk_header = pk.to_header_string();
        let doc_key = Self::doc_key(&doc_id, &pk);
        shard.entries.push(Entry {
            doc_id,
            pk,
            pk_header: pk_header.clone(),
            vector,
            deleted: false,
        });
        shard.key_to_id.insert(doc_key, id);
        shard
            .partition_entries
            .entry(pk_header)
            .or_default()
            .push(id);
    }

    fn append_entry(
        shard: &mut Shard,
        options: &VectorIndexOptions,
        doc_id: String,
        pk: PartitionKeyValue,
        vector: Vec<f32>,
    ) {
        Self::append_entry_without_graph(shard, doc_id, pk, vector);
        let id = shard.entries.len() - 1;
        if let Some(graph) = shard.graph.as_mut() {
            graph.insert(id, &shard.entries);
        } else {
            Self::rebuild_graph_if_needed(shard, options);
        }
    }

    fn rebuild_graph_if_needed(shard: &mut Shard, options: &VectorIndexOptions) {
        if shard.dimensions > 0 && !Self::is_flat(&shard.index_type) && shard.live_count() > 0 {
            shard.graph = Some(HnswGraph::build(
                &shard.entries,
                options,
                shard.distance_function,
            ));
        } else {
            shard.graph = None;
        }
    }

    fn maybe_rebuild(shard: &mut Shard, options: &VectorIndexOptions) {
        if shard.tombstones == 0 {
            return;
        }
        let live = shard.live_count();
        if live == 0
            || (shard.tombstones >= 32
                && (shard.tombstones as f64)
                    >= (shard.entries.len() as f64 * options.rebuild_tombstone_ratio))
        {
            let live_entries: Vec<Entry> = shard
                .entries
                .iter()
                .filter(|e| !e.deleted)
                .cloned()
                .map(|mut e| {
                    e.deleted = false;
                    e
                })
                .collect();
            shard.entries.clear();
            shard.key_to_id.clear();
            shard.partition_entries.clear();
            shard.tombstones = 0;
            shard.graph = None;
            for entry in live_entries {
                Self::append_entry_without_graph(shard, entry.doc_id, entry.pk, entry.vector);
            }
            Self::rebuild_graph_if_needed(shard, options);
        }
    }

    fn for_built_shards(
        &self,
        database_id: &str,
        container_id: &str,
        mut f: impl FnMut(&mut Shard, &VectorIndexOptions),
    ) {
        let prefix = Self::shard_key_prefix(database_id, container_id);
        let mut shards = self.shards.lock().unwrap();
        for (key, shard) in shards.iter_mut() {
            if key.starts_with(&prefix) {
                f(shard, &self.options);
            }
        }
    }
}

#[async_trait]
impl VectorIndexProvider for HnswVectorIndexProvider {
    fn is_enabled(&self) -> bool {
        self.options.enabled
    }

    async fn ensure_index(
        &self,
        database_id: &str,
        container_id: &str,
        path: &str,
        index_type: &str,
        distance_function: VectorDistanceFunction,
    ) -> CosmosResult<bool> {
        if !self.options.enabled {
            return Ok(false);
        }
        let normalized = Self::normalize_path(path);
        let key = Self::shard_key(database_id, container_id, &normalized);

        if self.shards.lock().unwrap().contains_key(&key) {
            return Ok(true);
        }

        let shard = self
            .build_shard(
                database_id,
                container_id,
                &normalized,
                index_type,
                distance_function,
            )
            .await?;
        match shard {
            Some(shard) => {
                self.shards.lock().unwrap().insert(key, shard);
                Ok(true)
            }
            None => Ok(false),
        }
    }

    async fn search(&self, request: VectorSearchRequest) -> CosmosResult<Vec<VectorHit>> {
        if !self.options.enabled {
            return Ok(Vec::new());
        }
        let built = self
            .ensure_index(
                &request.database_id,
                &request.container_id,
                &request.path,
                &request.index_type,
                request.distance_function,
            )
            .await?;
        if !built {
            return Ok(Vec::new());
        }

        let normalized = Self::normalize_path(&request.path);
        let key = Self::shard_key(&request.database_id, &request.container_id, &normalized);
        let shards = self.shards.lock().unwrap();
        let Some(shard) = shards.get(&key) else {
            return Ok(Vec::new());
        };

        let query = &request.query_vector;
        if shard.dimensions == 0 || query.len() != shard.dimensions || request.top_k == 0 {
            return Ok(Vec::new());
        }

        let ranked: Vec<(usize, f64)> = if let Some(pk) = request.partition_key.as_ref() {
            let pk_header = pk.to_header_string();
            let Some(ids) = shard.partition_entries.get(&pk_header) else {
                return Ok(Vec::new());
            };
            let live_count = ids.iter().filter(|id| !shard.entries[**id].deleted).count();
            if shard.graph.is_none() || live_count <= self.options.partition_exact_scan_threshold {
                Self::exact_rank(
                    ids.iter().map(|id| (*id, &shard.entries[*id])),
                    query,
                    shard.distance_function,
                    request.top_k,
                )
            } else {
                Self::partition_filtered_graph_search(shard, query, &pk_header, request.top_k)
            }
        } else if let Some(graph) = shard.graph.as_ref() {
            let k = shard.entries.len().min(request.top_k + shard.tombstones);
            graph
                .search(query, k, &shard.entries)
                .into_iter()
                .map(|s| (s.id, s.distance))
                .take(request.top_k)
                .collect()
        } else {
            Self::exact_rank(
                shard.entries.iter().enumerate(),
                query,
                shard.distance_function,
                request.top_k,
            )
        };

        let hits = ranked
            .into_iter()
            .filter_map(|(id, distance)| {
                let e = shard.entries.get(id)?;
                if e.deleted {
                    return None;
                }
                Some(VectorHit {
                    document_id: e.doc_id.clone(),
                    partition_key: e.pk.clone(),
                    distance,
                    score: vector_math::score(&e.vector, query, shard.distance_function),
                })
            })
            .take(request.top_k)
            .collect();
        Ok(hits)
    }

    fn on_upsert(&self, database_id: &str, container_id: &str, document: &CosmosDocument) {
        let doc_id = document.id.clone();
        let pk = document.partition_key.clone();
        let body = document.body.clone();
        self.for_built_shards(database_id, container_id, |shard, options| {
            let doc_key = Self::doc_key(&doc_id, &pk);
            if let Some(old_id) = shard.key_to_id.remove(&doc_key) {
                if !shard.entries[old_id].deleted {
                    shard.entries[old_id].deleted = true;
                    shard.tombstones += 1;
                }
            }
            if let Some(vector) = Self::extract_vector(&body, &shard.path) {
                if shard.dimensions == 0 {
                    shard.dimensions = vector.len();
                }
                if vector.len() == shard.dimensions {
                    Self::append_entry(shard, options, doc_id.clone(), pk.clone(), vector);
                }
            }
            Self::maybe_rebuild(shard, options);
        });
    }

    fn on_delete(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) {
        let doc_key = Self::doc_key(document_id, partition_key);
        self.for_built_shards(database_id, container_id, |shard, options| {
            if let Some(old_id) = shard.key_to_id.remove(&doc_key) {
                if !shard.entries[old_id].deleted {
                    shard.entries[old_id].deleted = true;
                    shard.tombstones += 1;
                    Self::maybe_rebuild(shard, options);
                }
            }
        });
    }

    fn on_container_cleared(&self, database_id: &str, container_id: &str) {
        self.for_built_shards(database_id, container_id, |shard, _| {
            shard.entries.clear();
            shard.key_to_id.clear();
            shard.partition_entries.clear();
            shard.tombstones = 0;
            shard.graph = None;
        });
    }

    fn on_container_dropped(&self, database_id: &str, container_id: &str) {
        let prefix = Self::shard_key_prefix(database_id, container_id);
        self.shards
            .lock()
            .unwrap()
            .retain(|key, _| !key.starts_with(&prefix));
    }
}

/// A [`DocumentStore`] decorator that keeps a [`VectorIndexProvider`] in sync
/// with document mutations. Ports `VectorIndexingDocumentStore`. All storage is
/// delegated to the wrapped inner store; only document- and container-mutating
/// operations additionally notify the index. Works for any backing store.
pub struct VectorIndexingDocumentStore {
    inner: Arc<dyn DocumentStore>,
    index: Arc<dyn VectorIndexProvider>,
}

impl VectorIndexingDocumentStore {
    pub fn new(inner: Arc<dyn DocumentStore>, index: Arc<dyn VectorIndexProvider>) -> Self {
        Self { inner, index }
    }
}

#[async_trait]
impl DocumentStore for VectorIndexingDocumentStore {
    // ---- Databases (pass-through) ----
    async fn create_database(&self, id: &str) -> CosmosResult<CosmosDatabase> {
        self.inner.create_database(id).await
    }
    async fn get_database(&self, id: &str) -> CosmosResult<CosmosDatabase> {
        self.inner.get_database(id).await
    }
    async fn list_databases(&self) -> CosmosResult<FeedResponse<CosmosDatabase>> {
        self.inner.list_databases().await
    }
    async fn replace_database(&self, database: CosmosDatabase) -> CosmosResult<CosmosDatabase> {
        self.inner.replace_database(database).await
    }
    async fn delete_database(&self, id: &str) -> CosmosResult<()> {
        self.inner.delete_database(id).await
    }

    // ---- Containers ----
    async fn create_container(
        &self,
        database_id: &str,
        container: CosmosContainer,
    ) -> CosmosResult<CosmosContainer> {
        self.inner.create_container(database_id, container).await
    }
    async fn get_container(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<CosmosContainer> {
        self.inner.get_container(database_id, container_id).await
    }
    async fn list_containers(
        &self,
        database_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosContainer>> {
        self.inner.list_containers(database_id).await
    }
    async fn replace_container(
        &self,
        database_id: &str,
        container: CosmosContainer,
    ) -> CosmosResult<CosmosContainer> {
        let container_id = container.id.clone();
        let result = self.inner.replace_container(database_id, container).await?;
        // Indexing/embedding policy may have changed; drop shards so they rebuild lazily.
        self.index.on_container_dropped(database_id, &container_id);
        Ok(result)
    }
    async fn delete_container(&self, database_id: &str, container_id: &str) -> CosmosResult<()> {
        self.inner
            .delete_container(database_id, container_id)
            .await?;
        self.index.on_container_dropped(database_id, container_id);
        Ok(())
    }

    // ---- Documents ----
    async fn create_document(
        &self,
        database_id: &str,
        container_id: &str,
        document: JsonObject,
        is_indexed: Option<bool>,
    ) -> CosmosResult<CosmosDocument> {
        let doc = self
            .inner
            .create_document(database_id, container_id, document, is_indexed)
            .await?;
        self.index.on_upsert(database_id, container_id, &doc);
        Ok(doc)
    }
    async fn read_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<CosmosDocument> {
        self.inner
            .read_document(database_id, container_id, document_id, partition_key)
            .await
    }
    async fn replace_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        document: JsonObject,
        if_match: Option<&str>,
        is_indexed: Option<bool>,
    ) -> CosmosResult<CosmosDocument> {
        let doc = self
            .inner
            .replace_document(
                database_id,
                container_id,
                document_id,
                document,
                if_match,
                is_indexed,
            )
            .await?;
        self.index.on_upsert(database_id, container_id, &doc);
        Ok(doc)
    }
    async fn upsert_document(
        &self,
        database_id: &str,
        container_id: &str,
        document: JsonObject,
        is_indexed: Option<bool>,
    ) -> CosmosResult<CosmosDocument> {
        let doc = self
            .inner
            .upsert_document(database_id, container_id, document, is_indexed)
            .await?;
        self.index.on_upsert(database_id, container_id, &doc);
        Ok(doc)
    }
    #[allow(clippy::too_many_arguments)]
    async fn patch_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
        operations: &[PatchOperation],
        if_match: Option<&str>,
        condition: Option<&str>,
    ) -> CosmosResult<CosmosDocument> {
        let doc = self
            .inner
            .patch_document(
                database_id,
                container_id,
                document_id,
                partition_key,
                operations,
                if_match,
                condition,
            )
            .await?;
        self.index.on_upsert(database_id, container_id, &doc);
        Ok(doc)
    }
    async fn delete_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<()> {
        self.inner
            .delete_document(database_id, container_id, document_id, partition_key)
            .await?;
        self.index
            .on_delete(database_id, container_id, document_id, partition_key);
        Ok(())
    }
    async fn empty_container(&self, database_id: &str, container_id: &str) -> CosmosResult<usize> {
        let count = self
            .inner
            .empty_container(database_id, container_id)
            .await?;
        self.index.on_container_cleared(database_id, container_id);
        Ok(count)
    }
    async fn get_global_lsn(&self) -> CosmosResult<i64> {
        self.inner.get_global_lsn().await
    }

    // ---- Batch ----
    async fn execute_batch(
        &self,
        database_id: &str,
        container_id: &str,
        partition_key: &PartitionKeyValue,
        operations: &[BatchOperationRequest],
    ) -> CosmosResult<Vec<BatchOperationResponse>> {
        let responses = self
            .inner
            .execute_batch(database_id, container_id, partition_key, operations)
            .await?;

        for (op, response) in operations.iter().zip(responses.iter()) {
            if !(200..300).contains(&response.status_code) {
                continue;
            }
            match op.operation_type {
                BatchOperationType::Create
                | BatchOperationType::Replace
                | BatchOperationType::Upsert
                | BatchOperationType::Patch => {
                    let id = op.id.clone().or_else(|| {
                        response
                            .resource_body
                            .as_ref()
                            .and_then(|b| b.get("id"))
                            .and_then(|v| v.as_str())
                            .map(|s| s.to_string())
                    });
                    if let Some(id) = id {
                        if let Ok(doc) = self
                            .inner
                            .read_document(database_id, container_id, &id, partition_key)
                            .await
                        {
                            self.index.on_upsert(database_id, container_id, &doc);
                        }
                    }
                }
                BatchOperationType::Delete => {
                    if let Some(id) = op.id.as_deref() {
                        self.index
                            .on_delete(database_id, container_id, id, partition_key);
                    }
                }
                BatchOperationType::Read => {}
            }
        }

        Ok(responses)
    }

    // ---- Bulk reads (pass-through) ----
    async fn read_many_documents(
        &self,
        database_id: &str,
        container_id: &str,
        items: &[(String, PartitionKeyValue)],
    ) -> CosmosResult<FeedResponse<CosmosDocument>> {
        self.inner
            .read_many_documents(database_id, container_id, items)
            .await
    }
    async fn list_documents(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosDocument>> {
        self.inner.list_documents(database_id, container_id).await
    }

    // ---- Users (pass-through) ----
    async fn create_user(&self, database_id: &str, user_id: &str) -> CosmosResult<CosmosUser> {
        self.inner.create_user(database_id, user_id).await
    }
    async fn get_user(&self, database_id: &str, user_id: &str) -> CosmosResult<CosmosUser> {
        self.inner.get_user(database_id, user_id).await
    }
    async fn list_users(&self, database_id: &str) -> CosmosResult<FeedResponse<CosmosUser>> {
        self.inner.list_users(database_id).await
    }
    async fn replace_user(&self, database_id: &str, user: CosmosUser) -> CosmosResult<CosmosUser> {
        self.inner.replace_user(database_id, user).await
    }
    async fn delete_user(&self, database_id: &str, user_id: &str) -> CosmosResult<()> {
        self.inner.delete_user(database_id, user_id).await
    }

    // ---- Permissions (pass-through) ----
    async fn create_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission: CosmosPermission,
    ) -> CosmosResult<CosmosPermission> {
        self.inner
            .create_permission(database_id, user_id, permission)
            .await
    }
    async fn get_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission_id: &str,
    ) -> CosmosResult<CosmosPermission> {
        self.inner
            .get_permission(database_id, user_id, permission_id)
            .await
    }
    async fn list_permissions(
        &self,
        database_id: &str,
        user_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosPermission>> {
        self.inner.list_permissions(database_id, user_id).await
    }
    async fn replace_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission: CosmosPermission,
    ) -> CosmosResult<CosmosPermission> {
        self.inner
            .replace_permission(database_id, user_id, permission)
            .await
    }
    async fn delete_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission_id: &str,
    ) -> CosmosResult<()> {
        self.inner
            .delete_permission(database_id, user_id, permission_id)
            .await
    }

    // ---- Offers (pass-through) ----
    async fn get_offer(&self, offer_id: &str) -> CosmosResult<CosmosOffer> {
        self.inner.get_offer(offer_id).await
    }
    async fn list_offers(&self) -> CosmosResult<FeedResponse<CosmosOffer>> {
        self.inner.list_offers().await
    }
    async fn replace_offer(&self, offer: CosmosOffer) -> CosmosResult<CosmosOffer> {
        self.inner.replace_offer(offer).await
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::inmemory::InMemoryDocumentStore;
    use cosmos_core::models::policies::VectorIndex;
    use serde_json::json;

    fn build() -> (
        Arc<VectorIndexingDocumentStore>,
        Arc<FlatVectorIndexProvider>,
    ) {
        let inner: Arc<dyn DocumentStore> = Arc::new(InMemoryDocumentStore::new());
        let provider = Arc::new(FlatVectorIndexProvider::new(
            inner.clone(),
            VectorIndexOptions::default(),
        ));
        let store = Arc::new(VectorIndexingDocumentStore::new(
            inner,
            provider.clone() as Arc<dyn VectorIndexProvider>,
        ));
        (store, provider)
    }

    async fn seed(store: &VectorIndexingDocumentStore) {
        store.create_database("db1").await.unwrap();
        let mut container =
            CosmosContainer::new("db1", "c1", PartitionKeyDefinition::new(vec!["/pk".into()]));
        container.indexing_policy.vector_indexes = Some(vec![VectorIndex {
            path: "/embedding".into(),
            index_type: "flat".into(),
        }]);
        store.create_container("db1", container).await.unwrap();
    }

    fn doc(id: &str, pk: &str, embedding: [f32; 2]) -> JsonObject {
        json!({ "id": id, "pk": pk, "embedding": [embedding[0], embedding[1]] })
            .as_object()
            .unwrap()
            .clone()
    }

    fn request(query: Vec<f32>, top_k: usize) -> VectorSearchRequest {
        VectorSearchRequest {
            database_id: "db1".into(),
            container_id: "c1".into(),
            path: "/embedding".into(),
            query_vector: query,
            distance_function: VectorDistanceFunction::Cosine,
            top_k,
            partition_key: None,
            index_type: "flat".into(),
        }
    }

    #[tokio::test]
    async fn search_returns_nearest_first() {
        let (store, provider) = build();
        seed(&store).await;
        store
            .create_document("db1", "c1", doc("a", "p1", [1.0, 0.0]), None)
            .await
            .unwrap();
        store
            .create_document("db1", "c1", doc("b", "p1", [0.0, 1.0]), None)
            .await
            .unwrap();
        store
            .create_document("db1", "c1", doc("c", "p1", [0.9, 0.1]), None)
            .await
            .unwrap();

        let hits = provider.search(request(vec![1.0, 0.0], 3)).await.unwrap();
        assert_eq!(hits.len(), 3);
        // Nearest to [1,0] is "a" (identical), then "c", then "b".
        assert_eq!(hits[0].document_id, "a");
        assert_eq!(hits[1].document_id, "c");
        assert_eq!(hits[2].document_id, "b");
        assert!(hits[0].distance <= hits[1].distance);
    }

    #[tokio::test]
    async fn top_k_limits_results() {
        let (store, provider) = build();
        seed(&store).await;
        for (i, id) in ["a", "b", "c", "d"].iter().enumerate() {
            store
                .create_document("db1", "c1", doc(id, "p1", [i as f32, 1.0]), None)
                .await
                .unwrap();
        }
        let hits = provider.search(request(vec![0.0, 1.0], 2)).await.unwrap();
        assert_eq!(hits.len(), 2);
    }

    #[tokio::test]
    async fn delete_removes_from_index() {
        let (store, provider) = build();
        seed(&store).await;
        store
            .create_document("db1", "c1", doc("a", "p1", [1.0, 0.0]), None)
            .await
            .unwrap();
        store
            .create_document("db1", "c1", doc("b", "p1", [0.0, 1.0]), None)
            .await
            .unwrap();
        // Build the shard, then delete.
        let _ = provider.search(request(vec![1.0, 0.0], 5)).await.unwrap();
        store
            .delete_document("db1", "c1", "a", &PartitionKeyValue::single(json!("p1")))
            .await
            .unwrap();
        let hits = provider.search(request(vec![1.0, 0.0], 5)).await.unwrap();
        assert_eq!(hits.len(), 1);
        assert_eq!(hits[0].document_id, "b");
    }

    #[tokio::test]
    async fn partition_scope_filters_results() {
        let (store, provider) = build();
        seed(&store).await;
        store
            .create_document("db1", "c1", doc("a", "p1", [1.0, 0.0]), None)
            .await
            .unwrap();
        store
            .create_document("db1", "c1", doc("b", "p2", [0.9, 0.1]), None)
            .await
            .unwrap();

        let mut req = request(vec![1.0, 0.0], 5);
        req.partition_key = Some(PartitionKeyValue::single(json!("p2")));
        let hits = provider.search(req).await.unwrap();
        assert_eq!(hits.len(), 1);
        assert_eq!(hits[0].document_id, "b");
    }

    #[tokio::test]
    async fn non_flat_index_builds_hnsw_graph() {
        let (store, provider) = build();
        store.create_database("db1").await.unwrap();
        let mut container =
            CosmosContainer::new("db1", "c1", PartitionKeyDefinition::new(vec!["/pk".into()]));
        container.indexing_policy.vector_indexes = Some(vec![VectorIndex {
            path: "/embedding".into(),
            index_type: "quantizedFlat".into(),
        }]);
        store.create_container("db1", container).await.unwrap();
        for i in 0..80 {
            store
                .create_document(
                    "db1",
                    "c1",
                    doc(&format!("left-{i}"), "p1", [1.0 + i as f32 * 0.001, 0.0]),
                    None,
                )
                .await
                .unwrap();
            store
                .create_document(
                    "db1",
                    "c1",
                    doc(&format!("right-{i}"), "p1", [0.0, 1.0 + i as f32 * 0.001]),
                    None,
                )
                .await
                .unwrap();
        }

        let mut req = request(vec![1.0, 0.0], 5);
        req.index_type = "quantizedFlat".into();
        let hits = provider.search(req).await.unwrap();

        assert_eq!(hits.len(), 5);
        assert!(hits.iter().all(|hit| hit.document_id.starts_with("left-")));
        let shards = provider.shards.lock().unwrap();
        let shard = shards.values().next().unwrap();
        assert!(shard.graph.is_some());
    }

    #[tokio::test]
    async fn disabled_provider_returns_empty() {
        let inner: Arc<dyn DocumentStore> = Arc::new(InMemoryDocumentStore::new());
        let options = VectorIndexOptions {
            enabled: false,
            ..Default::default()
        };
        let provider = FlatVectorIndexProvider::new(inner, options);
        assert!(!provider.is_enabled());
        let hits = provider.search(request(vec![1.0, 0.0], 5)).await.unwrap();
        assert!(hits.is_empty());
    }
}
