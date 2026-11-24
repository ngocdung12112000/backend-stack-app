namespace Identity.Domain;

/// <summary>Roles and their permissions (docs/05-api-design.md section 5). Seeded by the first migration.</summary>
public static class IdentitySeed
{
    public static readonly Guid CustomerRoleId = Guid.Parse("0193f1c2-0000-7000-8000-000000000001");
    public static readonly Guid SupportRoleId = Guid.Parse("0193f1c2-0000-7000-8000-000000000002");
    public static readonly Guid AdminRoleId = Guid.Parse("0193f1c2-0000-7000-8000-000000000003");

    public static readonly IReadOnlyList<(Guid Id, string Name)> Roles =
    [
        (CustomerRoleId, RoleNames.Customer),
        (SupportRoleId, RoleNames.Support),
        (AdminRoleId, RoleNames.Admin),
    ];

    private static readonly string[] CustomerPermissions =
        ["orders:read:own", "orders:create", "reviews:write:own", "profile:*:own", "wishlist:*:own"];

    private static readonly string[] SupportPermissions =
        ["orders:read", "orders:cancel", "orders:refund", "reviews:moderate", "users:read"];

    private static readonly string[] AdminPermissions =
    [
        .. SupportPermissions,
        "users:write", "catalog:write", "inventory:write", "promotions:write", "reports:read",
    ];

    public static IReadOnlyList<(Guid RoleId, string Permission)> Permissions { get; } =
    [
        .. CustomerPermissions.Select(p => (CustomerRoleId, p)),
        .. SupportPermissions.Select(p => (SupportRoleId, p)),
        .. AdminPermissions.Distinct().Select(p => (AdminRoleId, p)),
    ];
}
