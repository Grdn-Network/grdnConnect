using UnityModManagerNet;

public class Settings : UnityModManager.ModSettings, IDrawable
{
	[Draw("Network Port (1024-65535)")]
	public int Port = 7230;

	public override void Save(UnityModManager.ModEntry modEntry)
	{
		UnityModManager.ModSettings.Save<Settings>(this, modEntry);
	}

	public void OnChange()
	{
	}
}
