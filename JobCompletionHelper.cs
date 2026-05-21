using System;
using System.Collections.Generic;
using DV.InventorySystem;
using DV.Logic.Job;
using DV.RailTrack;
using DV.ThingTypes;
using DV.Utils;
using UnityEngine;

public static class JobCompletionHelper
{
	public static bool TryCompleteJob(string jobId)
	{
		try
		{
			JobsManager instance = SingletonBehaviour<JobsManager>.Instance;
			if ((UnityEngine.Object)(object)instance == (UnityEngine.Object)null)
			{
				Debug.LogError((object)"[GRDNConnect] JobsManager.Instance is null.");
				return false;
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
					return true;
				}

				// Relaxed completion: re-attempt after moving cars to destination tracks.
				// Off by default — opt-in via UMM settings.
				if (Main.Settings.RelaxedJobCompletion)
				{
					Main.ModEntry.Logger.Log($"[GRDNConnect] Relaxed mode: attempting force-complete for {jobId}");
					if (TryForceCompleteRelaxed(instance, currentJob))
					{
						Pay(wage, jobId);
						return true;
					}
				}

				Debug.LogWarning((object)$"[GRDNConnect] TryToCompleteAJob returned {val} for {jobId}. Cars may not be at destination.");
				return false;
			}
			Debug.LogWarning((object)("[GRDNConnect] Job not found: " + jobId));
			return false;
		}
		catch (Exception ex)
		{
			Debug.LogError((object)("[GRDNConnect] TryCompleteJob threw: " + ex.Message));
			return false;
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
