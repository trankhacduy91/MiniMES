using MiniMes.EdgeGateway.Drivers;

namespace MiniMes.UnitTests.Gateway;

// Spec M4b step 2: "opcua stop" -> retries after 1, 2, 4... s, never more than 30 s; back to 1 s once connected.
// Step 6: with jitter, each delay moves by up to +-20 %, still never above the cap.
public class BackoffTests
{
    [Fact]
    public void Doubles_up_to_the_cap()
    {
        var backoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        var delays = Enumerable.Range(0, 8).Select(_ => backoff.Next().TotalSeconds).ToArray();

        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30], delays);
    }

    [Fact]
    public void Reset_starts_again_from_the_first_delay()
    {
        var backoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        backoff.Next();
        backoff.Next();
        backoff.Next();

        backoff.Reset();

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.Next());
    }

    [Fact]
    public void Jitter_stays_within_20_percent_and_under_the_cap()
    {
        var backoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), jitter: 0.2, new Random(7));
        double[] nominal = [1, 2, 4, 8, 16, 30, 30, 30];

        foreach (var n in nominal)
        {
            var d = backoff.Next().TotalSeconds;
            Assert.InRange(d, n * 0.8, Math.Min(n * 1.2, 30));
        }
    }

    // The point of jitter: gateways that failed together do not retry together.
    [Fact]
    public void Two_gateways_spread_their_retries()
    {
        var a = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), jitter: 0.2, new Random(1));
        var b = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), jitter: 0.2, new Random(2));

        var differ = Enumerable.Range(0, 5).Count(_ => a.Next() != b.Next());

        Assert.True(differ >= 4);
    }
}
