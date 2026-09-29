using ClearSkies.Engine.Rendering;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>The world mesh pool's page runs: reused when freed, merged with free neighbours, never overlapping.</summary>
public class PageAllocatorTests
{
    [Fact]
    public void FreedRunIsReused()
    {
        var a = new PageAllocator(100);
        int x = a.Alloc(10), y = a.Alloc(5);
        Assert.Equal(0, x);
        Assert.Equal(10, y);
        a.Free(x, 10);
        Assert.Equal(0, a.Alloc(4));  // the freed run, split
        Assert.Equal(4, a.Alloc(6));  // and the rest of it
        Assert.Equal(15, a.Alloc(1)); // then past the top
    }

    [Fact]
    public void NeighboursMergeAndTopShrinks()
    {
        var a = new PageAllocator(30);
        int x = a.Alloc(10), y = a.Alloc(10), z = a.Alloc(10);
        a.Free(x, 10);
        a.Free(y, 10);
        Assert.Equal(0, a.Alloc(20)); // the two merged
        a.Free(0, 20);
        a.Free(z, 10);
        Assert.Equal(0, a.Top);       // everything free again
        Assert.Equal(0, a.Used);
        Assert.Equal(0, a.Alloc(30));
    }

    [Fact]
    public void FullPoolRefusesUntilItGrows()
    {
        var a = new PageAllocator(8);
        a.Alloc(8);
        Assert.Equal(-1, a.Alloc(1));
        a.Capacity = 16;
        Assert.Equal(8, a.Alloc(8));
    }

    [Fact]
    public void RandomRunsNeverOverlap()
    {
        var rng = new Random(7);
        var a = new PageAllocator(4096);
        var live = new List<(int Start, int Pages)>();
        var owner = new int[4096];
        for (int step = 0; step < 5000; step++)
        {
            if (live.Count > 0 && rng.Next(3) == 0)
            {
                int i = rng.Next(live.Count);
                var (start, pages) = live[i];
                for (int p = start; p < start + pages; p++) owner[p] = 0;
                a.Free(start, pages);
                live.RemoveAt(i);
                continue;
            }
            int n = 1 + rng.Next(40);
            int at = a.Alloc(n);
            if (at < 0) continue;
            for (int p = at; p < at + n; p++) { Assert.Equal(0, owner[p]); owner[p] = step + 1; }
            live.Add((at, n));
        }
        Assert.Equal(live.Sum(r => r.Pages), a.Used);
    }
}
