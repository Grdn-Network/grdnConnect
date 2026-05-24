using System;
using System.Collections.Generic;
using DV.InventorySystem;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using UnityEngine;

public static class JobCompletionHelper
{
	/// <summary>
	/// Attempts to complete a job by ID.
	/// Returns (true, null, wage) on success, or (false, reason, 0f) on failure.
	/// </summary>
	public static (bool ok, string error, float wage) TryCompleteJob(string jobId)
	{
		try
		{
			JobsManager instance = SingletonBehaviour<JobsManager>.Instance;
			if ((UnityEngine.Object)(object)instance == (UnityEngine.Object)null)
			{
				Debug.LogError((object)"[GRDNConnect] JobsManager.Instance is null.");
				return (false, "JobsManager is not available — is the game in a loaded save?", 0f);
			}

			foreach (Job currentJob in instance.currentJobs)
			{
				if (currentJob.ID != jobId)
					continue;

				float wage = currentJob.GetWageForTheJob();
				JobState val = instance.TryToCompleteAJob(currentJob);

				if ((int)val == 2)
				{
					Pay(wage, jobId);
					TryAdvanceJobChain(currentJob);
					return (true, null, wage);
				}

				// Relaxed completion: re-attempt after moving cars to destination tracks.
				// Off by default — opt-in via UMM settings.
				if (Main.Settings.RelaxedJobCompletion)
				{
					Main.ModEntry.Logger.Log($"[GRDNConnect] Relaxed mode: attempting force-complete for {jobId}");
					if (TryForceCompleteRelaxed(instance, currentJob))
					{
						Pay(wage, jobId);
						TryAdvanceJobChain(currentJob);
						return (true, null, wage);
					}
					return (false, "Cars are not yet at the destination track (relaxed completion also failed)", 0f);
				}

				Debug.LogWarning((object)$"[GRDNConnect] TryToCompleteAJob returned {val} for {jobId}.");
				string reason = (int)val == 1
					? "Cars are not yet spotted to the required destination track"
					: $"Job validator returned state '{val}' — cars may not be in the correct position";
				return (false, reason, 0f);
			}

			Debug.LogWarning((object)("[GRDNConnect] Job not found: " + jobId));
			return (false, "Job not found — it may already be completed, expired, or the ID is wrong", 0f);
		}
		catch (Exception ex)
		{
			Debug.LogError((object)("[GRDNConnect] TryCompleteJob threw: " + ex.Message));
			return (false, ex.Message, 0f);
		}
	}

	/// <summary>
	/// Relaxed completion: teleport each job car to its task's destination track,
	/// then run the normal validator. For when cars are in the right yard but not
	/// spotted to the exact track the job requires.
	/// </summary>
	private static bool TryForceCompleteRelaxed(JobsManager mgr, Job job)
	{
		try
		{
			const System.Reflection.BindingFlags bf =
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy;

			Type jobType = ((object)job).GetType();
			object taskList = jobType.GetField("tasks", bf)?.GetValue(job)
			               ?? jobType.GetProperty("tasks", bf)?.GetValue(job);
			if (!(taskList is System.Collections.IEnumerable tasks)) return false;

			var moves = new List<(Car car, Track dest)>();
			CollectCarMoves(tasks, bf, moves);

			if (moves.Count == 0)
			{
				Debug.LogWarning((object)"[GRDNConnect] Relaxed: no car→track moves found");
				return false;
			}

			foreach (var (car, dest) in moves)
			{
				var ct = ((object)car).GetType();
				var trackField = ct.GetField("_currentTrack", bf);
				if (trackField == null) continue;

				Track oldTrack = car.CurrentTrack;
				if (oldTrack != null)
				{
					var ot = ((object)oldTrack).GetType();
					(ot.GetField("currentCarsFullyOnTrack",    bf)?.GetValue(oldTrack) as System.Collections.IList)?.Remove(car);
					(ot.GetField("currentCarsPartiallyOnTrack", bf)?.GetValue(oldTrack) as System.Collections.IList)?.Remove(car);
				}

				trackField.SetValue(car, dest);

				if (dest != null)
				{
					var dt = ((object)dest).GetType();
					(dt.GetField("currentCarsFullyOnTrack", bf)?.GetValue(dest) as System.Collections.IList)?.Add(car);
				}

				Main.ModEntry.Logger.Log($"[GRDNConnect] Relaxed: spotted {car.ID} → {dest?.ID?.FullDisplayID ?? "?"}");
			}

			JobState val = mgr.TryToCompleteAJob(job);
			return (int)val == 2;
		}
		catch (Exception ex)
		{
			Debug.LogError((object)("[GRDNConnect] TryForceCompleteRelaxed: " + ex.Message));
			return false;
		}
	}

	private static void CollectCarMoves(System.Collections.IEnumerable tasks,
		System.Reflection.BindingFlags bf, List<(Car, Track)> moves)
	{
		foreach (var task in tasks)
		{
			if (task == null) continue;
			Type t = task.GetType();

			object destObj = t.GetField("destinationTrack", bf)?.GetValue(task)
			              ?? t.GetProperty("destinationTrack", bf)?.GetValue(task);
			object carsObj = t.GetField("cars", bf)?.GetValue(task)
			              ?? t.GetProperty("cars", bf)?.GetValue(task);

			if (destObj is Track dest && carsObj is System.Collections.IEnumerable cars)
				foreach (var car in cars)
					if (car is Car c) moves.Add((c, dest));

			object sub = t.GetField("tasks", bf)?.GetValue(task)
			          ?? t.GetProperty("tasks", bf)?.GetValue(task);
			if (sub is System.Collections.IEnumerable subTasks)
				CollectCarMoves(subTasks, bf, moves);
		}
	}

