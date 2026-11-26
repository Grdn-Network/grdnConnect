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
	/// <summary>
	/// Wire format version for the packets below. Bump it whenever a field is
	/// added, removed or changes meaning, because dv-mp auto-serializes these and
	/// a mismatched shape does not deserialize.
	///
	/// Host and client compare this before anything else. A mismatch is refused
	/// rather than warned about: the packets genuinely do not line up, so there is
	/// nothing useful to salvage. A difference in mod version alone, on the same
	/// protocol, is only a warning.
	///
	/// This byte is a second line of defence, not the first. LiteNetLib keys each
	/// subscription by a hash of the packet CLASS NAME, so the name is the real wire
	/// identity and the byte is only readable once a packet has already been matched
	/// to a type. Two builds that share a class name but not its fields will
	/// mis-deserialize before anything gets as far as comparing this. That is what
	/// happened between the betas and protocol 3, and why the protocol-3 packets
	/// carry a V3 suffix.
	///
	/// So: a new protocol version that changes any field MUST also rename its
	/// packet classes. Bumping the byte alone is not enough.
	///
	/// 3: config reply drops BotUrl and Secret, since clients no longer call the
	///    bot. Adds the bot request relay packets. Classes renamed to ...V3Packet,
	///    and the beta class names kept as tombstones at the bottom of this file.
	/// 2: added Protocol/ModVersion to both packets and ChannelsJson to the reply.
	/// 1: 0.93.0-beta.1 to beta.4. BotUrl + Secret only. Shared the unsuffixed class
	///    names, which is the bug the V3 suffix fixes.
	/// </summary>
	internal const byte ProtocolVersion = 3;

	/// <summary>
	/// How long a client waits for the host to come back with a bot result. The
	/// host's own HTTP call to the bot is the slow part; this only has to be longer
	/// than that plus a round trip.
	/// </summary>
	private const float RelayTimeoutSec = 8f;

	/// <summary>True once event subscriptions are in place.</summary>
	internal static bool TransportArmed { get; private set; }

	/// <summary>
	/// Client side: the host refused us on protocol version. Terminal, because
	/// retrying cannot change either build, so the retry loop stops instead of
	/// asking 40 times. The log line says what to do about it.
	/// </summary>
	internal static bool ProtocolMismatch { get; private set; }

	internal static void NoteProtocolMismatch() => ProtocolMismatch = true;

	/// <summary>
	/// Clear per-connection client state. A refusal or a "session open" answer is
	/// only true of the host that sent it, so both are dropped when the client
	/// session changes.
	/// </summary>
	internal static void ResetClientState()
	{
		ProtocolMismatch = false;
#if DVMP_API
		// Waiting coroutines time out on their own; dropping the entries means a
		// late reply from the old host finds nothing to complete.
		_pendingRelays.Clear();
#endif
		GRDNConnectBehaviour.ResetClientSessionState();
	}

	/// <summary>
	/// Client side: bot calls this client has asked the host to make, by request id,
	/// waiting for a reply. Entries are removed by the waiting coroutine, so a reply
	/// that arrives after the timeout finds nothing and is dropped.
	/// </summary>
#if DVMP_API
	private static readonly System.Collections.Generic.Dictionary<byte, BotCallResult> _pendingRelays =
		new System.Collections.Generic.Dictionary<byte, BotCallResult>();

	private static byte _nextRelayId;
#endif

	/// <summary>
	/// Client side: ask the host to make a bot call and wait for the answer. Yields
	/// until the host replies or the wait runs out.
	/// </summary>
	internal static System.Collections.IEnumerator RelayPost(
		string route, string bodyJson, BotCallResult result)
	{
#if DVMP_API
		if (!TransportArmed || !ConnectMpTransport.HasClientSession())
		{
			result.Fail("no multiplayer session");
			yield break;
		}

		byte id;
		unchecked { id = ++_nextRelayId; }
		_pendingRelays[id] = result;

		try { ConnectMpTransport.SendBotRequest(id, route, bodyJson); }
		catch (Exception ex)
		{
			_pendingRelays.Remove(id);
			result.Fail(ex.Message);
			yield break;
		}

		float deadline = UnityEngine.Time.realtimeSinceStartup + RelayTimeoutSec;
		while (!result.Done && UnityEngine.Time.realtimeSinceStartup < deadline)
			yield return null;

		_pendingRelays.Remove(id);

		if (!result.Done)
			result.Fail("the host did not answer");
#else
		result.Fail("built without the multiplayer API");
		yield break;
#endif
	}

	/// <summary>
	/// Host side: send a relayed bot result back to the client that asked. Takes the
	/// player as object so GRDNConnectBehaviour never names an MPAPI type.
	/// </summary>
	internal static void SendRelayResult(
		object player, byte requestId, bool ok, string responseJson, string error)
	{
#if DVMP_API
		if (!TransportArmed) return;
		try { ConnectMpTransport.SendBotResult(player, requestId, ok, responseJson, error); }
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Warning("[GRDNConnect] relay result failed: " + ex.Message);
		}
