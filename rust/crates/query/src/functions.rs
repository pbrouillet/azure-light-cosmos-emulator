//! Built-in scalar functions for the Cosmos SQL subset.
//!
//! Ports a commonly-used subset of `CosmosQueryEngine.EvaluateBuiltInFunction`
//! (string, type-check, math, array, and conditional functions). Aggregates are
//! handled separately in the executor.

use serde_json::Value;

use cosmos_core::models::vector::{vector_math, VectorDistanceFunction};

use crate::value::{as_f64, number, QVal};

fn as_str(v: &QVal) -> Option<&str> {
    match v {
        Some(Value::String(s)) => Some(s.as_str()),
        _ => None,
    }
}

/// Dispatches a built-in scalar function by (upper-cased) name.
/// Returns `Err` for unknown functions.
pub fn call(name: &str, args: &[QVal]) -> Result<QVal, String> {
    let upper = name.to_ascii_uppercase();
    let arg = |i: usize| args.get(i).cloned().unwrap_or(None);
    let result = match upper.as_str() {
        // ---- string ----
        "CONCAT" => {
            let mut s = String::new();
            for a in args {
                match a {
                    None => return Ok(None),
                    Some(v) => s.push_str(&stringify(v)),
                }
            }
            Some(Value::String(s))
        }
        "CONTAINS" => match (as_str(&arg(0)), as_str(&arg(1))) {
            (Some(h), Some(n)) => Some(Value::Bool(h.contains(n))),
            _ => None,
        },
        "STARTSWITH" => match (as_str(&arg(0)), as_str(&arg(1))) {
            (Some(h), Some(n)) => Some(Value::Bool(h.starts_with(n))),
            _ => None,
        },
        "ENDSWITH" => match (as_str(&arg(0)), as_str(&arg(1))) {
            (Some(h), Some(n)) => Some(Value::Bool(h.ends_with(n))),
            _ => None,
        },
        "UPPER" => as_str(&arg(0)).map(|s| Value::String(s.to_uppercase())),
        "LOWER" => as_str(&arg(0)).map(|s| Value::String(s.to_lowercase())),
        "TRIM" => as_str(&arg(0)).map(|s| Value::String(s.trim().to_string())),
        "LTRIM" => as_str(&arg(0)).map(|s| Value::String(s.trim_start().to_string())),
        "RTRIM" => as_str(&arg(0)).map(|s| Value::String(s.trim_end().to_string())),
        "REVERSE" => as_str(&arg(0)).map(|s| Value::String(s.chars().rev().collect())),
        "LENGTH" => as_str(&arg(0)).map(|s| Value::from(s.chars().count() as i64)),
        "REPLACE" => match (as_str(&arg(0)), as_str(&arg(1)), as_str(&arg(2))) {
            (Some(s), Some(old), Some(new)) => Some(Value::String(s.replace(old, new))),
            _ => None,
        },
        "SUBSTRING" => substring(&arg(0), &arg(1), args.get(2).cloned().flatten()),
        "INDEX_OF" => match (as_str(&arg(0)), as_str(&arg(1))) {
            (Some(h), Some(n)) => Some(Value::from(
                h.find(n)
                    .map(|b| h[..b].chars().count() as i64)
                    .unwrap_or(-1),
            )),
            _ => None,
        },
        "STRINGEQUALS" => match (as_str(&arg(0)), as_str(&arg(1))) {
            (Some(a), Some(b)) => Some(Value::Bool(a == b)),
            _ => None,
        },
        "TOSTRING" => arg(0).map(|v| Value::String(stringify(&v))),
        // ---- type checks ----
        "IS_DEFINED" => Some(Value::Bool(arg(0).is_some())),
        "IS_NULL" => Some(Value::Bool(matches!(arg(0), Some(Value::Null)))),
        "IS_STRING" => Some(Value::Bool(matches!(arg(0), Some(Value::String(_))))),
        "IS_NUMBER" => Some(Value::Bool(matches!(arg(0), Some(Value::Number(_))))),
        "IS_BOOL" => Some(Value::Bool(matches!(arg(0), Some(Value::Bool(_))))),
        "IS_ARRAY" => Some(Value::Bool(matches!(arg(0), Some(Value::Array(_))))),
        "IS_OBJECT" => Some(Value::Bool(matches!(arg(0), Some(Value::Object(_))))),
        "IS_PRIMITIVE" => Some(Value::Bool(matches!(
            arg(0),
            Some(Value::Null | Value::Bool(_) | Value::Number(_) | Value::String(_))
        ))),
        // ---- math ----
        "ABS" => unary_num(&arg(0), f64::abs),
        "CEILING" => unary_num(&arg(0), f64::ceil),
        "FLOOR" => unary_num(&arg(0), f64::floor),
        "ROUND" => unary_num(&arg(0), |v| v.round()),
        "TRUNC" => unary_num(&arg(0), f64::trunc),
        "SIGN" => unary_num(&arg(0), |v| v.signum().trunc() * (v != 0.0) as i32 as f64),
        "SQRT" => unary_num(&arg(0), f64::sqrt),
        "EXP" => unary_num(&arg(0), f64::exp),
        "LOG" => unary_num(&arg(0), f64::ln),
        "LOG10" => unary_num(&arg(0), f64::log10),
        "SIN" => unary_num(&arg(0), f64::sin),
        "COS" => unary_num(&arg(0), f64::cos),
        "TAN" => unary_num(&arg(0), f64::tan),
        "PI" => number(std::f64::consts::PI),
        "POWER" => match (as_f64(&arg(0)), as_f64(&arg(1))) {
            (Some(a), Some(b)) => number(a.powf(b)),
            _ => None,
        },
        "SQUARE" => unary_num(&arg(0), |v| v * v),
        "DEGREES" => unary_num(&arg(0), |v| v.to_degrees()),
        "RADIANS" => unary_num(&arg(0), |v| v.to_radians()),
        // ---- array ----
        "ARRAY_LENGTH" => match arg(0) {
            Some(Value::Array(a)) => Some(Value::from(a.len() as i64)),
            _ => None,
        },
        "ARRAY_CONTAINS" => match arg(0) {
            Some(Value::Array(a)) => {
                let needle = arg(1);
                let partial = matches!(arg(2), Some(Value::Bool(true)));
                Some(Value::Bool(array_contains(&a, &needle, partial)))
            }
            _ => None,
        },
        "ARRAY_CONCAT" => {
            let mut out = Vec::new();
            for a in args {
                match a {
                    Some(Value::Array(arr)) => out.extend(arr.iter().cloned()),
                    _ => return Ok(None),
                }
            }
            Some(Value::Array(out))
        }
        "ARRAY_SLICE" => array_slice(&arg(0), &arg(1), args.get(2).cloned().flatten()),
        // ---- conditional ----
        "IIF" => {
            if matches!(arg(0), Some(Value::Bool(true))) {
                arg(1)
            } else {
                arg(2)
            }
        }
        // ---- vector ----
        "VECTORDISTANCE" => vector_distance(args)?,
        // ---- full-text (emulator approximation matching .NET) ----
        "FULLTEXTCONTAINS" => full_text_contains(args)?,
        "FULLTEXTCONTAINSALL" => full_text_contains_all_any(args, true)?,
        "FULLTEXTCONTAINSANY" => full_text_contains_all_any(args, false)?,
        "FULLTEXTSCORE" => full_text_score(args)?,
        // Local shims for preview ranking helpers. The .NET port currently
        // exposes the full-text primitives; keep these deterministic until the
        // richer ranking surface is formalized in the core query contract.
        "RRF" => reciprocal_rank_fusion(args),
        "RANK" => rank_score(args),
        // ---- spatial ----
        "ST_DISTANCE" => st_distance(args)?,
        "ST_WITHIN" => st_within(args)?,
        "ST_INTERSECTS" => st_intersects(args)?,
        "ST_ISVALID" => st_is_valid(args)?,
        "ST_ISVALIDDETAILED" => st_is_valid_detailed(args)?,
        "ST_AREA" => st_area(args)?,
        _ => return Err(format!("Unknown function: {name}")),
    };
    Ok(result)
}

