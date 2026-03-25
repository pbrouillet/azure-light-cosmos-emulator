using System.Text.Json.Nodes;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Valid;

namespace Azure.Cosmos.LightEmulator.NoSql.Query;

/// <summary>
/// Helpers for parsing GeoJSON into NTS geometries and performing geodesic calculations.
/// Cosmos DB uses WGS84 (EPSG:4326) with coordinates as [longitude, latitude].
/// Distances are returned in meters, areas in square meters.
/// </summary>
internal static class SpatialHelper
{
    private const int Srid = 4326;
    private const double EarthRadiusMeters = 6_371_008.8; // mean Earth radius (meters)
    private const double EarthRadiusSq = EarthRadiusMeters * EarthRadiusMeters;

    private static readonly GeometryFactory Factory = new(new PrecisionModel(), Srid);

    /// <summary>
    /// Attempts to parse a GeoJSON JsonObject into an NTS Geometry.
    /// Returns null if the input is not valid GeoJSON.
    /// </summary>
    public static Geometry? TryParseGeoJson(object? value)
    {
        if (value is not JsonObject obj)
            return null;

        var type = obj["type"]?.GetValue<string>();
        if (string.IsNullOrEmpty(type))
            return null;

        try
        {
            return type switch
            {
                "Point" => ParsePoint(obj),
                "LineString" => ParseLineString(obj),
                "Polygon" => ParsePolygon(obj),
                "MultiPolygon" => ParseMultiPolygon(obj),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Validates a GeoJSON object and returns (isValid, reason).
    /// </summary>
    public static (bool IsValid, string Reason) ValidateGeoJson(object? value)
    {
        if (value is not JsonObject obj)
            return (false, "Not a valid GeoJSON object.");

        var type = obj["type"]?.GetValue<string>();
        if (string.IsNullOrEmpty(type))
            return (false, "GeoJSON object is missing the 'type' property.");

        if (type is not ("Point" or "LineString" or "Polygon" or "MultiPolygon"))
            return (false, $"Unsupported GeoJSON type '{type}'.");

        var coords = obj["coordinates"];
        if (coords is null)
            return (false, "GeoJSON object is missing the 'coordinates' property.");

        Geometry? geometry;
        try
        {
            geometry = TryParseGeoJson(value);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to parse GeoJSON: {ex.Message}");
        }

        if (geometry is null)
            return (false, "Failed to parse GeoJSON coordinates.");

        // Validate coordinate ranges
        foreach (var coord in geometry.Coordinates)
        {
            if (coord.X < -180 || coord.X > 180)
                return (false, $"Longitude value {coord.X} is out of range [-180, 180].");
            if (coord.Y < -90 || coord.Y > 90)
                return (false, $"Latitude value {coord.Y} is out of range [-90, 90].");
        }

        // Use NTS IsValidOp for topological checks
        var validOp = new IsValidOp(geometry);
        if (!validOp.IsValid)
        {
            var error = validOp.ValidationError;
            return (false, error?.Message ?? "Geometry is topologically invalid.");
        }

        return (true, string.Empty);
    }

    /// <summary>
    /// Computes the geodesic distance between two geometries in meters.
    /// For Point-to-Point uses the Haversine formula.
    /// For other combinations, computes distance between nearest points then converts.
    /// </summary>
    public static double GeodesicDistanceMeters(Geometry g1, Geometry g2)
    {
        // Fast path: point to point — use Haversine
        if (g1 is Point p1 && g2 is Point p2)
            return HaversineDistance(p1.Y, p1.X, p2.Y, p2.X);

        // For complex geometries, find nearest points and use Haversine
        var nearestPoints = NetTopologySuite.Operation.Distance.DistanceOp.NearestPoints(g1, g2);
        if (nearestPoints is null || nearestPoints.Length < 2)
            return 0;

        return HaversineDistance(nearestPoints[0].Y, nearestPoints[0].X, nearestPoints[1].Y, nearestPoints[1].X);
    }

    /// <summary>
    /// Computes the geodesic area of a polygon/multipolygon in square meters.
    /// Uses the spherical excess formula. Returns 0 for non-areal geometries.
    /// </summary>
    public static double GeodesicAreaSquareMeters(Geometry geometry)
    {
        return geometry switch
        {
            Polygon polygon => SphericalPolygonArea(polygon),
            MultiPolygon mp => mp.Geometries.Sum(g => SphericalPolygonArea((Polygon)g)),
            _ => 0
        };
    }

    /// <summary>
    /// Haversine formula for distance between two points in meters.
    /// </summary>
    private static double HaversineDistance(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
    {
        var lat1 = DegreesToRadians(lat1Deg);
        var lat2 = DegreesToRadians(lat2Deg);
        var dLat = DegreesToRadians(lat2Deg - lat1Deg);
        var dLon = DegreesToRadians(lon2Deg - lon1Deg);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    /// <summary>
    /// Computes the area of a polygon on a sphere using the spherical excess method.
    /// Handles both exterior ring and holes.
    /// </summary>
    private static double SphericalPolygonArea(Polygon polygon)
    {
        var area = Math.Abs(SphericalRingArea(polygon.ExteriorRing.Coordinates));

        for (var i = 0; i < polygon.NumInteriorRings; i++)
            area -= Math.Abs(SphericalRingArea(polygon.GetInteriorRingN(i).Coordinates));

        return Math.Abs(area);
    }

    /// <summary>
    /// Computes the spherical area of a ring using the spherical excess formula.
    /// Based on the Girard theorem / L'Huilier's theorem approach.
    /// </summary>
    private static double SphericalRingArea(Coordinate[] coords)
    {
        if (coords.Length < 4) // Minimum: 3 vertices + closing vertex
            return 0;

        var n = coords.Length - 1; // Ignore the closing duplicate vertex
        double totalAngle = 0;

        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            var k = (i + 2) % n;

            totalAngle += SphericalAngle(coords[i], coords[j], coords[k]);
        }

        // Spherical excess = sum of interior angles - (n - 2) * pi
        var excess = totalAngle - (n - 2) * Math.PI;
        return Math.Abs(excess) * EarthRadiusSq;
    }

    /// <summary>
    /// Computes the spherical angle at point B in the triangle A-B-C.
    /// </summary>
    private static double SphericalAngle(Coordinate a, Coordinate b, Coordinate c)
    {
        var ba = SphericalBearing(b, a);
        var bc = SphericalBearing(b, c);
        var angle = bc - ba;

        // Normalize to [0, 2π)
        while (angle < 0)
            angle += 2 * Math.PI;
        while (angle >= 2 * Math.PI)
            angle -= 2 * Math.PI;

        // The interior angle should be in (0, π)
        if (angle > Math.PI)
            angle = 2 * Math.PI - angle;

        return angle;
    }

    /// <summary>
    /// Computes the initial bearing from point 'from' to point 'to' on a sphere.
    /// </summary>
    private static double SphericalBearing(Coordinate from, Coordinate to)
    {
        var lat1 = DegreesToRadians(from.Y);
        var lat2 = DegreesToRadians(to.Y);
        var dLon = DegreesToRadians(to.X - from.X);

        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        return Math.Atan2(y, x);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    #region GeoJSON Parsing

    private static Point ParsePoint(JsonObject obj)
    {
        var coords = obj["coordinates"] as JsonArray
            ?? throw new ArgumentException("Point missing coordinates array.");

        if (coords.Count < 2)
            throw new ArgumentException("Point coordinates must have at least 2 elements.");

        var lon = coords[0]!.GetValue<double>();
        var lat = coords[1]!.GetValue<double>();
        return Factory.CreatePoint(new Coordinate(lon, lat));
    }

    private static LineString ParseLineString(JsonObject obj)
    {
        var coords = obj["coordinates"] as JsonArray
            ?? throw new ArgumentException("LineString missing coordinates array.");

        var points = ParseCoordinateArray(coords);
        return Factory.CreateLineString(points);
    }

    private static Polygon ParsePolygon(JsonObject obj)
    {
        var coords = obj["coordinates"] as JsonArray
            ?? throw new ArgumentException("Polygon missing coordinates array.");

        return ParsePolygonRings(coords);
    }

    private static MultiPolygon ParseMultiPolygon(JsonObject obj)
    {
        var coords = obj["coordinates"] as JsonArray
            ?? throw new ArgumentException("MultiPolygon missing coordinates array.");

        var polygons = new Polygon[coords.Count];
        for (var i = 0; i < coords.Count; i++)
        {
            var ringArray = coords[i] as JsonArray
                ?? throw new ArgumentException("MultiPolygon element is not an array.");
            polygons[i] = ParsePolygonRings(ringArray);
        }

        return Factory.CreateMultiPolygon(polygons);
    }

    private static Polygon ParsePolygonRings(JsonArray rings)
    {
        if (rings.Count == 0)
            throw new ArgumentException("Polygon must have at least one ring.");

        var exteriorRing = Factory.CreateLinearRing(ParseCoordinateArray(rings[0] as JsonArray
            ?? throw new ArgumentException("Polygon ring is not an array.")));

        var holes = new LinearRing[rings.Count - 1];
        for (var i = 1; i < rings.Count; i++)
        {
            holes[i - 1] = Factory.CreateLinearRing(ParseCoordinateArray(rings[i] as JsonArray
                ?? throw new ArgumentException("Polygon ring is not an array.")));
        }

        return Factory.CreatePolygon(exteriorRing, holes);
    }

    private static Coordinate[] ParseCoordinateArray(JsonArray coordArray)
    {
        var result = new Coordinate[coordArray.Count];
        for (var i = 0; i < coordArray.Count; i++)
        {
            var pair = coordArray[i] as JsonArray
                ?? throw new ArgumentException("Coordinate element is not an array.");
            if (pair.Count < 2)
                throw new ArgumentException("Coordinate must have at least 2 elements.");

            result[i] = new Coordinate(pair[0]!.GetValue<double>(), pair[1]!.GetValue<double>());
        }
        return result;
    }

    #endregion
}
