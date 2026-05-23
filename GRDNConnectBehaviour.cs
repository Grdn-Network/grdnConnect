using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using DV.Logic.Job;
using DV.ThingTypes;
using UnityEngine;
using UnityEngine.Networking;

public class GRDNConnectBehaviour : MonoBehaviour
{
	private HttpListener _listener;
	private static GRDNConnectBehaviour _instance;
	private static int _activePort = -1;   // port the listener is currently bound to

	// ── Session config — pushed by the bot on /session start ─────────────────
	// Cleared to null on destroy so a fresh session always picks up new values.
	private static string _sessionBotUrl    = null;
	private static string _sessionBotSecret = null;

	/// <summary>
	/// The bot's base URL for this session.
	/// Priority: session-config push → UMM Settings → hardcoded default (GRDNDefaults.cs).
	/// </summary>
	internal static string ActiveBotUrl =>
		!string.IsNullOrEmpty(_sessionBotUrl)           ? _sessionBotUrl           :
		!string.IsNullOrEmpty(Main.Settings.BotPushUrl) ? Main.Settings.BotPushUrl :
		GRDNDefaults.BotUrl;

	/// <summary>
	/// The bot's shared secret for this session.
	/// Priority: session-config push → UMM Settings → hardcoded default (GRDNDefaults.cs).
	/// </summary>
	internal static string ActiveBotSecret =>
		!string.IsNullOrEmpty(_sessionBotSecret)        ? _sessionBotSecret        :
		!string.IsNullOrEmpty(Main.Settings.BotSecret)  ? Main.Settings.BotSecret  :
		GRDNDefaults.BotSecret;

	private void Awake()
	{
		_instance = this;
	}

	private void Start()
	{
		StartListener(Main.Settings.Port);

		// If we're a multiplayer client, auto-fetch session config (botUrl + secret)
		// from the host's mod. Retries every 15 s until it succeeds so players who
		// join mid-op or before /session start still get the config automatically.
		StartCoroutine(FetchClientConfigLoop());

		// Optional CommsRadioAPI integration — compiled and run only when
		// CommsRadioAPI.dll is present in lib/ at build time.
#if COMMS_RADIO_API
		RadioIntegration.TryInit(this);
		// Poll the bot for the live channel list every 60 s.
		RadioIntegration.StartChannelPolling(this);

		// Verify Steam identity on startup — result is logged so you can confirm
		// radio pushes will carry a Steam ID without needing to trigger a swap first.
		var (steamId, steamName) = RadioIntegration.GetLocalSteamInfo();
		if (steamId > 0)
			Main.ModEntry.Logger.Log($"[GRDNConnect] Steam identity OK — id={steamId} name={steamName}");
		// Warning already printed inside GetLocalSteamInfo() if steamId == 0.
#endif
	}

	/// <summary>
	/// Stops the HTTP listener immediately (synchronous). Safe to call multiple times.
	/// Called from Main.Unload() BEFORE Object.Destroy() so the port is free when
	/// Load() runs in the same frame and creates a fresh instance.
	/// </summary>
	internal void StopListener()
	{
		try
		{
			if (_listener != null)
			{
				if (_listener.IsListening) _listener.Stop();
				_listener.Close();
				_listener = null;
				_activePort = -1;
			}
		}
		catch { }
	}

	/// <summary>
	/// Starts (or restarts) the HTTP listener on the given port.
	/// No-op if the listener is already running on that port.
	/// </summary>
	private void StartListener(int port)
	{
		if (port == _activePort && _listener?.IsListening == true) return;

		StopListener();

		// ALL of this is inside try/catch so a failure here never prevents
		// RadioIntegration.TryInit() from running in Start().
		try
		{
			_listener = new HttpListener();
			_listener.Prefixes.Add($"http://*:{port}/");
			_listener.Start();
			_activePort = port;
			Main.ModEntry.Logger.Log($"[GRDNConnect] Listening on port {port}.");
			((MonoBehaviour)this).StartCoroutine(ListenLoop());
		}
		catch (Exception ex)
		{
			_activePort = -1;
			_listener   = null;
			Main.ModEntry.Logger.Error($"[GRDNConnect] Failed to start on port {port}: {ex.Message}");
		}
	}

	/// <summary>
	/// Called from Settings.OnChange() — applies port change and live radio channel list.
	/// Restarts the listener only if the port actually changed and is valid.
	/// </summary>
	internal static void ApplySettingsChange()
	{
		if (_instance == null) return;

		int newPort = Main.Settings.Port;
		if (newPort >= 1024 && newPort <= 65535 && newPort != _activePort)
			_instance.StartListener(newPort);

#if COMMS_RADIO_API
		RadioIntegration.UpdateChannelsFromJson(Main.Settings.RadioChannelsJson);
#endif
	}

