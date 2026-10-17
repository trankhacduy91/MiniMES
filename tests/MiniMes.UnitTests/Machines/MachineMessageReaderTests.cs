using MiniMes.Application.Machines;

namespace MiniMes.UnitTests.Machines;

// Spec M4 step 3: "receive right, drop right". Every bad input is a reason, never an exception.
public class MachineMessageReaderTests
{
    private const string Smt = "minimes/VN-BN01/SMT-ASSY/L1/L1-SMT-01";
    private const string Head = "\"msgId\":\"b3f1c2d4-0000-4000-8000-000000000001\",\"ts\":\"2026-10-17T01:00:00.000Z\"";

    private static string Json(string fields) => "{" + Head + "," + fields + "}";

    [Theory]
    [InlineData("state", "\"machine\":\"L1-SMT-01\",\"state\":\"Down\"")]
    [InlineData("heartbeat", "\"machine\":\"L1-SMT-01\",\"state\":\"Running\"")]
    [InlineData("count", "\"machine\":\"L1-SMT-01\",\"counter\":15290,\"goodDelta\":3,\"ngDelta\":0")]
    [InlineData("alarm", "\"machine\":\"L1-SMT-01\",\"alarmCode\":\"E205\"")]
    [InlineData("alarm", "\"machine\":\"L1-SMT-01\",\"alarmCode\":null")]   // alarm cleared
    public void Valid_messages_are_accepted(string kind, string fields)
    {
        var result = MachineMessageReader.Read($"{Smt}/{kind}", Json(fields));

        Assert.True(result.IsAccepted, result.RejectReason);
        Assert.Equal((kind, "L1-SMT-01"), (result.Input!.Topic.Kind, result.Input.Message!.Machine));
    }

    [Fact]
    public void Process_tag_is_accepted_with_its_tag()
    {
        var result = MachineMessageReader.Read("minimes/VN-BN01/SMT-ASSY/L1/L1-REFLOW-01/process/peakTemp",
            Json("\"machine\":\"L1-REFLOW-01\",\"tag\":\"peakTemp\",\"value\":245.3,\"quality\":\"Good\",\"source\":\"opcua\""));

        Assert.True(result.IsAccepted, result.RejectReason);
        Assert.Equal(("peakTemp", 245.3), (result.Input!.Topic.Tag, result.Input.Message!.Value));
    }

    [Theory]
    [InlineData("online")]
    [InlineData("offline")]
    public void Status_is_raw_text_not_json(string payload)
    {
        var result = MachineMessageReader.Read($"{Smt}/status", payload);

        Assert.Equal(payload, result.Input!.Status);
        Assert.Null(result.Input.Message);
    }

    [Theory]
    [InlineData("minimes/VN-BN01/L1/state", "{}", "topic")]                    // 4 levels
    [InlineData($"{Smt}/temperature", "{}", "unknown kind")]
    [InlineData($"{Smt}/status", "ONLINE", "status must be")]
    [InlineData($"{Smt}/state", "{ not json", "invalid JSON")]
    [InlineData($"{Smt}/state", "null", "empty payload")]
    public void Bad_topic_or_payload_is_rejected(string topic, string payload, string reason)
        => AssertRejected(topic, payload, reason);

    [Theory]
    [InlineData("state", "\"machine\":\"L1-SMT-01\",\"state\":\"RUN\"", "invalid JSON")]            // not in the enum
    [InlineData("state", "\"machine\":\"L1-SMT-02\",\"state\":\"Down\"", "does not match the topic")]
    [InlineData("state", "\"machine\":\"l1-smt-01\",\"state\":\"Down\"", "does not match the topic")]
    [InlineData("state", "\"machine\":\"L1-SMT-01\"", "state is missing")]
    [InlineData("state", "\"machine\":\"L1-SMT-01\",\"state\":\"Offline\"", "cannot report Offline")]
    [InlineData("heartbeat", "\"machine\":\"L1-SMT-01\",\"state\":\"Offline\"", "cannot report Offline")]
    [InlineData("count", "\"machine\":\"L1-SMT-01\",\"goodDelta\":1", "counter is missing")]
    [InlineData("count", "\"machine\":\"L1-SMT-01\",\"counter\":-1", "negative")]
    [InlineData("process/peakTemp", "\"machine\":\"L1-SMT-01\",\"tag\":\"zone1\",\"value\":1", "tag 'zone1'")]
    [InlineData("process/peakTemp", "\"machine\":\"L1-SMT-01\",\"tag\":\"peakTemp\"", "value is missing")]
    public void Message_breaking_the_contract_is_rejected(string kind, string fields, string reason)
        => AssertRejected($"{Smt}/{kind}", Json(fields), reason);

    [Fact]
    public void Message_without_msg_id_is_rejected()
        => AssertRejected($"{Smt}/state", "{\"machine\":\"L1-SMT-01\",\"ts\":\"2026-10-17T01:00:00.000Z\",\"state\":\"Down\"}", "msgId");

    private static void AssertRejected(string topic, string payload, string reason)
    {
        var result = MachineMessageReader.Read(topic, payload);

        Assert.False(result.IsAccepted);
        Assert.Contains(reason, result.RejectReason);
    }
}
