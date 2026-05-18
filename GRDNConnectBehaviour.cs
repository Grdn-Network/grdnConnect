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
			HandleRequest(ctx);
		}
	}

	private void HandleRequest(HttpListenerContext ctx)
	{
		string text = ctx.Request.Url.AbsolutePath.ToLower();
		try
		{
			if (ctx.Request.HttpMethod == "GET" && text == "/jobs")
			{
				HandleGetJobs(ctx.Response);
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
				string s = GetJobField(item, "startingStation", "startStation", "originStation") ?? "—";
				string s2 = GetJobField(item, "finishStation", "destinationStation", "endStation") ?? "—";
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
