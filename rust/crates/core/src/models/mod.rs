//! Domain models. Ports `src/Core/Models/*` from the .NET project.

pub mod activity;
pub mod batch;
pub mod change_feed;
pub mod emulator;
pub mod feed;
pub mod headers;
pub mod partition_key;
pub mod patch;
pub mod policies;
pub mod programmability;
pub mod resources;
pub mod telemetry;
pub mod vector;

pub use activity::ActivityEntry;
pub use batch::{BatchOperationRequest, BatchOperationResponse, BatchOperationType};
pub use change_feed::{ChangeFeedItem, ChangeType};
pub use emulator::EmulatorRuntimeState;
pub use feed::FeedResponse;
pub use partition_key::{PartitionKeyDefinition, PartitionKeyKind, PartitionKeyValue};
pub use patch::{PatchOperation, PatchRequest};
pub use policies::{
    CompositeIndex, ConflictResolutionMode, ConflictResolutionPolicy, IndexingMode, IndexingPolicy,
    SpatialIndex, SpatialType, UniqueKey, UniqueKeyPolicy, VectorEmbedding, VectorEmbeddingPolicy,
    VectorIndex,
};
pub use programmability::{
    StoredProcedure, Trigger, TriggerOperation, TriggerType, UserDefinedFunction,
};
pub use resources::{
    CosmosContainer, CosmosDatabase, CosmosDocument, CosmosOffer, CosmosPermission, CosmosUser,
    JsonObject, OfferContent, PermissionMode,
};
pub use telemetry::QueryTelemetryEntry;
pub use vector::{
    vector_math, VectorDistanceFunction, VectorHit, VectorIndexOptions, VectorSearchRequest,
};
