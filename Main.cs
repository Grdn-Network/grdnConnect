using UnityEngine;
using UnityModManagerNet;

public class Main
{
	public static ModEntry ModEntry;

	public static Settings Settings;

	private static GameObject _hostObject;

	public static bool Load(ModEntry modEntry)
	{
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Expected O, but got Unknown
		ModEntry = modEntry;
		ModEntry.OnUnload = Unload;
		ModEntry.OnGUI = OnGUI;
		ModEntry.OnSaveGUI = OnSaveGUI;
		Settings = ModSettings.Load<Settings>(modEntry);
		_hostObject = new GameObject("GRDNConnect_Host");
		Object.DontDestroyOnLoad((Object)(object)_hostObject);
		_hostObject.AddComponent<GRDNConnectBehaviour>();
		ModEntry.Logger.Log($"[GRDNConnect] Loaded. HTTP server starting on port {Settings.Port}.");
		return true;
	}

	private static void OnGUI(ModEntry modEntry)
	{
		Extensions.Draw<Settings>(Settings, modEntry);
	}

	private static void OnSaveGUI(ModEntry modEntry)
	{
		((ModSettings)Settings).Save(modEntry);
	}

	private static bool Unload(ModEntry modEntry)
	{
		if ((Object)(object)_hostObject != (Object)null)
		{
			Object.Destroy((Object)(object)_hostObject);
		}
		ModEntry.Logger.Log("[GRDNConnect] Unloaded.");
		return true;
	}
}
