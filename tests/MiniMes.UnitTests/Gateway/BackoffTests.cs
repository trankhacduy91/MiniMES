using MiniMes.EdgeGateway.Drivers;

namespace MiniMes.UnitTests.Gateway;

// Spec M4b step 2: "opcua stop" -> retries after 1, 2, 4... s, never more than 30 s; back to 1 s once connected.
public class BackoffTests
{
    [Fact]
    public void Doubles_up_to_the_cap()
    {
        var backoff = Backoff.Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        var delays = Enumerable.Range(0, 8).Select(_ => backoff.Next().TotalSeconds).ToArray();

        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30], delays);
    }

    [Fact]
    public void Reset_starts_again_from_the_first_delay()
    {
        var backoff = Backoff.Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        backoff.Next();
        backoff.Next();
        backoff.Next();

        backoff.Reset();

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.Next());
    }
}
