using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Query;
using Azure.Cosmos.LightEmulator.Storage.ChangeFeed;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class SystemFunctionTests
{
    // ── Trig inverse functions ──────────────────────────────────────────

    [Theory]
    [InlineData("ACOS", 1.0, 0.0)]
    [InlineData("ASIN", 0.0, 0.0)]
    [InlineData("ATAN", 0.0, 0.0)]
    public async Task TrigInverse_ReturnsExpectedValue(string func, double input, double expected)
    {
        var result = await EvalScalar($"SELECT VALUE {func}({input}) FROM c");
        ToDouble(result).Should().BeApproximately(expected, 1e-10);
    }

    [Fact]
    public async Task Acos_OfZero_ReturnsPiOverTwo()
    {
        var result = await EvalScalar("SELECT VALUE ACOS(0) FROM c");
        ToDouble(result).Should().BeApproximately(Math.PI / 2, 1e-10);
    }

    [Fact]
    public async Task Asin_OfOne_ReturnsPiOverTwo()
    {
        var result = await EvalScalar("SELECT VALUE ASIN(1) FROM c");
        ToDouble(result).Should().BeApproximately(Math.PI / 2, 1e-10);
    }

    [Fact]
    public async Task Atan_OfOne_ReturnsPiOverFour()
    {
        var result = await EvalScalar("SELECT VALUE ATAN(1) FROM c");
        ToDouble(result).Should().BeApproximately(Math.PI / 4, 1e-10);
    }

    [Fact]
    public async Task Atn2_ReturnsAtan2()
    {
        var result = await EvalScalar("SELECT VALUE ATN2(1, 1) FROM c");
        ToDouble(result).Should().BeApproximately(Math.Atan2(1, 1), 1e-10);
    }

    [Fact]
    public async Task Cot_ReturnsCotangent()
    {
        var result = await EvalScalar("SELECT VALUE COT(1) FROM c");
        ToDouble(result).Should().BeApproximately(1.0 / Math.Tan(1.0), 1e-10);
    }

    // ── Other math functions ────────────────────────────────────────────

    [Fact]
    public async Task Square_ReturnsSquare()
    {
        var result = await EvalScalar("SELECT VALUE SQUARE(5) FROM c");
        ToDouble(result).Should().Be(25.0);
    }

    [Fact]
    public async Task Rand_ReturnsBetweenZeroAndOne()
    {
        var result = await EvalScalar("SELECT VALUE RAND() FROM c");
        var value = ToDouble(result);
        value.Should().BeGreaterThanOrEqualTo(0.0);
        value.Should().BeLessThan(1.0);
    }

    [Fact]
    public async Task NumberBin_BinsCorrectly()
    {
        var result = await EvalScalar("SELECT VALUE NumberBin(4.5, 2) FROM c");
        ToDouble(result).Should().Be(4.0);
    }

    [Fact]
    public async Task NumberBin_NegativeValue()
    {
        var result = await EvalScalar("SELECT VALUE NumberBin(-3.5, 2) FROM c");
        ToDouble(result).Should().Be(-4.0);
    }

    // ── INT* integer math family ────────────────────────────────────────

    [Fact]
    public async Task IntAdd_ReturnsSum()
    {
        var result = await EvalScalar("SELECT VALUE IntAdd(10, 20) FROM c");
        ToLong(result).Should().Be(30);
    }

    [Fact]
    public async Task IntSub_ReturnsDifference()
    {
        var result = await EvalScalar("SELECT VALUE IntSub(30, 12) FROM c");
        ToLong(result).Should().Be(18);
    }

    [Fact]
    public async Task IntMul_ReturnsProduct()
    {
        var result = await EvalScalar("SELECT VALUE IntMul(6, 7) FROM c");
        ToLong(result).Should().Be(42);
    }

    [Fact]
    public async Task IntDiv_ReturnsQuotient()
    {
        var result = await EvalScalar("SELECT VALUE IntDiv(20, 3) FROM c");
        ToLong(result).Should().Be(6);
    }

    [Fact]
    public async Task IntMod_ReturnsRemainder()
    {
        var result = await EvalScalar("SELECT VALUE IntMod(20, 3) FROM c");
        ToLong(result).Should().Be(2);
    }

    [Fact]
    public async Task IntBitAnd_ReturnsBitwiseAnd()
    {
        var result = await EvalScalar("SELECT VALUE IntBitAnd(15, 9) FROM c");
        ToLong(result).Should().Be(15 & 9);
    }

    [Fact]
    public async Task IntBitOr_ReturnsBitwiseOr()
    {
        var result = await EvalScalar("SELECT VALUE IntBitOr(15, 9) FROM c");
        ToLong(result).Should().Be(15 | 9);
    }

    [Fact]
    public async Task IntBitXor_ReturnsBitwiseXor()
    {
        var result = await EvalScalar("SELECT VALUE IntBitXor(15, 9) FROM c");
        ToLong(result).Should().Be(15 ^ 9);
    }

    [Fact]
    public async Task IntBitNot_ReturnsBitwiseNot()
    {
        var result = await EvalScalar("SELECT VALUE IntBitNot(0) FROM c");
        ToLong(result).Should().Be(~0L);
    }

    [Fact]
    public async Task IntBitLeftShift_Shifts()
    {
        var result = await EvalScalar("SELECT VALUE IntBitLeftShift(1, 4) FROM c");
        ToLong(result).Should().Be(1L << 4);
    }

    [Fact]
    public async Task IntBitRightShift_Shifts()
    {
        var result = await EvalScalar("SELECT VALUE IntBitRightShift(16, 2) FROM c");
        ToLong(result).Should().Be(16L >> 2);
    }

    [Fact]
    public async Task IntDiv_ByZero_ReturnsUndefined()
    {
        // Division by zero should produce undefined (represented as null in JSON output)
        var (store, engine) = CreateSut();
        await SeedDocument(store);
        var queryResult = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT IntDiv(10, 0) AS result FROM c");
        queryResult.Resources.Should().ContainSingle();
        queryResult.Resources[0]["result"].Should().BeNull();
    }

    [Fact]
    public async Task IntAdd_NonInteger_ReturnsUndefined()
    {
        var (store, engine) = CreateSut();
        await SeedDocument(store);
        var queryResult = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT IntAdd(1.5, 2) AS result FROM c");
        queryResult.Resources.Should().ContainSingle();
        queryResult.Resources[0]["result"].Should().BeNull();
    }

    // ── String functions ────────────────────────────────────────────────

    [Fact]
    public async Task IndexOf_FindsSubstring()
    {
        var result = await EvalScalar("SELECT VALUE INDEX_OF('Hello World', 'World') FROM c");
        ToInt(result).Should().Be(6);
    }

    [Fact]
    public async Task IndexOf_NotFound_ReturnsMinusOne()
    {
        var result = await EvalScalar("SELECT VALUE INDEX_OF('Hello', 'xyz') FROM c");
        ToInt(result).Should().Be(-1);
    }

    [Fact]
    public async Task IndexOf_WithStartIndex()
    {
        var result = await EvalScalar("SELECT VALUE INDEX_OF('abcabc', 'abc', 1) FROM c");
        ToInt(result).Should().Be(3);
    }

    [Fact]
    public async Task StringEquals_CaseSensitive()
    {
        var result = await EvalScalar("SELECT VALUE StringEquals('abc', 'abc') FROM c");
        result.Should().Be(true);
    }

    [Fact]
    public async Task StringEquals_CaseSensitive_Mismatch()
    {
        var result = await EvalScalar("SELECT VALUE StringEquals('abc', 'ABC') FROM c");
        result.Should().Be(false);
    }

    [Fact]
    public async Task StringEquals_CaseInsensitive()
    {
        var result = await EvalScalar("SELECT VALUE StringEquals('abc', 'ABC', true) FROM c");
        result.Should().Be(true);
    }

    [Fact]
    public async Task StringToArray_ParsesJsonArray()
    {
        var result = await EvalScalar("SELECT VALUE StringToArray('[\"a\",\"b\",\"c\"]') FROM c");
        var arr = result.Should().BeOfType<JsonArray>().Subject;
        arr.Should().HaveCount(3);
        arr[0]!.GetValue<string>().Should().Be("a");
        arr[1]!.GetValue<string>().Should().Be("b");
        arr[2]!.GetValue<string>().Should().Be("c");
    }

    [Fact]
    public async Task StringToArray_EmptyArray()
    {
        var result = await EvalScalar("SELECT VALUE StringToArray('[]') FROM c");
        var arr = result.Should().BeOfType<JsonArray>().Subject;
        arr.Should().HaveCount(0);
    }

    [Fact]
    public async Task StringToArray_InvalidJson_ReturnsNull()
    {
        // Invalid JSON returns undefined which projects as null
        var result = await EvalScalar("SELECT VALUE StringToArray('not json') FROM c");
        result.Should().BeNull();
    }

    // ── STRINGTO* functions ─────────────────────────────────────────────

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("  true  ", true)]
    public async Task StringToBoolean_ParsesValidValues(string input, bool expected)
    {
        var result = await EvalScalar($"SELECT VALUE STRINGTOBOOLEAN('{input}') FROM c");
        result.Should().Be(expected);
    }

    [Fact]
    public async Task StringToBoolean_InvalidValue_ReturnsNull()
    {
        var result = await EvalScalar("SELECT VALUE STRINGTOBOOLEAN('TRUE') FROM c");
        result.Should().BeNull();
    }

    [Fact]
    public async Task StringToNull_ParsesNull()
    {
        var result = await EvalScalar("SELECT VALUE STRINGTONULL('null') FROM c");
        result.Should().BeNull();
    }

    [Fact]
    public async Task StringToNull_WithWhitespace()
    {
        var result = await EvalScalar("SELECT VALUE STRINGTONULL('  null  ') FROM c");
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("100", 100.0)]
    [InlineData("3.14", 3.14)]
    [InlineData("  60  ", 60.0)]
    public async Task StringToNumber_ParsesValidValues(string input, double expected)
    {
        var result = await EvalScalar($"SELECT VALUE STRINGTONUMBER('{input}') FROM c");
        ToDouble(result).Should().BeApproximately(expected, 1e-10);
    }

    [Fact]
    public async Task StringToNumber_InvalidValue_ReturnsNull()
    {
        var result = await EvalScalar("SELECT VALUE STRINGTONUMBER('Hello') FROM c");
        result.Should().BeNull();
    }

    [Fact]
    public async Task StringToObject_ParsesJsonObject()
    {
        var result = await EvalScalar("SELECT VALUE STRINGTOOBJECT('{\"name\":\"test\"}') FROM c");
        var obj = result.Should().BeOfType<JsonObject>().Subject;
        obj["name"]!.GetValue<string>().Should().Be("test");
    }

    [Fact]
    public async Task StringToObject_EmptyObject()
    {
        var result = await EvalScalar("SELECT VALUE STRINGTOOBJECT('{}') FROM c");
        var obj = result.Should().BeOfType<JsonObject>().Subject;
        obj.Count.Should().Be(0);
    }

    [Fact]
    public async Task StringToObject_InvalidJson_ReturnsNull()
    {
        var result = await EvalScalar("SELECT VALUE STRINGTOOBJECT('not json') FROM c");
        result.Should().BeNull();
    }

    // ── IIF function ────────────────────────────────────────────────────

    [Fact]
    public async Task Iif_TrueCondition_ReturnsTrueValue()
    {
        var result = await EvalScalar("SELECT VALUE IIF(true, 123, 456) FROM c");
        ToDouble(result).Should().Be(123);
    }

    [Fact]
    public async Task Iif_FalseCondition_ReturnsFalseValue()
    {
        var result = await EvalScalar("SELECT VALUE IIF(false, 123, 456) FROM c");
        ToDouble(result).Should().Be(456);
    }

    [Fact]
    public async Task Iif_NonBooleanCondition_ReturnsFalseValue()
    {
        // Non-boolean values (numbers, strings) should return the false branch
        var result = await EvalScalar("SELECT VALUE IIF(123, 'yes', 'no') FROM c");
        result.Should().Be("no");
    }

    // ── ARRAY_CONTAINS enhancements ─────────────────────────────────────

    [Fact]
    public async Task ArrayContains_PartialMatch_FindsSubsetObject()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["items"] = new JsonArray(
                new JsonObject { ["category"] = "shirts", ["color"] = "blue" }),
            ["search"] = new JsonObject { ["category"] = "shirts" }
        };
        await store.CreateDocumentAsync("db", "coll", doc);
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ARRAY_CONTAINS(c.items, c.search, true) FROM c");
        result.Resources.Should().ContainSingle();
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ArrayContains_PartialMatch_WithInlineObjectLiteral_FindsSubset()
    {
        // Regression: the SQL parser must accept `{'key': value}` object
        // literals as expressions, in particular as the `search` argument to
        // ARRAY_CONTAINS with partial-match enabled. This is the pattern used
        // by client code such as `get_user_by_external_id`.
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "user1",
            ["tenantId"] = "t1",
            ["identity_links"] = new JsonArray(
                new JsonObject { ["provider"] = "aad", ["external_id"] = "abc-123" })
        };
        await store.CreateDocumentAsync("db", "coll", doc);
        var result = await engine.ExecuteQueryAsync(
            "db",
            "coll",
            "SELECT * FROM c WHERE ARRAY_CONTAINS(c.identity_links, {'external_id': @eid}, true)",
            new Dictionary<string, object?> { ["@eid"] = "abc-123" });
        result.Resources.Should().ContainSingle();
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("user1");
    }

    [Fact]
    public async Task ObjectLiteral_InProjection_ReturnsConstructedObject()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["name"] = "Alice"
        };
        await store.CreateDocumentAsync("db", "coll", doc);
        var result = await engine.ExecuteQueryAsync(
            "db",
            "coll",
            "SELECT VALUE {'who': c.name, 'tag': 'user'} FROM c");
        result.Resources.Should().ContainSingle();
        var value = result.Resources[0]["$1"]!.AsObject();
        value["who"]!.GetValue<string>().Should().Be("Alice");
        value["tag"]!.GetValue<string>().Should().Be("user");
    }

    [Fact]
    public async Task ArrayContains_PartialMatch_ReturnsFalseWhenNoMatch()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["items"] = new JsonArray(
                new JsonObject { ["category"] = "shirts", ["color"] = "blue" }),
            ["search"] = new JsonObject { ["category"] = "shorts" }
        };
        await store.CreateDocumentAsync("db", "coll", doc);
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ARRAY_CONTAINS(c.items, c.search, true) FROM c");
        result.Resources.Should().ContainSingle();
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task ArrayContainsAll_ReturnsTrue_WhenAllValuesPresent()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["tags"] = new JsonArray("a", "b", "c")
        };
        await store.CreateDocumentAsync("db", "coll", doc);
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ARRAY_CONTAINS_ALL(c.tags, 'a', 'b') FROM c");
        result.Resources.Should().ContainSingle();
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ArrayContainsAll_ReturnsFalse_WhenValueMissing()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["tags"] = new JsonArray("a", "b")
        };
        await store.CreateDocumentAsync("db", "coll", doc);
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ARRAY_CONTAINS_ALL(c.tags, 'a', 'z') FROM c");
        result.Resources.Should().ContainSingle();
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task ArrayContainsAny_ReturnsTrue_WhenAnyValuePresent()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["tags"] = new JsonArray("a", "b")
        };
        await store.CreateDocumentAsync("db", "coll", doc);
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ARRAY_CONTAINS_ANY(c.tags, 'z', 'a') FROM c");
        result.Resources.Should().ContainSingle();
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ArrayContainsAny_ReturnsFalse_WhenNonePresent()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["tags"] = new JsonArray("a", "b")
        };
        await store.CreateDocumentAsync("db", "coll", doc);
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ARRAY_CONTAINS_ANY(c.tags, 'x', 'y') FROM c");
        result.Resources.Should().ContainSingle();
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeFalse();
    }

    // ── Full-text search functions ──────────────────────────────────────

    [Fact]
    public async Task FullTextContains_ReturnsTrue_WhenTermPresent()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["text"] = "The quick brown fox jumps over the lazy dog"
        };
        await store.CreateDocumentAsync("db", "coll", doc);
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE FullTextContains(c.text, 'quick') FROM c");
        result.Resources.Should().ContainSingle();
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task FullTextContains_ReturnsFalse_WhenTermMissing()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["text"] = "Hello world"
        };
        await store.CreateDocumentAsync("db", "coll", doc);
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE FullTextContains(c.text, 'missing') FROM c");
        result.Resources.Should().ContainSingle();
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task FullTextContainsAll_ReturnsTrue_WhenAllTermsPresent()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["text"] = "The quick brown fox"
        };
        await store.CreateDocumentAsync("db", "coll", doc);
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE FullTextContainsAll(c.text, 'quick', 'brown') FROM c");
        result.Resources.Should().ContainSingle();
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task FullTextContainsAny_ReturnsTrue_WhenAnyTermPresent()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["text"] = "Hello world"
        };
        await store.CreateDocumentAsync("db", "coll", doc);
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE FullTextContainsAny(c.text, 'missing', 'world') FROM c");
        result.Resources.Should().ContainSingle();
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task FullTextScore_ReturnsMatchCount()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["text"] = "The quick brown fox"
        };
        await store.CreateDocumentAsync("db", "coll", doc);
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE FullTextScore(c.text, 'quick', 'brown', 'missing') FROM c");
        result.Resources.Should().ContainSingle();
        result.Resources[0]["$1"]!.GetValue<double>().Should().Be(2.0);
    }

    // ── Static datetime functions ───────────────────────────────────────

    [Fact]
    public async Task GetCurrentDateTime_ReturnsConsistentValueAcrossRows()
    {
        var (store, engine) = CreateSut();
        await store.CreateDocumentAsync("db", "coll", new JsonObject { ["id"] = "d1", ["tenantId"] = "t1" });
        await store.CreateDocumentAsync("db", "coll", new JsonObject { ["id"] = "d2", ["tenantId"] = "t1" });

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE GetCurrentDateTime() FROM c");
        result.Resources.Should().HaveCount(2);
        var val1 = result.Resources[0]["$1"]!.GetValue<string>();
        var val2 = result.Resources[1]["$1"]!.GetValue<string>();
        val1.Should().Be(val2);
    }

    [Fact]
    public async Task GetCurrentTimestamp_ReturnsConsistentValueAcrossRows()
    {
        var (store, engine) = CreateSut();
        await store.CreateDocumentAsync("db", "coll", new JsonObject { ["id"] = "d1", ["tenantId"] = "t1" });
        await store.CreateDocumentAsync("db", "coll", new JsonObject { ["id"] = "d2", ["tenantId"] = "t1" });

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE GetCurrentTimestamp() FROM c");
        result.Resources.Should().HaveCount(2);
        var val1 = result.Resources[0]["$1"]!.GetValue<long>();
        var val2 = result.Resources[1]["$1"]!.GetValue<long>();
        val1.Should().Be(val2);
    }

    [Fact]
    public async Task GetCurrentTicks_ReturnsConsistentValueAcrossRows()
    {
        var (store, engine) = CreateSut();
        await store.CreateDocumentAsync("db", "coll", new JsonObject { ["id"] = "d1", ["tenantId"] = "t1" });
        await store.CreateDocumentAsync("db", "coll", new JsonObject { ["id"] = "d2", ["tenantId"] = "t1" });

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE GetCurrentTicks() FROM c");
        result.Resources.Should().HaveCount(2);
        var val1 = result.Resources[0]["$1"]!.GetValue<long>();
        var val2 = result.Resources[1]["$1"]!.GetValue<long>();
        val1.Should().Be(val2);
    }

    // ── Array/Set functions ─────────────────────────────────────────────

    [Fact]
    public async Task SetIntersect_ReturnsCommonElements()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["arr1"] = new JsonArray(1, 2, 3),
            ["arr2"] = new JsonArray(2, 3, 4)
        };
        await store.CreateDocumentAsync("db", "coll", doc);

        var queryResult = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE SetIntersect(c.arr1, c.arr2) FROM c");

        queryResult.Resources.Should().ContainSingle();
        var arr = (JsonArray)queryResult.Resources[0]["$1"]!;
        var values = arr.Select(n => n!.GetValue<int>()).ToList();
        values.Should().BeEquivalentTo([2, 3]);
    }

    [Fact]
    public async Task SetUnion_ReturnsCombinedDistinctElements()
    {
        var (store, engine) = CreateSut();
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["arr1"] = new JsonArray(1, 2, 3),
            ["arr2"] = new JsonArray(2, 3, 4)
        };
        await store.CreateDocumentAsync("db", "coll", doc);

        var queryResult = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE SetUnion(c.arr1, c.arr2) FROM c");

        queryResult.Resources.Should().ContainSingle();
        var arr = (JsonArray)queryResult.Resources[0]["$1"]!;
        var values = arr.Select(n => n!.GetValue<int>()).ToList();
        values.Should().BeEquivalentTo([1, 2, 3, 4]);
    }

    // ── Type-checking functions ─────────────────────────────────────────

    [Fact]
    public async Task IsInteger_TrueForWholeNumber()
    {
        var result = await EvalScalar("SELECT VALUE IS_INTEGER(42) FROM c");
        result.Should().Be(true);
    }

    [Fact]
    public async Task IsInteger_FalseForFloat()
    {
        var result = await EvalScalar("SELECT VALUE IS_INTEGER(3.14) FROM c");
        result.Should().Be(false);
    }

    [Fact]
    public async Task IsInteger_FalseForString()
    {
        var result = await EvalScalar("SELECT VALUE IS_INTEGER('hello') FROM c");
        result.Should().Be(false);
    }

    [Fact]
    public async Task IsFinite_TrueForNumber()
    {
        var result = await EvalScalar("SELECT VALUE IS_FINITE(42) FROM c");
        result.Should().Be(true);
    }

    [Fact]
    public async Task IsFinite_FalseForString()
    {
        var result = await EvalScalar("SELECT VALUE IS_FINITE('hello') FROM c");
        result.Should().Be(false);
    }

    [Fact]
    public async Task IsNan_FalseForNumber()
    {
        var result = await EvalScalar("SELECT VALUE IS_NAN(42) FROM c");
        result.Should().Be(false);
    }

    // ── Date/Time functions ─────────────────────────────────────────────

    [Fact]
    public async Task DateTimeFromParts_ConstructsDatetime()
    {
        var result = await EvalScalar("SELECT VALUE DateTimeFromParts(2024, 3, 15, 10, 30, 45, 123) FROM c");
        result.Should().BeOfType<string>();
        var dt = DateTimeOffset.Parse((string)result!);
        dt.Year.Should().Be(2024);
        dt.Month.Should().Be(3);
        dt.Day.Should().Be(15);
        dt.Hour.Should().Be(10);
        dt.Minute.Should().Be(30);
        dt.Second.Should().Be(45);
        dt.Millisecond.Should().Be(123);
    }

    [Fact]
    public async Task DateTimeToTimestamp_ConvertsToUnixMs()
    {
        var result = await EvalScalar("SELECT VALUE DateTimeToTimestamp('2020-01-01T00:00:00.0000000Z') FROM c");
        ToLong(result).Should().Be(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task TimestampToDateTime_ConvertsFromUnixMs()
    {
        var ms = new DateTimeOffset(2020, 6, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var result = await EvalScalar($"SELECT VALUE TimestampToDateTime({ms}) FROM c");
        result.Should().BeOfType<string>();
        var dt = DateTimeOffset.Parse((string)result!);
        dt.Year.Should().Be(2020);
        dt.Month.Should().Be(6);
        dt.Day.Should().Be(15);
        dt.Hour.Should().Be(12);
    }

    [Fact]
    public async Task DateTimeBin_BinsToHour()
    {
        var result = await EvalScalar("SELECT VALUE DateTimeBin('2021-06-28T17:24:29.0000000Z', 'hour', 1) FROM c");
        result.Should().BeOfType<string>();
        var dt = DateTimeOffset.Parse((string)result!);
        dt.Should().Be(new DateTimeOffset(2021, 6, 28, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task DateTimeBin_BinsToDay()
    {
        var result = await EvalScalar("SELECT VALUE DateTimeBin('2021-06-28T17:24:29.0000000Z', 'day', 1) FROM c");
        result.Should().BeOfType<string>();
        var dt = DateTimeOffset.Parse((string)result!);
        dt.Day.Should().Be(28);
        dt.Hour.Should().Be(0);
        dt.Minute.Should().Be(0);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static async Task<object?> EvalScalar(string query)
    {
        var (store, engine) = CreateSut();
        await SeedDocument(store);
        var queryResult = await engine.ExecuteQueryAsync("db", "coll", query);
        queryResult.Resources.Should().ContainSingle();
        var resource = queryResult.Resources[0];
        // SELECT VALUE wraps result in {"$1": value}
        return resource.TryGetPropertyValue("$1", out var node) ? NormalizeNode(node) : NormalizeNode(resource);
    }

    private static object? NormalizeNode(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonValue jv when jv.TryGetValue<bool>(out var b) => b,
            JsonValue jv when jv.TryGetValue<long>(out var l) => l,
            JsonValue jv when jv.TryGetValue<double>(out var d) => d,
            JsonValue jv when jv.TryGetValue<string>(out var s) => s,
            JsonArray ja => ja,
            JsonObject jo => jo,
            _ => node.ToString()
        };
    }

    private static double ToDouble(object? value) => Convert.ToDouble(value);
    private static long ToLong(object? value) => Convert.ToInt64(value);
    private static int ToInt(object? value) => Convert.ToInt32(value);

    private static async Task SeedDocument(IDocumentStore store)
    {
        var doc = new JsonObject
        {
            ["id"] = "doc1",
            ["tenantId"] = "t1",
            ["name"] = "test"
        };
        await store.CreateDocumentAsync("db", "coll", doc);
    }

    private static (IDocumentStore Store, CosmosQueryEngine Engine) CreateSut()
    {
        var store = new SurrealDbDocumentStore(
            new SurrealDbConnectionManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            new InMemoryChangeFeedProvider());

        store.CreateDatabaseAsync("db").GetAwaiter().GetResult();
        store.CreateContainerAsync("db", new CosmosContainer
        {
            Id = "coll",
            DatabaseId = "db",
            PartitionKey = new PartitionKeyDefinition
            {
                Paths = ["/tenantId"]
            }
        }).GetAwaiter().GetResult();

        return (store, new CosmosQueryEngine(store, new IndexValidationService()));
    }
}