/// Returns `true` if `name` is a recognized aggregate function.
pub fn is_aggregate(name: &str) -> bool {
    matches!(
        name.to_ascii_uppercase().as_str(),
        "COUNT" | "SUM" | "AVG" | "MIN" | "MAX"
    )
}

fn stringify(v: &Value) -> String {
    match v {
        Value::String(s) => s.clone(),
        Value::Null => "null".to_string(),
        Value::Bool(b) => b.to_string(),
        Value::Number(n) => n.to_string(),
        other => other.to_string(),
    }
}

fn unary_num(v: &QVal, f: impl Fn(f64) -> f64) -> QVal {
    as_f64(v).and_then(|n| number(f(n)))
}

fn substring(s: &QVal, start: &QVal, length: Option<Value>) -> QVal {
    let s = as_str(s)?;
    let chars: Vec<char> = s.chars().collect();
    let start = as_f64(start)? as i64;
    if start < 0 {
        return Some(Value::String(String::new()));
    }
    let start = start as usize;
    if start >= chars.len() {
        return Some(Value::String(String::new()));
    }
    let end = match length {
        Some(Value::Number(n)) => {
            let len = n.as_f64().unwrap_or(0.0).max(0.0) as usize;
            (start + len).min(chars.len())
        }
        _ => chars.len(),
    };
    Some(Value::String(chars[start..end].iter().collect()))
}

