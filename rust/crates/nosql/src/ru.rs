//! Request Unit (RU) cost estimation. Ports `RuCostCalculator.cs`.

/// Point read: ~1 RU per 1KB (minimum 1).
pub fn point_read(document_size_bytes: usize) -> f64 {
    (document_size_bytes as f64 / 1024.0).ceil().max(1.0)
}

/// Create: base 5 RU + ~1 RU per KB.
pub fn create(document_size_bytes: usize) -> f64 {
    5.0 + (document_size_bytes as f64 / 1024.0).ceil()
}

/// Replace: base 5 RU + ~1 RU per KB.
pub fn replace(document_size_bytes: usize) -> f64 {
    5.0 + (document_size_bytes as f64 / 1024.0).ceil()
}

/// Upsert: base 5 RU + ~1 RU per KB.
pub fn upsert(document_size_bytes: usize) -> f64 {
    5.0 + (document_size_bytes as f64 / 1024.0).ceil()
}

/// Delete: flat 5 RU.
pub fn delete() -> f64 {
    5.0
}

/// Query cost: base 2.5 RU + result cost, times a cross-partition multiplier.
pub fn query(
    result_count: usize,
    total_result_size_bytes: usize,
    is_cross_partition: bool,
    partition_count: usize,
    scan_multiplier: f64,
) -> f64 {
    let base_cost = 2.5;
    let result_cost =
        result_count as f64 * 0.5 + (total_result_size_bytes.max(1) as f64 / 1024.0).ceil();
    let multiplier = if is_cross_partition {
        partition_count.max(2) as f64
    } else {
        1.0
    };
    ((base_cost + result_cost) * multiplier * scan_multiplier * 100.0).round() / 100.0
}

pub fn list_databases() -> f64 {
    1.0
}
pub fn list_containers() -> f64 {
    1.0
}
pub fn get_database() -> f64 {
    1.0
}
pub fn get_container() -> f64 {
    1.0
}
pub fn create_database() -> f64 {
    5.0
}
pub fn delete_database() -> f64 {
    5.0
}
pub fn create_container() -> f64 {
    5.0
}
pub fn delete_container() -> f64 {
    5.0
}
