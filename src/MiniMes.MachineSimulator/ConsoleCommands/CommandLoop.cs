using System.Globalization;
using Microsoft.Extensions.Hosting;
using MiniMes.Contracts.Machines;
using MiniMes.MachineSimulator.Machines;

namespace MiniMes.MachineSimulator.ConsoleCommands;

// Commands typed during a demo to make dirty data on purpose (spec 2.8 table). The loop only parses and queues:
// each machine applies its commands in its own loop.
public sealed class CommandLoop(MachineFleet fleet) : BackgroundService
{
    private static readonly (string Verb, string Usage)[] Commands =
    [
        ("list", "list                 state and counter of every machine"),
        ("down", "down <m> [sec]       force Down with alarm E999 (until \"up\" without sec)"),
        ("up", "up <m>               back to Running"),
        ("setup", "setup <m>            Setup (changeover)"),
        ("stop", "stop <m>             PlannedStop (break, meeting)"),
        ("ng", "ng <m> <n>           the next n products are NG"),
        ("reset", "reset <m>            counter back to 0"),
        ("mute", "mute <m>             stop sending, heartbeat included"),
        ("unmute", "unmute <m>           resume sending"),
        ("dup", "dup <m>              send the next message twice, same msgId"),
        ("skew", "skew <m> <sec>       shift the machine clock (0 = back to normal)"),
        ("tag", "tag <m> <tag> <value>  publish one process value, e.g. tag L1-REFLOW-01 peakTemp 245.3"),
        ("help", "help"),
    ];

    private static readonly string Help = string.Join(Environment.NewLine, Commands.Select(c => c.Usage));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("Type 'help' for commands, Ctrl+C to stop.");
        while (!stoppingToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                // ReadLine cannot be cancelled: wait for it with the token, so Ctrl+C does not hang on the console.
                line = await Task.Run(Console.ReadLine, stoppingToken).WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            if (line is null) return;                          // no console (stdin closed): machines keep running
            if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine(Execute(line));
        }
    }

    public string Execute(string line)
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = p[0].ToLowerInvariant();
        if (verb == "help") return Help;
        if (verb == "list") return List();
        if (!Commands.Any(c => c.Verb == verb)) return "Unknown command. Type 'help'.";

        if (p.Length < 2) return Usage(verb);
        var machine = fleet.Find(p[1]);
        if (machine is null) return $"Unknown machine {p[1]}. Known: {string.Join(", ", fleet.Machines.Select(m => m.Code))}";

        MachineCommand? command = verb switch
        {
            "down" when p.Length == 2 => new GoDown(null),
            "down" when Positive(p, 2) is { } sec => new GoDown(sec),
            "up" => new ChangeState(MachineState.Running),
            "setup" => new ChangeState(MachineState.Setup),
            "stop" => new ChangeState(MachineState.PlannedStop),
            "ng" when Positive(p, 2) is { } n => new ForceNg(n),
            "reset" => new ResetCounter(),
            "mute" => new Mute(true),
            "unmute" => new Mute(false),
            "dup" => new DuplicateNext(),
            "skew" when p.Length > 2 && int.TryParse(p[2], CultureInfo.InvariantCulture, out var s) => new SetSkew(s),
            "tag" when p.Length > 3 && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => new PublishTag(p[2], v),
            _ => null,
        };
        if (command is null) return Usage(verb);
        machine.Post(command);
        return "ok";
    }

    private string List()
    {
        var rows = fleet.Machines.Select(m => m.Snapshot).Select(s =>
            $"{s.Code,-14} {s.State,-11} counter {s.Counter,8}  good {s.Good,6}  ng {s.Ng,4}  " +
            $"{(s.Connected ? "connected" : "NO BROKER")}{(s.Muted ? " muted" : "")}{(s.SkewSec != 0 ? $" skew {s.SkewSec}s" : "")}" +
            (s.DownUntil is { } until ? $" down until {until.ToLocalTime():HH:mm:ss}" : ""));
        return string.Join(Environment.NewLine, rows);
    }

    private static int? Positive(string[] p, int i)
        => p.Length > i && int.TryParse(p[i], CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    private static string Usage(string verb) => "Usage: " + Commands.Single(c => c.Verb == verb).Usage;
}