fn array_contains(arr: &[Value], needle: &QVal, partial: bool) -> bool {
    let Some(needle) = needle else {
        return false;
    };
    if partial {
        if let Value::Object(target) = needle {
            return arr.iter().any(|item| {
                if let Value::Object(obj) = item {
                    target.iter().all(|(k, v)| obj.get(k) == Some(v))
                } else {
                    false
                }
            });
        }
    }
    arr.iter().any(|item| item == needle)
}

fn array_slice(arr: &QVal, start: &QVal, count: Option<Value>) -> QVal {
    let Some(Value::Array(a)) = arr else {
        return None;
    };
    let len = a.len() as i64;
    let mut start = as_f64(start)? as i64;
    if start < 0 {
        start = (len + start).max(0);
    }
    let start = start.min(len).max(0) as usize;
    let end = match count {
        Some(Value::Number(n)) => {
            let c = n.as_f64().unwrap_or(0.0).max(0.0) as usize;
            (start + c).min(a.len())
        }
        _ => a.len(),
    };
    Some(Value::Array(a[start..end.max(start)].to_vec()))
}

fn vector_distance(args: &[QVal]) -> Result<QVal, String> {
    if args.len() < 2 {
        return Err("VectorDistance requires at least two arguments.".into());
    }
    let Some(a) = extract_vector(args.first().unwrap()) else {
        return Ok(None);
    };
    let Some(b) = extract_vector(args.get(1).unwrap()) else {
        return Ok(None);
    };
    if a.len() != b.len() {
        return Err("VectorDistance vectors must have the same number of dimensions.".into());
    }
    let metric = args
        .get(3)
        .and_then(|v| match v {
            Some(Value::Object(o)) => o.get("distanceFunction").and_then(Value::as_str),
            _ => None,
        })
        .map(|s| VectorDistanceFunction::parse(Some(s)))
        .unwrap_or_default();
    Ok(number(vector_math::score(&a, &b, metric)))
}

fn extract_vector(value: &QVal) -> Option<Vec<f32>> {
    match value {
        Some(Value::Array(items)) => items
            .iter()
            .map(|v| v.as_f64().map(|n| n as f32))
            .collect::<Option<Vec<_>>>(),
        _ => None,
    }
}

fn full_text_contains(args: &[QVal]) -> Result<QVal, String> {
    if args.len() != 2 {
        return Err("FullTextContains expects two arguments.".into());
    }
    Ok(Some(Value::Bool(
        match (as_str(&args[0]), as_str(&args[1])) {
            (Some(text), Some(term)) => text.to_lowercase().contains(&term.to_lowercase()),
            _ => false,
        },
    )))
}

