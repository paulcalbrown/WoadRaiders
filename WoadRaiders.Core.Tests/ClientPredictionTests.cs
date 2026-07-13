using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class ClientPredictionTests
{
    [Fact]
    public void Predict_matches_a_plain_server_simulation()
    {
        var inputs = RightInputs(10);

        var prediction = new ClientPrediction(1, Vector3.Zero);
        foreach (var input in inputs)
            prediction.Predict(input);

        // Predicting locally must land exactly where an authoritative sim would.
        Assert.Equal(ReferenceSim(inputs), prediction.Position);
    }

    [Fact]
    public void Reconcile_with_matching_server_state_keeps_prediction()
    {
        var inputs = RightInputs(6);
        var prediction = new ClientPrediction(1, Vector3.Zero);
        foreach (var input in inputs)
            prediction.Predict(input);

        // Server has processed the first 3 inputs.
        var serverPosition = ReferenceSim(inputs.Take(3));
        var corrected = prediction.Reconcile(serverPosition, 0f, 0f, lastProcessedInput: 3);

        // Replaying inputs 4..6 on top of the server position reproduces the full sim.
        Assert.Equal(ReferenceSim(inputs), corrected);
        Assert.Equal(3, prediction.PendingInputCount); // 4, 5, 6 still in flight
    }

    [Fact]
    public void Reconcile_snaps_to_server_when_all_inputs_acked()
    {
        var prediction = new ClientPrediction(1, Vector3.Zero);
        foreach (var input in RightInputs(5))
            prediction.Predict(input);

        var serverPosition = new Vector3(123f, 0f, -45f);
        var corrected = prediction.Reconcile(serverPosition, 0f, 0f, lastProcessedInput: 5);

        // Nothing in flight → we simply trust the server exactly.
        Assert.Equal(serverPosition, corrected);
        Assert.Equal(0, prediction.PendingInputCount);
    }

    [Fact]
    public void Reconcile_reproduces_the_attack_root_without_drift()
    {
        // Move three ticks, then swing: the swing roots the player. A reconcile that
        // lands before the swing must replay the moving ticks as moving — not wrongly
        // rooted by a stale attack window carried over from the later swing — and land
        // exactly where a clean full simulation does. (Restoring the authoritative
        // attack timers is what makes that hold; without it the position drifts short,
        // which is the "glide" the player saw when attacking mid-move.)
        var inputs = new List<PlayerInput>
        {
            new() { MoveX = 1f, Sequence = 1 },
            new() { MoveX = 1f, Sequence = 2 },
            new() { MoveX = 1f, Sequence = 3 },
            new() { Attack = true, Sequence = 4 },
            new() { Sequence = 5 },
            new() { Sequence = 6 },
        };

        var prediction = new ClientPrediction(1, Vector3.Zero);
        foreach (var input in inputs)
            prediction.Predict(input);

        // Server has processed only the first two (moving) inputs — before the swing.
        var atTwo = SimulateState(inputs.Take(2));
        prediction.Reconcile(atTwo.Pos, atTwo.Anim, atTwo.Cooldown, lastProcessedInput: 2);

        Assert.Equal(SimulateState(inputs).Pos, prediction.Position);
    }

    [Fact]
    public void Step_records_last_processed_input_sequence()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A");

        world.SetInput(1, new PlayerInput { MoveX = 1f, Sequence = 7 });
        world.Step();

        Assert.Equal(7u, player.LastProcessedInput);
    }

    private static List<PlayerInput> RightInputs(int count)
    {
        var list = new List<PlayerInput>();
        for (uint seq = 1; seq <= count; seq++)
            list.Add(new PlayerInput { MoveX = 1f, MoveZ = 0f, Sequence = seq });
        return list;
    }

    private static Vector3 ReferenceSim(IEnumerable<PlayerInput> inputs) => SimulateState(inputs).Pos;

    // A clean authoritative simulation of the input stream: the final position plus the
    // attack timers, which a reconcile needs to reproduce the swing-root exactly.
    private static (Vector3 Pos, float Anim, float Cooldown) SimulateState(IEnumerable<PlayerInput> inputs)
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "ref");
        foreach (var input in inputs)
        {
            world.SetInput(1, input);
            world.Step();
        }
        return (player.Position, player.AttackAnimRemaining, player.AttackCooldown);
    }
}
