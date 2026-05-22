// DefectMonitor.cs
// Polls active locomotives for operational defects and POSTs voice alerts
// to the bot's /defect-alert endpoint.
//
// DEFECTS MONITORED
// ─────────────────
//   Hot Box         — wheel bearing overheat (WheelslipController / DamagableTrainCar)
//   Derailment      — TrainCar.derailed flag
//   Air Hose        — brake pipe pressure loss on a coupled train
//   Dragging Equip  — general damage exceeds 60% threshold (DamagableTrainCar)
//
// RANDOM CONSIST CHECK
// ─────────────────────
//   Every 60 minutes, each active loco has a 10% chance of receiving a
//   "no defects" advisory call that includes car count and speed.
//
// AUDIO FORMAT
// ─────────────
//   Messages are written to mimic CSX MicroHBD CPU 3 defect detector style:
//   "Attention. Train, zero three four. GRDN detector. Hot box detected.
//    Rear truck. Reduce speed and inspect. End of message."
//
//   Digits are spelled out individually for proper TTS cadence.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

public class DefectMonitor : MonoBehaviour
{
    // ── Timing ────────────────────────────────────────────────────────────────
    private const float DEFECT_POLL_SEC  = 10f;   // how often to scan for defects
    private const float CONSIST_CHECK_SEC = 3600f; // 1 hour
    private const float CONSIST_CHANCE    = 0.10f; // 10 % chance per loco per hour

