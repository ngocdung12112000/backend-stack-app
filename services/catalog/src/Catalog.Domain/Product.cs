namespace Catalog.Domain;

/// <summary>Placeholder aggregate. Real fields come with F03.</summary>
public sealed class Product
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public decimal Price { get; private set; }
    public string Currency { get; private set; } = "USD";

    private Product() { }

    public static Product Create(Guid id, string name, decimal price, string currency = "USD") =>
        new() { Id = id, Name = name, Price = price, Currency = currency };
}
