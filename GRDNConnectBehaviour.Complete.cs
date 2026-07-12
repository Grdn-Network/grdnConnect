// GRDNConnectBehaviour.Complete.cs
// Slice 1 of the /complete + /activate overhaul (grdnConnect#19).
// Resolves "the job attached to a given train's consist" so completion can be
// driven by a train number (supplied by the bot / radio / chat) instead of a raw
// job ID. Identity is resolved upstream as steamId -> Discord -> assigned train,
// so this never reads the caller's live loco (a player may step out of the cab).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using DV.Logic.Job;
using DV.ThingTypes;
using UnityEngine;

public partial class GRDNConnectBehaviour
{
	// ── Loco lookup cache (perf: avoid a full-scene scan per resolve; see #7) ──
	private static TrainCar[] _locoCache;
	private static float _locoCacheTime = -999f;
	private const float LOCO_CACHE_TTL = 2f;

	// All locomotives, cached briefly so repeated resolves don't re-scan the scene.
	private static TrainCar[] GetLocosCached()
	{
		if (_locoCache != null && Time.realtimeSinceStartup - _locoCacheTime < LOCO_CACHE_TTL)
			return _locoCache;
		var locos = new List<TrainCar>();
		foreach (var tc in UnityEngine.Object.FindObjectsOfType<TrainCar>())
			if (tc != null && tc.IsLoco) locos.Add(tc);
		_locoCache = locos.ToArray();
		_locoCacheTime = Time.realtimeSinceStartup;
		return _locoCache;
	}

	// Trailing-digit train number from an ID, e.g. "L-034" -> "034". Null if none.
	private static string ExtractTrainNumber(string id)
	{
		if (string.IsNullOrEmpty(id)) return null;
		var m = System.Text.RegularExpressions.Regex.Match(id, @"(\d+)$");
		return m.Success ? m.Groups[1].Value : null;
	}

	// Same train, ignoring leading zeros ("034" == "34").
	private static bool TrainNumbersMatch(string a, string b)
	{
		if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
		return a == b || a.TrimStart('0') == b.TrimStart('0');
	}

	// Resolves the job attached to the consist of the loco with this train number.
	// Returns the jobId, or null if no matching loco/job. Uses the same car-GUID
	// matching as /locos so the result is consistent with the train board.
	internal string ResolveJobIdForTrain(string trainNumber)
	{
		if (string.IsNullOrEmpty(trainNumber)) return null;
		try
		{
			var carGuidToJobs = new Dictionary<string, List<Job>>();
			var activeJobs = JobCompletionHelper.GetCurrentJobsForApi();
			if (activeJobs != null)
				foreach (var job in activeJobs)
					foreach (var guid in GetCarGuidsFromJob(job))
					{
						if (!carGuidToJobs.TryGetValue(guid, out var jl))
							carGuidToJobs[guid] = jl = new List<Job>();
						if (!jl.Contains(job)) jl.Add(job);
					}

			foreach (var loco in GetLocosCached())
			{
				if (loco == null || !loco.IsLoco) continue;
				if (!TrainNumbersMatch(ExtractTrainNumber(loco.ID), trainNumber)) continue;
				if (loco.trainset?.cars == null) return null;

				foreach (var car in loco.trainset.cars)
				{
					var guid = car?.logicCar?.carGuid;
					if (string.IsNullOrEmpty(guid)) continue;
					if (carGuidToJobs.TryGetValue(guid, out var jobs) && jobs.Count > 0)
					{
						Main.LogVerbose($"[GRDNConnect] Train {trainNumber} -> job {jobs[0].ID}");
						return jobs[0].ID;
					}
				}
				Main.LogVerbose($"[GRDNConnect] Train {trainNumber}: loco found but no job on its consist");
				return null;
			}
			Main.LogVerbose($"[GRDNConnect] Train {trainNumber}: no matching loco in the world");
			return null;
		}
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Warning("[GRDNConnect] ResolveJobIdForTrain: " + ex.Message);
			return null;
		}
	}

	// Resolve the job on the consist that this car belongs to (any car in it).
	// Used by the in-game chat command, which has the sender's OccupiedCar directly.
	internal string ResolveJobIdForConsist(TrainCar anyCar)
	{
		if (anyCar?.trainset?.cars == null) return null;
		try
		{
			var carGuidToJobs = new Dictionary<string, List<Job>>();
			var activeJobs = JobCompletionHelper.GetCurrentJobsForApi();
			if (activeJobs != null)
				foreach (var job in activeJobs)
					foreach (var guid in GetCarGuidsFromJob(job))
					{
						if (!carGuidToJobs.TryGetValue(guid, out var jl))
							carGuidToJobs[guid] = jl = new List<Job>();
						if (!jl.Contains(job)) jl.Add(job);
					}

			foreach (var car in anyCar.trainset.cars)
			{
				var guid = car?.logicCar?.carGuid;
				if (string.IsNullOrEmpty(guid)) continue;
				if (carGuidToJobs.TryGetValue(guid, out var jobs) && jobs.Count > 0)
					return jobs[0].ID;
			}
			return null;
		}
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Warning("[GRDNConnect] ResolveJobIdForConsist: " + ex.Message);
			return null;
		}
	}

	// Train number for a consist: trailing digits of the first loco in it.
	internal string TrainNumberOfConsist(TrainCar anyCar)
	{
		if (anyCar == null) return null;
		if (anyCar.trainset?.cars != null)
			foreach (var c in anyCar.trainset.cars)
				if (c != null && c.IsLoco)
				{
					var n = ExtractTrainNumber(c.ID);
					if (!string.IsNullOrEmpty(n)) return n;
				}
		return ExtractTrainNumber(anyCar.ID);
	}

	// ── POST /activate-job ────────────────────────────────────────────────────
	// Body: { jobId } or { trainNumber }. Starts/activates the job on that train's
	// consist. Resolution mirrors /complete-job. The actual activation lives in
	// JobCompletionHelper.TryActivateJob and NEEDS IN-GAME VERIFICATION of DV's
	// take-job method.
	private void HandleActivateJob(HttpListenerRequest req, HttpListenerResponse res)
	{
		string json;
		using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
			json = sr.ReadToEnd();

		string jobId = ExtractJsonString(json, "jobId");
		string trainNumber = ExtractJsonString(json, "trainNumber");
		if (string.IsNullOrEmpty(jobId) && !string.IsNullOrEmpty(trainNumber))
		{
			jobId = ResolveJobIdForTrain(trainNumber);
			if (string.IsNullOrEmpty(jobId))
			{
				SendJson(res, 404, "{\"ok\":false,\"error\":\"No job found on train " + Escape(trainNumber) + " consist\"}");
				return;
			}
		}
		if (string.IsNullOrEmpty(jobId))
		{
			SendJson(res, 400, "{\"ok\":false,\"error\":\"Missing jobId or trainNumber\"}");
			return;
		}

		var (ok, error) = JobCompletionHelper.TryActivateJob(jobId);
		var sb = new StringBuilder();
		sb.Append("{");
		sb.Append($"\"ok\":{(ok ? "true" : "false")}");
		sb.Append($",\"jobId\":\"{Escape(jobId)}\"");
		if (!string.IsNullOrEmpty(error)) sb.Append($",\"error\":\"{Escape(error)}\"");
		sb.Append("}");
		SendJson(res, ok ? 200 : 409, sb.ToString());
	}
}