fn full_text_contains_all_any(args: &[QVal], all: bool) -> Result<QVal, String> {
    if args.len() < 2 {
        return Err(format!(
            "FullTextContains{} expects at least two arguments.",
            if all { "All" } else { "Any" }
        ));
    }
    let Some(text) = as_str(&args[0]) else {
        return Ok(Some(Value::Bool(false)));
    };
    let text = text.to_lowercase();
    let mut any_found = false;
    for arg in &args[1..] {
        let Some(term) = as_str(arg) else {
            continue;
        };
        let found = text.contains(&term.to_lowercase());
        if all && !found {
            return Ok(Some(Value::Bool(false)));
        }
        any_found |= found;
    }
    Ok(Some(Value::Bool(if all { true } else { any_found })))
}

fn full_text_score(args: &[QVal]) -> Result<QVal, String> {
    if args.len() < 2 {
        return Err("FullTextScore expects at least two arguments.".into());
    }
    let Some(text) = as_str(&args[0]) else {
        return Ok(Some(Value::from(0.0)));
    };
    let text = text.to_lowercase();
    let score = args[1..]
        .iter()
        .filter_map(as_str)
        .filter(|term| text.contains(&term.to_lowercase()))
        .count() as f64;
    Ok(number(score))
}

fn reciprocal_rank_fusion(args: &[QVal]) -> QVal {
    let k = 60.0;
    number(
        args.iter()
            .filter_map(as_f64)
            .filter(|rank| *rank > 0.0)
            .map(|rank| 1.0 / (k + rank))
            .sum(),
    )
}

fn rank_score(args: &[QVal]) -> QVal {
    number(args.iter().filter_map(as_f64).sum())
}

#[derive(Debug, Clone)]
enum Geometry {
    Point(Point),
    LineString(Vec<Point>),
    Polygon(Vec<Vec<Point>>),
    MultiPolygon(Vec<Vec<Vec<Point>>>),
}

#[derive(Debug, Clone, Copy)]
struct Point {
    lon: f64,
    lat: f64,
}

fn st_distance(args: &[QVal]) -> Result<QVal, String> {
    if args.len() != 2 {
        return Err("ST_DISTANCE expects two arguments.".into());
    }
    let (Some(a), Some(b)) = (parse_geojson(&args[0]), parse_geojson(&args[1])) else {
        return Ok(None);
    };
    Ok(number(geodesic_distance(&a, &b)))
}

fn st_within(args: &[QVal]) -> Result<QVal, String> {
    if args.len() != 2 {
        return Err("ST_WITHIN expects two arguments.".into());
    }
    let (Some(a), Some(b)) = (parse_geojson(&args[0]), parse_geojson(&args[1])) else {
        return Ok(None);
    };
    Ok(Some(Value::Bool(within(&a, &b))))
}

fn st_intersects(args: &[QVal]) -> Result<QVal, String> {
    if args.len() != 2 {
        return Err("ST_INTERSECTS expects two arguments.".into());
    }
    let (Some(a), Some(b)) = (parse_geojson(&args[0]), parse_geojson(&args[1])) else {
        return Ok(None);
    };
    Ok(Some(Value::Bool(intersects(&a, &b))))
}

fn st_is_valid(args: &[QVal]) -> Result<QVal, String> {
    if args.len() != 1 {
        return Err("ST_ISVALID expects one argument.".into());
    }
    Ok(Some(Value::Bool(validate_geojson(&args[0]).0)))
}

fn st_is_valid_detailed(args: &[QVal]) -> Result<QVal, String> {
    if args.len() != 1 {
        return Err("ST_ISVALIDDETAILED expects one argument.".into());
    }
    let (valid, reason) = validate_geojson(&args[0]);
    let mut map = serde_json::Map::new();
    map.insert("valid".into(), Value::Bool(valid));
    map.insert("reason".into(), Value::String(reason));
    Ok(Some(Value::Object(map)))
}

fn st_area(args: &[QVal]) -> Result<QVal, String> {
    if args.len() != 1 {
        return Err("ST_AREA expects one argument.".into());
    }
    let Some(g) = parse_geojson(&args[0]) else {
        return Ok(None);
    };
    Ok(number(area(&g)))
}