	/// <summary>
	/// Returns true if this game instance is the server host, or if DVMP is not loaded (singleplayer).
	/// Uses reflection so GRDNConnect has zero compile-time dependency on dv-multiplayer.
	/// </summary>
	internal static bool IsHostOrSingleplayer()
	{
		try
		{
			foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				// Only inspect assemblies that look like the multiplayer mod
				string asmName = asm.GetName().Name;
				if (!asmName.StartsWith("Multiplayer", StringComparison.OrdinalIgnoreCase))
					continue;

				// Search for the MultiplayerAPI type (namespace may vary across DVMP versions)
				Type apiType = null;
				foreach (Type t in asm.GetExportedTypes())
				{
					if (t.Name == "MultiplayerAPI")
					{
						apiType = t;
						break;
					}
				}
				if (apiType == null) continue;

				// IsMultiplayerLoaded == false → singleplayer even with mod installed
				PropertyInfo isLoadedProp = apiType.GetProperty("IsMultiplayerLoaded",
					BindingFlags.Public | BindingFlags.Static);
				if (isLoadedProp != null)
				{
					bool isLoaded = (bool)isLoadedProp.GetValue(null);
					if (!isLoaded) return true; // singleplayer session
				}

				// Prefer Instance.IsHost / Instance.IsSinglePlayer (cleaner API)
				PropertyInfo instanceProp = apiType.GetProperty("Instance",
					BindingFlags.Public | BindingFlags.Static);
				if (instanceProp != null)
				{
					object instance = instanceProp.GetValue(null);
					if (instance != null)
					{
						Type instanceType = instance.GetType();
						bool? isSingle = instanceType.GetProperty("IsSinglePlayer")?.GetValue(instance) as bool?;
						if (isSingle == true) return true;
						bool? isHost = instanceType.GetProperty("IsHost")?.GetValue(instance) as bool?;
						if (isHost.HasValue) return isHost.Value;
					}
				}

				// Fallback: Server != null → hosting
				PropertyInfo serverProp = apiType.GetProperty("Server",
					BindingFlags.Public | BindingFlags.Static);
				if (serverProp != null)
				{
					return serverProp.GetValue(null) != null;
				}

				// DVMP found but API shape unrecognised — refuse requests to be safe
				return false;
			}

			// DVMP assembly not loaded at all — singleplayer
			return true;
		}
		catch
		{
			return true; // fail open — reflection error means assume singleplayer
		}
	}

	private void OnDestroy()
	{
		if (_instance == this) _instance = null;
		StopListener();
	}

	private IEnumerator ListenLoop()
	{
		while (_listener.IsListening)
		{
			IAsyncResult async = _listener.BeginGetContext(null, null);
			while (!async.IsCompleted)
			{
				yield return null;
			}
			HttpListenerContext ctx;
			try
			{
				ctx = _listener.EndGetContext(async);
			}
			catch
			{
				break;
			}
			try { HandleRequest(ctx); }
			catch (Exception ex) { Main.ModEntry.Logger.Error("[GRDNConnect] Unhandled: " + ex.Message); }
		}
	}

	private void HandleRequest(HttpListenerContext ctx)
	{
		string text = ctx.Request.Url.AbsolutePath.ToLower();
		try
		{
			// /client-config is served by everyone — clients query the HOST's port
			// to auto-fetch botUrl + secret without any manual UMM settings entry.
			if (ctx.Request.HttpMethod == "GET" && text == "/client-config")
			{
				HandleClientConfig(ctx.Response);
				return;
			}

			// Reject all other requests if this player is not the host/singleplayer.
			// Checked per-request so the state is current (not locked in at startup).
			if (!IsHostOrSingleplayer())
			{
				SendJson(ctx.Response, 403, "{\"error\":\"Not the host\"}");
				return;
			}

			if (ctx.Request.HttpMethod == "GET" && text == "/jobs")
			{
				HandleGetJobs(ctx.Response);
			}
			else if (ctx.Request.HttpMethod == "GET" && text == "/server-info")
			{
				HandleGetServerInfo(ctx.Response);
			}
			else if (ctx.Request.HttpMethod == "GET" && text == "/debug-assemblies")
			{
				HandleDebugAssemblies(ctx.Response);
			}
			else if (ctx.Request.HttpMethod == "GET" && text == "/debug-multiplayer")
			{
				HandleDebugMultiplayer(ctx.Response);
			}
			else if (ctx.Request.HttpMethod == "GET" && text == "/locos")
			{
				HandleGetLocos(ctx.Response);
			}
			else if (ctx.Request.HttpMethod == "GET" && text == "/debug-locos")
			{
				HandleDebugLocos(ctx.Response);
			}
			else if (ctx.Request.HttpMethod == "POST" && text == "/complete-job")
			{
				HandleCompleteJob(ctx.Request, ctx.Response);
			}
			else if (ctx.Request.HttpMethod == "POST" && text == "/session-config")
			{
				HandleSessionConfig(ctx.Request, ctx.Response);
			}
			else if (ctx.Request.HttpMethod == "GET" && text == "/debug-boturl")
			{
				HandleDebugBotUrl(ctx.Response);
			}
			else
			{
				SendJson(ctx.Response, 404, "{\"error\":\"Not found\"}");
			}
		}
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Error("[GRDNConnect] " + ex.Message);
			SendJson(ctx.Response, 500, "{\"error\":\"Internal server error\"}");
		}
	}

	private void HandleDebugMultiplayer(HttpListenerResponse res)
	{
		var sb = new StringBuilder();
		sb.Append("{");
		try
		{
			Assembly mpAsm = null;
			foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
				if (asm.GetName().Name.Equals("Multiplayer", StringComparison.OrdinalIgnoreCase))
					{ mpAsm = asm; break; }

			if (mpAsm == null) { sb.Append("\"error\":\"assembly not found\""); }
			else
			{
				Type mainType = mpAsm.GetType("Multiplayer.Multiplayer");
				if (mainType == null) { sb.Append("\"error\":\"Multiplayer.Multiplayer type not found\""); }
				else
				{
					// Dump all static fields
					sb.Append("\"fields\":[");
					bool first = true;
					foreach (FieldInfo f in mainType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
					{
						if (!first) sb.Append(",");
						first = false;
						string valStr = "?";
						try { object v = f.GetValue(null); valStr = v == null ? "null" : v.GetType().FullName; } catch { valStr = "error"; }
						sb.Append($"{{\"name\":\"{Escape(f.Name)}\",\"type\":\"{Escape(f.FieldType.FullName)}\",\"value\":\"{Escape(valStr)}\"}}");
					}
					sb.Append("],");

					// Dump all static properties
					sb.Append("\"properties\":[");
					first = true;
					foreach (PropertyInfo p in mainType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
					{
						if (!first) sb.Append(",");
						first = false;
						sb.Append($"{{\"name\":\"{Escape(p.Name)}\",\"type\":\"{Escape(p.PropertyType.FullName)}\"}}");
					}
					sb.Append("]");
				}
			}
		}
		catch (Exception ex) { sb.Append($"\"error\":\"{Escape(ex.Message)}\""); }
		sb.Append("}");
		SendJson(res, 200, sb.ToString());
	}

	private void HandleDebugAssemblies(HttpListenerResponse res)
	{
		var sb = new StringBuilder();
		sb.Append("[");
		bool first = true;
		foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (!first) sb.Append(",");
			first = false;
			string asmName = asm.GetName().Name;
			sb.Append($"{{\"assembly\":\"{Escape(asmName)}\"");

			// For multiplayer assemblies, list all types
			if (asmName.StartsWith("Multiplayer", StringComparison.OrdinalIgnoreCase))
			{
				sb.Append(",\"types\":[");
				bool firstType = true;
				try
				{
					foreach (Type t in asm.GetTypes())
					{
						if (!firstType) sb.Append(",");
						firstType = false;
						sb.Append($"\"{Escape(t.FullName)}\"");
					}
				}
				catch { }
				sb.Append("]");
			}
			sb.Append("}");
		}
		sb.Append("]");
		SendJson(res, 200, sb.ToString());
	}

	private void HandleGetServerInfo(HttpListenerResponse res)
	{
		string serverName = null;
		string password = null;

		try
		{
			// Target the Multiplayer assembly directly — we know it exists from debug
			Assembly mpAsm = null;
			foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (asm.GetName().Name.Equals("Multiplayer", StringComparison.OrdinalIgnoreCase))
				{
					mpAsm = asm;
					break;
				}
			}

			if (mpAsm == null)
			{
				Main.ModEntry.Logger.Warning("[GRDNConnect] Multiplayer assembly not found");
			}
			else
			{
				Type settingsType = mpAsm.GetType("Multiplayer.Settings");
				if (settingsType == null)
				{
					Main.ModEntry.Logger.Warning("[GRDNConnect] Multiplayer.Settings type not found");
				}
				else
				{
								// Confirmed via /debug-multiplayer: Settings field on Multiplayer.Multiplayer
					Type mainType = mpAsm.GetType("Multiplayer.Multiplayer");
					FieldInfo settingsField = mainType?.GetField("Settings",
						BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
					object settingsObj = settingsField?.GetValue(null);

					if (settingsObj == null)
					{
						Main.ModEntry.Logger.Warning("[GRDNConnect] Settings not found on any known type — check UMM log");
					}
					else
					{
						// UMM settings use public fields with [Draw], not properties
						serverName = (settingsType.GetField("ServerName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(settingsObj)
							?? settingsType.GetProperty("ServerName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(settingsObj))?.ToString();
						password = (settingsType.GetField("Password", BindingFlags.Public | BindingFlags.Instance)?.GetValue(settingsObj)
							?? settingsType.GetProperty("Password", BindingFlags.Public | BindingFlags.Instance)?.GetValue(settingsObj))?.ToString();
						Main.ModEntry.Logger.Log($"[GRDNConnect] server-info: name='{serverName}' hasPassword={!string.IsNullOrEmpty(password)}");
					}
				}
			}
		}
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Warning("[GRDNConnect] server-info error: " + ex.Message);
		}

		string nameJson = !string.IsNullOrEmpty(serverName) ? $"\"{Escape(serverName)}\"" : "null";
		string passJson = !string.IsNullOrEmpty(password)   ? $"\"{Escape(password)}\"" : "null";
		SendJson(res, 200, $"{{\"serverName\":{nameJson},\"password\":{passJson}}}");
	}

	private unsafe void HandleGetJobs(HttpListenerResponse res)
	{
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		IEnumerable<Job> currentJobsForApi = JobCompletionHelper.GetCurrentJobsForApi();
		List<string> list = new List<string>();
		if (currentJobsForApi != null)
		{
			foreach (Job item in currentJobsForApi)
			{
				string jobState = JobCompletionHelper.GetJobState(item);
				string s = GetChainDataField(item, "chainOriginYardId") ?? "—";
				string s2 = GetChainDataField(item, "chainDestinationYardId") ?? "—";
				string[] obj = new string[11]
				{
					"{\"id\":\"",
					Escape(item.ID),
					"\",\"type\":\"",
					null,
					null,
					null,
					null,
					null,
					null,
					null,
					null
				};
				JobType jobType = item.jobType;
				obj[3] = Escape(((object)(*(JobType*)(&jobType))/*cast due to constrained. prefix*/).ToString());
				obj[4] = "\",\"state\":\"";
				obj[5] = Escape(jobState);
				obj[6] = "\",\"departure\":\"";
				obj[7] = Escape(s);
				obj[8] = "\",\"destination\":\"";
				obj[9] = Escape(s2);
				obj[10] = "\"}";
				list.Add(string.Concat(obj));
			}
		}
		SendJson(res, 200, "[" + string.Join(",", list) + "]");
	}

	private void HandleCompleteJob(HttpListenerRequest req, HttpListenerResponse res)
	{
		string json;
		using (StreamReader streamReader = new StreamReader(req.InputStream, Encoding.UTF8))
		{
			json = streamReader.ReadToEnd();
		}
		string text = ExtractJsonString(json, "jobId");
		if (string.IsNullOrEmpty(text))
		{
			SendJson(res, 400, "{\"ok\":false,\"error\":\"Missing jobId\"}");
			return;
		}
		var (ok, error) = JobCompletionHelper.TryCompleteJob(text);
		string errorField = !string.IsNullOrEmpty(error) ? $",\"error\":\"{Escape(error)}\"" : "";
		SendJson(res, ok ? 200 : 404, $"{{\"ok\":{(ok ? "true" : "false")},\"jobId\":\"{Escape(text)}\"{errorField}}}");
	}

	// ── GET /debug-boturl ────────────────────────────────────────────────────
	// Returns the URL and secret source that GRDN Crew and radio integrations
	// will use. Hit http://localhost:7230/debug-boturl in a browser while in-game
	// to confirm the game is pointing at your bot, not at itself.
	private void HandleDebugBotUrl(HttpListenerResponse res)
	{
		string sessionUrl = _sessionBotUrl;
		string settingsUrl = Main.Settings.BotPushUrl;
		string activeUrl = ActiveBotUrl;

		// Mask secret — show first 4 chars then ****
		string sessionSecret = _sessionBotSecret;
		string settingsSecret = Main.Settings.BotSecret;
		string MaskSecret(string s) =>
			string.IsNullOrEmpty(s) ? "(empty)" :
			(s.Length <= 4 ? "****" : s.Substring(0, 4) + "****");

		string source = !string.IsNullOrEmpty(sessionUrl) ? "session-config (pushed by bot)" : "UMM Settings (BotPushUrl)";

		var sb = new StringBuilder();
		sb.Append("{");
		sb.Append($"\"activeUrl\":\"{Escape(activeUrl ?? "")}\",");
		sb.Append($"\"source\":\"{Escape(source)}\",");
		sb.Append($"\"sessionUrl\":\"{Escape(sessionUrl ?? "(not set)")}\",");
		sb.Append($"\"settingsUrl\":\"{Escape(settingsUrl ?? "(not set)")}\",");
		sb.Append($"\"sessionSecret\":\"{Escape(MaskSecret(sessionSecret))}\",");
		sb.Append($"\"settingsSecret\":\"{Escape(MaskSecret(settingsSecret))}\"");
		sb.Append("}");

		Main.ModEntry.Logger.Log($"[GRDNConnect] /debug-boturl → activeUrl={activeUrl ?? "(null)"} source={source}");
		SendJson(res, 200, sb.ToString());
	}

	// ── POST /session-config ──────────────────────────────────────────────────
	// Called by the bot when an ops session opens (/session start).
	// Body: { "botUrl": "http://vps-ip:3000", "secret": "..." }
	// Stores values in static fields so GRDNCrewMode and DefectMonitor
	// always have the current bot address without any UMM settings entry.
	private void HandleSessionConfig(HttpListenerRequest req, HttpListenerResponse res)
	{
		string json;
		using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
			json = sr.ReadToEnd();

		string botUrl = ExtractJsonString(json, "botUrl");
		string secret = ExtractJsonString(json, "secret");

		if (!string.IsNullOrEmpty(botUrl))
			_sessionBotUrl = botUrl.TrimEnd('/');
		// Allow secret to be set to empty string to clear it
		if (secret != null)
			_sessionBotSecret = secret;

		// Update live radio channel list (always safe to call — no-ops if no channels key)
		RadioIntegration.UpdateChannelsFromJson(json);

		Main.ModEntry.Logger.Log(
			$"[GRDNConnect] Session config received — url={_sessionBotUrl ?? "(unchanged)"}");
		SendJson(res, 200, "{\"ok\":true}");
	}

	// ── GET /client-config ───────────────────────────────────────────────────
	// Served by the HOST's mod so clients can auto-fetch botUrl + secret.
	// NOT behind the IsHostOrSingleplayer guard — that's the whole point.
	// Returns null fields if no session has been started yet (client retries).
	private void HandleClientConfig(HttpListenerResponse res)
	{
		string url    = _sessionBotUrl    ?? Main.Settings.BotPushUrl ?? "";
		string secret = _sessionBotSecret ?? Main.Settings.BotSecret  ?? "";

		if (string.IsNullOrEmpty(url))
		{
			// Host hasn't run /session start yet — tell clients to retry
			SendJson(res, 200, "{\"botUrl\":null,\"secret\":null}");
			return;
		}

		SendJson(res, 200,
			$"{{\"botUrl\":\"{Escape(url)}\",\"secret\":\"{Escape(secret)}\"}}");
	}

	// ── Server IP discovery (for client auto-fetch) ───────────────────────────
	// Tries to find the host's IP via reflection into dv-multiplayer / Mirror.
	// Returns null if undeterminable — caller retries later.
	private static string TryGetServerAddress()
	{
		try
		{
			const BindingFlags bf =
				BindingFlags.Public | BindingFlags.NonPublic |
				BindingFlags.Instance | BindingFlags.Static;

			foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				string asmName = asm.GetName().Name;

				if (asmName.StartsWith("Multiplayer", StringComparison.OrdinalIgnoreCase))
				{
					// Approach A: Multiplayer.Multiplayer.Settings — has host IP the client typed in
					Type mainType = asm.GetType("Multiplayer.Multiplayer");
					object settings = mainType?.GetField("Settings", bf)?.GetValue(null);
					if (settings != null)
					{
						Type st = settings.GetType();
						foreach (var fn in new[] {
							"ServerAddress", "ServerIp", "ServerIP", "IpAddress",
							"Address", "Ip", "IP", "Host", "HostAddress",
							"LastServerIp", "LastServerAddress", "RemoteEndPoint" })
						{
							string v = st.GetField(fn, bf)?.GetValue(settings)?.ToString()
							        ?? st.GetProperty(fn, bf)?.GetValue(settings)?.ToString();
							if (!string.IsNullOrEmpty(v) &&
								v != "localhost" && v != "127.0.0.1") return v;
						}
					}

					// Approach B: MultiplayerAPI.Instance — the public dv-multiplayer API
					Type apiType = null;
					try
					{
						foreach (Type t in asm.GetExportedTypes())
							if (t.Name == "MultiplayerAPI") { apiType = t; break; }
					}
					catch { }

					if (apiType != null)
					{
						object inst = apiType.GetProperty("Instance", bf)?.GetValue(null);
						if (inst != null)
						{
							var it = inst.GetType();
							foreach (var fn in new[] {
								"ServerAddress", "NetworkAddress", "ServerIp",
								"HostAddress", "ConnectionAddress" })
							{
								string v = it.GetProperty(fn, bf)?.GetValue(inst)?.ToString()
								        ?? it.GetField(fn, bf)?.GetValue(inst)?.ToString();
								if (!string.IsNullOrEmpty(v) &&
									v != "localhost" && v != "127.0.0.1") return v;
							}
						}
					}
				}

				// Approach C: Mirror.NetworkManager.singleton.networkAddress
				if (asmName.Equals("Mirror", StringComparison.OrdinalIgnoreCase))
				{
					Type nmType = asm.GetType("Mirror.NetworkManager");
					if (nmType != null)
					{
						object singleton = nmType.GetProperty("singleton", bf)?.GetValue(null);
						if (singleton != null)
						{
							string v = nmType.GetField("networkAddress", bf)?.GetValue(singleton)?.ToString();
							if (!string.IsNullOrEmpty(v) &&
								v != "localhost" && v != "127.0.0.1") return v;
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Warning("[GRDNConnect] TryGetServerAddress: " + ex.Message);
		}
		return null;
	}

	// ── Auto-fetch client config loop ─────────────────────────────────────────
	// Runs in the background for every player. Exits immediately if host or if
	// config is already available. Otherwise queries host:port/client-config
	// every 15 s until it gets a valid botUrl or gives up after ~10 minutes.
	private IEnumerator FetchClientConfigLoop()
	{
		// Small delay — let the multiplayer session fully connect first
		yield return new WaitForSeconds(8f);

		for (int attempt = 0; attempt < 40; attempt++)
		{
			// Already have config (from UMM settings or a prior session push)
			if (!string.IsNullOrEmpty(ActiveBotUrl)) yield break;

			// We're hosting or in singleplayer — bot pushes to us directly, no need to fetch
			if (IsHostOrSingleplayer()) yield break;

			string serverIp = TryGetServerAddress();
			if (!string.IsNullOrEmpty(serverIp))
			{
				string url = $"http://{serverIp}:{Main.Settings.Port}/client-config";
				Main.ModEntry.Logger.Log($"[GRDNConnect] Client: fetching session config from host at {url}");

				using (var req = UnityWebRequest.Get(url))
				{
					req.timeout = 5;
					yield return req.SendWebRequest();

					if (req.error == null)
					{
						string botUrl = ExtractJsonString(req.downloadHandler.text, "botUrl");
						string secret = ExtractJsonString(req.downloadHandler.text, "secret");

						if (!string.IsNullOrEmpty(botUrl))
						{
							_sessionBotUrl    = botUrl.TrimEnd('/');
							if (secret != null) _sessionBotSecret = secret;
							Main.ModEntry.Logger.Log(
								$"[GRDNConnect] Client session config received — botUrl={_sessionBotUrl}");
							yield break;  // success
						}
						// botUrl null = host hasn't started a session yet — keep retrying
						Main.ModEntry.Logger.Log(
							"[GRDNConnect] Host has no session config yet — retrying in 15 s");
					}
					else
					{
						Main.ModEntry.Logger.Warning(
							$"[GRDNConnect] Client config fetch failed ({req.responseCode}): {req.error}");
					}
				}
			}
			else
			{
				Main.ModEntry.Logger.Warning(
					"[GRDNConnect] Could not determine server address — retrying in 15 s");
			}

			yield return new WaitForSeconds(15f);
		}

		Main.ModEntry.Logger.Warning(
			"[GRDNConnect] Client config auto-fetch gave up. " +
			"Set BotPushUrl in UMM Settings as a manual fallback.");
	}

	private void SendJson(HttpListenerResponse res, int code, string json)
	{
		res.StatusCode = code;
		res.ContentType = "application/json";
		byte[] bytes = Encoding.UTF8.GetBytes(json);
		res.ContentLength64 = bytes.Length;
		res.OutputStream.Write(bytes, 0, bytes.Length);
		res.OutputStream.Close();
	}

	private string GetJobField(Job job, params string[] names)
	{
		Type type = ((object)job).GetType();
		foreach (string name in names)
		{
			FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
			{
				object value = field.GetValue(job);
				if (value != null)
				{
					return value.ToString();
				}
			}
			PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property != null)
			{
				object value2 = property.GetValue(job);
				if (value2 != null)
				{
					return value2.ToString();
				}
			}
		}
		return null;
	}

	private string GetChainDataField(Job job, string fieldName)
	{
		try
		{
			Type type = ((object)job).GetType();
			PropertyInfo chainDataProp = type.GetProperty("chainData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (chainDataProp == null) return null;
			object chainData = chainDataProp.GetValue(job);
			if (chainData == null) return null;
			PropertyInfo field = chainData.GetType().GetProperty(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field == null) return null;
			return field.GetValue(chainData)?.ToString();
		}
		catch { return null; }
	}

	private string ExtractJsonString(string json, string key)
	{
		int num = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
		if (num < 0)
		{
			return null;
		}
		num = json.IndexOf(':', num);
		if (num < 0)
		{
			return null;
		}
		num = json.IndexOf('"', num);
		if (num < 0)
		{
			return null;
		}
		int num2 = json.IndexOf('"', num + 1);
		if (num2 < 0)
		{
			return null;
		}
		return json.Substring(num + 1, num2 - num - 1);
	}

	private string Escape(string s)
	{
		return s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
	}

	// -------------------------------------------------------------------------
	// GET /debug-locos
	// Human-readable dump of the job→GUID→loco matching process.
	// Hit this in a browser while in-game to diagnose why jobs aren't appearing.
	private void HandleDebugLocos(HttpListenerResponse res)
	{
		var sb = new StringBuilder();
		try
		{
			const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
			                      | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

			// ── OLD APPROACH: GUID map from task trees (kept for comparison) ──────
			sb.AppendLine("=== OLD APPROACH: task-tree GUID map ===");
			var carGuidToJobs = new Dictionary<string, List<Job>>();
			var activeJobs = JobCompletionHelper.GetCurrentJobsForApi();
			if (activeJobs != null)
			{
				foreach (var job in activeJobs)
				{
					var guids = GetCarGuidsFromJob(job).ToList();
					sb.AppendLine($"JOB {job.ID} ({job.jobType}) → {guids.Count} car GUIDs: {string.Join(", ", guids.Select(g => g.Substring(0, Math.Min(8, g.Length)) + "..."))}");
					foreach (var g in guids)
					{
						if (!carGuidToJobs.TryGetValue(g, out var list))
							carGuidToJobs[g] = list = new List<Job>();
						if (!list.Contains(job)) list.Add(job);
					}
				}
			}
			else
			{
				sb.AppendLine("activeJobs is NULL");
			}
			sb.AppendLine($"Total GUIDs in map: {carGuidToJobs.Count}");

			// ── NEW APPROACH: car → logicCar → Job field scan ────────────────────
			sb.AppendLine("\n=== NEW APPROACH: logicCar field scan ===");
			TrainCar[] allCars = UnityEngine.Object.FindObjectsOfType<TrainCar>();
			foreach (var loco in allCars)
			{
				if (!loco.IsLoco) continue;
				sb.AppendLine($"\nLOCO: {loco.ID} | trainset cars: {loco.trainset?.cars?.Count ?? 0}");
				if (loco.trainset?.cars == null) { sb.AppendLine("  (no trainset)"); continue; }

				foreach (var coupled in loco.trainset.cars)
				{
					if (coupled == null) { sb.AppendLine("  car: null"); continue; }
					var guid = coupled.logicCar?.carGuid ?? "(no logicCar)";
					var shortGuid = guid.Length > 8 ? guid.Substring(0, 8) + "..." : guid;
					var oldMatch = carGuidToJobs.ContainsKey(guid);

					// New scan: find Job refs directly on logicCar
					var foundJobs = new List<string>();
					if (coupled.logicCar != null)
					{
						var lc  = coupled.logicCar;
						var lct = ((object)lc).GetType();
						foreach (var fi in lct.GetFields(bf))
						{
							try { if (fi.GetValue(lc) is Job j) foundJobs.Add($"{fi.Name}={j.ID}"); }
							catch { }
						}
						foreach (var pi in lct.GetProperties(bf))
						{
							try { if (pi.GetValue(lc) is Job j) foundJobs.Add($"{pi.Name}={j.ID}"); }
							catch { }
						}
					}

					string newResult = foundJobs.Count > 0
						? string.Join(", ", foundJobs)
						: "none";
					sb.AppendLine($"  car: {coupled.ID ?? "?"} | guid: {shortGuid} | old: {oldMatch} | new: {newResult}");
				}
			}
		}
		catch (Exception ex)
		{
			sb.AppendLine("ERROR: " + ex.Message + "\n" + ex.StackTrace);
		}

		res.StatusCode = 200;
		res.ContentType = "text/plain";
		var bytes = Encoding.UTF8.GetBytes(sb.ToString());
		res.ContentLength64 = bytes.Length;
		res.OutputStream.Write(bytes, 0, bytes.Length);
		res.OutputStream.Close();
	}

	// GET /locos
	// Returns every locomotive in the world with the jobs currently on its
	// trainset, matched by car GUID through the active job task trees.
	// Shape: [{ locoId, locoType, jobs: [{ jobId, type, departure, destination }] }]
	// -------------------------------------------------------------------------

	private void HandleGetLocos(HttpListenerResponse res)
	{
		var sb = new StringBuilder();
		sb.Append("[");
		bool firstLoco = true;

		try
		{
			// When live train board is disabled, return locos with empty job arrays
			// so the bot falls back to manual /assign entries.
			Dictionary<string, List<Job>> carGuidToJobs = null;
			if (Main.Settings.LiveTrainBoardEnabled)
			{
				carGuidToJobs = new Dictionary<string, List<Job>>();
				var activeJobs = JobCompletionHelper.GetCurrentJobsForApi();
				if (activeJobs != null)
				{
					foreach (var job in activeJobs)
					{
						foreach (var guid in GetCarGuidsFromJob(job))
						{
							if (!carGuidToJobs.TryGetValue(guid, out var jlist))
								carGuidToJobs[guid] = jlist = new List<Job>();
							if (!jlist.Contains(job)) jlist.Add(job);
						}
					}
				}
				Main.ModEntry.Logger.Log($"[GRDNConnect] /locos: {carGuidToJobs.Count} car GUID(s) mapped");
			}

			TrainCar[] allCars = UnityEngine.Object.FindObjectsOfType<TrainCar>();
			foreach (var loco in allCars)
			{
				if (!loco.IsLoco) continue;

				var seenJobIds = new HashSet<string>();
				var locoJobs   = new List<Job>();

				if (carGuidToJobs != null && loco.trainset?.cars != null)
				{
					foreach (var car in loco.trainset.cars)
					{
						var guid = car?.logicCar?.carGuid;
						if (string.IsNullOrEmpty(guid)) continue;
						if (carGuidToJobs.TryGetValue(guid, out var jobs))
							foreach (var job in jobs)
								if (seenJobIds.Add(job.ID)) locoJobs.Add(job);
					}
				}

				if (!firstLoco) sb.Append(",");
				firstLoco = false;

				string locoId   = Escape(loco.ID ?? loco.logicCar?.carGuid ?? "unknown");
				string locoType = Escape(((object)loco.carType).ToString());

				sb.Append($"{{\"locoId\":\"{locoId}\",\"locoType\":\"{locoType}\",\"jobs\":[");
				bool firstJob = true;
				foreach (var job in locoJobs)
				{
					if (!firstJob) sb.Append(",");
					firstJob = false;
					string dep   = Escape(GetChainDataField(job, "chainOriginYardId")      ?? "—");
					string des   = Escape(GetChainDataField(job, "chainDestinationYardId") ?? "—");
					string jt    = Escape(((object)job.jobType).ToString());
					string state = Escape(JobCompletionHelper.GetJobState(job) ?? "Unknown");
					string trk   = Escape(GetDestinationTrack(job) ?? "—");
					string cargo = Escape(GetJobCargoType(job)      ?? "—");
					sb.Append($"{{\"jobId\":\"{Escape(job.ID)}\",\"type\":\"{jt}\",\"state\":\"{state}\"," +
					          $"\"departure\":\"{dep}\",\"destination\":\"{des}\"," +
					          $"\"track\":\"{trk}\",\"cargo\":\"{cargo}\"}}");
				}
				sb.Append("]}");
			}
		}
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Error("[GRDNConnect] /locos error: " + ex.Message);
		}

		sb.Append("]");
		SendJson(res, 200, sb.ToString());
	}

	/// <summary>
	/// Walks the job's task tree and returns the relevant destination track ID.
	/// For ShuntingLoad/ShuntingUnload: returns the LAST destination track found
	/// (where cars end up after loading/unloading, not the facility spot).
	/// For all other types: returns the FIRST destination track found.
	/// </summary>
	private string GetDestinationTrack(Job job)
	{
		try
		{
			const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
			                      | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
			Type jobType = ((object)job).GetType();
			object taskList = jobType.GetField("tasks", bf)?.GetValue(job)
			               ?? jobType.GetProperty("tasks", bf)?.GetValue(job);
			if (!(taskList is IEnumerable tasks)) return null;

			// SL/SU: last task destination is where cars end up after the operation,
			// not the intermediate loading/unloading facility track.
			string jobTypeName = ((object)job.jobType).ToString();
			bool isShunting = jobTypeName == "ShuntingLoad" || jobTypeName == "ShuntingUnload";
			return isShunting
				? FindLastTrackInTasks(tasks, bf)
				: FindTrackInTasks(tasks, bf);
		}
		catch { }
		return null;
	}

	// Returns the FIRST destinationTrack encountered (depth-first).
	private string FindTrackInTasks(IEnumerable tasks, BindingFlags bf)
	{
		foreach (var task in tasks)
		{
			if (task == null) continue;
			Type t = task.GetType();

			// TransportTask.destinationTrack → Track.ID → TrackID.FullDisplayID
			object trackObj = t.GetField("destinationTrack", bf)?.GetValue(task)
			               ?? t.GetProperty("destinationTrack", bf)?.GetValue(task);
			if (trackObj != null)
			{
				string full = ExtractFullDisplayID(trackObj, bf);
				if (!string.IsNullOrEmpty(full)) return full;
			}

			// Recurse into SequentialTasks.tasks / ParallelTasks.tasks
			object sub = t.GetField("tasks", bf)?.GetValue(task)
			          ?? t.GetProperty("tasks", bf)?.GetValue(task);
			if (sub is IEnumerable subTasks)
			{
				string found = FindTrackInTasks(subTasks, bf);
				if (found != null) return found;
			}
		}
		return null;
	}

	// Returns the LAST destinationTrack encountered (depth-first, keeps going).
	private string FindLastTrackInTasks(IEnumerable tasks, BindingFlags bf)
	{
		string last = null;
		foreach (var task in tasks)
		{
			if (task == null) continue;
			Type t = task.GetType();

			object trackObj = t.GetField("destinationTrack", bf)?.GetValue(task)
			               ?? t.GetProperty("destinationTrack", bf)?.GetValue(task);
			if (trackObj != null)
			{
				string full = ExtractFullDisplayID(trackObj, bf);
				if (!string.IsNullOrEmpty(full)) last = full;
			}

			object sub = t.GetField("tasks", bf)?.GetValue(task)
			          ?? t.GetProperty("tasks", bf)?.GetValue(task);
			if (sub is IEnumerable subTasks)
			{
				string found = FindLastTrackInTasks(subTasks, bf);
				if (found != null) last = found;
			}
		}
		return last;
	}

	// Extracts Track.ID.FullDisplayID from a Track object via reflection.
	private string ExtractFullDisplayID(object trackObj, BindingFlags bf)
	{
		object trackId = trackObj.GetType().GetProperty("ID", bf)?.GetValue(trackObj)
		              ?? trackObj.GetType().GetField("<ID>k__BackingField", bf)?.GetValue(trackObj);
		return trackId?.GetType().GetProperty("FullDisplayID", bf)?.GetValue(trackId)?.ToString();
	}

	/// <summary>
	/// Returns a human-readable cargo type string from the job's tasks.
	/// Checks WarehouseTask.cargoType first (ShuntingLoad/Unload), then
	/// TransportTask.transportedCargoPerCar for haul jobs.
	/// </summary>
	private string GetJobCargoType(Job job)
	{
		try
		{
			const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
			                      | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
			Type jobType = ((object)job).GetType();
			object taskList = jobType.GetField("tasks", bf)?.GetValue(job)
			               ?? jobType.GetProperty("tasks", bf)?.GetValue(job);
			if (taskList is IEnumerable tasks)
				return FindCargoInTasks(tasks, bf);
		}
		catch { }
		return null;
	}

	private string FindCargoInTasks(IEnumerable tasks, BindingFlags bf)
	{
		foreach (var task in tasks)
		{
			if (task == null) continue;
			Type t = task.GetType();

			// WarehouseTask.cargoType (ShuntingLoad / ShuntingUnload)
			object cargo = t.GetField("cargoType", bf)?.GetValue(task)
			            ?? t.GetProperty("cargoType", bf)?.GetValue(task);
			if (cargo != null)
			{
				string s = cargo.ToString();
				if (s != "None" && s != "0") return s;
			}

			// TransportTask.transportedCargoPerCar — grab first non-empty entry
			object perCar = t.GetField("transportedCargoPerCar", bf)?.GetValue(task);
			if (perCar is IEnumerable perCarList)
			{
				foreach (var entry in perCarList)
				{
					if (entry == null) continue;
					var et = entry.GetType();
					object ct = et.GetField("cargoType", bf)?.GetValue(entry)
					         ?? et.GetProperty("cargoType", bf)?.GetValue(entry);
					if (ct != null)
					{
						string s = ct.ToString();
						if (s != "None" && s != "0") return s;
					}
				}
			}

			// Recurse
			object sub = t.GetField("tasks", bf)?.GetValue(task)
			          ?? t.GetProperty("tasks", bf)?.GetValue(task);
			if (sub is IEnumerable subTasks)
			{
				string found = FindCargoInTasks(subTasks, bf);
				if (found != null) return found;
			}
		}
		return null;
	}

	/// <summary>
	/// Returns all logic-car carGuids associated with a Job.
	///
	/// Primary path: job.jobChainController.carsForJobChain
	///   Set at chain creation for every job type (Transport, ShuntingLoad, etc.)
	///   — no task-tree traversal needed, works regardless of accept state.
	///
	/// Fallback: recursive task-tree scan (works for Transport / WarehouseTask
	///   which expose a 'cars' field; kept for safety).
	/// </summary>
	private IEnumerable<string> GetCarGuidsFromJob(Job job)
	{
		var guids = new List<string>();
		try
		{
			const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
			                      | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
			Type jobType = ((object)job).GetType();

			// ── Primary: jobChainController.carsForJobChain ──────────────────────
			object jcc = jobType.GetField("jobChainController", bf)?.GetValue(job)
			          ?? jobType.GetProperty("jobChainController", bf)?.GetValue(job);

			if (jcc != null)
			{
				Type jccType = jcc.GetType();
				object carsObj = jccType.GetField("carsForJobChain", bf)?.GetValue(jcc)
				              ?? jccType.GetProperty("carsForJobChain", bf)?.GetValue(jcc);

				if (carsObj is IEnumerable carsList)
				{
					foreach (var car in carsList)
					{
						if (car == null) continue;
						Type ct = car.GetType();
						string g = ct.GetProperty("carGuid", bf)?.GetValue(car)?.ToString()
						        ?? ct.GetField("carGuid",    bf)?.GetValue(car)?.ToString();
						if (!string.IsNullOrEmpty(g) && g.Length >= 8) guids.Add(g);
					}
				}

				if (guids.Count > 0)
				{
					Main.ModEntry.Logger.Log($"[GRDNConnect] Job '{job.ID}': {guids.Count} car(s) via carsForJobChain");
					return guids;
				}
			}

			// ── Fallback: recursive task-tree scan ────────────────────────────────
			object taskList = jobType.GetField("tasks", bf)?.GetValue(job)
			               ?? jobType.GetProperty("tasks", bf)?.GetValue(job);

			if (taskList is IEnumerable tasks)
				foreach (var task in tasks)
					ExtractCarGuids(task, guids, 0);

			Main.ModEntry.Logger.Log($"[GRDNConnect] Job '{job.ID}': {guids.Count} car(s) via task-tree fallback");
		}
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Error("[GRDNConnect] GetCarGuidsFromJob: " + ex.Message);
		}
		return guids;
	}

	/// <summary>
	/// Recursively extracts carGuid strings from a task object (and any sub-tasks).
	/// Scans every field and property on the task — no hardcoded field names needed.
	/// For each enumerable member, tries to read carGuid directly (Car/logic layer)
	/// or via logicCar.carGuid (TrainCar/physical layer). When an item is neither
	/// (e.g. a CarsPerTrack wrapper), recurses one level deeper into that item.
	/// </summary>
	private void ExtractCarGuids(object task, List<string> guids, int depth = 0)
	{
		if (task == null || depth > 5) return;
		try
		{
			Type t = task.GetType();
			const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
			                      | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

			// Scan every field and property — no guessing on names
			var members = t.GetFields(bf).Cast<MemberInfo>()
			               .Concat(t.GetProperties(bf).Cast<MemberInfo>());

			foreach (var member in members)
			{
				object val;
				try
				{
					val = member is FieldInfo fi
					    ? fi.GetValue(task)
					    : ((PropertyInfo)member).GetValue(task);
				}
				catch { continue; }

				if (val == null || val is string) continue;
				if (!(val is IEnumerable items)) continue;

				foreach (var item in items)
				{
					if (item == null) continue;
					var ct = item.GetType();

					// Direct carGuid (Car / logic layer)
					string g = ct.GetProperty("carGuid", bf)?.GetValue(item)?.ToString()
					        ?? ct.GetField("carGuid",    bf)?.GetValue(item)?.ToString();

					// Fallback: TrainCar (physical layer) → logicCar.carGuid
					if (string.IsNullOrEmpty(g))
					{
						object lc = ct.GetProperty("logicCar", bf)?.GetValue(item)
						         ?? ct.GetField("logicCar",    bf)?.GetValue(item);
						if (lc != null)
						{
							var lct = lc.GetType();
							g = lct.GetProperty("carGuid", bf)?.GetValue(lc)?.ToString()
							 ?? lct.GetField("carGuid",    bf)?.GetValue(lc)?.ToString();
						}
					}

					if (!string.IsNullOrEmpty(g) && g.Length >= 8)
					{
						guids.Add(g);
					}
					else if (!(item is UnityEngine.Object) && !(item is string))
					{
						// Wrapper object (e.g. CarsPerTrack) — look inside it for Car lists
						ExtractCarGuids(item, guids, depth + 1);
					}
				}
			}

			// Recurse into nested task lists (SequentialTasks, ParallelTasks, etc.)
			var sub = t.GetMethod("GetTaskList",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (sub?.Invoke(task, null) is IEnumerable subTasks)
				foreach (var subTask in subTasks)
					ExtractCarGuids(subTask, guids, depth + 1);
		}
		catch { }
	}
}
