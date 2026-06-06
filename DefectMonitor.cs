// DefectMonitor.cs
// Polls active locomotives for operational defects and POSTs voice alerts
// to the bot's /defect-alert endpoint.
//
// DEFECTS MONITORED
// ─────────────────
//   Hot Box         — wheel bearing overheat (WheelslipController / DamagableTrainCar)
//   Derailment      — TrainCar.derailed flag
//   Dragging Equip  — general damage exceeds 85% threshold (DamagableTrainCar)
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
    private readonly Dictionary<string, DateTime> _cooldowns = new Dictionary<string, DateTime>();
    private readonly System.Random _rng = new System.Random();

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

            // Scan every poll — even without a session URL — so _fired stays
            // current with pre-existing defects. That way, when a session starts
            // and the URL becomes available, we don't flood the bot with alerts
            // for airhoses/derails that were already present at load time.
            string pushUrl = GRDNConnectBehaviour.ActiveBotUrl?.TrimEnd('/');
            bool   canPost = !string.IsNullOrEmpty(pushUrl);

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

                    if (_fired.Contains(defectKey))
                    {
                        if (!_cooldowns.TryGetValue(defectKey, out DateTime lastFired)) continue;
                        double elapsed = (DateTime.UtcNow - lastFired).TotalMinutes;
                        if (elapsed < 12.0 || _rng.NextDouble() > 0.08) continue;
                    }
                    _fired.Add(defectKey);
                    _cooldowns[defectKey] = DateTime.UtcNow;

                    if (!canPost)
                    {
                        // No session yet — track silently so it won't alert when session starts
                        Main.ModEntry.Logger.Log($"[Defect] {defectKey} — pre-session, suppressed");
                        continue;
                    }

                    string msg = BuildDefectMessage(train, defectType, detail);
                    Main.ModEntry.Logger.Log($"[Defect] {defectKey} — alerting");
                    StartCoroutine(PostAlert(pushUrl, train, defectType, detail, msg));
                }
            }

            // Clear resolved defects so they can re-alert if they recur
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
                    var wsCtrl = bogies[i] == null ? null : GetComponentInChildrenByTypeName(bogies[i], "WheelslipController");
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
            var dmg = GetComponentByTypeName(loco, "DamagableTrainCar");
            if (dmg != null)
            {
                float wheelDmg = GetFloat(dmg, bf,
                    "wheelBearingsDamage", "WheelBearingsDamage", "wheelsHeat");
                if (wheelDmg > 0.75f) // > 75 % wheel bearing damage
                    results.Add(($"{train}:hotbox:brg", "Hot Box", "wheel bearing"));
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
                    var dmg = GetComponentByTypeName(car, "DamagableTrainCar");
                    if (dmg == null) continue;

                    float level = GetFloat(dmg, bf,
                        "currentDamagePercentage", "DamagePercentage",
                        "damagePercentage", "damage");
                    if (level > 0.85f)
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
            string pushUrl = GRDNConnectBehaviour.ActiveBotUrl?.TrimEnd('/');
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

                    string msg          = BuildConsistMessage(train, carCount, speedMph);
                    string consistDetail = $"{carCount} {speedMph}";  // bot uses this to stitch clips
                    Main.ModEntry.Logger.Log($"[Defect] Consist check — train {train}, {carCount} cars, {speedMph} mph");
                    StartCoroutine(PostAlert(pushUrl, train, "Consist Check", consistDetail, msg));
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
        string secret = GRDNConnectBehaviour.ActiveBotSecret ?? "";
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

            if (req.error != null)
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
    // Reflection-based component lookup
    // Avoids compile-time hard deps on DV.BrakeSystem.dll and wherever
    // WheelslipController / DamagableTrainCar live in the game assemblies.
    // ═════════════════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

    private static Type FindTypeByName(string name)
    {
        if (_typeCache.TryGetValue(name, out Type cached)) return cached;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(name, false);
                if (t != null) { _typeCache[name] = t; return t; }
            }
            catch { }
        }
        _typeCache[name] = null;
        return null;
    }

    private static Component GetComponentByTypeName(Component host, string typeName)
    {
        var type = FindTypeByName(typeName);
        return type != null ? host.GetComponent(type) : null;
    }

    private static Component GetComponentInChildrenByTypeName(Component host, string typeName)
    {
        var type = FindTypeByName(typeName);
        return type != null ? host.GetComponentInChildren(type) : null;
    }

    /// <summary>Returns the value of a named property or field, or null if not found.</summary>
    private static object GetMemberValue(object obj, BindingFlags bf, string name)
    {
        var t     = obj.GetType();
        var prop  = t.GetProperty(name, bf);
        if (prop  != null) return prop.GetValue(obj);
        var field = t.GetField(name, bf);
        return field?.GetValue(obj);
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