fn validate_geojson(value: &QVal) -> (bool, String) {
    let Some(Value::Object(obj)) = value else {
        return (false, "Not a valid GeoJSON object.".into());
    };
    let Some(Value::String(kind)) = obj.get("type") else {
        return (
            false,
            "GeoJSON object is missing the 'type' property.".into(),
        );
    };
    if !matches!(
        kind.as_str(),
        "Point" | "LineString" | "Polygon" | "MultiPolygon"
    ) {
        return (false, format!("Unsupported GeoJSON type '{kind}'."));
    }
    if !obj.contains_key("coordinates") {
        return (
            false,
            "GeoJSON object is missing the 'coordinates' property.".into(),
        );
    }
    let Some(geometry) = parse_geojson(value) else {
        return (false, "Failed to parse GeoJSON coordinates.".into());
    };
    for point in geometry_points(&geometry) {
        if point.lon < -180.0 || point.lon > 180.0 {
            return (
                false,
                format!("Longitude value {} is out of range [-180, 180].", point.lon),
            );
        }
        if point.lat < -90.0 || point.lat > 90.0 {
            return (
                false,
                format!("Latitude value {} is out of range [-90, 90].", point.lat),
            );
        }
    }
    (true, String::new())
}

fn parse_geojson(value: &QVal) -> Option<Geometry> {
    let Some(Value::Object(obj)) = value else {
        return None;
    };
    let kind = obj.get("type")?.as_str()?;
    let coords = obj.get("coordinates")?;
    match kind {
        "Point" => parse_point(coords).map(Geometry::Point),
        "LineString" => parse_line(coords).map(Geometry::LineString),
        "Polygon" => parse_polygon(coords).map(Geometry::Polygon),
        "MultiPolygon" => coords
            .as_array()?
            .iter()
            .map(parse_polygon)
            .collect::<Option<Vec<_>>>()
            .map(Geometry::MultiPolygon),
        _ => None,
    }
}

fn parse_point(value: &Value) -> Option<Point> {
    let arr = value.as_array()?;
    Some(Point {
        lon: arr.first()?.as_f64()?,
        lat: arr.get(1)?.as_f64()?,
    })
}

fn parse_line(value: &Value) -> Option<Vec<Point>> {
    value.as_array()?.iter().map(parse_point).collect()
}

fn parse_polygon(value: &Value) -> Option<Vec<Vec<Point>>> {
    value.as_array()?.iter().map(parse_line).collect()
}

fn geometry_points(g: &Geometry) -> Vec<Point> {
    match g {
        Geometry::Point(p) => vec![*p],
        Geometry::LineString(points) => points.clone(),
        Geometry::Polygon(rings) => rings.iter().flatten().copied().collect(),
        Geometry::MultiPolygon(polys) => polys.iter().flatten().flatten().copied().collect(),
    }
}

fn geodesic_distance(a: &Geometry, b: &Geometry) -> f64 {
    if intersects(a, b) {
        return 0.0;
    }
    let ap = geometry_points(a);
    let bp = geometry_points(b);
    ap.iter()
        .flat_map(|pa| bp.iter().map(move |pb| haversine(*pa, *pb)))
        .fold(f64::INFINITY, f64::min)
}

fn haversine(a: Point, b: Point) -> f64 {
    const R: f64 = 6_371_008.8;
    let (lat1, lat2) = (a.lat.to_radians(), b.lat.to_radians());
    let dlat = (b.lat - a.lat).to_radians();
    let dlon = (b.lon - a.lon).to_radians();
    let h = (dlat / 2.0).sin().powi(2) + lat1.cos() * lat2.cos() * (dlon / 2.0).sin().powi(2);
    R * 2.0 * h.sqrt().atan2((1.0 - h).sqrt())
}

