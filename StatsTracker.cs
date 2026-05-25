// StatsTracker.cs
// Polls active locomotives every 5 seconds, accumulates car-miles (speed × time × carCount),
// and flushes the totals to the bot's POST /stats-push endpoint every 60 seconds.
//
// Miles metric: each train accumulates (speed in m/s) × (poll interval) converted to miles.
// Tracks how far the locomotive itself travelled — not weighted by consist size.
//
// Only runs on the session host (IsHostOrSingleplayer guard). Clients never push stats —
// the host sees the full world and is authoritative.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

public class StatsTracker : MonoBehaviour
{
    // ── Timing ────────────────────────────────────────────────────────────────
    private const float POLL_SEC  = 5f;    // speed-sample interval
    private const float FLUSH_SEC = 60f;   // push-to-bot interval

    // Metres per mile — used to convert accumulated metres to miles
    private const float M_PER_MILE  = 1609.344f;

    // Minimum speed to register movement (filters out physics drift when "stopped")
    private const float MIN_SPEED_MS = 0.5f;   // ~1.8 km/h

    // ── State ─────────────────────────────────────────────────────────────────
    // Keyed by train number (digits extracted from loco.ID).
    // Values are car-miles accumulated since the last flush.
    private readonly Dictionary<string, float> _accumulated = new Dictionary<string, float>();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        StartCoroutine(PollLoop());
        StartCoroutine(FlushLoop());
    }

    // ═════════════════════════════════════════════════════════════════════════
    // POLL — sample every loco's speed + consist size every POLL_SEC seconds
    // ═════════════════════════════════════════════════════════════════════════

    private IEnumerator PollLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(POLL_SEC);

            // Clients never own the stats — only the host can see all locos
            if (!GRDNConnectBehaviour.IsHostOrSingleplayer()) continue;

            var allCars = UnityEngine.Object.FindObjectsOfType<TrainCar>();
            foreach (var loco in allCars)
            {
                if (!loco.IsLoco) continue;

                string train = ExtractNumber(loco.ID);
                if (string.IsNullOrEmpty(train)) continue;

                float speedMs = Mathf.Abs(loco.GetForwardSpeed()); // m/s

                // Ignore physics drift when the loco is effectively stationary
                if (speedMs < MIN_SPEED_MS) continue;

                // Δmiles = speed(m/s) × Δt(s) / m_per_mile  (loco distance only, no consist weighting)
                float delta = speedMs * POLL_SEC / M_PER_MILE;

                if (!_accumulated.TryGetValue(train, out float prev)) prev = 0f;
                _accumulated[train] = prev + delta;
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // FLUSH — push accumulated totals every FLUSH_SEC seconds
    // ═════════════════════════════════════════════════════════════════════════

    private IEnumerator FlushLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(FLUSH_SEC);

            if (!GRDNConnectBehaviour.IsHostOrSingleplayer()) continue;

            string pushUrl = GRDNConnectBehaviour.ActiveBotUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(pushUrl)) continue;
            if (_accumulated.Count == 0) continue;

            // Snapshot and clear atomically so the poll loop accumulates fresh data
            // while the HTTP request is in flight.
            var snapshot = new Dictionary<string, float>(_accumulated);
            _accumulated.Clear();

            StartCoroutine(PushStats(pushUrl, snapshot));
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // HTTP push
    // ═════════════════════════════════════════════════════════════════════════

    private IEnumerator PushStats(string pushUrl, Dictionary<string, float> entries)
    {
        string secret = GRDNConnectBehaviour.ActiveBotSecret ?? "";
        string url    = pushUrl + "/stats-push";

        // Hand-build JSON — no external serializer dependency
        var sb = new StringBuilder();
        sb.Append("{\"entries\":[");
        bool first = true;
        foreach (var kv in entries)
        {
            if (!first) sb.Append(",");
            first = false;
            string miles = kv.Value.ToString("F4", CultureInfo.InvariantCulture);
            sb.Append($"{{\"trainNumber\":\"{Esc(kv.Key)}\",\"carMiles\":{miles}}}");
        }
        sb.Append("]}");

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
                Main.ModEntry.Logger.Warning($"[StatsTracker] Push failed: {req.error}");
            else
                Main.ModEntry.Logger.Log($"[StatsTracker] Pushed miles for {entries.Count} train(s)");
        }
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
        s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
}
