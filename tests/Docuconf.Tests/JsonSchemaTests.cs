using System.Text.Json.Nodes;

namespace Docuconf.Tests;

// The contract-first mode checks json values against the contract's JSON Schema (draft 2020-12), as the other SDKs do.
public sealed class JsonSchemaTests
{
    private const string Contract = """
        {
          "apiVersion": "docuconf.dev/v1alpha1",
          "kind": "ConfigContract",
          "metadata": {"name": "orders"},
          "vars": {
            "LIMITS": {"type": "json", "description": "Rate limits", "schema": {
              "type": "object", "additionalProperties": false, "required": ["perMinute"],
              "properties": {
                "perMinute": {"type": "integer", "minimum": 1},
                "tier": {"enum": ["free", "paid"]},
                "tags": {"type": "array", "maxItems": 2, "items": {"type": "string", "maxLength": 2}}
              }}},
            "SIGNING": {"type": "json", "description": "Signing settings", "secret": true, "schema": {
              "type": "object", "properties": {"key": {"type": "string", "minLength": 32}}}},
            "ANY": {"type": "json", "description": "Any JSON document"}
          }
        }
        """;

    private static ContractLoadResult Validate(string name, string value) =>
        DocuconfContract.FromJson(Contract).Validate(new Dictionary<string, string> { [name] = value });

    [Theory]
    [InlineData("""{"perMinute":60}""")]
    [InlineData("""{"perMinute":1,"tier":"paid","tags":["eu","🚀🚀"]}""")] // maxLength counts code points: 🚀🚀 is 2
    public void A_value_that_matches_the_schema_loads(string value)
    {
        var result = Validate("LIMITS", value);

        Assert.Empty(result.Violations);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(value), (JsonNode?)result.Values["LIMITS"]));
    }

    [Theory]
    [InlineData("""{"perMinute":0}""", "/perMinute fails minimum")]
    [InlineData("""{"perMinute":1,"perHour":5}""", "/perHour fails additionalProperties")]
    [InlineData("""{"tier":"free"}""", "the value fails required")]
    [InlineData("""{"perMinute":1.5}""", "/perMinute fails type")]
    [InlineData("""{"perMinute":1,"tier":"gold"}""", "/tier fails enum")]
    [InlineData("""{"perMinute":1,"tags":["a","b","c"]}""", "/tags fails maxItems")]
    [InlineData("""{"perMinute":1,"tags":["abc"]}""", "/tags/0 fails maxLength")]
    [InlineData("""{"perMinute":1,"tags":["e\u0301e"]}""", "/tags/0 fails maxLength")] // 3 code points, 2 graphemes
    [InlineData("[]", "the value fails type")]
    public void A_value_that_violates_the_schema_is_a_schema_mismatch(string value, string expected)
    {
        var violation = Assert.Single(Validate("LIMITS", value).Violations);

        Assert.Equal(("LIMITS", Codes.SchemaMismatch), (violation.Input, violation.Code));
        Assert.Contains(expected, violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_mismatch_never_shows_the_value()
    {
        var violation = Assert.Single(Validate("SIGNING", """{"key":"hunter2"}""").Violations);

        Assert.Equal("[schema_mismatch] SIGNING: /key fails minLength (value redacted)", violation.ToString());
    }

    // Lengths count code points even when the schema names its dialect.
    [Fact]
    public void Lengths_count_code_points_with_an_explicit_dialect()
    {
        var contract = DocuconfContract.FromJson("""
            {"apiVersion": "docuconf.dev/v1alpha1", "kind": "ConfigContract", "metadata": {"name": "orders"},
             "vars": {"CODE": {"type": "json", "description": "A short code", "schema":
               {"$schema": "https://json-schema.org/draft/2020-12/schema", "type": "string", "minLength": 2, "maxLength": 2}}}}
            """);

        Assert.Empty(contract.Validate(new Dictionary<string, string> { ["CODE"] = "\"🚀🚀\"" }).Violations);
        var violation = Assert.Single(contract.Validate(new Dictionary<string, string> { ["CODE"] = "\"e\u0301e\"" }).Violations);
        Assert.Equal("[schema_mismatch] CODE: the value fails maxLength: is 3 characters, more than 2", violation.ToString());
    }

    [Fact]
    public void A_variable_without_a_schema_takes_any_json()
    {
        Assert.Empty(Validate("ANY", """{"a":[1,"two",null,true]}""").Violations);
    }

    [Fact]
    public void A_schema_that_is_not_json_schema_is_rejected_when_the_contract_is_read()
    {
        var ex = Assert.Throws<ContractException>(() => DocuconfContract.FromJson("""
            {"apiVersion": "docuconf.dev/v1alpha1", "kind": "ConfigContract", "metadata": {"name": "orders"},
             "vars": {"LIMITS": {"type": "json", "description": "Rate limits", "schema": {"type": 12}}}}
            """));

        Assert.Contains(ex.Errors, e => e.StartsWith("vars.LIMITS: schema is not a valid JSON Schema", StringComparison.Ordinal));
    }
}