    // ── State ─────────────────────────────────────────────────────────────────
    // Track active (train+type) alerts so we only fire once per incident.
    // Cleared when the defect resolves.
    private readonly HashSet<string> _fired = new HashSet<string>();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        StartCoroutine(DefectPollLoop());
        StartCoroutine(ConsistCheckLoop());
    }

    // ═════════════════════════════════════════════════════════════════════════
    // DEFECT POLL
    // ═════════════════════════════════════════════════════════════════════════

    private IEnumerator DefectPollLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(DEFECT_POLL_SEC);

            string pushUrl = Main.Settings.BotPushUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(pushUrl)) continue;

            var allCars = UnityEngine.Object.FindObjectsOfType<TrainCar>();
            var nowFired = new HashSet<string>();

            foreach (var car in allCars)
            {
                if (!car.IsLoco) continue;

                string train = ExtractNumber(car.ID);
                if (string.IsNullOrEmpty(train)) continue;

                foreach (var (defectKey, defectType, detail) in ScanDefects(car, train))
                {
                    nowFired.Add(defectKey);

                    if (_fired.Contains(defectKey)) continue; // already alerted
                    _fired.Add(defectKey);

                    string msg = BuildDefectMessage(train, defectType, detail);
                    Main.ModEntry.Logger.Log($"[Defect] {defectKey} — alerting");
                    StartCoroutine(PostAlert(pushUrl, train, defectType, detail, msg));
                }
            }

            // Clear resolved defects so they can fire again if they recur
            _fired.RemoveWhere(k => !nowFired.Contains(k));
        }
    }

    // ── Defect detection ──────────────────────────────────────────────────────
    // Returns list of (uniqueKey, defectType, humanDetail) tuples.

    private List<(string key, string type, string detail)> ScanDefects(TrainCar loco, string train)
    {
        var results = new List<(string, string, string)>();
        const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
                              | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        // ── Derailment ────────────────────────────────────────────────────────
        // Check TrainCar.derailed — highest priority, alert immediately.
        try
        {
            bool derailed = GetBool(loco, bf, "derailed", "Derailed", "IsDerailed");
            if (derailed)
                results.Add(($"{train}:derail", "Derailment", null));
        }
        catch { }

        // ── Hot Box ───────────────────────────────────────────────────────────
        // Check bogie/wheel overheat via WheelslipController or damage components.
        try
        {
            var bogies = loco.Bogies;
            if (bogies != null)
            {
                for (int i = 0; i < bogies.Length; i++)
                {
                    var wsCtrl = bogies[i]?.GetComponentInChildren<WheelslipController>();
                    if (wsCtrl == null) continue;
                    bool hot = GetBool(wsCtrl, bf,
                        "IsOverheated", "isOverheated", "WheelsOverheated",
                        "wheelsOverheated", "overheated");
                    if (hot)
                        results.Add(($"{train}:hotbox:{i}", "Hot Box", i == 0 ? "front truck" : "rear truck"));
                }
            }
        }
        catch { }

        // Fallback: check DamagableTrainCar for wheel-bearing-specific damage
        try
        {
            var dmg = loco.GetComponent<DamagableTrainCar>();
            if (dmg != null)
            {
                float wheelDmg = GetFloat(dmg, bf,
                    "wheelBearingsDamage", "WheelBearingsDamage", "wheelsHeat");
                if (wheelDmg > 0.75f) // > 75 % wheel bearing damage
                    results.Add(($"{train}:hotbox:brg", "Hot Box", "wheel bearing"));
            }
        }
        catch { }

        // ── Air Hose / Brake Pipe ─────────────────────────────────────────────
        // A brake pipe pressure anomaly on a coupled train suggests an air hose issue.
        try
        {
            var brakes = loco.brakeSystem;
            if (brakes != null && (loco.trainset?.cars?.Count ?? 1) > 1)
            {
                float pipePsi = GetFloat(brakes, bf,
                    "brakePipePressure", "BrakePipePressure",
                    "mainReservoirPressure", "MainReservoirPressure");
                // Pressure significantly below working value (typically ~90 psi / 6 bar)
                // suggests an open air hose or parted train
                if (pipePsi > 0f && pipePsi < 40f)
                    results.Add(($"{train}:airhose", "Air Hose Defect", null));
            }
        }
        catch { }

        // ── Dragging Equipment / General Damage ───────────────────────────────
        // Overall damage > 60 % on any car in the consist → dragging equipment warning.
        try
        {
            if (loco.trainset?.cars != null)
            {
                foreach (var car in loco.trainset.cars)
                {
                    if (car == null) continue;
                    var dmg = car.GetComponent<DamagableTrainCar>();
                    if (dmg == null) continue;

                    float level = GetFloat(dmg, bf,
                        "currentDamagePercentage", "DamagePercentage",
                        "damagePercentage", "damage");
                    if (level > 0.60f)
                    {
                        string carId = car.ID ?? "unknown";
                        results.Add(($"{train}:drag:{carId}", "Dragging Equipment", $"car {ExtractNumber(carId)}"));
                        break; // one alert per consist per poll
                    }
                }
            }
        }
        catch { }

        return results;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // RANDOM CONSIST CHECK (10 % per hour)
    // ═════════════════════════════════════════════════════════════════════════

    private IEnumerator ConsistCheckLoop()
    {
        // Stagger first check so it doesn't fire immediately on load
        yield return new WaitForSeconds(CONSIST_CHECK_SEC);

        var rng = new System.Random();

        while (true)
        {
            string pushUrl = Main.Settings.BotPushUrl?.TrimEnd('/');
            if (!string.IsNullOrEmpty(pushUrl))
            {
                var allCars = UnityEngine.Object.FindObjectsOfType<TrainCar>();
                foreach (var loco in allCars)
                {
                    if (!loco.IsLoco) continue;
                    if (rng.NextDouble() > CONSIST_CHANCE) continue;

                    string train    = ExtractNumber(loco.ID);
                    int    carCount = loco.trainset?.cars?.Count ?? 1;
                    int    speedMph = Mathf.RoundToInt(Mathf.Abs(loco.GetForwardSpeed()) * 2.237f);

                    string msg = BuildConsistMessage(train, carCount, speedMph);
                    Main.ModEntry.Logger.Log($"[Defect] Consist check — train {train}, {carCount} cars, {speedMph} mph");
                    StartCoroutine(PostAlert(pushUrl, train, "Consist Check", null, msg));
                }
            }

            yield return new WaitForSeconds(CONSIST_CHECK_SEC);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // MESSAGE FORMATTING  (CSX MicroHBD CPU 3 style)
    // ═════════════════════════════════════════════════════════════════════════

    private static string BuildDefectMessage(string train, string defectType, string detail)
    {
        string trainSpoken = SpeakDigits(train);
        string action      = ActionFor(defectType);
        string body        = detail != null
            ? $"{defectType} detected. {Capitalise(detail)}."
            : $"{defectType} detected.";

        return $"Attention. Train, {trainSpoken}. G R D N detector. {body} {action}. End of message.";
    }

    private static string BuildConsistMessage(string train, int carCount, int speedMph)
    {
        string trainSpoken = SpeakDigits(train);
        string cars        = carCount == 1 ? "one car" : $"{carCount} cars";
        string speed       = speedMph > 0 ? $" Speed, {speedMph}." : "";
        return $"Train, {trainSpoken}. G R D N detector. No defects detected. {cars}.{speed} End of message.";
    }

    private static string ActionFor(string defectType)
    {
        switch (defectType)
        {
            case "Derailment":         return "Stop immediately and contact dispatch";
            case "Hot Box":            return "Reduce speed and inspect";
            case "Air Hose Defect":    return "Check brake line and reduce speed";
            case "Dragging Equipment": return "Stop train and inspect consist";
            default:                   return "Reduce speed and inspect";
        }
    }

    /// <summary>Spell each digit individually: "034" → "zero three four"</summary>
    private static readonly string[] _digitWords =
        { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };

    private static string SpeakDigits(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var parts = new List<string>();
        foreach (char c in s)
            if (char.IsDigit(c)) parts.Add(_digitWords[c - '0']);
        return string.Join(" ", parts);
    }

    private static string Capitalise(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

    // ═════════════════════════════════════════════════════════════════════════
    // HTTP push
    // ═════════════════════════════════════════════════════════════════════════

    private IEnumerator PostAlert(string pushUrl, string train, string defectType, string detail, string message)
    {
        string secret = Main.Settings.BotSecret ?? "";
        string url    = pushUrl + "/defect-alert";

        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append($"\"trainNumber\":\"{Esc(train)}\",");
        sb.Append($"\"defectType\":\"{Esc(defectType)}\",");
        sb.Append($"\"message\":\"{Esc(message)}\"");
        if (!string.IsNullOrEmpty(detail))
            sb.Append($",\"detail\":\"{Esc(detail)}\"");
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
                Main.ModEntry.Logger.Warning($"[Defect] Push failed: {req.error}");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Reflection helpers — safe property/field readers
    // ═════════════════════════════════════════════════════════════════════════

    private static bool GetBool(object obj, BindingFlags bf, params string[] names)
    {
        var t = obj.GetType();
        foreach (var name in names)
        {
            var prop  = t.GetProperty(name, bf);
            if (prop  != null) return (bool)prop.GetValue(obj);
            var field = t.GetField(name, bf);
            if (field != null) return (bool)field.GetValue(obj);
        }
        return false;
    }

    private static float GetFloat(object obj, BindingFlags bf, params string[] names)
    {
        var t = obj.GetType();
        foreach (var name in names)
        {
            var prop  = t.GetProperty(name, bf);
            if (prop  != null) return Convert.ToSingle(prop.GetValue(obj));
            var field = t.GetField(name, bf);
            if (field != null) return Convert.ToSingle(field.GetValue(obj));
        }
        return 0f;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Utility
    // ═════════════════════════════════════════════════════════════════════════

    private static string ExtractNumber(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var m = Regex.Match(id, @"(\d+)$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Esc(string s) =>
        s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") ?? "";
}
