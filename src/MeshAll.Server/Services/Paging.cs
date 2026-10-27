namespace MeshAll.Server.Services;

/// <summary>Bounds every list request before it reaches the database.</summary>
public readonly record struct PageRequest
{
    public const int MaximumSize = 50;

    public int Number
    {
        get;
    }
    public int Size
    {
        get;
    }
    public long Offset => ((long)Number - 1) * Size;

    public PageRequest() : this(1, MaximumSize)
    {
    }

    public PageRequest(int number = 1, int size = MaximumSize)
    {
        Number = Math.Max(1, number);
        Size = Math.Clamp(size, 1, MaximumSize);
    }

    public PageRequest ClampTo(long totalCount)
    {
        PageRequest normalized = new(Number, Size == 0 ? MaximumSize : Size);
        long lastPage = totalCount <= 0 ? 1 : 1 + (totalCount - 1) / normalized.Size;
        return new PageRequest((int)Math.Min(normalized.Number, lastPage), normalized.Size);
    }
}

public sealed record PageResult<T>(
    IReadOnlyList<T> Items,
    long TotalCount,
    int PageNumber,
    int PageSize)
{
    public long TotalPages => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public long FirstItem => TotalCount == 0 ? 0 : ((long)PageNumber - 1) * PageSize + 1;
    public long LastItem => TotalCount == 0 ? 0 : Math.Min(TotalCount, FirstItem + Items.Count - 1);

    public static PageResult<T> Empty(int pageSize = PageRequest.MaximumSize) =>
        new([], 0, 1, Math.Clamp(pageSize, 1, PageRequest.MaximumSize));

    public static PageResult<T> FromSnapshot(IReadOnlyList<T> snapshot, PageRequest request)
    {
        PageRequest boundedRequest = request.ClampTo(snapshot.Count);
        T[] items = snapshot.Skip((int)Math.Min(int.MaxValue, boundedRequest.Offset))
            .Take(boundedRequest.Size)
            .ToArray();

        return new PageResult<T>(items, snapshot.Count, boundedRequest.Number, boundedRequest.Size);
    }
}
