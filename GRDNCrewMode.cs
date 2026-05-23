// GRDNCrewMode.cs
// GRDN Crew radio mode — get in a loco, open this mode, press ASSIGN.
// Reads your loco type and number automatically, pushes both to the bot.
//
// HOW IT WORKS
// ─────────────
// On open  : reads your current loco type + number.
// ASSIGN   : sends POST /update-crew { fromTrainNumber, toTrainNumber, locoType }
//
//   fromTrainNumber = last successfully registered train this session,
//                     or current loco if this is the first use (confirms initial loco).
//   toTrainNumber   = current loco number.
//   locoType        = current loco type code (DE6, S282, BE2, …).
//
// The bot updates train_number + loco_type in the DB, then refreshes the
// train board and your Discord nickname automatically.
//
// FIRST-TIME REQUIREMENT
// ───────────────────────
// You must have run /setcrew in Discord at least once per account to link
// your Discord ID to a train number. After that this mode handles all
// mid-op loco changes — including loco type updates — without Discord.
//
// SWITCHING LOCOS MID-OP
// ───────────────────────
// 1. Board the new loco.
// 2. Open GRDN CREW — screen shows the new loco's type + number.
// 3. Press ASSIGN.
// The bot moves your registration from the old number to the new one.
#if COMMS_RADIO_API

using System;
using System.Collections;
using System.Text;
using CommsRadioAPI;
using DV;
using UnityEngine;
using UnityEngine.Networking;

public class GRDNCrewState : AStateBehaviour
{
    private readonly MonoBehaviour _host;
    private readonly string        _trainNumber;  // current loco number  (null = not in a loco)
    private readonly string        _locoType;     // current loco type code (null = unknown)
    private readonly bool          _sent;

    // Persists across mode opens for the life of the game session.
    // Tracks the last train number successfully registered via this mode,
    // so the bot knows which registration to move when the player switches locos.
    private static string _lastRegisteredTrain = null;

    // ── Entry point — called by RadioIntegration.TryInit ─────────────────────
    public GRDNCrewState(MonoBehaviour host)
        : this(host,
               RadioIntegration.GetPlayerTrainNumber(),
               RadioIntegration.GetPlayerLocoTypeCode(),
               sent: false)
    { }

    // ── Internal constructor ──────────────────────────────────────────────────
    private GRDNCrewState(MonoBehaviour host, string trainNumber, string locoType, bool sent)
        : base(new CommsRadioState(
            "GRDN CREW",
            MakeContent(trainNumber, locoType, sent),
            // Button: "ASSIGN" when in a loco and not already sent, blank otherwise
            (!sent && trainNumber != null) ? "ASSIGN" : "",
            LCDArrowState.Off,
            LEDState.Off,
            (!sent && trainNumber != null) ? ButtonBehaviourType.Override : ButtonBehaviourType.Ignore))
    {
        _host        = host;
        _trainNumber = trainNumber;
        _locoType    = locoType;
        _sent        = sent;
    }

    // ── State machine ─────────────────────────────────────────────────────────
    public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
    {
        // Only respond to Activate; ignore Up/Down (no list to scroll)
        if (action != InputAction.Activate || _trainNumber == null || _sent)
            return this;

        // fromTrain: where the bot looks to find the player's registration.
        // First use → assume current loco is already registered (just updates locoType).
        // Subsequent uses → use the last successfully registered number.
        string fromTrain = _lastRegisteredTrain ?? _trainNumber;

        _host.StartCoroutine(PostCrewAssign(fromTrain, _trainNumber, _locoType));

        // Return sent state — disables ASSIGN until user leaves/re-opens the mode
        return new GRDNCrewState(_host, _trainNumber, _locoType, sent: true);
    }

    // ── HTTP push ─────────────────────────────────────────────────────────────
    private IEnumerator PostCrewAssign(string fromTrain, string toTrain, string locoType)
    {
        string pushUrl = Main.Settings.BotPushUrl?.TrimEnd('/');
        string secret  = Main.Settings.BotSecret ?? "";

        if (string.IsNullOrEmpty(pushUrl))
        {
            Main.ModEntry.Logger.Warning("[CrewMode] BotPushUrl not set — skipped.");
            yield break;
        }

        // locoType is optional — omit the field entirely if unknown
        string locoField = !string.IsNullOrEmpty(locoType)
            ? $",\"locoType\":\"{Esc(locoType)}\""
            : "";

        string body = $"{{\"fromTrainNumber\":\"{Esc(fromTrain)}\",\"toTrainNumber\":\"{Esc(toTrain)}\"{locoField}}}";
        byte[] raw  = Encoding.UTF8.GetBytes(body);

        using (var req = new UnityWebRequest(pushUrl + "/update-crew", "POST"))
        {
            req.uploadHandler   = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(secret))
                req.SetRequestHeader("x-secret", secret);

            yield return req.SendWebRequest();

            if (req.error == null)
            {
                _lastRegisteredTrain = toTrain;
                Main.ModEntry.Logger.Log(
                    $"[CrewMode] Registered → [{locoType ?? "?"}] {toTrain} (from {fromTrain})");
            }
            else
            {
                Main.ModEntry.Logger.Warning(
                    $"[CrewMode] Request failed ({req.responseCode}): {req.error}");
            }
        }
    }

    // ── Display ───────────────────────────────────────────────────────────────
    private static string MakeContent(string trainNumber, string locoType, bool sent)
    {
        if (sent)               return "Assigning...";
        if (trainNumber == null) return "Get in a loco";

        // e.g. "DE6  034" or just "034" if type unknown
        string prefix = !string.IsNullOrEmpty(locoType) ? locoType + "  " : "";
        return $"{prefix}{trainNumber}";
    }

    private static string Esc(string s) =>
        s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
}
#endif
