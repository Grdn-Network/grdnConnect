// ModSyncSender.cs
// BETA. Scans the host's installed UMM mods, classifies each with DVMP's
// ModCompatibilityManager (via reflection, so there is no compile-time
// dependency on dv-multiplayer, matching GRDNConnectBehaviour), and POSTs the
// list to the bot's /mod-sync endpoint. Host-only, triggered explicitly from
// the mod's UMM settings button, never automatic.
//
// The bot gates the endpoint behind MOD_SYNC_BETA, so this is a no-op on the
// bot side until the beta is enabled there.
//
// Category mapping (DVMP MultiplayerCompatibility -> bot section):
//   All / Undefined / (no info) -> required
//   Client                      -> optional
//   Host                        -> host
//   Incompatible                -> skipped
//
// A mod that a required mod depends on is then promoted to required, whatever
// DVMP calls it. See PromoteRequirements.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityModManagerNet;

public class ModSyncSender : MonoBehaviour
{
    internal static ModSyncSender Instance;

    // UnityWebRequest waits indefinitely by default, so a connection that opens
    // and then stalls would leave this coroutine running for the session.
    private const int RequestTimeoutSeconds = 15;

    private void Awake() => Instance = this;

    // ── Reflection cache for Multiplayer.API.ModCompatibilityManager ────────────
    private static bool _resolved;
    private static Type _mgrType;
    private static PropertyInfo _instanceProp;
    private static MethodInfo _tryGetCompat;

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        try
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.GetName().Name.StartsWith("Multiplayer", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (Type t in asm.GetExportedTypes())
                    if (t.Name == "ModCompatibilityManager") { _mgrType = t; break; }
                if (_mgrType != null) break;
            }
            if (_mgrType == null) return; // DVMP not loaded / not found

