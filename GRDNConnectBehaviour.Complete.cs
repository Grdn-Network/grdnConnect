// GRDNConnectBehaviour.Complete.cs
// Slice 1 of the /complete + /activate overhaul (grdnConnect#19).
// Resolves "the job attached to a given train's consist" so completion can be
// driven by a train number (supplied by the bot / radio / chat) instead of a raw
// job ID. Identity is resolved upstream as steamId -> Discord -> assigned train,
// so this never reads the caller's live loco (a player may step out of the cab).
using System;
using System.Collections.Generic;
using System.Linq;
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
	private string ResolveJobIdForTrain(string trainNumber)
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
}
