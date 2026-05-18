using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using DV.Logic.Job;
using DV.ThingTypes;
using UnityEngine;

public class GRDNConnectBehaviour : MonoBehaviour
{
	private HttpListener _listener;

	private void Start()
	{
		int port = Main.Settings.Port;
		_listener = new HttpListener();
		_listener.Prefixes.Add($"http://*:{port}/");
		try
		{
			_listener.Start();
			Main.ModEntry.Logger.Log($"[GRDNConnect] Listening on port {port}.");
			((MonoBehaviour)this).StartCoroutine(ListenLoop());
		}
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Error("[GRDNConnect] Failed to start: " + ex.Message);
		}
	}

	/// <summary>
	/// Returns true if this game instance is the server host, or if DVMP is not loaded (singleplayer).
	/// Uses reflection so GRDNConnect has zero compile-time dependency on dv-multiplayer.
	/// </summary>
	private bool IsHostOrSingleplayer()
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
		if (_listener != null && _listener.IsListening)
		{
			_listener.Stop();
			_listener.Close();
		}
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
			// Reject requests if this player is not the host/singleplayer.
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
			else if (ctx.Request.HttpMethod == "POST" && text == "/complete-job")
			{
				HandleCompleteJob(ctx.Request, ctx.Response);
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
		bool flag = JobCompletionHelper.TryCompleteJob(text);
		SendJson(res, flag ? 200 : 404, "{\"ok\":" + (flag ? "true" : "false") + ",\"jobId\":\"" + Escape(text) + "\"}");
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
}
