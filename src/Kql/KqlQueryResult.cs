namespace Azure.Cosmos.LightEmulator.Kql;

public record KqlColumnSchema(string Name, string KqlType);

public record KqlTableSchema(string TableName, IReadOnlyList<KqlColumnSchema> Columns);

public record KqlQueryResult(
    KqlTableSchema Schema,
    IReadOnlyList<Dictionary<string, object?>> Rows);
