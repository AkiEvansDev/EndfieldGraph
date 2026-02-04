using System;
using System.Collections.Generic;
using System.Text;

namespace EndfieldGraph.ViewModels.Common;

public sealed class ReentrancyGuard
{
    private int depth;

    public bool IsSuppressed => depth > 0;

    public IDisposable Suppress()
    {
        depth++;
        return new Scope(this);
    }

    private sealed class Scope(ReentrancyGuard g) : IDisposable
    {
        public void Dispose() => g.depth = Math.Max(0, g.depth - 1);
    }
}