	/// <summary>
	/// Advances the job chain after a successful completion.
	/// Reflects into the job's chain GameObject, finds the chain controller component,
	/// and calls its job-completed handler — which activates the next job in the chain
	/// (e.g. ShuntingUnload that follows a Transport).
	/// Safe to call even if TryToCompleteAJob already fired state-change events,
	/// because if the chain was already advanced the next job will already be active
	/// and the call will be a no-op or log a warning via DV's own guard.
	/// </summary>
	private static void TryAdvanceJobChain(Job job)
	{
		const System.Reflection.BindingFlags bf =
			System.Reflection.BindingFlags.Public    | System.Reflection.BindingFlags.NonPublic  |
			System.Reflection.BindingFlags.Instance  | System.Reflection.BindingFlags.FlattenHierarchy;

		try
		{
			var jobType = ((object)job).GetType();

			// ── Primary: jobChainController — confirmed field name from DV internals ─
			// It is a MonoBehaviour component, NOT a GameObject.
			// Call the completion handler on it directly (same approach used by GetCarGuidsFromJob).
			object jcc = jobType.GetField("jobChainController", bf)?.GetValue(job)
			          ?? jobType.GetProperty("jobChainController", bf)?.GetValue(job);

			if (jcc != null)
			{
				var jccType = jcc.GetType();
				foreach (var methodName in new[] { "JobCompleted", "OnJobCompleted", "CompleteJob", "HandleJobCompletion", "AdvanceChain" })
				{
					var m = jccType.GetMethod(methodName, bf, null, new Type[] { jobType }, null)
					     ?? jccType.GetMethod(methodName, bf, null, Type.EmptyTypes, null);
					if (m == null) continue;

					object[] args = m.GetParameters().Length > 0 ? new object[] { job } : null;
					m.Invoke(jcc, args);
					Main.ModEntry.Logger.Log($"[GRDNConnect] Chain advanced: {jccType.Name}.{methodName}({job.ID})");
					return;
				}
				// Controller found but no method matched — continue to GO fallback
				Main.ModEntry.Logger.Warning($"[GRDNConnect] Chain advance: {jcc.GetType().Name} found but no handler method matched for {job.ID}");
			}

			// ── Fallback: get the chain GameObject and walk its components ────────────
			// Handles the case where jcc was found but had no matching method (walks siblings),
			// and any future variant where the controller is stored as a GO reference.
			GameObject chainGO = null;

			// If jcc is a MonoBehaviour, its gameObject may host sibling controllers
			if (jcc is MonoBehaviour jccMb && jccMb != null)
				chainGO = jccMb.gameObject;

			// Also try any remaining GO/MB field name variants
			if (chainGO == null)
			{
				foreach (var name in new[] { "jobChainGO", "chainGO", "chainGameObject" })
				{
					var f   = jobType.GetField(name, bf);
					object fval = f?.GetValue(job);
					if (fval is GameObject go1 && go1 != null) { chainGO = go1; break; }
					if (fval is MonoBehaviour mb1 && mb1 != null) { chainGO = mb1.gameObject; break; }

					var p   = jobType.GetProperty(name, bf);
					object pval = p?.GetValue(job);
					if (pval is GameObject go2 && go2 != null) { chainGO = go2; break; }
					if (pval is MonoBehaviour mb2 && mb2 != null) { chainGO = mb2.gameObject; break; }
				}
			}

			if (chainGO == null)
			{
				Main.ModEntry.Logger.Warning($"[GRDNConnect] Chain advance: no chain controller or chainGO found on {job.ID}");
				return;
			}

			foreach (var comp in chainGO.GetComponents<MonoBehaviour>())
			{
				if (comp == null) continue;
				var t = comp.GetType();

				foreach (var methodName in new[] { "JobCompleted", "OnJobCompleted", "CompleteJob", "HandleJobCompletion", "AdvanceChain" })
				{
					var m = t.GetMethod(methodName, bf, null, new Type[] { jobType }, null)
					     ?? t.GetMethod(methodName, bf, null, Type.EmptyTypes, null);
					if (m == null) continue;

					object[] args = m.GetParameters().Length > 0 ? new object[] { job } : null;
					m.Invoke(comp, args);
					Main.ModEntry.Logger.Log($"[GRDNConnect] Chain advanced (GO scan): {t.Name}.{methodName}({job.ID})");
					return;
				}
			}

			Main.ModEntry.Logger.Warning($"[GRDNConnect] Chain advance: no handler method found for {job.ID}");
		}
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Warning($"[GRDNConnect] TryAdvanceJobChain: {ex.Message}");
		}
	}

	private static void Pay(float wage, string jobId)
	{
		Inventory inv = SingletonBehaviour<Inventory>.Instance;
		if ((UnityEngine.Object)(object)inv != (UnityEngine.Object)null)
		{
			inv.SetMoney(inv.PlayerMoney + (double)wage);
			Debug.Log((object)$"[GRDNConnect] Job {jobId} completed. Paid ${wage} to player.");
		}
		else
		{
			Debug.LogWarning((object)"[GRDNConnect] Job completed but Inventory.Instance is null — player not paid.");
		}
	}

	public static IEnumerable<Job> GetCurrentJobsForApi()
	{
		return SingletonBehaviour<JobsManager>.Instance?.currentJobs;
	}

	public static string GetJobState(Job job)
	{
		return ((job != null) ? ((object)job.State).ToString() : null) ?? "Unknown";
	}
}
