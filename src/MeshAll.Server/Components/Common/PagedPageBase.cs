using MeshAll.Server.Services;

namespace MeshAll.Server.Components.Common;

public abstract class PagedPageBase<TItem> : PageBase
{
    protected PageResult<TItem> Rows { get; private set; } = PageResult<TItem>.Empty();
    protected string Search { get; private set; } = "";
    protected int PageNumber { get; private set; } = 1;
    protected int PageSize { get; private set; } = PageRequest.MaximumSize;

    protected abstract PageResult<TItem> ReadPage(PageRequest request);

    protected override Task LoadAsync()
    {
        Rows = ReadPage(new PageRequest(PageNumber, PageSize));
        PageNumber = Rows.PageNumber;
        return Task.CompletedTask;
    }

    protected Task ChangePageAsync(int number)
    {
        PageNumber = Math.Max(1, number);
        return LoadAsync();
    }

    protected Task ChangePageSizeAsync(int size)
    {
        PageSize = Math.Clamp(size, 1, PageRequest.MaximumSize);
        return ResetPageAsync();
    }

    protected Task ChangeSearchAsync(string search)
    {
        Search = search;
        return ResetPageAsync();
    }

    protected Task ResetPageAsync()
    {
        PageNumber = 1;
        return LoadAsync();
    }
}
