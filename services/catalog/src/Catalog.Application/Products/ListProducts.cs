using BuildingBlocks.Common;
using Catalog.Domain;
using MediatR;

namespace Catalog.Application.Products;

public interface IProductRepository
{
    Task<IReadOnlyList<Product>> ListAsync(int page, int pageSize, CancellationToken ct);
    Task<long> CountAsync(CancellationToken ct);
}

public sealed record ProductDto(Guid Id, string Name, decimal Price, string Currency);

public sealed record ListProductsQuery(int Page = 1, int PageSize = 20) : IRequest<PagedResult<ProductDto>>;

public sealed class ListProductsHandler(IProductRepository repository)
    : IRequestHandler<ListProductsQuery, PagedResult<ProductDto>>
{
    public async Task<PagedResult<ProductDto>> Handle(ListProductsQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 100);
        var items = await repository.ListAsync(page, size, ct);
        var total = await repository.CountAsync(ct);
        return new PagedResult<ProductDto>(
            items.Select(p => new ProductDto(p.Id, p.Name, p.Price, p.Currency)).ToList(), page, size, total);
    }
}
