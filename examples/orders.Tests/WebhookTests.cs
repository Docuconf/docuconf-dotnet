using System.Security.Cryptography;
using System.Text;
using Docuconf;

namespace Orders.Api.Tests;

public sealed class WebhookTests
{
    private static readonly string OldKey = new('o', 32);
    private static readonly string NewKey = new('n', 32);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"order":"42","status":"paid"}""");

    private static string Sign(string key) => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Body));

    private static Dictionary<string, string> Environment(string keys) =>
        new() { ["ORDERS__DATABASEURL"] = "postgres://u:p@db/orders", ["WEBHOOK_KEYS"] = keys };

    /// <summary>Loads WEBHOOK_KEYS as the service does at startup.</summary>
    private static List<string>? Keys(string value) => DocuconfTesting.Load<OrdersOptions>(Environment(value)).WebhookKeys;

    // A key rotation: each step is a rollout with a new WEBHOOK_KEYS, and a webhook signed with the key in use always
    // verifies.
    [Theory]
    [InlineData("before", "o", true, false)]
    [InlineData("overlap", "o,n", true, true)]
    [InlineData("after", "n", false, true)]
    public void A_rotation_never_turns_away_a_webhook(string step, string keys, bool acceptsOld, bool acceptsNew)
    {
        var set = Keys(keys.Replace("o", OldKey, StringComparison.Ordinal).Replace("n", NewKey, StringComparison.Ordinal));

        Assert.True(Webhook.Verify(set, Body, Sign(OldKey)) == acceptsOld, $"{step}: old key");
        Assert.True(Webhook.Verify(set, Body, Sign(NewKey)) == acceptsNew, $"{step}: new key");
        Assert.False(Webhook.Verify(set, Body, Sign(new string('x', 32))), $"{step}: another key");
    }

    [Fact]
    public void A_bad_or_missing_signature_is_rejected()
    {
        Assert.False(Webhook.Verify(Keys(OldKey), Body, "not hex"));
        Assert.False(Webhook.Verify(Keys(OldKey), Body, null));
        Assert.False(Webhook.Verify(null, Body, Sign(OldKey))); // no keys configured
    }

    // The key set's constraints catch an empty or truncated key, and a third key, at startup, without printing any key.
    [Theory]
    [InlineData(",", "out_of_range")]                      // an empty second key
    [InlineData(",nnnnnnnnnn", "out_of_range")]           // a truncated key
    [InlineData(",new,xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "too_many_items")]
    public void A_bad_key_set_fails_at_startup(string suffix, string code)
    {
        var value = OldKey + suffix.Replace("new", NewKey, StringComparison.Ordinal);

        var problem = Assert.Single(DocuconfTesting.Validate<OrdersOptions>(Environment(value)));

        Assert.Equal(("WEBHOOK_KEYS", code), (problem.Input, problem.Code));
        Assert.DoesNotContain(OldKey, problem.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("nnnnnnnnnn", problem.ToString(), StringComparison.Ordinal);
    }
}
