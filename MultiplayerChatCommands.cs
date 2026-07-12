// MultiplayerChatCommands.cs
// In-game chat commands (/complete, /activate) via the dvmp MultiplayerAPI.
// Compiled only when lib/MultiplayerAPI.dll is present (DVMP_API defined); without
// it this is an empty stub and the mod builds/runs fine without dvmp.
//
// Runs HOST-SIDE: dvmp invokes the command callback on the server with the sender
// as an IPlayer. We resolve the job from the sender's OccupiedCar consist (or the
// train they were last in), complete/activate it via JobCompletionHelper (the same
// code path as the Discord commands), and whisper the result back to the sender.
#if DVMP_API

using System;
using System.Collections.Generic;
using MPAPI;
using MPAPI.Interfaces;

internal static class MultiplayerChatCommands
{
	private static GRDNConnectBehaviour _mod;

	// Last train each player was seen occupying (username -> train number), so a
	// player who has stepped out of the cab to read cars can still run the command.
	private static readonly Dictionary<string, string> _lastTrain =
		new Dictionary<string, string>();

	internal static void TryInit(GRDNConnectBehaviour mod)
	{
		_mod = mod;
		MultiplayerAPI.ServerStarted += RegisterCommands;
		// If a server is already running when we init, register immediately.
		if (MultiplayerAPI.Server != null) RegisterCommands(MultiplayerAPI.Server);
		Main.ModEntry.Logger.Log("[GRDNConnect] Multiplayer chat commands armed.");
	}

	private static void RegisterCommands(IServer server)
	{
		server.RegisterChatCommand("complete", "c",
			() => "Complete the job on your loco's consist.\r\n\t\t/complete [jobId]",
			(args, sender) => Handle(server, sender, args, activate: false));

		server.RegisterChatCommand("activate", "a",
			() => "Start the job on your loco's consist.\r\n\t\t/activate [jobId]",
			(args, sender) => Handle(server, sender, args, activate: true));

		Main.ModEntry.Logger.Log("[GRDNConnect] Registered chat commands /complete (/c) and /activate (/a).");
	}

	// NOTE: assumes dvmp dispatches chat commands on the Unity main thread (it polls
	// the network on the main thread). The Job APIs require that. If an in-game test
	// shows a "can only be called from the main thread" error, marshal Handle onto the
	// mod's coroutine host instead.
	private static void Handle(IServer server, IPlayer sender, string args, bool activate)
	{
		try
		{
			if (_mod == null || sender == null) return;

			string jobId = string.IsNullOrWhiteSpace(args) ? null : args.Trim();
			string user  = sender.Username ?? "";

			// Track / recall the train the player is (or was last) in.
			var car = sender.OccupiedCar;
			string train = null;
			if (car != null)
			{
				train = _mod.TrainNumberOfConsist(car);
				if (!string.IsNullOrEmpty(train) && user.Length > 0) _lastTrain[user] = train;
			}
			else if (user.Length > 0)
			{
				_lastTrain.TryGetValue(user, out train);
			}

			// Resolve the job when none was typed.
			if (jobId == null)
			{
				if (car != null) jobId = _mod.ResolveJobIdForConsist(car);
				else if (!string.IsNullOrEmpty(train)) jobId = _mod.ResolveJobIdForTrain(train);
			}

			if (string.IsNullOrEmpty(jobId))
			{
				server.SendWhisperChatMessage(
					"No job found. Get in your loco, or pass a job ID: /" +
					(activate ? "activate" : "complete") + " <jobId>", sender);
				return;
			}

			if (activate)
			{
				var (ok, err) = JobCompletionHelper.TryActivateJob(jobId);
				server.SendWhisperChatMessage(ok ? $"Started {jobId}." : $"Could not start {jobId}: {err}", sender);
			}
			else
			{
				var (ok, err, wage) = JobCompletionHelper.TryCompleteJob(jobId);
				server.SendWhisperChatMessage(
					ok ? $"Completed {jobId}. Paid ${wage:0}." : $"Could not complete {jobId}: {err}", sender);
			}

			// TODO(grdn-bot#27): post a job-event to the bot for unified logging
			// (source=chat, user, train, jobId, action, wage) once the endpoint exists.
		}
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Warning("[GRDNConnect] chat command error: " + ex.Message);
			try { server.SendWhisperChatMessage("Command error: " + ex.Message, sender); } catch { }
		}
	}
}

#endif
