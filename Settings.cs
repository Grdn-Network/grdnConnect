using UnityModManagerNet;

public class Settings : UnityModManager.ModSettings, IDrawable
{
	[Draw("Network Port (1024-65535)")]
	public int Port = 7230;

	[Draw("Live Train Board — pull job/track data from game (disable for hardcore/manual ops)")]
	public bool LiveTrainBoardEnabled = true;

	[Draw("Relaxed Job Completion — /complete works at destination station, not just the exact track")]
	public bool RelaxedJobCompletion = false;

	public override void Save(UnityModManager.ModEntry modEntry)
	{
		UnityModManager.ModSettings.Save<Settings>(this, modEntry);
	}

	public void OnChange()
	{
	}
}
