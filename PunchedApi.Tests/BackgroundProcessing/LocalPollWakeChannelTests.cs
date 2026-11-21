using PunchedApi.Application.BackgroundProcessing;

namespace PunchedApi.Tests.BackgroundProcessing;

public class LocalPollWakeChannelTests
{
    [Fact]
    public async Task A_signal_arriving_before_the_wait_is_not_lost()
    {
        var channel = new LocalPollWakeChannel();

        channel.Notify("outbox-commit");

        Assert.True(await channel.WaitForWakeAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task A_signal_arriving_during_the_wait_wakes_the_waiter()
    {
        var channel = new LocalPollWakeChannel();

        var wait = channel.WaitForWakeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        channel.Notify("outbox-commit");

        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task An_idle_wait_times_out_without_a_signal()
    {
        var channel = new LocalPollWakeChannel();

        Assert.False(await channel.WaitForWakeAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_surfaces_as_an_operation_cancelled()
    {
        var channel = new LocalPollWakeChannel();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            channel.WaitForWakeAsync(TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task Many_signals_coalesce_into_one_wake()
    {
        var channel = new LocalPollWakeChannel();

        for (var i = 0; i < 50; i++) channel.Notify($"signal-{i}");

        Assert.True(await channel.WaitForWakeAsync(TimeSpan.FromSeconds(1), CancellationToken.None));

        // The burst was coalesced: the next wait must not return immediately.
        Assert.False(await channel.WaitForWakeAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None));
    }
}
