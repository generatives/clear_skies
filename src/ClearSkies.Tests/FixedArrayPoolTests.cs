using ClearSkies.Engine.Core;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Arrays of one length, reused after they're returned, up to a cap.</summary>
public class FixedArrayPoolTests
{
    [Fact]
    public void ReturnedArrayIsRentedAgain()
    {
        var pool = new FixedArrayPool<int>(16, maxKept: 4);
        var a = pool.Rent();
        Assert.Equal(16, a.Length);
        pool.Return(a);
        Assert.Same(a, pool.Rent());
        Assert.NotSame(a, pool.Rent()); // empty again: a new one
    }

    [Fact]
    public void KeepsNoMoreThanItsCap()
    {
        var pool = new FixedArrayPool<byte>(8, maxKept: 2);
        for (int i = 0; i < 5; i++) pool.Return(new byte[8]);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void RefusesArraysOfAnotherLength()
    {
        var pool = new FixedArrayPool<byte>(8, maxKept: 2);
        Assert.Throws<ArgumentException>(() => pool.Return(new byte[9]));
        pool.Return(null); // ignored
        Assert.Equal(0, pool.Count);
    }
}