            _instanceProp = _mgrType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            _tryGetCompat = _mgrType.GetMethod("TryGetCompatibility",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }
        catch (Exception e)
        {
            Main.ModEntry.Logger.Warning($"[ModSync] Resolve failed: {e.Message}");
        }
    }

    // Returns "required" | "optional" | "host", or null to skip (Incompatible).
    // Falls back to "required" whenever classification isn't available, which is
    // DVMP's own safe default (an undefined/unknown mod is treated as required).
    private static string CategoryFor(string modId)
    {
        try
        {
            Resolve();
            if (_mgrType == null || _instanceProp == null || _tryGetCompat == null) return "required";

            object mgr = _instanceProp.GetValue(null);
            if (mgr == null) return "required";

            object[] args = { modId, null };
            bool ok = (bool)_tryGetCompat.Invoke(mgr, args);
            if (!ok || args[1] == null) return "required";

            switch (args[1].ToString())
            {
                case "Host":         return "host";
                case "Client":       return "optional";
                case "Incompatible": return null;
                default:             return "required"; // All / Undefined / anything new
            }
        }
        catch
        {
            return "required";
        }
    }

    // ── Trigger (from the UMM settings button) ──────────────────────────────────
    public void Trigger()
    {
        if (!GRDNConnectBehaviour.IsHostOrSingleplayer())
        {
            Main.ModEntry.Logger.Log("[ModSync] Not the host - nothing to sync.");
            return;
        }
        string botUrl = GRDNConnectBehaviour.ActiveBotUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(botUrl))
        {
            Main.ModEntry.Logger.Warning("[ModSync] No bot URL configured.");
            return;
        }
        StartCoroutine(SendScan(botUrl));
    }

    private IEnumerator SendScan(string botUrl)
    {
        string secret = GRDNConnectBehaviour.ActiveBotSecret ?? "";
        string url    = botUrl + "/mod-sync";
        string json   = BuildScanJson(out int count);

        Main.ModEntry.Logger.Log($"[ModSync] Sending scan of {count} mod(s) to {url}");
        byte[] raw = Encoding.UTF8.GetBytes(json);

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler   = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout         = RequestTimeoutSeconds;
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(secret))
                req.SetRequestHeader("x-secret", secret);

            yield return req.SendWebRequest();

            if (req.error != null)
                Main.ModEntry.Logger.Warning($"[ModSync] Push failed: {req.error}");
            else
                Main.ModEntry.Logger.Log($"[ModSync] Bot replied: {req.downloadHandler.text}");
        }
    }

    // ── Build { "mods": [ { id, name, version, url, category }, ... ] } ──────────
    private static string BuildScanJson(out int count)
    {
        // Classify first, emit second, because a mod's category can still change
        // once every other mod's requirements are known.
        var included = new List<UnityModManager.ModEntry>();
        var category = new Dictionary<string, string>();

        foreach (var entry in UnityModManager.modEntries)
        {
            if (entry == null || !entry.Enabled || entry.Info == null) continue;

            string id = entry.Info.Id;
            if (string.IsNullOrEmpty(id) || category.ContainsKey(id)) continue;

            string cat = CategoryFor(id);
            if (cat == null) continue; // Incompatible with multiplayer

            category[id] = cat;
            included.Add(entry);
        }

        PromoteRequirements(included, category);

        count = 0;
        var sb = new StringBuilder();
        sb.Append("{\"mods\":[");
        bool first = true;

        foreach (var entry in included)
        {
            string id      = entry.Info.Id;
            string name    = string.IsNullOrEmpty(entry.Info.DisplayName) ? id : entry.Info.DisplayName;
            string version = entry.Info.Version ?? "";
            string url     = TrustedUrl(entry.Info.HomePage);

            if (!first) sb.Append(",");
            first = false;
            sb.Append("{");
            sb.Append($"\"id\":\"{Esc(id)}\",");
            sb.Append($"\"name\":\"{Esc(name)}\",");
            sb.Append($"\"version\":\"{Esc(version)}\",");
            sb.Append($"\"url\":\"{Esc(url)}\",");
            sb.Append($"\"category\":\"{category[id]}\"");
            sb.Append("}");
            count++;
        }

        sb.Append("]}");
        return sb.ToString();
    }

    // A mod that a required mod depends on is itself required, whatever DVMP calls
    // it. UMM will not load a mod whose requirement is missing, so a player who
    // skips the dependency loses the mod that needed it, and dv-mp then refuses
    // the join over that mod. The live case is CommsRadioAPI, which dv-mp
    // hardcodes as Client while GRDNConnect lists it under Requirements.
    //
    // Repeats to a fixpoint because requirements chain. Every pass that changes
    // anything promotes at least one mod, and promotion is one way, so the loop
    // ends after at most one pass per mod.
    private static void PromoteRequirements(
        List<UnityModManager.ModEntry> included,
        Dictionary<string, string> category)
    {
        var promoted = new List<string>();
        bool changed = true;

        while (changed)
        {
            changed = false;

            foreach (var entry in included)
            {
                if (category[entry.Info.Id] != "required") continue;

                // UMM has already parsed "Id-1.2.3" into key and version here, so
                // these keys are plain ids and need no stripping of our own.
                foreach (string needed in entry.Requirements.Keys)
                {
                    string current;
                    if (!category.TryGetValue(needed, out current) || current == "required")
                        continue;

                    category[needed] = "required";
                    promoted.Add(needed);
                    changed = true;
                }
            }
        }

        if (promoted.Count > 0)
            Main.ModEntry.Logger.Log(
                "[ModSync] Promoted to required as a dependency: "
                + string.Join(", ", promoted.ToArray()));
    }

    // Only pass through github / nexus homepages; the bot keeps its curated link
    // for anything else (or fills it in manually once).
    private static string TrustedUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        try
        {
            string host = new Uri(url).Host.ToLowerInvariant();
            if (host == "github.com" || host == "www.github.com" ||
                host == "nexusmods.com" || host == "www.nexusmods.com")
                return url;
        }
        catch { }
        return "";
    }

    // JSON forbids a raw character below 0x20 inside a string, so a display name
    // carrying a newline or a tab would produce a body the bot rejects.
    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else         sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
