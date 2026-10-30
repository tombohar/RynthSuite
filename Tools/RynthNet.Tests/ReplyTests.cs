using RynthCore.Plugin.RynthNet;
using RynthCore.Plugin.Shared;

namespace RynthNet.Tests;

// Every /rn command answers in chat (2026-10-28). They all did; what was missing is that a several-line
// answer (/rn help, /rn list, /rn status) lost every line but the first to the host's 100 ms limit, and
// that a refusal looked like a success. HandleLocal needs the network node and the host, so the
// argument parsing behind each verb's right and wrong forms, and the shared queue, are checked here.
internal static partial class Program
{
    private static void Replies()
    {
        // bc / bca: "[delay] command".
        CommandParser.DelayAndCommand("500 /say hi", out int delay, out string command);
        Check(delay == 500 && command == "/say hi", "/rn bc 500 /say hi: the delay, then the command");
        CommandParser.DelayAndCommand("/say hi", out delay, out command);
        Check(delay == 0 && command == "/say hi", "/rn bc /say hi: no delay");
        CommandParser.DelayAndCommand("", out _, out command);
        Check(command.Length == 0, "/rn bc alone: no command (it answers 'no command given.')");

        // bct: tags first; none is the usage.
        Check(CommandParser.TagsAndRest("heal,box 250 /ra start", out var tags, out string after)
              && tags.Count == 2 && after == "250 /ra start", "/rn bct heal,box 250 /ra start");
        Check(!CommandParser.TagsAndRest("", out _, out _), "/rn bct alone: the usage");

        // tell: a name and a command; a name alone is the usage.
        var known = new[] { "Zed Healer", "Alpha" };
        Check(CommandParser.NameAndCommand("Zed Healer /ra start", known, out string name, out command)
              && name == "Zed Healer" && command == "/ra start", "/rn tell Zed Healer /ra start: the longest known name");
        Check(CommandParser.NameAndCommand("Somebody, /say hi", known, out name, out command) && name == "Somebody",
              "/rn tell Somebody, /say hi: the comma splits");
        Check(!CommandParser.NameAndCommand("Alpha", known, out _, out _), "/rn tell Alpha alone: the usage");
        Check(!CommandParser.NameAndCommand("", known, out _, out _), "/rn tell alone: the usage");

        // A several-line answer: every line goes out, one per tick, even when the host refuses some.
        var written = new List<string>();
        int call = 0;
        var reply = new CommandReply("[RynthNet]", (t, c) => { if (++call % 2 == 0) return false; written.Add(t); return true; });
        for (int i = 0; i < 11; i++) reply.Ok("help line " + i);
        for (int i = 0; i < 40 && reply.Pending > 0; i++) reply.Flush();
        Check(written.Count == 11 && written[10] == "[RynthNet] help line 10", "/rn help: all 11 lines written, in order");
    }
}
