namespace Torvello.Application.Sync;

// Explicit public whitelist: never serialize a raw sync payload to the website.
public sealed record MenuSnapshot(DateTimeOffset PublishedAt, IReadOnlyList<MenuCategoryDto> Categories);
public sealed record PublicMenuDto(string CafeName, DateTimeOffset UpdatedAt, IReadOnlyList<MenuCategoryDto> Categories);
public sealed record MenuCategoryDto(string Id, string Name, int SortOrder, IReadOnlyList<MenuItemDto> Items);
public sealed record MenuItemDto(string Id, string Name, string? Description, IReadOnlyList<MenuOptionDto> Sizes, IReadOnlyList<MenuOptionDto> AddOns);
public sealed record MenuOptionDto(string Id, string Name, long PricePaisa);
public interface IPublicMenuService
{
    Task<PublicMenuDto?> GetAsync(CancellationToken ct);
}
