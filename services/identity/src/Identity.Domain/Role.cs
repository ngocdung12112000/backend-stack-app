namespace Identity.Domain;

public static class RoleNames
{
    public const string Customer = "Customer";
    public const string Support = "Support";
    public const string Admin = "Admin";
}

public sealed class Role
{
    private readonly List<RolePermission> _permissions = [];

    private Role() { }

    public Role(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public IReadOnlyCollection<RolePermission> Permissions => _permissions;
}

public sealed class RolePermission(Guid roleId, string permission)
{
    public Guid RoleId { get; private set; } = roleId;
    public string Permission { get; private set; } = permission;
}
