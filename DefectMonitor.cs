// DefectMonitor.cs
// Polls every active locomotive for defects (overheating wheels, hotboxes, etc.)
// and POSTs to the bot's /defect-alert endpoint when a new defect is detected.
//
// Only runs while BotPushUrl is configured.
// Attach as a MonoBehaviour component alongside GRDNConnectBehaviour.
//
// DV defect model:
//   TrainCar → DamagableTrainCar component → damage state per car component.
//   We check for wheelsHot (hotbox) and other notable damage flags.
//   Each unique (locoId + defectType) is tracked so we only fire once per incident.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

public class DefectMonitor : MonoBehaviour
{
    // How often to poll for defects (seconds)
    private const float POLL_INTERVAL = 10f;

    // Track which (trainNumber + defectType) combos have already been reported
    // so we don't spam the same alert. Cleared when the loco is no longer defective.
    private readonly HashSet<string> _activeAlerts = new HashSet<string>();

    private void Start()
    {
        StartCoroutine(PollLoop());
    }

    // ── Poll loop ─────────────────────────────────────────────────────────────

    private IEnumerator PollLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(POLL_INTERVAL);

            string pushUrl = Main.Settings.BotPushUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(pushUrl)) continue;

            try
            {
                CheckAllLocos(pushUrl);
            }
            catch (Exception ex)
            {
                Main.ModEntry.Logger.Warning("[DefectMonitor] Poll error: " + ex.Message);
            }
        }
    }

    // ── Defect scan ───────────────────────────────────────────────────────────

    private void CheckAllLocos(string pushUrl)
    {
        var allCars = UnityEngine.Object.FindObjectsOfType<TrainCar>();
        var currentAlerts = new HashSet<string>();

        foreach (var car in allCars)
        {
            if (!car.IsLoco) continue;

            string trainNumber = ExtractTrainNumber(car.ID);
            if (string.IsNullOrEmpty(trainNumber)) continue;

            // Check for defects via DamagableTrainCar
            var defects = GetDefects(car);
            foreach (var (defectType, location) in defects)
            {
                string key = $"{trainNumber}:{defectType}";
                currentAlerts.Add(key);

                if (_activeAlerts.Contains(key)) continue; // already reported

                _activeAlerts.Add(key);
                Main.ModEntry.Logger.Log($"[DefectMonitor] New defect on train {trainNumber}: {defectType}");
                StartCoroutine(PostDefectAlert(pushUrl, trainNumber, defectType, location));
            }
        }

        // Clear alerts for defects that have been resolved
        _activeAlerts.RemoveWhere(k => !currentAlerts.Contains(k));
    }

    /// <summary>
    /// Inspects a TrainCar and returns any active defects.
    /// Returns a list of (defectType, location?) tuples.
    /// </summary>
    private List<(string type, string location)> GetDefects(TrainCar car)
    {
        var results = new List<(string, string)>();

        try
        {
            // DamagableTrainCar holds per-component damage state
            var damageable = car.GetComponent<DamagableTrainCar>();
            if (damageable == null) return results;

            // Check each bogie for wheel damage (hotbox = overheated wheels)
            // The DV API for damage state can vary — we use reflection to stay safe.
            var bogies = car.Bogies;
            if (bogies != null)
            {
                for (int i = 0; i < bogies.Length; i++)
                {
                    var bogie = bogies[i];
                    if (bogie == null) continue;

                    // Check for WheelslipController overheat or Bogie damage
                    var wsCtrl = bogie.GetComponentInChildren<WheelslipController>();
                    if (wsCtrl != null)
                    {
                        // WheelslipController.IsOverheated or similar
                        bool overheated = IsOverheated(wsCtrl);
                        if (overheated)
                            results.Add(("Hotbox", $"bogie {i + 1}"));
                    }
                }
            }

            // Check overall damage level — fire if critically damaged
            float damageLevel = GetDamageLevel(damageable);
            if (damageLevel > 0.85f) // >85% damage
                results.Add(("Critical damage", null));
        }
        catch (Exception ex)
        {
            Main.ModEntry.Logger.Warning($"[DefectMonitor] GetDefects error on {car.ID}: " + ex.Message);
        }

        return results;
    }

    // ── DV API helpers (reflection-safe) ─────────────────────────────────────

    private bool IsOverheated(WheelslipController ctrl)
    {
        if (ctrl == null) return false;
        try
        {
            // Try common property names — adjust if DV version differs
            var t = ((object)ctrl).GetType();
            foreach (var name in new[] { "IsOverheated", "isOverheated", "WheelsOverheated" })
            {
                var prop = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop != null) return (bool)prop.GetValue(ctrl);
                var field = t.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null) return (bool)field.GetValue(ctrl);
            }
        }
        catch { }
        return false;
    }

    private float GetDamageLevel(DamagableTrainCar damageable)
    {
        try
        {
            var t = ((object)damageable).GetType();
            foreach (var name in new[] { "currentDamagePercentage", "DamagePercentage", "damagePercentage" })
            {
                var prop = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop != null) return Convert.ToSingle(prop.GetValue(damageable));
                var field = t.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null) return Convert.ToSingle(field.GetValue(damageable));
            }
        }
        catch { }
        return 0f;
    }

    // ── HTTP push ─────────────────────────────────────────────────────────────

    private IEnumerator PostDefectAlert(string pushUrl, string trainNumber, string defectType, string location)
    {
        string secret = Main.Settings.BotSecret ?? "";
        string url    = pushUrl + "/defect-alert";

        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append($"\"trainNumber\":\"{Esc(trainNumber)}\",");
        sb.Append($"\"defectType\":\"{Esc(defectType)}\"");
        if (!string.IsNullOrEmpty(location))
            sb.Append($",\"location\":\"{Esc(location)}\"");
        sb.Append("}");

        byte[] raw = Encoding.UTF8.GetBytes(sb.ToString());

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler   = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(secret))
                req.SetRequestHeader("x-secret", secret);

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
                Main.ModEntry.Logger.Warning($"[DefectMonitor] Push failed: {req.error}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ExtractTrainNumber(string locoId)
    {
        if (string.IsNullOrEmpty(locoId)) return null;
        var m = Regex.Match(locoId, @"(\d+)$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Esc(string s) =>
        s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
}