#endif
	}

#if DVMP_API
	/// <summary>Client side: a relayed result arrived. Unknown ids are stale, so dropped.</summary>
	internal static void CompleteRelay(byte id, bool ok, string responseJson, string error)
	{
		if (!_pendingRelays.TryGetValue(id, out var result)) return;

		result.ResponseJson = responseJson ?? "";
		result.Error        = error ?? "";
		result.Ok           = ok;
		result.Done         = true;
	}
#endif

#if DVMP_API
	/// <summary>
	/// Last config broadcast to clients, so an unchanged re-push is skipped.
	/// Cleared when the server stops, because a fresh session must broadcast even
	/// if its config happens to be identical.
	/// </summary>
	private static string _lastBroadcast;

	internal static void ResetBroadcastState() => _lastBroadcast = null;
#endif

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
	internal static void BroadcastSessionConfig(bool sessionActive, string channelsJson)
	{
#if DVMP_API
		if (!TransportArmed) return;

		// Guard against a re-push of config that has not changed, so clients are not
		// sent a packet that would tell them nothing new. The bot also filters on its
		// side; this covers any caller that does not.
		string stamp = $"{sessionActive}\n{channelsJson}";
		if (stamp == _lastBroadcast) return;
		_lastBroadcast = stamp;

		try { ConnectMpTransport.SendConfigToAll(sessionActive, channelsJson); }
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
		MPAPI.MultiplayerAPI.ServerStopped += () =>
		{
			_server = null;
			_connectClients.Clear();
			ConnectMpChannel.ResetBroadcastState();
		};
		MPAPI.MultiplayerAPI.ClientStarted += OnClientStarted;
		MPAPI.MultiplayerAPI.ClientStopped += () =>
		{
			_client = null;
			ConnectMpChannel.ResetClientState();
		};

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

		server.RegisterPacket<ConnectConfigRequestV3Packet>((packet, sender) =>
		{
			string clientVersion = string.IsNullOrEmpty(packet.ModVersion)
				? "unknown" : packet.ModVersion;

			// Protocol gate. dv-mp's own login check compares mod Ids but not
			// versions, so a client on a different build gets this far looking
			// healthy. Refuse here instead of handing over config that does not
			// match the shape they can read.
			if (packet.Protocol != ConnectMpChannel.ProtocolVersion)
			{
				_connectClients.Remove(sender);
				Main.ModEntry.Logger.Warning(
					$"[GRDNConnect] {sender.Username} runs GRDNConnect {clientVersion} " +
					$"(config protocol {packet.Protocol}); this host runs {Main.ModVersion} " +
					$"(protocol {ConnectMpChannel.ProtocolVersion}). No session config sent, so their radio " +
					"and voice switching will not work until both sides run the same version.");

				// Tell them why. Empty config, our versions, so their log names the
				// problem rather than timing out in silence.
				SendConfigTo(sender, false, "[]");
				return;
			}

			if (!_connectClients.Contains(sender)) _connectClients.Add(sender);

			// Serve whatever the host currently holds. Before /session start there is
			// no session, so SessionActive is false and the client keeps asking on a
			// slow retry. The bot's /session start also triggers a broadcast, so the
			// client gets it either way.
			var (active, channelsJson) = GRDNConnectBehaviour.GetSessionConfigForClients();
			SendConfigTo(sender, active, channelsJson);

			if (clientVersion != Main.ModVersion)
				Main.ModEntry.Logger.Warning(
					$"[GRDNConnect] {sender.Username} runs GRDNConnect {clientVersion}, " +
					$"this host runs {Main.ModVersion}. Same config protocol, so this works, " +
					"but matching versions is worth doing.");

			Main.ModEntry.Logger.Log(
				$"[GRDNConnect] {sender.Username} runs GRDNConnect; sent session config " +
				(active ? "(session open)." : "(no session started yet)."));
		});

		// Tombstone handler for the 0.93.0 beta wire format.
		//
		// LiteNetLib keys a subscription by a hash of the packet class name, so the
		// name is the wire identity. betas 1 to 4 send a class of this name holding a
		// single byte, and protocol 3 renamed its own packets precisely so those bytes
		// can never be read as a protocol-3 packet. Registering the old name with the
		// old shape gives a beta client's packet somewhere to land, instead of an
		// unregistered hash.
		//
		// The reply carries empty strings and nothing else. A beta client reads that as
		// "no session started yet" and keeps asking on its own retry, which is the only
		// honest answer available: its half of the protocol cannot be served. A real URL
		// or token here would hand a credential back to exactly the clients this release
		// stopped giving one to.
		server.RegisterPacket<ConnectConfigRequestPacket>((packet, sender) =>
		{
			Main.ModEntry.Logger.Warning(
				$"[GRDNConnect] {sender.Username} runs a 0.93.0 beta build (config protocol " +
				$"{packet.Version}); this host runs {Main.ModVersion} (config protocol " +
				$"{ConnectMpChannel.ProtocolVersion}). Their radio and voice switching will not " +
				"work until they update. Sent an empty config.");

			if (_server == null) return;
			((MPAPI.Interfaces.IServer)_server).SendPacketToPlayer(
				new ConnectConfigPacket { BotUrl = "", Secret = "" }, sender);
		});

		// A client asking the host to spend its token on a bot call. The route
		// allowlist is the whole protection here, so it is checked before anything
		// else happens and a refusal is logged with the player who asked.
		server.RegisterPacket<ConnectBotRequestV3Packet>((packet, sender) =>
		{
			if (packet.Protocol != ConnectMpChannel.ProtocolVersion)
			{
				SendBotResult(sender, packet.RequestId, false, "", "version mismatch with the host");
				return;
			}

			if (!BotCall.IsRelayable(packet.Route))
			{
				Main.ModEntry.Logger.Warning(
					$"[GRDNConnect] Refused to relay '{packet.Route}' for {sender.Username}: " +
					"not an allowed client route.");
				SendBotResult(sender, packet.RequestId, false, "", "route not allowed");
				return;
			}

			GRDNConnectBehaviour.RelayBotCallForClient(
				sender, sender.Username, packet.RequestId, packet.Route, packet.BodyJson);
		});
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void OnClientStarted(MPAPI.Interfaces.IClient client)
	{
		_client = client;
		ConnectMpChannel.ResetClientState();

		client.RegisterPacket<ConnectConfigV3Packet>(packet =>
		{
			string hostVersion = string.IsNullOrEmpty(packet.ModVersion)
				? "unknown" : packet.ModVersion;

			if (packet.Protocol != ConnectMpChannel.ProtocolVersion)
			{
				ConnectMpChannel.NoteProtocolMismatch();
				Main.ModEntry.Logger.Error(
					$"[GRDNConnect] Version mismatch with the host. This game runs GRDNConnect " +
					$"{Main.ModVersion} (config protocol {ConnectMpChannel.ProtocolVersion}), the host runs " +
					$"{hostVersion} (protocol {packet.Protocol}). Radio and crew voice switching " +
					"are off for this session. Install the host's version of GRDNConnect.");
				return;
			}

			if (hostVersion != Main.ModVersion)
				Main.ModEntry.Logger.Warning(
					$"[GRDNConnect] Host runs GRDNConnect {hostVersion}, this game runs " +
					$"{Main.ModVersion}. Compatible, but worth matching.");

			GRDNConnectBehaviour.ApplySessionConfigFromHost(
				packet.SessionActive, packet.ChannelsJson);
		});

		client.RegisterPacket<ConnectBotResultV3Packet>(packet =>
		{
			ConnectMpChannel.CompleteRelay(
				packet.RequestId, packet.Ok, packet.ResponseJson, packet.Error);
		});

		Main.ModEntry.Logger.Log("[GRDNConnect] client session started; config handler registered.");
		SendConfigRequest();
	}

	/// <summary>Client to host: "I run GRDNConnect, send me the session config."</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	internal static void SendConfigRequest()
	{
		if (_client == null) return;
		((MPAPI.Interfaces.IClient)_client).SendPacketToServer(new ConnectConfigRequestV3Packet
		{
			Protocol   = ConnectMpChannel.ProtocolVersion,
			ModVersion = Main.ModVersion,
		});
		Main.LogVerbose("[GRDNConnect] session config requested from the host.");
	}

	/// <summary>True when a client session is live, so the retry loop knows to keep asking.</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	internal static bool HasClientSession() => _client != null;

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void SendConfigTo(
		MPAPI.Interfaces.IPlayer player, bool sessionActive, string channelsJson)
	{
		((MPAPI.Interfaces.IServer)_server).SendPacketToPlayer(new ConnectConfigV3Packet
		{
			Protocol      = ConnectMpChannel.ProtocolVersion,
			ModVersion    = Main.ModVersion,
			SessionActive = sessionActive,
			ChannelsJson  = channelsJson ?? "[]",
		}, player);
	}

	/// <summary>Host side: hand a relayed bot result back to the client that asked.</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	internal static void SendBotResult(
		object player, byte requestId, bool ok, string responseJson, string error)
	{
		if (_server == null || !(player is MPAPI.Interfaces.IPlayer target)) return;
		((MPAPI.Interfaces.IServer)_server).SendPacketToPlayer(new ConnectBotResultV3Packet
		{
			Protocol     = ConnectMpChannel.ProtocolVersion,
			RequestId    = requestId,
			Ok           = ok,
			ResponseJson = responseJson ?? "",
			Error        = error ?? "",
		}, target);
	}

	/// <summary>Client side: ask the host to make a bot call for us.</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	internal static void SendBotRequest(byte requestId, string route, string bodyJson)
	{
		if (_client == null) return;
		((MPAPI.Interfaces.IClient)_client).SendPacketToServer(new ConnectBotRequestV3Packet
		{
			Protocol  = ConnectMpChannel.ProtocolVersion,
			RequestId = requestId,
			Route     = route ?? "",
			BodyJson  = bodyJson ?? "{}",
		});
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	internal static void SendConfigToAll(bool sessionActive, string channelsJson)
	{
		if (_server == null) return;
		foreach (var obj in _connectClients)
			SendConfigTo((MPAPI.Interfaces.IPlayer)obj, sessionActive, channelsJson);
		if (_connectClients.Count > 0)
			Main.ModEntry.Logger.Log(
				$"[GRDNConnect] pushed session config to {_connectClients.Count} client(s).");
	}
}

