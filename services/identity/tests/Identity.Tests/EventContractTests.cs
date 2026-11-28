using System.Text.Json;
using Identity.Application;

namespace Identity.Tests;

/// <summary>Producer-side contract check: every event's data carries the properties its JSON Schema requires.</summary>
public class EventContractTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);

    public static TheoryData<IdentityEvent> Events => new()
    {
        new UserRegisteredV1(Id, "a@b.co", "Ann"),
        new PasswordResetRequestedV1(Id, "a@b.co", "token", Now),
        new UserLockedV1(Id, Guid.NewGuid(), Now),
        new UserDeletedV1(Id, Now),
    };

    [Theory]
    [MemberData(nameof(Events))]
    public void F01_Events_MatchTheirJsonSchemaRequiredFields(IdentityEvent e)
    {
        var schemaPath = Path.Combine(RepoRoot(), "contracts", "events", $"{e.Type}.schema.json");
        File.Exists(schemaPath).Should().BeTrue($"{schemaPath} must exist");

        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var required = schema.RootElement.GetProperty("properties").GetProperty("data").GetProperty("required")
            .EnumerateArray().Select(x => x.GetString()!).ToList();
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(e.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        data.RootElement.EnumerateObject().Select(p => p.Name).Should().Contain(required);
        e.Topic.Should().Be("identity.user-events");
        e.AggregateId.Should().Be(Id.ToString());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StackShop.Backend.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
