// ConsoleCommands.cs
// In-game console commands for GRDNConnect, registered the same way DLE and
// Persistent Jobs do it. Compiled only when lib/CommandTerminal.dll is present;
// without it this file is empty and the mod builds and runs exactly as before.
//
//   connect.lag         one-shot report: frame percentiles, hitches, GC, heap,
//                       live cars, listener spin, and per-route HTTP handler cost
//   connect.lag watch   toggles a line every 10 seconds so the numbers can be
//                       correlated with what is actually happening in the world
//
// Deliberately NOT host-guarded. The meter is read-only, and a client-side
// reading is the case that matters most here: the mod's client paths are the
// ones that have historically broken without the host ever noticing.

#if COMMAND_TERMINAL

using CommandTerminal;
using UnityEngine;

internal static class ConsoleCommands
{
	[RegisterCommand("connect.lag",
		Help = "GRDNConnect: lag meter dump (frame percentiles, hitches, GC, heap, live cars, listener spin, HTTP handler cost). 'connect.lag watch' toggles a 10 second periodic log line.",
		MinArgCount = 0, MaxArgCount = 1)]
	public static void Lag(CommandArg[] args)
	{
		if (args.Length > 0 &&
			string.Equals(args[0].String, "watch", System.StringComparison.OrdinalIgnoreCase))
		{
			PerfMeter.Watch = !PerfMeter.Watch;
			string state = PerfMeter.Watch
				? "ON, one line every 10s in the log"
				: "off";
			Debug.Log($"connect.lag: watch {state}.");
			Main.ModEntry.Logger.Log($"[Lag] watch {state}.");
			return;
		}

		string report = PerfMeter.FullReport();
		Debug.Log(report);
		Main.ModEntry.Logger.Log(report);
	}
}

#endif
