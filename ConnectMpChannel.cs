// ConnectMpChannel.cs
// Carries the session config (bot URL + per-session secret) from the host to
// clients over dv-multiplayer's mod packet API, replacing the old HTTP fetch.
//
// WHY THIS REPLACES THE HTTP FETCH
// ─────────────────────────────────
// The old path had the client discover the host's IP by reflection and then GET
// http://<host>:7230/client-config. That needed two things to be true at once:
//   1. the host's address had to be discoverable (it was not: dv-mp's public API
//      exposes no server address, and the reflection probe returned null, which is
//      the "Could not determine server address" line in the log), and
//   2. the host's port 7230 had to be reachable from the client, which for most
//      players it is not, because nothing forwards it through their router.
// Neither is required here: the game already has a working, NAT-traversing,
// authenticated connection between host and client, so the config rides on that.
//
// SHAPE (mirrors DLE's DleMpChannel, deliberately)
// ────────────────────────────────────────────────
// ConnectMpChannel holds pure BCL state and is safe to call from anywhere,
// singleplayer included. ConnectMpTransport touches MPAPI types in method bodies
// only and every one of those methods is NoInlining, so the soft-referenced
// MultiplayerAPI.dll is not loaded until such a method actually RUNS. In
// singleplayer none of them ever do.
//
// A client greeting a host that does not run GRDNConnect costs that host one
// logged dv-mp parse warning and nothing else.

using System;
using System.Runtime.CompilerServices;

internal static class ConnectMpChannel
{
	/// <summary>True once event subscriptions are in place.</summary>
	internal static bool TransportArmed { get; private set; }

	/// <summary>
	/// Arm the transport. Safe to call repeatedly (mod load AND world load):
	/// assemblies load lazily in .NET, so at our load time dv-mp may not have
	/// touched its own API yet and MultiplayerAPI.dll can be absent from the
	/// AppDomain even though the mod is installed. This forces it in from the
	/// Multiplayer mod's own folder. Does nothing when DVMP is not installed,
	/// and never throws outward.
	/// </summary>
	internal static void TryInit()
	{
#if DVMP_API
		if (TransportArmed) return;
		try
		{
			var domain = AppDomain.CurrentDomain.GetAssemblies();
			bool apiLoaded = false;
			System.Reflection.Assembly mp = null;
			foreach (var a in domain)
			{
				string n = a.GetName().Name;
				if (n == "MultiplayerAPI") { apiLoaded = true; break; }
				if (n == "Multiplayer") mp = a;
			}

			if (!apiLoaded)
			{
				if (mp == null) return; // no DVMP installed: pure singleplayer
				string apiPath = System.IO.Path.Combine(
					System.IO.Path.GetDirectoryName(mp.Location) ?? "", "MultiplayerAPI.dll");
				if (!System.IO.File.Exists(apiPath))
				{
					Main.ModEntry.Logger.Warning(
						"[GRDNConnect] Multiplayer is loaded but MultiplayerAPI.dll is missing next to it; " +
						"client config sync disabled.");
					return;
				}
				System.Reflection.Assembly.LoadFrom(apiPath);
				Main.LogVerbose("[GRDNConnect] MultiplayerAPI.dll force-loaded from the Multiplayer mod folder.");
			}

			ConnectMpTransport.Init();
			TransportArmed = true;
		}
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Warning(
				$"[GRDNConnect] MP transport init failed ({ex.GetType().Name}: {ex.Message}); " +
				"client config sync disabled.");
		}
#endif
	}

	/// <summary>
	/// Client side: ask the host for the session config. No-op in singleplayer, when
	/// DVMP is absent, or when no client session is live.
	/// </summary>
	internal static void RequestSessionConfig()
	{
#if DVMP_API
		if (!TransportArmed) return;
		try
		{
			if (!ConnectMpTransport.HasClientSession()) return;
			ConnectMpTransport.SendConfigRequest();
		}
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Warning("[GRDNConnect] config request failed: " + ex.Message);
		}
#endif
	}

	/// <summary>
	/// Host side: the bot just pushed new session config, so push it straight out to
	/// every connected client that asked for it. Without this, a client who joined
	/// before /session start would sit with no secret until they reconnected.
	/// No-op in singleplayer or when not hosting.
	/// </summary>
	internal static void BroadcastSessionConfig(string botUrl, string secret)
	{
#if DVMP_API
		if (!TransportArmed) return;
		try { ConnectMpTransport.SendConfigToAll(botUrl, secret); }
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Warning("[GRDNConnect] config broadcast failed: " + ex.Message);
		}
