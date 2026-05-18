using System;
using System.Collections.Generic;
using DV.InventorySystem;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using UnityEngine;

public static class JobCompletionHelper
{
	public static bool TryCompleteJob(string jobId)
	{
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Invalid comparison between Unknown and I4
		//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
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
				{
					continue;
				}
				float wageForTheJob = currentJob.GetWageForTheJob();
				JobState val = instance.TryToCompleteAJob(currentJob);
				if ((int)val == 2)
				{
					Inventory instance2 = SingletonBehaviour<Inventory>.Instance;
					if ((UnityEngine.Object)(object)instance2 != (UnityEngine.Object)null)
					{
						instance2.SetMoney(instance2.PlayerMoney + (double)wageForTheJob);
						Debug.Log((object)$"[GRDNConnect] Job {jobId} completed. Paid ${wageForTheJob} to player.");
					}
					else
					{
						Debug.LogWarning((object)"[GRDNConnect] Job completed but Inventory.Instance is null — player not paid.");
					}
					return true;
				}
				Debug.LogWarning((object)$"[GRDNConnect] TryToCompleteAJob returned {val} for job {jobId}. Job may not meet completion requirements.");
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

	public static IEnumerable<Job> GetCurrentJobsForApi()
	{
		return SingletonBehaviour<JobsManager>.Instance?.currentJobs;
	}

	public static string GetJobState(Job job)
	{
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		return ((job != null) ? ((object)job.State/*cast due to constrained. prefix*/).ToString() : null) ?? "Unknown";
	}
}
