using UnityModManagerNet;

public class Settings : ModSettings, IDrawable
{
	[Draw("Network Port (1024-65535)")]
	public int Port = 7230;

	public override void Save(ModEntry modEntry)
	{
		ModSettings.Save<Settings>(this, modEntry);
	}

	public void OnChange()
	{
	}
}
