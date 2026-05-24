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

		// Hotbox / defect monitor — always active; skips pushes when no bot URL
		// is available (URL is pushed by the bot on /session start, not at load time)
		_hostObject.AddComponent<DefectMonitor>();

		// Car-miles stats tracker — polls every 5 s, flushes to bot every 60 s.
		// Host-only; exits silently when no bot URL is available.
		_hostObject.AddComponent<StatsTracker>();

		ModEntry.Logger.Log($"[GRDNConnect] Loaded. HTTP server starting on port {Settings.Port}.");
		return true;
	}

	private static void OnGUI(UnityModManager.ModEntry modEntry)
	{
		UnityModManagerNet.Extensions.Draw<Settings>(Settings, modEntry);
	}

	private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
	{
		((UnityModManager.ModSettings)Settings).Save(modEntry);
	}

	private static bool Unload(UnityModManager.ModEntry modEntry)
	{
		if ((Object)(object)_hostObject != (Object)null)
		{
			// Stop the HTTP server NOW — Object.Destroy() is deferred to end-of-frame.
			// Without this, if Load() is called in the same frame (e.g. UMM mod reload),
			// the old listener is still bound to the port when the new one tries to start.
			_hostObject.GetComponent<GRDNConnectBehaviour>()?.StopListener();
			Object.Destroy((Object)(object)_hostObject);
			_hostObject = null;
		}
		ModEntry.Logger.Log("[GRDNConnect] Unloaded.");
		return true;
	}
}