/// <summary>
/// Client to server: "this client runs GRDNConnect, send me config." Carries both
/// versions so the host can refuse an incompatible protocol and name the build in
/// its log. Auto-serialized.
/// </summary>
public class ConnectConfigRequestV3Packet : MPAPI.Interfaces.Packets.IPacket
{
	public byte   Protocol   { get; set; }
	public string ModVersion { get; set; }
}

/// <summary>
/// Server to client: what a client needs for its radio, which is no longer a
/// credential. Clients reach the bot only by asking the host (see BotCall), so no
/// URL and no token are sent. A Protocol that does not match the client's is a
/// refusal, whatever else the packet holds. Auto-serialized.
/// </summary>
public class ConnectConfigV3Packet : MPAPI.Interfaces.Packets.IPacket
{
	public byte   Protocol   { get; set; }
	public string ModVersion { get; set; }

	/// <summary>
	/// True once the host holds bot config, meaning an ops session is open. False
	/// tells the client to keep asking.
	/// </summary>
	public bool SessionActive { get; set; }

	/// <summary>
	/// The channels array from the bot's /session-config, relayed verbatim so clients
	/// need no bot access of their own. Parsed by RadioIntegration.ParseChannels.
	/// </summary>
	public string ChannelsJson { get; set; }
}

