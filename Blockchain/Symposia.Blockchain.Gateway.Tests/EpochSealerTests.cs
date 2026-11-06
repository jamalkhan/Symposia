using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.Blockchain.Gateway.Sealing;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>Sealer decision logic (issue #72) against an in-memory chain.</summary>
public sealed class EpochSealerTests
{
    private sealed class FakeChain : IEpochSealChain
    {
        public ulong Next;
        public ulong FinalThrough; // epochs <= this are past finality
        public bool AnyFinal = true;
        public Dictionary<ulong, List<string>> Reporters = [];
        public Dictionary<ulong, ulong> CountOverride = [];
        public List<(ulong Epoch, IReadOnlyList<string> Nodes)> Seals = [];
        public bool OtherSealerWinsNext;
        public bool FailSeal;

        public Task<ulong> NextSealableEpochAsync(CancellationToken ct) => Task.FromResult(Next);

        public Task<bool> IsEpochFinalAsync(ulong epoch, CancellationToken ct) => Task.FromResult(AnyFinal && epoch <= FinalThrough);

        public Task<ulong> ReporterCountAsync(ulong epoch, CancellationToken ct) =>
            Task.FromResult(CountOverride.TryGetValue(epoch, out var c) ? c : (ulong)Reporters.GetValueOrDefault(epoch, []).Count);

        public Task<IReadOnlyCollection<string>> ReportersAsync(ulong epoch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<string>>(Reporters.GetValueOrDefault(epoch, []));

        public Task SealAsync(ulong epoch, IReadOnlyList<string> nodes, CancellationToken ct)
        {
            if (OtherSealerWinsNext)
            {
                OtherSealerWinsNext = false;
                Next = epoch + 1;
                throw new InvalidOperationException("EpochAlreadySealed");
            }

            if (FailSeal)
            {
                throw new InvalidOperationException("reverted");
            }

            Seals.Add((epoch, nodes));
            Next = epoch + 1;
            return Task.CompletedTask;
        }
    }

    private static EpochSealer Sealer(FakeChain chain, int maxPerPass = 50) =>
        new(chain, Options.Create(new GatewayOptions { EpochSealer = new EpochSealerOptions { MaxEpochsPerPass = maxPerPass } }),
            NullLogger<EpochSealer>.Instance);

    [Fact]
    public async Task SealsEveryFinalEpochInOrderWithItsReporters()
    {
        var chain = new FakeChain { FinalThrough = 2, Reporters = { [0] = ["0xB", "0xa"], [2] = ["0xc"] } };

        Assert.Equal(3, await Sealer(chain).RunOnceAsync(default));

        Assert.Equal([0UL, 1UL, 2UL], chain.Seals.Select(s => s.Epoch));
        Assert.Equal(["0xa", "0xB"], chain.Seals[0].Nodes); // sorted, stable
        Assert.Empty(chain.Seals[1].Nodes); // empty epochs are sealed too, so later ones aren't blocked
        Assert.Equal(3UL, chain.Next);
    }

    [Fact]
    public async Task DoesNothingWhileTheFinalityWindowIsOpen()
    {
        var chain = new FakeChain { AnyFinal = false };
        Assert.Equal(0, await Sealer(chain).RunOnceAsync(default));
        Assert.Empty(chain.Seals);
    }

    [Fact]
    public async Task StopsWhenLogsDisagreeWithTheDistributorCount()
    {
        var chain = new FakeChain { FinalThrough = 3, Reporters = { [1] = ["0xa"] }, CountOverride = { [1] = 2 } };

        Assert.Equal(1, await Sealer(chain).RunOnceAsync(default));

        Assert.Equal([0UL], chain.Seals.Select(s => s.Epoch)); // epoch 1 held back, nothing after it attempted
        Assert.Equal(1UL, chain.Next);
    }

    [Fact]
    public async Task TreatsAnotherSealerWinningAsSuccess()
    {
        var chain = new FakeChain { FinalThrough = 1, OtherSealerWinsNext = true };

        Assert.Equal(1, await Sealer(chain).RunOnceAsync(default));

        Assert.Equal([1UL], chain.Seals.Select(s => s.Epoch));
    }

    [Fact]
    public async Task SurfacesARealSealFailure()
    {
        var chain = new FakeChain { FinalThrough = 0, FailSeal = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sealer(chain).RunOnceAsync(default));
    }

    [Fact]
    public async Task CapsEpochsPerPass()
    {
        var chain = new FakeChain { FinalThrough = 100 };
        Assert.Equal(5, await Sealer(chain, maxPerPass: 5).RunOnceAsync(default));
        Assert.Equal(5UL, chain.Next);
    }
}
