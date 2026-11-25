// BotCall.cs
// One way out of the mod to the bot.
//
// On the host, and in singleplayer, the request goes straight over HTTP with the
// host's own token. On a client it is relayed through the host over the Derail
// Valley multiplayer packet channel, because a client holds no bot credential and
// has no route to the bot.
//
// Clients used to call the bot themselves, which meant handing every player in the
// session a token the bot accepted. Relaying removes the credential from clients
// entirely and leaves the host as the only caller per session.

using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Networking;

/// <summary>Filled in when the call finishes, however it finishes.</summary>
internal sealed class BotCallResult
{
	internal bool   Done;
	internal bool   Ok;
	internal string ResponseJson = "";
	internal string Error        = "";

	internal void Fail(string error)
	{
		Error = error;
		Ok    = false;
		Done  = true;
	}
}

internal static class BotCall
{
	/// <summary>
	/// Routes a client may ask the host to call for it. The host spends its own
	/// token on these, so anything added here is something any player in the
	/// session can make the host do. Keep it to what a radio mode needs.
	/// </summary>
	private static readonly HashSet<string> ClientRelayable = new HashSet<string>
	{
		"radio-change",
		"update-crew",
	};

	internal static bool IsRelayable(string route) =>
		route != null && ClientRelayable.Contains(route);

	/// <summary>
	/// POST a JSON body to a bot route. Route is a bare name with no slash, for
	/// example "radio-change". Yields until the call finishes or gives up, so the
	/// caller can read <paramref name="result"/> straight after.
	/// </summary>
	internal static IEnumerator Post(string route, string bodyJson, BotCallResult result)
	{
		if (GRDNConnectBehaviour.IsHostOrSingleplayer())
		{
			yield return PostDirect(route, bodyJson, result);
			yield break;
		}

		yield return ConnectMpChannel.RelayPost(route, bodyJson, result);
	}

	/// <summary>
	/// The host's own HTTP call. Also what the host runs on a client's behalf when
	/// relaying, which is why it is separate from Post.
	/// </summary>
	internal static IEnumerator PostDirect(string route, string bodyJson, BotCallResult result)
	{
		string baseUrl = GRDNConnectBehaviour.ActiveBotUrl?.TrimEnd('/');
		if (string.IsNullOrEmpty(baseUrl))
		{
			result.Fail("no bot URL");
			yield break;
		}

		string secret = GRDNConnectBehaviour.ActiveBotSecret ?? "";
		byte[] raw    = Encoding.UTF8.GetBytes(bodyJson ?? "{}");

		using (var req = new UnityWebRequest($"{baseUrl}/{route}", "POST"))
		{
			req.uploadHandler   = new UploadHandlerRaw(raw);
			req.downloadHandler = new DownloadHandlerBuffer();
			req.SetRequestHeader("Content-Type", "application/json");
			if (!string.IsNullOrEmpty(secret))
				req.SetRequestHeader("x-secret", secret);

			yield return req.SendWebRequest();

			if (req.error != null)
			{
				result.Fail(req.error);
				yield break;
			}

			result.ResponseJson = req.downloadHandler.text ?? "";
			result.Ok           = true;
			result.Done         = true;
		}
	}
}
