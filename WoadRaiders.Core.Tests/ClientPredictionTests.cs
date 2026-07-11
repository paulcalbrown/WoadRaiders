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
        var corrected = prediction.Reconcile(serverPosition, lastProcessedInput: 3);

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
        var corrected = prediction.Reconcile(serverPosition, lastProcessedInput: 5);

        // Nothing in flight → we simply trust the server exactly.
        Assert.Equal(serverPosition, corrected);
        Assert.Equal(0, prediction.PendingInputCount);
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

    private static Vector3 ReferenceSim(IEnumerable<PlayerInput> inputs)
    {
        var world = new GameWorld();
        world.AddPlayer(1, "ref");
        foreach (var input in inputs)
        {
            world.SetInput(1, input);
            world.Step();
        }
        return world.Players[1].Position;
    }
}