/// <summary>
/// Client to server: "make this bot call for me." The host checks the route against
/// its own allowlist before spending its token on it. Auto-serialized.
/// </summary>
public class ConnectBotRequestV3Packet : MPAPI.Interfaces.Packets.IPacket
{
	public byte   Protocol  { get; set; }

	/// <summary>Correlates the reply. Wraps, which is fine for one call at a time per player.</summary>
	public byte   RequestId { get; set; }

	/// <summary>Bare route name, no slash, for example "radio-change".</summary>
	public string Route     { get; set; }

	public string BodyJson  { get; set; }
}

/// <summary>Server to client: what the bot said, or why it did not get asked. Auto-serialized.</summary>
public class ConnectBotResultV3Packet : MPAPI.Interfaces.Packets.IPacket
{
	public byte   Protocol     { get; set; }
	public byte   RequestId    { get; set; }
	public bool   Ok           { get; set; }
	public string ResponseJson { get; set; }
	public string Error        { get; set; }
}

/// <summary>
/// Tombstone: the config request exactly as 0.93.0-beta.1 to beta.4 send it.
///
/// This build never sends it. It exists so a beta client's packet lands on a
/// registered handler rather than an unknown type name. Those builds set Version
/// and nothing ever read it, so that line had no version gate at all; the field is
/// here only to match what goes on the wire.
///
/// Do not change these fields. They record what shipped, not a design.
/// </summary>
public class ConnectConfigRequestPacket : MPAPI.Interfaces.Packets.IPacket
{
	public byte Version { get; set; }
}

/// <summary>
/// Tombstone: the config reply exactly as 0.93.0-beta.1 to beta.4 read it.
///
/// Only ever sent with both fields empty. A beta client reads empty strings as "the
/// host has not started a session yet" and keeps asking.
///
/// Never put a real URL or token in here. These are the clients that hold no
/// credential of their own by design, and filling this in would hand one back to
/// every one of them.
/// </summary>
public class ConnectConfigPacket : MPAPI.Interfaces.Packets.IPacket
{
	public string BotUrl { get; set; }
	public string Secret { get; set; }
}

#endif