fn within(a: &Geometry, b: &Geometry) -> bool {
    match (a, b) {
        (Geometry::Point(p), Geometry::Polygon(poly)) => point_in_polygon(*p, poly),
        (Geometry::Point(p), Geometry::MultiPolygon(polys)) => {
            polys.iter().any(|poly| point_in_polygon(*p, poly))
        }
        (Geometry::Point(a), Geometry::Point(b)) => nearly_same_point(*a, *b),
        (Geometry::Polygon(poly), Geometry::Polygon(container)) => poly
            .first()
            .is_some_and(|ring| ring.iter().all(|p| point_in_polygon(*p, container))),
        _ => false,
    }
}

fn intersects(a: &Geometry, b: &Geometry) -> bool {
    within(a, b)
        || within(b, a)
        || match (a, b) {
            (Geometry::Polygon(pa), Geometry::Polygon(pb)) => polygon_edges(pa).iter().any(|ea| {
                polygon_edges(pb)
                    .iter()
                    .any(|eb| segments_intersect(*ea, *eb))
            }),
            (Geometry::LineString(line), Geometry::Polygon(poly))
            | (Geometry::Polygon(poly), Geometry::LineString(line)) => line.windows(2).any(|w| {
                polygon_edges(poly)
                    .iter()
                    .any(|edge| segments_intersect((w[0], w[1]), *edge))
            }),
            _ => false,
        }
}

fn point_in_polygon(point: Point, rings: &[Vec<Point>]) -> bool {
    let Some(outer) = rings.first() else {
        return false;
    };
    if !point_in_ring(point, outer) {
        return false;
    }
    !rings.iter().skip(1).any(|hole| point_in_ring(point, hole))
}

fn point_in_ring(point: Point, ring: &[Point]) -> bool {
    let mut inside = false;
    let mut j = ring.len().saturating_sub(1);
    for i in 0..ring.len() {
        let pi = ring[i];
        let pj = ring[j];
        if ((pi.lat > point.lat) != (pj.lat > point.lat))
            && (point.lon < (pj.lon - pi.lon) * (point.lat - pi.lat) / (pj.lat - pi.lat) + pi.lon)
        {
            inside = !inside;
        }
        j = i;
    }
    inside
}

fn polygon_edges(poly: &[Vec<Point>]) -> Vec<(Point, Point)> {
    poly.first()
        .map(|ring| ring.windows(2).map(|w| (w[0], w[1])).collect())
        .unwrap_or_default()
}

fn segments_intersect(a: (Point, Point), b: (Point, Point)) -> bool {
    fn orient(a: Point, b: Point, c: Point) -> f64 {
        (b.lon - a.lon) * (c.lat - a.lat) - (b.lat - a.lat) * (c.lon - a.lon)
    }
    let o1 = orient(a.0, a.1, b.0);
    let o2 = orient(a.0, a.1, b.1);
    let o3 = orient(b.0, b.1, a.0);
    let o4 = orient(b.0, b.1, a.1);
    (o1 > 0.0) != (o2 > 0.0) && (o3 > 0.0) != (o4 > 0.0)
}

fn nearly_same_point(a: Point, b: Point) -> bool {
    (a.lon - b.lon).abs() < 1e-12 && (a.lat - b.lat).abs() < 1e-12
}

fn area(g: &Geometry) -> f64 {
    match g {
        Geometry::Polygon(poly) => polygon_area(poly),
        Geometry::MultiPolygon(polys) => polys.iter().map(|p| polygon_area(p)).sum(),
        _ => 0.0,
    }
}

fn polygon_area(poly: &[Vec<Point>]) -> f64 {
    fn ring_area(ring: &[Point]) -> f64 {
        const R2: f64 = 6_371_008.8 * 6_371_008.8;
        if ring.len() < 4 {
            return 0.0;
        }
        let mut sum = 0.0;
        for w in ring.windows(2) {
            let lon1 = w[0].lon.to_radians();
            let lon2 = w[1].lon.to_radians();
            let lat1 = w[0].lat.to_radians();
            let lat2 = w[1].lat.to_radians();
            sum += (lon2 - lon1) * (lat1.sin() + lat2.sin());
        }
        (sum * R2 / 2.0).abs()
    }
    let Some(outer) = poly.first() else {
        return 0.0;
    };
    let holes: f64 = poly.iter().skip(1).map(|r| ring_area(r)).sum();
    (ring_area(outer) - holes).abs()
}
