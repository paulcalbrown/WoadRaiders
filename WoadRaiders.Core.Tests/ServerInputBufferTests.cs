using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class ServerInputBufferTests
{
    private static PlayerInput Seq(uint s) => new() { MoveX = 1f, Sequence = s };

    [Fact]
    public void Holds_until_the_cushion_is_primed()
    {
        var buffer = new ServerInputBuffer();

        for (uint s = 1; s < ServerInputBuffer.TargetBuffer; s++)
        {
            buffer.Enqueue(Seq(s));
            Assert.False(buffer.TryDequeue(out _)); // still priming — hold
        }

        buffer.Enqueue(Seq(ServerInputBuffer.TargetBuffer));
        Assert.True(buffer.TryDequeue(out var first)); // cushion reached → flow
        Assert.Equal(1u, first.Sequence);              // oldest first
    }

    [Fact]
    public void Consumes_every_input_once_in_order()
    {
        var buffer = new ServerInputBuffer();
        for (uint s = 1; s <= 5; s++)
            buffer.Enqueue(Seq(s));

        uint expected = 1;
        while (buffer.TryDequeue(out var input))
            Assert.Equal(expected++, input.Sequence);

        Assert.Equal(6u, expected); // 1..5 all delivered — none dropped or duplicated
    }

    [Fact]
    public void Two_inputs_between_ticks_are_both_kept()
    {
        // The old "latest input wins" model dropped the first of these — the bug this fixes.
        var buffer = new ServerInputBuffer();
        buffer.Enqueue(Seq(1));
        buffer.Enqueue(Seq(2));

        Assert.True(buffer.TryDequeue(out var a));
        Assert.True(buffer.TryDequeue(out var b));
        Assert.Equal(1u, a.Sequence);
        Assert.Equal(2u, b.Sequence);
    }

    [Fact]
    public void Ignores_stale_and_duplicate_sequences()
    {
        var buffer = new ServerInputBuffer();
        buffer.Enqueue(Seq(5));
        buffer.Enqueue(Seq(5)); // duplicate
        buffer.Enqueue(Seq(3)); // stale
        buffer.Enqueue(Seq(6));

        Assert.Equal(2, buffer.Count); // only 5 and 6 survived
        buffer.TryDequeue(out var a);
        buffer.TryDequeue(out var b);
        Assert.Equal(5u, a.Sequence);
        Assert.Equal(6u, b.Sequence);
    }

    [Fact]
    public void Fast_forwards_past_the_cap_dropping_oldest()
    {
        var buffer = new ServerInputBuffer();
        var overflow = ServerInputBuffer.MaxBuffer + 3;
        for (uint s = 1; s <= (uint)overflow; s++)
            buffer.Enqueue(Seq(s));

        Assert.Equal(ServerInputBuffer.MaxBuffer, buffer.Count); // capped
        Assert.True(buffer.TryDequeue(out var first));
        // The oldest were dropped; processing resumes near "now".
        Assert.Equal((uint)(overflow - ServerInputBuffer.MaxBuffer + 1), first.Sequence);
    }

    [Fact]
    public void Steady_one_in_one_out_never_starves_after_priming()
    {
        var buffer = new ServerInputBuffer();
        uint seq = 0;
        for (var i = 0; i < ServerInputBuffer.TargetBuffer; i++)
            buffer.Enqueue(Seq(++seq)); // prime the cushion

        // Now one input arrives and one is consumed each tick — the localhost steady
        // state. With matched rates the cushion holds and the buffer never starves.
        for (var tick = 0; tick < 100; tick++)
        {
            buffer.Enqueue(Seq(++seq));
            Assert.True(buffer.TryDequeue(out _), "matched in/out rate must not starve");
        }
    }
}
