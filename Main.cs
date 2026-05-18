using UnityEngine;
using UnityModManagerNet;

public class Main
{
	public static UnityModManager.ModEntry ModEntry;

	public static Settings Settings;

	private static GameObject _hostObject;

	public static bool Load(UnityModManager.ModEntry modEntry)
	{
		ModEntry = modEntry;
		ModEntry.OnUnload = Unload;
		ModEntry.OnGUI = OnGUI;
		ModEntry.OnSaveGUI = OnSaveGUI;
		Settings = UnityModManager.ModSettings.Load<Settings>(modEntry);
		_hostObject = new GameObject("GRDNConnect_Host");
		Object.DontDestroyOnLoad((Object)(object)_hostObject);
		_hostObject.AddComponent<GRDNConnectBehaviour>();
		ModEntry.Logger.Log($"[GRDNConnect] Loaded. HTTP server starting on port {Settings.Port}.");
		return true;
	}

	private static void OnGUI(UnityModManager.ModEntry modEntry)
	{
		Extensions.Draw<Settings>(Settings, modEntry);
	}

	private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
	{
		((UnityModManager.ModSettings)Settings).Save(modEntry);
	}

	private static bool Unload(UnityModManager.ModEntry modEntry)
	{
		if ((Object)(object)_hostObject != (Object)null)
		{
			Object.Destroy((Object)(object)_hostObject);
		}
		ModEntry.Logger.Log("[GRDNConnect] Unloaded.");
		return true;
	}
}
