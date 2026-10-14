using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NutanixMcp.Server.Retrieval;
using Xunit;

namespace NutanixMcp.Server.Tests;

public class IdleUnloaderTest
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task CreatesInstanceLazilyOnFirstUse()
    {
        var factory = new CountingFactory();
        using var unloader = new IdleUnloader<Resource>(factory.Create, s_timeout, new FakeTimeProvider());

        factory.Created.Should().Be(0);
        await unloader.UseAsync(r => Task.FromResult(r.Id));

        factory.Created.Should().Be(1);
        unloader.IsLoaded.Should().BeTrue();
    }

    [Fact]
    public async Task ReusesInstanceWithinTimeout()
    {
        var factory = new CountingFactory();
        var time = new FakeTimeProvider();
        using var unloader = new IdleUnloader<Resource>(factory.Create, s_timeout, time);

        await unloader.UseAsync(r => Task.FromResult(r.Id));
        time.Advance(s_timeout - TimeSpan.FromSeconds(1));
        await unloader.UseAsync(r => Task.FromResult(r.Id));

        factory.Created.Should().Be(1);
    }

    [Fact]
    public async Task DisposesInstanceAfterIdleTimeout()
    {
        var factory = new CountingFactory();
        var time = new FakeTimeProvider();
        using var unloader = new IdleUnloader<Resource>(factory.Create, s_timeout, time);

        await unloader.UseAsync(r => Task.FromResult(r.Id));
        time.Advance(s_timeout);

        unloader.IsLoaded.Should().BeFalse();
        factory.Instances[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task UseAfterRecentUseRestartsIdleWindow()
    {
        var factory = new CountingFactory();
        var time = new FakeTimeProvider();
        using var unloader = new IdleUnloader<Resource>(factory.Create, s_timeout, time);

        await unloader.UseAsync(r => Task.FromResult(r.Id));
        time.Advance(TimeSpan.FromMinutes(6));
        await unloader.UseAsync(r => Task.FromResult(r.Id));
        time.Advance(TimeSpan.FromMinutes(6));

        unloader.IsLoaded.Should().BeTrue();
        time.Advance(TimeSpan.FromMinutes(4));
        unloader.IsLoaded.Should().BeFalse();
    }

    [Fact]
    public async Task RecreatesInstanceAfterUnload()
    {
        var factory = new CountingFactory();
        var time = new FakeTimeProvider();
        using var unloader = new IdleUnloader<Resource>(factory.Create, s_timeout, time);

        await unloader.UseAsync(r => Task.FromResult(r.Id));
        time.Advance(s_timeout);
        int id = await unloader.UseAsync(r => Task.FromResult(r.Id));

        factory.Created.Should().Be(2);
        id.Should().Be(2);
    }

    [Fact]
    public async Task DoesNotDisposeWhileInUse()
    {
        var factory = new CountingFactory();
        var time = new FakeTimeProvider();
        using var unloader = new IdleUnloader<Resource>(factory.Create, s_timeout, time);
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var inFlight = unloader.UseAsync(_ => gate.Task);
        time.Advance(s_timeout * 2);
        factory.Instances[0].Disposed.Should().BeFalse();

        gate.SetResult(0);
        await inFlight;
        time.Advance(s_timeout);
        factory.Instances[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task InfiniteTimeoutNeverUnloads()
    {
        var factory = new CountingFactory();
        var time = new FakeTimeProvider();
        using var unloader = new IdleUnloader<Resource>(factory.Create, Timeout.InfiniteTimeSpan, time);

        await unloader.UseAsync(r => Task.FromResult(r.Id));
        time.Advance(TimeSpan.FromDays(30));

        unloader.IsLoaded.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeReleasesLoadedInstance()
    {
        var factory = new CountingFactory();
        var unloader = new IdleUnloader<Resource>(factory.Create, s_timeout, new FakeTimeProvider());

        await unloader.UseAsync(r => Task.FromResult(r.Id));
        unloader.Dispose();

        factory.Instances[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task FailedActionStillReleasesInstanceForUnload()
    {
        var factory = new CountingFactory();
        var time = new FakeTimeProvider();
        using var unloader = new IdleUnloader<Resource>(factory.Create, s_timeout, time);

        var act = () => unloader.UseAsync<int>(_ => throw new InvalidOperationException("boom"));
        await act.Should().ThrowAsync<InvalidOperationException>();
        time.Advance(s_timeout);

        factory.Instances[0].Disposed.Should().BeTrue();
    }

    private sealed class Resource(int id) : IDisposable
    {
        public int Id { get; } = id;
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class CountingFactory
    {
        public List<Resource> Instances { get; } = [];
        public int Created => Instances.Count;

        public Resource Create()
        {
            var r = new Resource(Instances.Count + 1);
            Instances.Add(r);
            return r;
        }
    }
}
