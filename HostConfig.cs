// HostConfig.cs
// Bot connection settings, held in Mods/GRDNConnect/connect.cfg instead of the
// UMM settings GUI.
//
// WHY THESE THREE AND NOT THE REST
// ─────────────────────────────────
// Only the bot URL, the shared secret and the radio channel list live here. They
// are not things a player toggles: the bot pushes all three at /session start and
// they reach clients over the DVMP packet channel, so for anyone on the GRDN bot
// this file never needs opening. It exists for a host running their own bot, and
// for a manual fallback when the push has not happened yet.
//
// The live toggles (Live Train Board, Relaxed Completion, Interchange Mode,
// Realistic Radio, Verbose Logging, Port) stay in the UMM GUI on purpose: those
// get changed mid-session and applying them live is the point.
//
// FORMAT
// ──────
// INI-style, one key per line, '#' or ';' starts a comment. Radio channels are
// repeated lines rather than nested structure, which is the whole reason this is
// not JSON: a channel list is much easier to hand-edit as
//     channel = Main Line | 123456789
// than as a single-line JSON blob in a text box.
//
// A secret written here is readable by anyone with the file. That is the same
// trust level as the machine running the game, and it is only ever a fallback:
// the preferred path is the runtime push, which never touches disk.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class HostConfig
{
	private const string FileName = "connect.cfg";

	internal static string BotUrl            { get; private set; } = "";
	internal static string BotSecret         { get; private set; } = "";
	/// <summary>Channels re-encoded as the JSON shape RadioIntegration already parses.</summary>
	internal static string RadioChannelsJson { get; private set; } = "[]";

	internal static string FilePath =>
		Path.Combine(Main.ModEntry?.Path ?? ".", FileName);

	/// <summary>
	/// Read connect.cfg, creating it first (migrating any values already in
	/// Settings.xml) when it does not exist. Never throws: a broken config must not
	/// stop the mod loading, it just leaves the fallbacks empty.
	/// </summary>
	internal static void Load()
	{
		try
		{
			string path = FilePath;
			if (!File.Exists(path)) CreateWithMigration(path);
			Parse(File.ReadAllLines(path));

			Main.ModEntry.Logger.Log(
				$"[GRDNConnect] connect.cfg loaded — " +
				$"url={(string.IsNullOrEmpty(BotUrl) ? "(unset)" : BotUrl)}, " +
				$"secret={(string.IsNullOrEmpty(BotSecret) ? "(unset)" : "set")}, " +
				$"{ChannelCount} channel(s).");
		}
		catch (Exception ex)
		{
			Main.ModEntry.Logger.Warning(
				$"[GRDNConnect] connect.cfg could not be read ({ex.GetType().Name}: {ex.Message}); " +
				"using runtime values only.");
		}
	}

	private static int ChannelCount;

	// ── Parsing ───────────────────────────────────────────────────────────────

	private static void Parse(string[] lines)
	{
		string url = "", secret = "";
		var channels = new List<KeyValuePair<string, string>>();

		foreach (string raw in lines)
		{
			string line = raw?.Trim();
			if (string.IsNullOrEmpty(line)) continue;
			if (line[0] == '#' || line[0] == ';') continue;

			int eq = line.IndexOf('=');
			if (eq <= 0) continue;

			string key = line.Substring(0, eq).Trim().ToLowerInvariant();
			string val = line.Substring(eq + 1).Trim();

			switch (key)
			{
				case "bot_url":    url    = val.TrimEnd('/'); break;
				case "bot_secret": secret = val;              break;
				case "channel":
				{
					// "Display Name | 123456789" — split on the LAST pipe so a name
					// containing a pipe still works.
					int bar = val.LastIndexOf('|');
					if (bar <= 0) break;
					string name = val.Substring(0, bar).Trim();
					string vcId = val.Substring(bar + 1).Trim();
					if (name.Length > 0 && vcId.Length > 0)
						channels.Add(new KeyValuePair<string, string>(name, vcId));
					break;
				}
			}
		}

		BotUrl            = url;
		BotSecret         = secret;
		ChannelCount      = channels.Count;
		RadioChannelsJson = ToJson(channels);
	}

	/// <summary>
	/// Re-encode the channel lines into the JSON array RadioIntegration already
	/// understands, so the working parser is reused rather than duplicated.
	/// </summary>
	private static string ToJson(List<KeyValuePair<string, string>> channels)
	{
		if (channels.Count == 0) return "[]";
		var sb = new StringBuilder("[");
		for (int i = 0; i < channels.Count; i++)
		{
			if (i > 0) sb.Append(',');
			sb.Append("{\"name\":\"").Append(Esc(channels[i].Key))
			  .Append("\",\"vcId\":\"").Append(Esc(channels[i].Value)).Append("\"}");
		}
		return sb.Append(']').ToString();
	}

	private static string Esc(string s) =>
		s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";

	// ── First-run creation + migration ────────────────────────────────────────

	/// <summary>
	/// Write a fresh connect.cfg, carrying over anything already set in Settings.xml.
	/// Settings.xml is read as raw XML rather than through the Settings class,
	/// because these fields have been removed from that class: UMM would simply
	/// drop the old elements and the values would be lost on the next save.
	/// Settings.xml itself is left untouched.
	/// </summary>
	private static void CreateWithMigration(string path)
	{
		string oldUrl = "", oldSecret = "", oldChannelsJson = "";
		try
		{
			string xml = Path.Combine(Main.ModEntry?.Path ?? ".", "Settings.xml");
			if (File.Exists(xml))
			{
				string text = File.ReadAllText(xml);
				oldUrl          = XmlValue(text, "BotPushUrl");
				oldSecret       = XmlValue(text, "BotSecret");
				oldChannelsJson = XmlValue(text, "RadioChannelsJson");
			}
		}
		catch { /* migration is best effort; a fresh file is still better than none */ }

		bool migrated = !string.IsNullOrEmpty(oldUrl)
		             || !string.IsNullOrEmpty(oldSecret)
		             || (!string.IsNullOrEmpty(oldChannelsJson) && oldChannelsJson != "[]");

		var sb = new StringBuilder();
		sb.AppendLine("# GRDNConnect host configuration");
		sb.AppendLine("#");
		sb.AppendLine("# Only needed if you run your own bot, or as a fallback before the bot has");
		sb.AppendLine("# pushed its config. On the GRDN bot these are filled in automatically at");
		sb.AppendLine("# /session start and forwarded to clients, so you can leave this file alone.");
		sb.AppendLine("#");
		sb.AppendLine("# Everything else (port, live train board, relaxed completion, interchange");
		sb.AppendLine("# mode, realistic radio, verbose logging) is in the UMM settings GUI, because");
		sb.AppendLine("# those apply live and are meant to be changed mid-session.");
		sb.AppendLine("#");
		sb.AppendLine("# Reload after editing with 'connect.reload' in the in-game console.");
		sb.AppendLine();
		sb.AppendLine("# Base URL of the bot. Leave empty to use the public default,");
		sb.AppendLine($"# {GRDNDefaults.BotUrl}");
		sb.AppendLine("bot_url = " + oldUrl);
		sb.AppendLine();
		sb.AppendLine("# Shared secret. Normally left empty: the bot mints a fresh per-session");
		sb.AppendLine("# token and pushes it at runtime, which never touches disk. Anyone who can");
		sb.AppendLine("# read this file can read a secret written here.");
		sb.AppendLine("bot_secret = " + oldSecret);
		sb.AppendLine();
		sb.AppendLine("# Radio channels, one per line:  channel = Display Name | DiscordVoiceChannelId");
		sb.AppendLine("# Get the id by right-clicking the voice channel in Discord, Copy Channel ID.");
		sb.AppendLine("# Example:");
		sb.AppendLine("#   channel = Main Line | 123456789012345678");

		foreach (var kv in ParseLegacyChannels(oldChannelsJson))
			sb.AppendLine($"channel = {kv.Key} | {kv.Value}");

		File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

		Main.ModEntry.Logger.Log(migrated
			? "[GRDNConnect] connect.cfg created and seeded from your existing UMM settings " +
			  "(Settings.xml left as-is)."
			: "[GRDNConnect] connect.cfg created with defaults.");
	}

	/// <summary>Pull one element's text out of Settings.xml without an XML parser dependency.</summary>
	private static string XmlValue(string xml, string element)
	{
		var m = Regex.Match(xml, $"<{Regex.Escape(element)}>(.*?)</{Regex.Escape(element)}>",
			RegexOptions.Singleline);
		if (!m.Success) return "";
		return m.Groups[1].Value
			.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
			.Replace("&quot;", "\"").Replace("&apos;", "'")
			.Trim();
	}

	/// <summary>Pull name/vcId pairs out of the old RadioChannelsJson so they survive migration.</summary>
	private static List<KeyValuePair<string, string>> ParseLegacyChannels(string json)
	{
		var result = new List<KeyValuePair<string, string>>();
		if (string.IsNullOrEmpty(json)) return result;
		foreach (Match m in Regex.Matches(json,
			"\\{[^}]*?\"name\"\\s*:\\s*\"(.*?)\"[^}]*?\"vcId\"\\s*:\\s*\"(.*?)\"[^}]*?\\}",
			RegexOptions.Singleline))
		{
			string name = m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\").Trim();
			string vcId = m.Groups[2].Value.Trim();
			// A name with a pipe would break the "name | id" line format on re-read.
			name = name.Replace("|", "/");
			if (name.Length > 0 && vcId.Length > 0)
				result.Add(new KeyValuePair<string, string>(name, vcId));
		}
		return result;
	}
}