#endif
	}
}

#if DVMP_API

internal static class ConnectMpTransport
{
	private static object _server;   // IServer while hosting
	private static object _client;   // IClient while connected

	// Clients that have identified themselves as running GRDNConnect. Only these
	// get config packets, so a modless client is never sent anything.
	private static readonly System.Collections.Generic.List<object> _connectClients =
		new System.Collections.Generic.List<object>();

	[MethodImpl(MethodImplOptions.NoInlining)]
	internal static void Init()
	{
		MPAPI.MultiplayerAPI.ServerStarted += OnServerStarted;
		MPAPI.MultiplayerAPI.ServerStopped += () => { _server = null; _connectClients.Clear(); };
		MPAPI.MultiplayerAPI.ClientStarted += OnClientStarted;
		MPAPI.MultiplayerAPI.ClientStopped += () => { _client = null; };

		// A session may already be live (mod reloaded into a running game).
		if (MPAPI.MultiplayerAPI.Server != null) OnServerStarted(MPAPI.MultiplayerAPI.Server);
		if (MPAPI.MultiplayerAPI.Client != null) OnClientStarted(MPAPI.MultiplayerAPI.Client);

		Main.ModEntry.Logger.Log("[GRDNConnect] DVMP config channel armed.");
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void OnServerStarted(MPAPI.Interfaces.IServer server)
	{
		_server = server;
		_connectClients.Clear();

		server.RegisterPacket<ConnectConfigRequestPacket>((packet, sender) =>
		{
			if (!_connectClients.Contains(sender)) _connectClients.Add(sender);

			// Serve whatever the host currently holds. When no session has been
			// started yet both are empty; the client keeps asking on a slow retry
			// and the bot's /session start also triggers a broadcast, so the client
			// gets it either way.
			var (url, secret) = GRDNConnectBehaviour.GetSessionConfigForClients();
			SendConfigTo(sender, url, secret);

			Main.ModEntry.Logger.Log(
				$"[GRDNConnect] {sender.Username} runs GRDNConnect; sent session config " +
				(string.IsNullOrEmpty(url) ? "(none yet, no session started)." : $"(url={url})."));
		});
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void OnClientStarted(MPAPI.Interfaces.IClient client)
	{
		_client = client;

		client.RegisterPacket<ConnectConfigPacket>(packet =>
		{
			GRDNConnectBehaviour.ApplySessionConfigFromHost(packet.BotUrl, packet.Secret);
		});

		Main.ModEntry.Logger.Log("[GRDNConnect] client session started; config handler registered.");
		SendConfigRequest();
	}

	/// <summary>Client to host: "I run GRDNConnect, send me the session config."</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	internal static void SendConfigRequest()
	{
		if (_client == null) return;
		((MPAPI.Interfaces.IClient)_client).SendPacketToServer(
			new ConnectConfigRequestPacket { Version = 1 });
		Main.LogVerbose("[GRDNConnect] session config requested from the host.");
	}

	/// <summary>True when a client session is live, so the retry loop knows to keep asking.</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	internal static bool HasClientSession() => _client != null;

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void SendConfigTo(MPAPI.Interfaces.IPlayer player, string botUrl, string secret)
	{
		((MPAPI.Interfaces.IServer)_server).SendPacketToPlayer(new ConnectConfigPacket
		{
			BotUrl = botUrl ?? "",
			Secret = secret ?? "",
		}, player);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	internal static void SendConfigToAll(string botUrl, string secret)
	{
		if (_server == null) return;
		foreach (var obj in _connectClients)
			SendConfigTo((MPAPI.Interfaces.IPlayer)obj, botUrl, secret);
		if (_connectClients.Count > 0)
			Main.ModEntry.Logger.Log(
				$"[GRDNConnect] pushed session config to {_connectClients.Count} client(s).");
	}
}

/// <summary>Client to server: "this client runs GRDNConnect, send me config." Auto-serialized.</summary>
public class ConnectConfigRequestPacket : MPAPI.Interfaces.Packets.IPacket
{
	public byte Version { get; set; }
}

/// <summary>
/// Server to client: the session's bot URL and per-session secret. Empty strings mean
/// the host has not started an ops session yet, and the client should keep asking.
/// Auto-serialized.
/// </summary>
public class ConnectConfigPacket : MPAPI.Interfaces.Packets.IPacket
{
	public string BotUrl { get; set; }
	public string Secret { get; set; }
}

#endif
