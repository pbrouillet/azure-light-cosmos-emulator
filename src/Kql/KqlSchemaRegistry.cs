using Kusto.Language;
using Kusto.Language.Symbols;

namespace Azure.Cosmos.LightEmulator.Kql;

/// <summary>
/// Manages table schemas for KQL parsing and validation.
/// </summary>
public class KqlSchemaRegistry
{
    private static readonly Dictionary<string, ScalarSymbol> TypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["string"] = ScalarTypes.String,
        ["long"] = ScalarTypes.Long,
        ["int"] = ScalarTypes.Int,
        ["real"] = ScalarTypes.Real,
        ["decimal"] = ScalarTypes.Decimal,
        ["bool"] = ScalarTypes.Bool,
        ["datetime"] = ScalarTypes.DateTime,
        ["timespan"] = ScalarTypes.TimeSpan,
        ["guid"] = ScalarTypes.Guid,
        ["dynamic"] = ScalarTypes.Dynamic,
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, KqlTableSchema> _tables = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterTable(KqlTableSchema schema)
    {
        lock (_lock)
        {
            _tables[schema.TableName] = schema;
        }
    }

    public KqlTableSchema? GetTable(string name)
    {
        lock (_lock)
        {
            return _tables.GetValueOrDefault(name);
        }
    }

    public IReadOnlyList<KqlTableSchema> GetAllTables()
    {
        lock (_lock)
        {
            return _tables.Values.ToList();
        }
    }

    public GlobalState GetGlobalState()
    {
        lock (_lock)
        {
            var tableSymbols = new List<TableSymbol>();

            foreach (var schema in _tables.Values)
            {
                var columns = schema.Columns
                    .Select(c => new ColumnSymbol(c.Name, TypeMap.GetValueOrDefault(c.KqlType, ScalarTypes.String)))
                    .ToList();
                tableSymbols.Add(new TableSymbol(schema.TableName, columns));
            }

            var db = new DatabaseSymbol("monitoring", tableSymbols);
            var cluster = new ClusterSymbol("emulator", new[] { db });

            return GlobalState.Default
                .WithCluster(cluster)
                .WithDatabase(db);
        }
    }
}
