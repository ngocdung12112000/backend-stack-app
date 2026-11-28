using BuildingBlocks.Common;

namespace Identity.Domain;

/// <summary>The editable fields of an address (F02 BR-04), already validated and normalised by the Application layer.</summary>
public sealed record AddressDetails(
    string RecipientName, string Phone, string Line1, string? Line2, string? Ward, string? District,
    string City, string CountryCode, string? PostalCode);

/// <summary>A shipping address owned by one user. Orders copy it as a snapshot (BR-05), so editing or deleting it never alters past orders.</summary>
public sealed class Address
{
    private Address() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string RecipientName { get; private set; } = string.Empty;
    public string Phone { get; private set; } = string.Empty;
    public string Line1 { get; private set; } = string.Empty;
    public string? Line2 { get; private set; }
    public string? Ward { get; private set; }
    public string? District { get; private set; }
    public string City { get; private set; } = string.Empty;
    public string CountryCode { get; private set; } = string.Empty;
    public string? PostalCode { get; private set; }
    public bool IsDefault { get; internal set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; internal set; }

    internal static Address Create(Guid userId, AddressDetails d, bool isDefault, DateTime now)
    {
        var a = new Address { Id = Ids.NewGuidV7(), UserId = userId, IsDefault = isDefault, CreatedAt = now };
        a.Apply(d, now);
        return a;
    }

    internal void Apply(AddressDetails d, DateTime now)
    {
        RecipientName = d.RecipientName;
        Phone = d.Phone;
        Line1 = d.Line1;
        Line2 = d.Line2;
        Ward = d.Ward;
        District = d.District;
        City = d.City;
        CountryCode = d.CountryCode;
        PostalCode = d.PostalCode;
        UpdatedAt = now;
    }
}

/// <summary>
/// Aggregate over all addresses of one user. It owns the F02 invariants: at most <see cref="MaxAddresses"/> (BR-01) and exactly one
/// default when the book is not empty (BR-02). MySQL has no partial unique index, so the rule lives here and the infrastructure
/// serialises writers by locking the user row for the duration of the transaction.
/// </summary>
public sealed class AddressBook(Guid userId, IEnumerable<Address> addresses)
{
    public const int MaxAddresses = 10;

    private readonly List<Address> _addresses = addresses.ToList();

    public IReadOnlyList<Address> Addresses => _addresses;

    public Address? Find(Guid id) => _addresses.FirstOrDefault(a => a.Id == id);

    public Address? Default => _addresses.FirstOrDefault(a => a.IsDefault);

    /// <summary>Adds an address; the first one becomes the default. Returns null when the limit is reached.</summary>
    public Address? Add(AddressDetails details, DateTime now)
    {
        if (_addresses.Count >= MaxAddresses) return null;
        var address = Address.Create(userId, details, isDefault: _addresses.Count == 0, now);
        _addresses.Add(address);
        return address;
    }

    /// <summary>Replaces the editable fields. Returns false when the address is not in this book. The default flag is untouched.</summary>
    public bool Replace(Guid id, AddressDetails details, DateTime now)
    {
        var address = Find(id);
        if (address is null) return false;
        address.Apply(details, now);
        return true;
    }

    /// <summary>Makes <paramref name="id"/> the only default (AC-2). Returns false when it is not in this book.</summary>
    public bool SetDefault(Guid id, DateTime now)
    {
        var target = Find(id);
        if (target is null) return false;
        if (target.IsDefault) return true;

        foreach (var other in _addresses.Where(a => a.IsDefault))
        {
            other.IsDefault = false;
            other.UpdatedAt = now;
        }

        target.IsDefault = true;
        target.UpdatedAt = now;
        return true;
    }

    /// <summary>Removes an address. Deleting the default promotes the most recently updated remaining one (BR-02).</summary>
    public Address? Remove(Guid id, DateTime now)
    {
        var address = Find(id);
        if (address is null) return null;

        _addresses.Remove(address);
        if (address.IsDefault && _addresses.Count > 0)
        {
            var next = _addresses.OrderByDescending(a => a.UpdatedAt).ThenByDescending(a => a.Id).First();
            next.IsDefault = true;
            next.UpdatedAt = now;
        }

        return address;
    }
}
