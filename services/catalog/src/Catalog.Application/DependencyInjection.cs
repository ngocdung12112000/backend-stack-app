using Catalog.Application.Products;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddCatalogApplication(this IServiceCollection services)
    {
        services.AddMediatR(c =>
        {
            c.RegisterServicesFromAssemblyContaining<ListProductsQuery>();
            c.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        services.AddValidatorsFromAssemblyContaining<ListProductsQuery>(includeInternalTypes: true);
        services.AddScoped<ProductClassifier>();
        services.AddScoped<ProductWriter>();
        services.AddScoped<SlugService>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}

/// <summary>Runs FluentValidation validators for the request shape; business rules live in the domain.</summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var context = new ValidationContext<TRequest>(request);
        var failures = (await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, ct))))
            .SelectMany(r => r.Errors).Where(f => f is not null).ToList();
        if (failures.Count > 0) throw new ValidationException(failures);
        return await next();
    }
}

public sealed class ProductDetailsRequestValidator : AbstractValidator<CreateProductCommand>
{
    public ProductDetailsRequestValidator()
    {
        RuleFor(c => c.Details).NotNull();
        RuleFor(c => c.Details.Name).NotEmpty().MaximumLength(200).When(c => c.Details is not null);
    }
}

public sealed class UpdateProductValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductValidator()
    {
        RuleFor(c => c.Details).NotNull();
        RuleFor(c => c.Details.Name).NotEmpty().MaximumLength(200).When(c => c.Details is not null);
    }
}

public sealed class SetOptionsValidator : AbstractValidator<SetOptionsCommand>
{
    public SetOptionsValidator() => RuleFor(c => c.Body.Options).NotNull();
}

public sealed class UpsertVariantValidator : AbstractValidator<UpsertVariantCommand>
{
    public UpsertVariantValidator()
    {
        RuleFor(c => c.Sku).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Body).NotNull();
    }
}

public sealed class SetImagesValidator : AbstractValidator<SetImagesCommand>
{
    public SetImagesValidator() => RuleFor(c => c.Body.Images).NotNull();
}

public sealed class ListProductsValidator : AbstractValidator<ListProductsQuery>
{
    public ListProductsValidator() =>
        RuleFor(q => q.Ids).Must(ids => ids is null || ids.Count <= ListProductsHandler.MaxBatchIds)
            .WithMessage($"At most {ListProductsHandler.MaxBatchIds} ids per request.");
}

public sealed class CreateCategoryValidator : AbstractValidator<Categories.CreateCategoryCommand>
{
    public CreateCategoryValidator() => RuleFor(c => c.Body.Name).NotEmpty().MaximumLength(100);
}

public sealed class UpdateCategoryValidator : AbstractValidator<Categories.UpdateCategoryCommand>
{
    public UpdateCategoryValidator() => RuleFor(c => c.Body.Name).NotEmpty().MaximumLength(100);
}

public sealed class CreateBrandValidator : AbstractValidator<Brands.CreateBrandCommand>
{
    public CreateBrandValidator() => RuleFor(c => c.Body.Name).NotEmpty().MaximumLength(100);
}

public sealed class UpdateBrandValidator : AbstractValidator<Brands.UpdateBrandCommand>
{
    public UpdateBrandValidator() => RuleFor(c => c.Body.Name).NotEmpty().MaximumLength(100);
}
