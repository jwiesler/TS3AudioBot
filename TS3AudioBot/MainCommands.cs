// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using TS3AudioBot.Algorithm;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.CommandSystem.Ast;
using TS3AudioBot.CommandSystem.CommandResults;
using TS3AudioBot.CommandSystem.Commands;
using TS3AudioBot.CommandSystem.Text;
using TS3AudioBot.Config;
using TS3AudioBot.Dependency;
using TS3AudioBot.Environment;
using TS3AudioBot.Helper;
using TS3AudioBot.Helper.Diagnose;
using TS3AudioBot.History;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;
using TS3AudioBot.Plugins;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Rights;
using TS3AudioBot.Search;
using TS3AudioBot.Sessions;
using TS3AudioBot.Web.Api;
using TS3AudioBot.Web.Model;
using TSLib;
using TSLib.Audio;
using TSLib.Full;
using TSLib.Full.Book;
using TSLib.Helper;
using TSLib.Messages;
using static TS3AudioBot.CommandSystem.CommandSystemTypes;

namespace TS3AudioBot
{
	public static class MainCommands
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		internal static ICommandBag Bag { get; } = new MainCommandsBag();

		internal class MainCommandsBag : ICommandBag
		{
			public IReadOnlyCollection<BotCommand> BagCommands { get; } = CommandManager.GetBotCommands(null, typeof(MainCommands)).ToArray();
			public IReadOnlyCollection<string> AdditionalRights { get; } = new string[] { RightHighVolume, RightDeleteAllPlaylists, RightBypassManageCheck };
		}

		public const string RightHighVolume = "ts3ab.admin.volume";
		public const string RightDeleteAllPlaylists = "ts3ab.admin.list";

		private const string YesNoOption = " !(yes|no)";

		// [...] = Optional
		// <name> = Placeholder for a text
		// [text] = Option for fixed text
		// (a|b) = either or switch

		// ReSharper disable UnusedMember.Global
		[Command("add")]
		public static void CommandAdd(
			ResolveContext resolveContext, PlayManager playManager, InvokerData invoker, string url, params string[] attributes) {
			var result = resolveContext.Load(url).UnwrapThrow();

			playManager.Enqueue(result.BaseData, new MetaData(invoker.ClientUid, null, PlayManager.ParseStartTime(attributes))).UnwrapThrow();
		}

		[Command("add")]
		public static void CommandAdd(PlayManager playManager, InvokerData invoker, IAudioResourceResult rsc, params string[] attributes)
			=> playManager.Enqueue(rsc.AudioResource, new MetaData(invoker.ClientUid, null, PlayManager.ParseStartTime(attributes))).UnwrapThrow();

		[Command("alias add")]
		public static void CommandAliasAdd(CommandManager commandManager, ConfBot confBot, string commandName, string command)
		{
			commandManager.RegisterAlias(commandName, command).UnwrapThrow();

			var confEntry = confBot.Commands.Alias.GetOrCreateItem(commandName);
			confEntry.Value = command;
			confBot.SaveWhenExists().UnwrapThrow();
		}

		[Command("alias remove")]
		public static void CommandAliasRemove(CommandManager commandManager, ConfBot confBot, string commandName)
		{
			commandManager.UnregisterAlias(commandName).UnwrapThrow();

			confBot.Commands.Alias.RemoveItem(commandName);
			confBot.SaveWhenExists().UnwrapThrow();
		}

		[Command("alias list")]
		public static JsonArray<string> CommandAliasList(CommandManager commandManager)
			=> new JsonArray<string>(commandManager.AllAlias.ToArray(), x => string.Join(",", x));

		[Command("alias show")]
		public static string CommandAliasShow(CommandManager commandManager, string commandName)
			=> commandManager.GetAlias(commandName)?.AliasString;

		[Command("api token")]
		[Usage("[<duration>]", "Optionally specifies a duration this key is valid in hours.")]
		public static string CommandApiToken(TokenManager tokenManager, ClientCall invoker, double? validHours = null)
		{
			if (invoker.Visibiliy.HasValue && invoker.Visibiliy != TextMessageTargetMode.Private)
				throw new CommandException(strings.error_use_private, CommandExceptionReason.CommandError);
			if (invoker.IsAnonymous)
				throw new MissingContextCommandException(strings.error_no_uid_found, typeof(ClientCall));

			TimeSpan? validSpan = null;
			try
			{
				if (validHours.HasValue)
					validSpan = TimeSpan.FromHours(validHours.Value);
			}
			catch (OverflowException oex)
			{
				throw new CommandException(strings.error_invalid_token_duration, oex, CommandExceptionReason.CommandError);
			}
			return tokenManager.GenerateToken(invoker.ClientUid.Value, validSpan);
		}

		[Command("bot avatar set")]
		public static void CommandBotAvatarSet(Ts3Client ts3Client, string url)
		{
			url = TextUtil.ExtractUrlFromBb(url);
			Uri uri;
			try { uri = new Uri(url); }
			catch (Exception ex) { throw new CommandException(strings.error_media_invalid_uri, ex, CommandExceptionReason.CommandError); }

			WebWrapper.GetResponseLoc(uri, x =>
			{
				using (var stream = x.GetResponseStream())
				{
					var imageResult = ImageUtil.ResizeImageSave(stream, out _);
					if (!imageResult.Ok)
						return imageResult.Error;
					return ts3Client.UploadAvatar(imageResult.Value);
				}
			}).UnwrapThrow();
		}

		[Command("bot avatar clear")]
		public static void CommandBotAvatarClear(Ts3Client ts3Client) => ts3Client.DeleteAvatar().UnwrapThrow();

		[Command("bot badges")]
		public static void CommandBotBadges(Ts3Client ts3Client, string badges) => ts3Client.ChangeBadges(badges).UnwrapThrow();

		[Command("bot description set")]
		public static void CommandBotDescriptionSet(Ts3Client ts3Client, string description) => ts3Client.ChangeDescription(description).UnwrapThrow();

		[Command("bot diagnose", "_undocumented")]
		public static JsonArray<SelfDiagnoseMessage> CommandBotDiagnose(Player player, IVoiceTarget target, Connection book)
		{
			var problems = new List<SelfDiagnoseMessage>();
			// ** Diagnose common playback problems and more **

			var self = book.Self();
			var curChan = book.CurrentChannel();

			// Check talk power
			if (!self.TalkPowerGranted && self.TalkPower < curChan.NeededTalkPower)
				problems.Add(new SelfDiagnoseMessage { Description = "The bot does not have enough talk power.", LevelValue = SelfDiagnoseLevel.Warning });

			// Check volume 0
			if (player.Volume == 0)
				problems.Add(new SelfDiagnoseMessage { Description = "The volume level is at 0.", LevelValue = SelfDiagnoseLevel.Warning });

			// Check if send mode hasn't been selected yet
			if (target.SendMode == TargetSendMode.None)
				problems.Add(new SelfDiagnoseMessage { Description = "Send mode is currently 'None', use '!whisper off' for example to send via voice.", LevelValue = SelfDiagnoseLevel.Warning });

			// ... more

			return new JsonArray<SelfDiagnoseMessage>(problems, x =>
			{
				if (x.Count == 0)
					return "No problems detected";
				var strb = new StringBuilder("The following issues have been found:");
				foreach (var prob in x)
					strb.Append("\n- ").Append(prob.Description);
				return strb.ToString();
			});
		}

		[Command("bot disconnect")]
		public static void CommandBotDisconnect(BotManager bots, Bot bot) => bots.StopBot(bot);

		[Command("bot commander")]
		public static JsonValue<bool> CommandBotCommander(Ts3Client ts3Client)
		{
			var value = ts3Client.IsChannelCommander().UnwrapThrow();
			return new JsonValue<bool>(value, string.Format(strings.info_status_channelcommander, value ? strings.info_on : strings.info_off));
		}
		[Command("bot commander on")]
		public static void CommandBotCommanderOn(Ts3Client ts3Client) => ts3Client.SetChannelCommander(true).UnwrapThrow();
		[Command("bot commander off")]
		public static void CommandBotCommanderOff(Ts3Client ts3Client) => ts3Client.SetChannelCommander(false).UnwrapThrow();

		[Command("bot come")]
		public static void CommandBotCome(Ts3Client ts3Client, ClientCall invoker, string password = null)
		{
			var channel = invoker?.ChannelId;
			if (channel == null)
				throw new CommandException(strings.error_no_target_channel, CommandExceptionReason.CommandError);
			ts3Client.MoveTo(channel.Value, password).UnwrapThrow();
		}

		[Command("bot connect template")]
		public static BotInfo CommandBotConnectTo(BotManager bots, string name)
		{
			var botInfo = bots.RunBotTemplate(name);
			if (!botInfo.Ok)
				throw new CommandException(strings.error_could_not_create_bot + $" ({botInfo.Error})", CommandExceptionReason.CommandError);
			return botInfo.Value;
		}

		[Command("bot connect to")]
		public static BotInfo CommandBotConnectNew(BotManager bots, string address, string password = null)
		{
			var botConf = bots.CreateNewBot();
			botConf.Connect.Address.Value = address;
			if (!string.IsNullOrEmpty(password))
				botConf.Connect.ServerPassword.Password.Value = password;
			var botInfo = bots.RunBot(botConf);
			if (!botInfo.Ok)
				throw new CommandException(strings.error_could_not_create_bot + $" ({botInfo.Error})", CommandExceptionReason.CommandError);
			return botInfo.Value;
		}

		[Command("bot info")]
		public static BotInfo CommandBotInfo(Bot bot) => bot.GetInfo();

		[Command("bot info client", "_undocumented")]
		public static JsonValue<ClientInfo> CommandBotInfoClient(Ts3Client ts3Client, ApiCall _)
			=> new JsonValue<ClientInfo>(ts3Client.GetSelf().UnwrapThrow(), string.Empty);

		[Command("bot list")]
		public static JsonArray<BotInfo> CommandBotList(BotManager bots, ConfRoot config)
		{
			var botInfoList = bots.GetBotInfolist();
			var botConfigList = config.GetAllBots();
			var infoList = new Dictionary<string, BotInfo>();
			foreach (var botInfo in botInfoList.Where(x => !string.IsNullOrEmpty(x.Name)))
				infoList[botInfo.Name] = botInfo;
			foreach (var botConfig in botConfigList)
			{
				if (infoList.ContainsKey(botConfig.Name))
					continue;
				infoList[botConfig.Name] = new BotInfo
				{
					Id = null,
					Name = botConfig.Name,
					Server = botConfig.Connect.Address,
					Status = BotStatus.Offline,
				};
			}
			return new JsonArray<BotInfo>(infoList.Values.Concat(botInfoList.Where(x => string.IsNullOrEmpty(x.Name))).ToArray(),
				bl => string.Join("\n", bl.Select(x => x.ToString())));
		}

		[Command("bot move")]
		public static void CommandBotMove(Ts3Client ts3Client, ulong channel, string password = null) => ts3Client.MoveTo((ChannelId)channel, password).UnwrapThrow();

		[Command("bot name")]
		public static void CommandBotName(Ts3Client ts3Client, string name) => ts3Client.ChangeName(name).UnwrapThrow();

		[Command("bot save")]
		public static void CommandBotSetup(ConfBot botConfig, string name) => botConfig.SaveNew(name).UnwrapThrow();

		[Command("bot setup")]
		public static void CommandBotSetup(Ts3Client ts3Client, string adminToken = null)
		{
			if (!ts3Client.SetupRights(adminToken))
				throw new CommandException(strings.cmd_bot_setup_error, CommandExceptionReason.CommandError);
		}

		[Command("bot template", "cmd_bot_use_help")]
		public static object CommandBotTemplate(ExecutionInformation info, IReadOnlyList<Type> returnTypes, BotManager bots, string botName, ICommand cmd)
		{
			using (var botLock = bots.GetBotLock(botName))
				return CommandBotUseInternal(info, returnTypes, botLock, cmd);
		}

		[Command("bot use")]
		public static object CommandBotUse(ExecutionInformation info, IReadOnlyList<Type> returnTypes, BotManager bots, int botId, ICommand cmd)
		{
			using (var botLock = bots.GetBotLock(botId))
				return CommandBotUseInternal(info, returnTypes, botLock, cmd);
		}

		private static object CommandBotUseInternal(ExecutionInformation info, IReadOnlyList<Type> returnTypes, BotLock botLock, ICommand cmd)
		{
			if (botLock is null)
				throw new CommandException(strings.error_bot_does_not_exist, CommandExceptionReason.CommandError);

			var exeInfo = info.CopyWithParent(botLock.Bot.Injector);
			string backUpId = NLog.MappedDiagnosticsContext.Get("BotId");
			NLog.MappedDiagnosticsContext.Set("BotId", botLock.Bot.Id.ToString());
			try
			{
				return cmd.Execute(exeInfo, Array.Empty<ICommand>(), returnTypes);
			}
			finally
			{
				NLog.MappedDiagnosticsContext.Set("BotId", backUpId);
			}
		}

		[Command("clear")]
		public static void CommandClear(PlayManager playManager) => playManager.Clear();

		[Command("command parse", "cmd_parse_command_help")]
		public static JsonValue<AstNode> CommandParse(string parameter)
		{
			var node = CommandParser.ParseCommandRequest(parameter);
			var strb = new StringBuilder();
			strb.AppendLine();
			node.Write(strb, 0);
			return new JsonValue<AstNode>(node, strb.ToString());
		}

		[Command("command tree", "_undocumented")]
		public static string CommandTree(CommandManager commandManager)
		{
			return CommandManager.GetTree(commandManager.RootGroup);
		}

		[Command("data song cover get", "_undocumented")]
		public static DataStream CommandData(ResolveContext resourceFactory, PlayManager playManager) =>
			new DataStream(response =>
			{
				var cur = playManager.CurrentPlayData;
				if (cur == null)
					return false;
				if (resourceFactory.GetThumbnail(cur.PlayResource).GetOk(out var stream)
					&& ImageUtil.ResizeImageSave(stream, out var mime).GetOk(out var image))
				{
					using (image)
					{
						response.ContentType = mime;
						image.CopyTo(response.Body);
						return true;
					}
				}
				return false;
			});

		[Command("eval")]
		[Usage("<command> <arguments...>", "Executes the given command on arguments")]
		[Usage("<strings...>", "Concat the strings and execute them with the command system")]
		public static object CommandEval(ExecutionInformation info, IReadOnlyList<ICommand> arguments, IReadOnlyList<Type> returnTypes)
		{
			// Evaluate the first argument on the rest of the arguments
			if (arguments.Count == 0)
				throw new CommandException(strings.error_cmd_at_least_one_argument, CommandExceptionReason.MissingParameter);
			var leftArguments = arguments.TrySegment(1);
			var arg0 = arguments[0].Execute(info, Array.Empty<ICommand>(), ReturnCommandOrString);
			if (arg0 is ICommand cmd)
				return cmd.Execute(info, leftArguments, returnTypes);

			// We got a string back so parse and evaluate it
			var args = ((IPrimitiveResult<string>)arg0).Get();

			cmd = CommandManager.AstToCommandResult(CommandParser.ParseCommandRequest(args));
			return cmd.Execute(info, leftArguments, returnTypes);
		}

		[Command("get", "_undocumented")]
		[Usage("<index> <list...>", "Get an element out of a list")]
		public static object CommandGet(uint index, System.Collections.IEnumerable list)
		{
			foreach (var i in list)
			{
				if (index == 0)
					return i;
				index--;
			}
			return null;
		}

		[Command("getmy id")]
		public static ushort CommandGetId(ClientCall invoker)
			=> invoker.ClientId?.Value ?? throw new CommandException(strings.error_not_found, CommandExceptionReason.CommandError);
		[Command("getmy uid")]
		public static string CommandGetUid(ClientCall invoker)
			=> invoker.ClientUid.Value;
		[Command("getmy name")]
		public static string CommandGetName(ClientCall invoker)
			=> invoker.NickName ?? throw new CommandException(strings.error_not_found, CommandExceptionReason.CommandError);
		[Command("getmy dbid")]
		public static ulong CommandGetDbId(ClientCall invoker)
			=> invoker.DatabaseId?.Value ?? throw new CommandException(strings.error_not_found, CommandExceptionReason.CommandError);
		[Command("getmy channel")]
		public static ulong CommandGetChannel(ClientCall invoker)
			=> invoker.ChannelId?.Value ?? throw new CommandException(strings.error_not_found, CommandExceptionReason.CommandError);
		[Command("getmy all")]
		public static JsonValue<ClientCall> CommandGetUser(ClientCall invoker)
			=> new JsonValue<ClientCall>(invoker, $"Client: Id:{invoker.ClientId} DbId:{invoker.DatabaseId} ChanId:{invoker.ChannelId} Uid:{invoker.ClientUid}"); // LOC: TODO

		[Command("getuser uid byid")]
		public static string CommandGetUidById(Ts3Client ts3Client, ushort id) => ts3Client.GetFallbackedClientById((ClientId)id).UnwrapThrow().Uid.Value;
		[Command("getuser name byid")]
		public static string CommandGetNameById(Ts3Client ts3Client, ushort id) => ts3Client.GetFallbackedClientById((ClientId)id).UnwrapThrow().Name;

		[Command("getuser name byuid")]
		public static string CommandGetNameByUId(TsFullClient fullClient, string uid) {
			var res = fullClient.GetClientNameFromUid(Uid.To(uid));
			if (!res.Ok) {
				throw new CommandException(res.Error.Message, CommandExceptionReason.CommandError);
			}

			return res.Value.Name;
		}

		[Command("getuser dbid byid")]
		public static ulong CommandGetDbIdById(Ts3Client ts3Client, ushort id) => ts3Client.GetFallbackedClientById((ClientId)id).UnwrapThrow().DatabaseId.Value;
		[Command("getuser channel byid")]
		public static ulong CommandGetChannelById(Ts3Client ts3Client, ushort id) => ts3Client.GetFallbackedClientById((ClientId)id).UnwrapThrow().ChannelId.Value;
		[Command("getuser all byid")]
		public static JsonValue<ClientList> CommandGetUserById(Ts3Client ts3Client, ushort id)
		{
			var client = ts3Client.GetFallbackedClientById((ClientId)id).UnwrapThrow();
			return new JsonValue<ClientList>(client, $"Client: Id:{client.ClientId} DbId:{client.DatabaseId} ChanId:{client.ChannelId} Uid:{client.Uid}");
		}
		[Command("getuser id byname")]
		public static ushort CommandGetIdByName(Ts3Client ts3Client, string username) => ts3Client.GetClientByName(username).UnwrapThrow().ClientId.Value;
		[Command("getuser all byname")]
		public static JsonValue<ClientList> CommandGetUserByName(Ts3Client ts3Client, string username)
		{
			var client = ts3Client.GetClientByName(username).UnwrapThrow();
			return new JsonValue<ClientList>(client, $"Client: Id:{client.ClientId} DbId:{client.DatabaseId} ChanId:{client.ChannelId} Uid:{client.Uid}");
		}
		[Command("getuser name bydbid")]
		public static string CommandGetNameByDbId(Ts3Client ts3Client, ulong dbId) => ts3Client.GetDbClientByDbId((ClientDbId)dbId).UnwrapThrow().Name;
		[Command("getuser uid bydbid")]
		public static string CommandGetUidByDbId(Ts3Client ts3Client, ulong dbId) => ts3Client.GetDbClientByDbId((ClientDbId)dbId).UnwrapThrow().Uid.Value;

		private static readonly TextMod HelpCommand = new TextMod(TextModFlag.Bold);
		private static readonly TextMod HelpCommandParam = new TextMod(TextModFlag.Italic);

		[Command("help")]
		public static string CommandHelp(CallerInfo callerInfo)
		{
			var tmb = new TextModBuilder(callerInfo.IsColor);
			tmb.AppendLine("TS3AudioBot at your service!");
			tmb.AppendLine("To get some basic help on how to get started use one of the following commands:");
			tmb.Append("!help play", HelpCommand).AppendLine(" : basics for playing songs");
			tmb.Append("!help playlists", HelpCommand).AppendLine(" : how to manage playlists");
			tmb.Append("!help history", HelpCommand).AppendLine(" : viewing and accesing the play history");
			tmb.Append("!help bot", HelpCommand).AppendLine(" : useful features to configure your bot");
			tmb.Append("!help all", HelpCommand).AppendLine(" : show all commands");
			tmb.Append("!help command", HelpCommand).Append(" <command path>", HelpCommandParam).AppendLine(" : help text of a specific command");
			var str = tmb.ToString();
			return str;
		}

		[Command("help all", "_undocumented")]
		public static JsonArray<string> CommandHelpAll(CommandManager commandManager)
		{
			var botComList = commandManager.RootGroup.Commands.Select(c => c.Key).ToArray();
			return new JsonArray<string>(botComList, bcl =>
			{
				var strb = new StringBuilder();
				foreach (var botCom in bcl)
					strb.Append(botCom).Append(", ");
				strb.Length -= 2;
				return strb.ToString();
			});
		}

		[Command("help command", "_undocumented")]
		public static JsonObject CommandHelpCommand(CommandManager commandManager, IFilter filter = null, params string[] command)
		{
			if (command.Length == 0)
			{
				return new JsonEmpty(strings.error_cmd_at_least_one_argument);
			}

			CommandGroup group = commandManager.RootGroup;
			ICommand target = group;
			filter = filter ?? Filter.DefaultFilter;
			var realPath = new List<string>();
			for (int i = 0; i < command.Length; i++)
			{
				var possibilities = filter.Filter(group.Commands, command[i]).ToList();
				if (possibilities.Count <= 0)
					throw new CommandException(strings.cmd_help_error_no_matching_command, CommandExceptionReason.CommandError);
				if (possibilities.Count > 1)
					throw new CommandException(string.Format(strings.cmd_help_error_ambiguous_command, string.Join(", ", possibilities.Select(kvp => kvp.Key))), CommandExceptionReason.CommandError);

				realPath.Add(possibilities[0].Key);
				target = possibilities[0].Value;

				if (i < command.Length - 1)
				{
					group = target as CommandGroup;
					if (group is null)
						throw new CommandException(string.Format(strings.cmd_help_error_no_further_subfunctions, string.Join(" ", realPath, 0, i)), CommandExceptionReason.CommandError);
				}
			}

			switch (target)
			{
			case BotCommand targetB:
				return new JsonValue<BotCommand>(targetB);
			case CommandGroup targetCg:
				var subList = targetCg.Commands.Select(g => g.Key).ToArray();
				return new JsonArray<string>(subList, string.Format(strings.cmd_help_info_contains_subfunctions, string.Join(", ", subList)));
			case OverloadedFunctionCommand targetOfc:
				var strb = new StringBuilder();
				foreach (var botCom in targetOfc.Functions.OfType<BotCommand>())
					strb.Append(botCom);
				return new JsonValue<string>(strb.ToString());
			case AliasCommand targetAlias:
				return new JsonValue<string>(string.Format("'{0}' is an alias for:\n{1}", string.Join(" ", realPath), targetAlias.AliasString));
			default:
				throw new CommandException(strings.cmd_help_error_unknown_error, CommandExceptionReason.CommandError);
			}
		}

		[Command("history add")]
		public static void CommandHistoryQueue(HistoryManager historyManager, PlayManager playManager, InvokerData invoker, uint hid)
		{
			var ale = historyManager.GetEntryById(hid).UnwrapThrow();
			playManager.Enqueue(ale.AudioResource, new MetaData(invoker.ClientUid, null)).UnwrapThrow();
		}

		[Command("history clean")]
		public static JsonEmpty CommandHistoryClean(DbStore database, CallerInfo caller, UserSession session = null)
		{
			if (caller.ApiCall)
			{
				database.CleanFile();
				return new JsonEmpty(string.Empty);
			}

			string ResponseHistoryClean(string message)
			{
				if (TextUtil.GetAnswer(message) == Answer.Yes)
				{
					database.CleanFile();
					return strings.info_cleanup_done;
				}
				return null;
			}
			session.SetResponse(ResponseHistoryClean);
			return new JsonEmpty($"{strings.cmd_history_clean_confirm_clean} {strings.info_bot_might_be_unresponsive} {YesNoOption}");
		}

		[Command("history clean removedefective")]
		public static JsonEmpty CommandHistoryCleanRemove(HistoryManager historyManager, ResolveContext resourceFactory, CallerInfo caller, UserSession session = null)
		{
			if (caller.ApiCall)
			{
				historyManager.RemoveBrokenLinks(resourceFactory);
				return new JsonEmpty(string.Empty);
			}

			string ResponseHistoryCleanRemove(string message)
			{
				if (TextUtil.GetAnswer(message) == Answer.Yes)
				{
					historyManager.RemoveBrokenLinks(resourceFactory);
					return strings.info_cleanup_done;
				}
				return null;
			}
			session.SetResponse(ResponseHistoryCleanRemove);
			return new JsonEmpty($"{strings.cmd_history_clean_removedefective_confirm_clean} {strings.info_bot_might_be_unresponsive} {YesNoOption}");
		}

		[Command("history clean upgrade", "_undocumented")]
		public static void CommandHistoryCleanUpgrade(HistoryManager historyManager, Ts3Client ts3Client)
		{
			historyManager.UpdadeDbIdToUid(ts3Client);
		}

		[Command("history delete")]
		public static JsonEmpty CommandHistoryDelete(HistoryManager historyManager, CallerInfo caller, uint id, UserSession session = null)
		{
			var ale = historyManager.GetEntryById(id).UnwrapThrow();

			if (caller.ApiCall)
			{
				historyManager.RemoveEntry(ale);
				return new JsonEmpty(string.Empty);
			}

			string ResponseHistoryDelete(string message)
			{
				Answer answer = TextUtil.GetAnswer(message);
				if (answer == Answer.Yes)
				{
					historyManager.RemoveEntry(ale);
				}
				return null;
			}

			session.SetResponse(ResponseHistoryDelete);
			string name = ale.AudioResource.ResourceTitle;
			if (name.Length > 100)
				name = name.Substring(100) + "...";
			return new JsonEmpty(string.Format(strings.cmd_history_delete_confirm + YesNoOption, name, id));
		}

		[Command("history from")]
		public static JsonArray<AudioLogEntry> CommandHistoryFrom(HistoryManager historyManager, string userUid, int? amount = null)
		{
			var query = new SeachQuery { UserUid = userUid };
			if (amount.HasValue)
				query.MaxResults = amount.Value;

			var results = historyManager.Search(query).ToArray();
			return new JsonArray<AudioLogEntry>(results, historyManager.Format);
		}

		[Command("history id", "cmd_history_id_uint_help")]
		public static JsonValue<AudioLogEntry> CommandHistoryId(HistoryManager historyManager, uint id)
		{
			var result = historyManager.GetEntryById(id).UnwrapThrow();
			return new JsonValue<AudioLogEntry>(result, r => historyManager.Format(r));
		}

		[Command("history id", "cmd_history_id_string_help")]
		public static JsonValue<uint> CommandHistoryId(HistoryManager historyManager, string special)
		{
			if (special == "last")
				return new JsonValue<uint>(historyManager.HighestId, string.Format(strings.cmd_history_id_last, historyManager.HighestId));
			else if (special == "next")
				return new JsonValue<uint>(historyManager.HighestId + 1, string.Format(strings.cmd_history_id_next, historyManager.HighestId + 1));
			else
				throw new CommandException("Unrecognized name descriptor", CommandExceptionReason.CommandError);
		}

		[Command("history last", "cmd_history_last_int_help")]
		public static JsonArray<AudioLogEntry> CommandHistoryLast(HistoryManager historyManager, int amount)
		{
			var query = new SeachQuery { MaxResults = amount };
			var results = historyManager.Search(query).ToArray();
			return new JsonArray<AudioLogEntry>(results, historyManager.Format);
		}

		[Command("history rename")]
		public static void CommandHistoryRename(HistoryManager historyManager, uint id, string newName)
		{
			var ale = historyManager.GetEntryById(id).UnwrapThrow();

			if (string.IsNullOrWhiteSpace(newName))
				throw new CommandException(strings.cmd_history_rename_invalid_name, CommandExceptionReason.CommandError);

			historyManager.RenameEntry(ale, newName);
		}

		[Command("history till", "cmd_history_till_DateTime_help")]
		public static JsonArray<AudioLogEntry> CommandHistoryTill(HistoryManager historyManager, DateTime time)
		{
			var query = new SeachQuery { LastInvokedAfter = time };
			var results = historyManager.Search(query).ToArray();
			return new JsonArray<AudioLogEntry>(results, historyManager.Format);
		}

		[Command("history till", "cmd_history_till_string_help")]
		public static JsonArray<AudioLogEntry> CommandHistoryTill(HistoryManager historyManager, string time)
		{
			DateTime tillTime;
			switch (time.ToLowerInvariant())
			{
			case "hour": tillTime = DateTime.Now.AddHours(-1); break;
			case "today": tillTime = DateTime.Today; break;
			case "yesterday": tillTime = DateTime.Today.AddDays(-1); break;
			case "week": tillTime = DateTime.Today.AddDays(-7); break;
			default: throw new CommandException(strings.error_unrecognized_descriptor, CommandExceptionReason.CommandError);
			}
			var query = new SeachQuery { LastInvokedAfter = tillTime };
			var results = historyManager.Search(query).ToArray();
			return new JsonArray<AudioLogEntry>(results, historyManager.Format);
		}

		[Command("history title")]
		public static JsonArray<AudioLogEntry> CommandHistoryTitle(HistoryManager historyManager, string part)
		{
			var query = new SeachQuery { TitlePart = part };
			var results = historyManager.Search(query).ToArray();
			return new JsonArray<AudioLogEntry>(results, historyManager.Format);
		}

		[Command("if")]
		[Usage("<argument0> <comparator> <argument1> <then>", "Compares the two arguments and returns or executes the then-argument")]
		[Usage("<argument0> <comparator> <argument1> <then> <else>", "Same as before and return the else-arguments if the condition is false")]
		public static object CommandIf(ExecutionInformation info, IReadOnlyList<Type> returnTypes, string arg0, string cmp, string arg1, ICommand then, ICommand other = null)
		{
			Func<double, double, bool> comparer;
			switch (cmp)
			{
			case "<": comparer = (a, b) => a < b; break;
			case ">": comparer = (a, b) => a > b; break;
			case "<=": comparer = (a, b) => a <= b; break;
			case ">=": comparer = (a, b) => a >= b; break;
			case "==": comparer = (a, b) => Math.Abs(a - b) < 1e-6; break;
			case "!=": comparer = (a, b) => Math.Abs(a - b) > 1e-6; break;
			default: throw new CommandException(strings.cmd_if_unknown_operator, CommandExceptionReason.CommandError);
			}

			bool cmpResult;
			// Try to parse arguments into doubles
			if (double.TryParse(arg0, NumberStyles.Number, CultureInfo.InvariantCulture, out var d0)
				&& double.TryParse(arg1, NumberStyles.Number, CultureInfo.InvariantCulture, out var d1))
			{
				cmpResult = comparer(d0, d1);
			}
			else
			{
				cmpResult = comparer(string.CompareOrdinal(arg0, arg1), 0);
			}

			// If branch
			if (cmpResult)
				return then.Execute(info, Array.Empty<ICommand>(), returnTypes);
			// Else branch
			if (other != null)
				return other.Execute(info, Array.Empty<ICommand>(), returnTypes);

			// Try to return nothing
			if (returnTypes.Contains(null))
				return null;
			throw new CommandException(strings.error_nothing_to_return, CommandExceptionReason.NoReturnMatch);
		}

		private static readonly TextMod SongDone = new TextMod(TextModFlag.Color, Color.Gray);
		private static readonly TextMod SongCurrent = new TextMod(TextModFlag.Bold);

		private static int GetIndexExpression(PlayManager playManager, string expression)
		{
			int index;
			if (string.IsNullOrEmpty(expression))
			{
				index = playManager.Queue.Index;
			}
			else if (expression.StartsWith("@"))
			{
				var subOffset = expression.Substring(1).Trim();
				if (string.IsNullOrEmpty(subOffset))
					index = 0;
				else if (!int.TryParse(subOffset, out index))
					throw new CommandException(strings.error_unrecognized_descriptor, CommandExceptionReason.CommandError);
				index += playManager.Queue.Index;
			}
			else if (!int.TryParse(expression, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
			{
				throw new CommandException(strings.error_unrecognized_descriptor, CommandExceptionReason.CommandError);
			}
			return index;
		}

		private const int SearchMaxItems = 1000;

		public class PlaylistSearchResult {
			// offset of this query, offset + results <= totalresults
			[JsonProperty(PropertyName = "offset")]
			public int Offset { get; set; }

			// total #results with duplicate entries (multiple matches)
			[JsonProperty(PropertyName = "totalresults")]
			public int TotalResults { get; set; }

			// #results with duplicate entries in this result instance
			[JsonProperty(PropertyName = "results")]
			public int Results { get; set; }

			// the actual results (this set only contains unique items)
			[JsonProperty(PropertyName = "items")]
			public List<PlaylistSearchItemInfo> Items { get; set; }
		}

		[Command("items")]
		public static JsonValue<PlaylistSearchResult> CommandItems(ResourceSearch resourceSearch, string query) {
			return CommandItemsFrom(resourceSearch, 0, 50, query);
		}

		[Command("itemsf")]
		public static JsonValue<PlaylistSearchResult> CommandItemsFrom(ResourceSearch resourceSearch, int from, int count, string query) {
			Stopwatch timer = new Stopwatch();
			timer.Start();
			var res = resourceSearch.Find(query, from, Math.Min(SearchMaxItems, count)).UnwrapThrow();
			Log.Info($"Search for \"{query}\" took {timer.ElapsedMilliseconds}ms");

			return new JsonValue<PlaylistSearchResult>(new PlaylistSearchResult { Offset = from, Items = res.Items, Results = res.ConsumedResults, TotalResults = res.TotalResults }, result => {
				StringBuilder builder = new StringBuilder();
				builder.Append("Found ").Append(result.TotalResults).Append(" result(s).");
				if (result.TotalResults > result.Items.Count)
					builder.Append(" Showing only ").Append(result.Items.Count).Append(" unique items out of ").Append(result.Results).Append(" items.");

				for (int i = 0; i < result.Items.Count; ++i) {
					var item = result.Items[i];
					builder.AppendLine().Append(i + result.Offset).Append(": ").Append(item.ResourceTitle).Append(" (");

					for (var j = 0; j < item.ContainingLists.Count; j++) {
						var cl = item.ContainingLists[j];
						if (j != 0)
							builder.Append(',');
						builder.Append(cl.Id).Append(':').Append(cl.Index);
					}

					builder.Append(')');
				}

				return builder.ToString();
			});
		}

		[Command("info")]
		public static JsonValue<QueueInfo> CommandInfo(ResolveContext resourceFactory, PlayManager playManager, string offset = null, int? count = null)
			=> CommandInfo(resourceFactory, playManager, GetIndexExpression(playManager, offset ?? "@-1"), count);

		[Command("info")]
		public static JsonValue<QueueInfo> CommandInfo(ResolveContext resourceFactory, PlayManager playManager, int offset, int? count = null)
		{
			const int maxSongs = 20;
			var playIndex = playManager.Queue.Index;
			var plist = playManager.Queue.Items;
			int offsetV = Tools.Clamp(offset, 0, plist.Count);
			int countV = Tools.Clamp(count ?? 3, 0, Math.Min(maxSongs, plist.Count - offsetV));
			var items = plist.Skip(offsetV).Take(countV).Select(item => resourceFactory.ToApiFormat(item.AudioResource)).ToArray();

			var plInfo = new QueueInfo
			{
				Id = ".mix",
				SongCount = plist.Count,
				DisplayOffset = offsetV,
				Items = items,
				PlaybackIndex = playIndex,
			};

			return JsonValue.Create(plInfo, x =>
			{
				if (x.SongCount == 0)
					return strings.info_currently_not_playing;

				var tmb = new TextModBuilder();

				string CurLine(int i) => $"{x.DisplayOffset + i}: {x.Items[i].Title}";

				tmb.AppendFormat(strings.cmd_info_header, x.Id.Mod().Bold(), x.SongCount.ToString()).Append("\n");

				for (int i = 0; i < x.Items.Length; i++)
				{
					var line = CurLine(i);
					var plIndex = x.DisplayOffset + i;
					if (plIndex == x.PlaybackIndex)
						tmb.AppendLine("> " + line, SongCurrent);
					else if (plIndex < x.PlaybackIndex)
						tmb.AppendLine(line, SongDone);
					else if (plIndex > x.PlaybackIndex)
						tmb.AppendLine(line);
					else
						break; // ?
				}

				return tmb.ToString();
			});
		}

		[Command("json merge")]
		public static JsonArray<object> CommandJsonMerge(ExecutionInformation info, ApiCall _, IReadOnlyList<ICommand> arguments)
		{
			if (arguments.Count == 0)
				return new JsonArray<object>(Array.Empty<object>(), string.Empty);

			var jsonArr = arguments
				.Select(arg =>
				{
					object res;
					try { res = arg.Execute(info, Array.Empty<ICommand>(), ReturnJson); }
					catch (CommandException) { return null; }
					if (res is JsonObject o)
						return o.GetSerializeObject();
					else
						throw new CommandException(strings.error_nothing_to_return, CommandExceptionReason.NoReturnMatch);
				})
				.ToArray();

			return new JsonArray<object>(jsonArr, string.Empty);
		}

		[Command("json api", "_undocumented")]
		public static JsonObject CommandJsonApi(CommandManager commandManager, ApiCall _, BotManager botManager = null)
		{
			var bots = botManager?.GetBotInfolist() ?? Array.Empty<BotInfo>();
			var api = OpenApiGenerator.Generate(commandManager, bots);
			return new JsonValue<JObject>(api, string.Empty);
		}

		[Command("kickme")]
		public static void CommandKickme(Ts3Client ts3Client, ClientCall invoker)
			=> CommandKickme(ts3Client, invoker, false);

		[Command("kickme far", "cmd_kickme_help")]
		public static void CommandKickmeFar(Ts3Client ts3Client, ClientCall invoker)
			=> CommandKickme(ts3Client, invoker, true);

		private static void CommandKickme(Ts3Client ts3Client, ClientCall invoker, bool far)
		{
			if (!invoker.ClientId.HasValue)
				return;

			E<LocalStr> result = far
				? ts3Client.KickClientFromServer(invoker.ClientId.Value)
				: ts3Client.KickClientFromChannel(invoker.ClientId.Value);
			if (!result.Ok)
				throw new CommandException(strings.cmd_kickme_missing_permission, CommandExceptionReason.CommandError);
		}

		[Command("reload lists")]
		public static string CommandReloadPlaylists(PlaylistManager playlistManager, ResourceSearch search) {
			Log.Info("Reloading playlists...");
			playlistManager.ReloadFromDisk();
			Log.Info("Reloading search...");
			search.Rebuild();
			return "Lists and search reloaded";
		}

		[Command("reload search")]
		public static string CommandReloadSearch(ResourceSearch search) {
			Log.Info("Reloading search...");
			search.Rebuild();
			return "Search reloaded";
		}

		[Command("reload rights")]
		public static JsonEmpty CommandReloadRights(RightsManager rightsManager) => CommandRightsReload(rightsManager);

		private const string RightBypassManageCheck = "list.manage.unowned";

		private static void ThrowPlaylistNoPermission(string id, string action) {
			throw new CommandException("You can't " + action + " the playlist \"" + id + "\"", CommandExceptionReason.Unauthorized);
		}

		private static bool IsPlaylistManagableBy(IPlaylistEditors playlist, InvokerData invoker, ExecutionInformation info) {
			return playlist.Owner == invoker.ClientUid || info.HasRights(RightBypassManageCheck);
		}

		public static void CheckPlaylistManageable(string id, IPlaylistEditors playlist, ExecutionInformation info, string action = "manage")
		{
			if (info.TryGet<CallerInfo>(out var caller) && caller.SkipRightsChecks)
				return;

			if (info.TryGet<InvokerData>(out var invoker) && IsPlaylistManagableBy(playlist, invoker, info))
				return;

			ThrowPlaylistNoPermission(id, action);
		}

		public static void CheckPlaylistModifiable(string id, IPlaylistEditors playlist, ExecutionInformation info, string action = "modify") {
			if (info.TryGet<CallerInfo>(out var caller) && caller.SkipRightsChecks)
				return;

			if (info.TryGet<InvokerData>(out var invoker) && playlist.AdditionalEditors.Contains(invoker.ClientUid))
				return;

			if (IsPlaylistManagableBy(playlist, invoker, info))
				return;

			ThrowPlaylistNoPermission(id, action);
		}

		public static E<LocalStr> ModifyPlaylist(PlaylistManager playlistManager, string userProvidedId, ExecutionInformation info, Action<PlaylistDatabase.PlaylistEditor> action) {
			return playlistManager.ModifyPlaylist(userProvidedId, editor => {
				CheckPlaylistModifiable(editor.Id, editor.Playlist, info);
				action(editor);
			});
		}

		public static E<LocalStr> ManagePlaylist(PlaylistManager playlistManager, string userProvidedId, ExecutionInformation info, Action<PlaylistDatabase.PlaylistEditor> action) {
			return playlistManager.ModifyPlaylist(userProvidedId, editor => {
				CheckPlaylistManageable(editor.Id, editor.Playlist, info);
				action(editor);
			});
		}

		public static R<int> ListAddItem(PlaylistManager playlistManager, ExecutionInformation info, string userProvidedId, AudioResource resource) {
			int? index = null;
			ModifyPlaylist(playlistManager, userProvidedId, info, editor => {
				if(editor.Add(resource))
					index = editor.Playlist.Count - 1;
			}).UnwrapThrow();
			if(index.HasValue)
				return index.Value;
			return R.Err;
		}
		
		[Command("list create", "_undocumented")]
		public static void CommandListCreate(PlaylistManager playlistManager, InvokerData invoker, string listId)
			=> playlistManager.CreatePlaylist(listId, invoker.ClientUid).UnwrapThrow();

		[Command("list delete")]
		public static JsonEmpty CommandListDelete(PlaylistManager playlistManager, UserSession session, ExecutionInformation info, string userProvidedId) {
			var (list, id) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
			CheckPlaylistManageable(id, list, info, "manage");
			string ResponseListDelete(string message)
			{
				if (TextUtil.GetAnswer(message) == Answer.Yes)
				{
					playlistManager.DeletePlaylist(id).UnwrapThrow();
				}
				return null;
			}

			session.SetResponse(ResponseListDelete);
			return new JsonEmpty(string.Format(strings.cmd_list_delete_confirm, id));
		}

		[Command("list delete")]
		public static void CommandListDelete(PlaylistManager playlistManager, ApiCall _, string listId)
			=> playlistManager.DeletePlaylist(listId).UnwrapThrow();

		[Command("list editor show")]
		public static string CommandListEditorList(TsFullClient client, PlaylistManager playlistManager, string userProvidedId) {
			var (list, id) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
			StringBuilder result = new StringBuilder();
			result.Append("The playlist \"").Append(id).Append("\"");
			if (list.AdditionalEditors.Count == 0) {
				result.Append(" has no additional editors.");
			} else {
				result.Append(" has the following additional editors:");
				foreach(var editor in list.AdditionalEditors) {
					string name = UidToClientName(client, editor);
					result.AppendLine().Append("- ").Append(name);
				}
			}

			return result.ToString();
		}

		[Command("list editor toggle")]
		public static string CommandListEditorAdd(Ts3Client cli, PlaylistManager playlistManager, ExecutionInformation info, string userProvidedId, string clientName) {
			var newStatus = false;
			string id = null;
			playlistManager.ModifyPlaylistEditors(userProvidedId, (realId, editors) => {
				CheckPlaylistManageable(id, editors, info);
				id = realId;
				var client = cli.GetClientByNameExact(clientName).UnwrapThrow();
				var uid = client.Uid;
				if(uid == editors.Owner)
					throw new CommandException("The editor status of the owner of this list can't be toggled.", CommandExceptionReason.CommandError);
				newStatus = editors.ToggleAdditionalEditor(uid);
			}).UnwrapThrow();

			if (newStatus)
				return "Added " + clientName + " to the editors of \"" + id + "\".";
			return "Removed " + clientName + " from the editors of \"" + id + "\".";
		}

		[Command("list from", "_undocumented")]
		public static JsonValue<PlaylistInfo> PropagiateLoad(TsFullClient client, PlaylistManager playlistManager, ResolveContext resolver, ExecutionInformation info, InvokerData invoker, string resolverName, string listId, string url)
		{
			var getList = resolver.LoadPlaylistFrom(url, invoker.ClientUid, resolverName).UnwrapThrow();
			return ImportMerge(client, playlistManager, resolver, getList, info, invoker.ClientUid, listId);
		}

		[Command("list import", "cmd_list_get_help")] // TODO readjust help texts
		public static JsonValue<PlaylistInfo> CommandListImport(TsFullClient client, PlaylistManager playlistManager, ResolveContext resolver, ExecutionInformation info, InvokerData invoker, string listId, string link)
		{
			var getList = resolver.LoadPlaylistFrom(link, Uid.Null).UnwrapThrow();
			return ImportMerge(client, playlistManager, resolver, getList, info, invoker.ClientUid, listId); ;
		}

		private static JsonValue<PlaylistInfo> ImportMerge(TsFullClient client, PlaylistManager playlistManager, ResolveContext resolver, Playlist addList, ExecutionInformation info, Uid invoker, string listId)
		{
			if (!playlistManager.ExistsPlaylist(listId))
				playlistManager.CreatePlaylist(listId, invoker).UnwrapThrow();

			string id = null;
			ModifyPlaylist(playlistManager, listId, info, editor => {
				id = editor.Id;
				editor.AddRange(addList.Items);
			}).UnwrapThrow();

			return CommandListShow(client, playlistManager, resolver, id, null, null);
		}

		public static void DoBoundsCheck(IPlaylist playlist, int index) {
			if (!Tools.IsBetweenExcludingUpper(index, 0, playlist.Count))
				throw new CommandException(strings.error_playlist_item_index_out_of_range, CommandExceptionReason.CommandError);
		}

		public static void DoBoundsCheckRange(IPlaylist playlist, int from, int to) {
			DoBoundsCheck(playlist, from);
			DoBoundsCheck(playlist, to);
			if (!(from < to))
				throw new CommandException(strings.error_playlist_item_index_out_of_range, CommandExceptionReason.CommandError);
		}

		[Command("list item get", "_undocumented")]
		public static PlaylistItem CommandListItemGet(PlaylistManager playlistManager, string name, int index)
		{
			var (plist, _) = playlistManager.GetPlaylist(name).UnwrapThrow();
			DoBoundsCheck(plist, index);

			return new PlaylistItem(plist[index]);
		}

		public class GainValue {
			public int? Value { get; }

			public GainValue(int? value) { Value = value; }
		}

		[Command("list item gain get")]
		public static JsonValue<GainValue> CommandListItemGainGet(PlaylistManager playlistManager, string userProvidedId, int index)
		{
			var (plist, _) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
			DoBoundsCheck(plist, index);

			return new JsonValue<GainValue>(new GainValue(plist[index].Gain), g => g.Value.HasValue ? $"Gain is {g.Value.Value}db" : "Gain is not set.");
		}

		[Command("list item gain set")]
		public static JsonValue<GainValue> CommandListItemGainSet(PlaylistManager playlistManager, ExecutionInformation info, string userProvidedId, int index, int? value = null) {
			int? gain;
			lock (playlistManager.Lock) {
				var (list, id) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
				DoBoundsCheck(list, index);
				var resource = list[index];
				if (playlistManager.TryGetUniqueResourceInfo(resource, out var resInfo)) {
					foreach (var resInfoContainingList in resInfo.ContainingLists) {
						var (containingList, _) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
						CheckPlaylistModifiable(resInfoContainingList.Key, containingList, info);
					}

					playlistManager.ChangeItemAtDeepSane(id, index, resource.WithGain(value)).UnwrapThrow();
					gain = value;
				} else {
					gain = resource.Gain;
				}
			}

			return new JsonValue<GainValue>(new GainValue(gain), g => g.Value.HasValue ? $"Set the gain to {g.Value.Value}." : "Reset the gain.");
		}

		[Command("list item info")]
		public static string CommandListItemInfo(PlaylistManager playlistManager, string userProvidedId, int index) {
			var (list, id) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
			if (index < 0 || index >= list.Count)
				throw new CommandException(strings.error_playlist_item_index_out_of_range, CommandExceptionReason.CommandError);
			var item = list[index];
			var builder = new StringBuilder();
			builder.Append("Item at index ").Append(index).Append(" of list \"").Append(id).AppendLine("\":");
			builder.Append("Resource title: ").Append(item.ResourceTitle).AppendLine();
			builder.Append("Resource id: ").Append(item.ResourceId).AppendLine();
			builder.Append("Audio type: ").Append(item.AudioType).AppendLine();
			bool contained = playlistManager.TryGetUniqueResourceInfo(item, out var uniqueResourceInfo);
			builder.Append("Is in database: ").Append(contained).AppendLine();
			if (contained) {
				builder.Append("Database resource equals: ").Append(Equals(uniqueResourceInfo.Resource, item))
					.AppendLine();
				builder.Append("Database resource reference equals: ").Append(ReferenceEquals(uniqueResourceInfo.Resource, item))
					.AppendLine();
				builder.Append("Database resource containing lists: ");

				int j = 0;
				foreach (var kv in uniqueResourceInfo.ContainingLists) {
					if (j++ != 0)
						builder.Append(',');
					builder.Append(kv.Key).Append(':').Append(kv.Value);
				}
				builder.AppendLine();
			}

			builder.Append("Hash: ").Append(item.GetHashCode()).AppendLine();

			builder.Append("Gain: ");
			if (item.Gain.HasValue)
				builder.Append(item.Gain.Value);
			else
				builder.Append("Not set");
			builder.AppendLine();

			builder.Append("Title is set by user: ");
			if (item.TitleIsUserSet.HasValue)
				builder.Append(item.TitleIsUserSet.Value);
			else
				builder.Append("Not set");
			builder.AppendLine();

			if (item.AdditionalData != null && item.AdditionalData.Count > 0) {
				builder.AppendLine("Additional data: ");
				foreach (var kv in item.AdditionalData) {
					builder.AppendLine().Append("- ").Append(kv.Key).Append(" -> ").Append(kv.Value);
				}
			}
			return builder.ToString();
		}

		[Command("list item equal")]
		public static string CommandListItemInfo(
			PlaylistManager playlistManager, string userProvidedIdA, int indexA,
			string userProvidedIdB, int indexB) {
			var (listA, _) = playlistManager.GetPlaylist(userProvidedIdA).UnwrapThrow();
			var (listB, _) = playlistManager.GetPlaylist(userProvidedIdB).UnwrapThrow();
			DoBoundsCheck(listA, indexA);
			DoBoundsCheck(listB, indexB);

			var itemA = listA[indexA];
			var itemB = listB[indexB];
			var builder = new StringBuilder();
			builder.Append("Audio resources are equal: ").Append(Equals(itemA, itemB)).AppendLine();
			builder.Append("Audio resources are reference equal: ").Append(ReferenceEquals(itemA, itemB)).AppendLine();
			builder.Append("Audio types are equal: ").Append(Equals(itemA.AudioType, itemB.AudioType)).AppendLine();
			builder.Append("Resource ids equal: ").Append(Equals(itemA.ResourceId, itemB.ResourceId)).AppendLine();
			builder.Append("Titles are equal: ").Append(Equals(itemA.ResourceTitle, itemB.ResourceTitle)).AppendLine();
			builder.Append("Hash values are equal: ").Append(Equals(itemA.GetHashCode(), itemB.GetHashCode())).AppendLine();
			return builder.ToString();
		}

		[Command("list item move")] // TODO return modified elements
		public static void CommandListItemMove(PlaylistManager playlistManager, ExecutionInformation info, string userProvidedId, int from, int to)
		{
			ModifyPlaylist(playlistManager, userProvidedId, info, editor =>
			{
				DoBoundsCheck(editor.Playlist, from);
				DoBoundsCheck(editor.Playlist, to);

				if (from == to)
					return;

				editor.MoveItem(from, to);
			}).UnwrapThrow();
		}

		[Command("list item delete")] // TODO return modified elements
		public static JsonEmpty CommandListItemDelete(PlaylistManager playlistManager, ExecutionInformation info, string userProvidedId, int index /* TODO param */)
		{
			AudioResource deletedItem = null;
			ModifyPlaylist(playlistManager, userProvidedId, info, editor =>
			{
				DoBoundsCheck(editor.Playlist, index);

				deletedItem = editor.RemoveItemAt(index);
			}).UnwrapThrow();
			return new JsonEmpty(string.Format(strings.info_removed, deletedItem.ResourceTitle));
		}

		[Command("list item name")] // TODO return modified elements
		public static string CommandListItemName(PlaylistManager playlistManager, ExecutionInformation info, string userProvidedId, int index, string title) {
			bool success = false;
			ModifyPlaylist(playlistManager, userProvidedId, info, editor => {
				DoBoundsCheck(editor.Playlist, index);
				success = editor.ChangeItemAt(index, editor.Playlist[index].WithUserTitle(title));
			}).UnwrapThrow();

			if (!success)
				return $"Failed to set the name because this resource is already contained in {userProvidedId} with this name.";
			return $"Changed the name of the item at position {index} in {userProvidedId} to {title}";
		}

		[Command("list list")]
		[Usage("<pattern>", "Filters all lists cantaining the given pattern.")]
		public static JsonArray<PlaylistInfo> CommandListList(PlaylistManager playlistManager, TsFullClient tsFullClient)
		{
			var files = playlistManager.GetAvailablePlaylists();
			if (files.Length <= 0)
				return new JsonArray<PlaylistInfo>(files, strings.error_playlist_not_found);

			Array.Sort(files);
			return new JsonArray<PlaylistInfo>(files, fi =>
				string.Join("\n", fi.Select(x =>
					x.Id +
					" (Owner: " + tsFullClient.GetClientNameFromUid(Uid.To(x.OwnerId)).Unwrap().Name +
					", Songs: " + x.SongCount + ')'
				))
			);
		}

		[Command("list merge")]
		public static void CommandListMerge(PlaylistManager playlistManager, ExecutionInformation info, string baseListId, string mergeListId) // future overload?: (IROP, IROP) -> IROP
		{
			var (otherList, _) = playlistManager.GetPlaylist(mergeListId).UnwrapThrow();
			ModifyPlaylist(playlistManager, baseListId, info, editor => {
				editor.AddRange(otherList.Items);
			}).UnwrapThrow();
		}

		[Command("list queue")]
		public static void CommandListQueue(PlaylistManager playlistManager, PlayManager playManager, InvokerData invoker, string userProvidedId)
		{
			var (plist, id) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
			playManager.Enqueue(plist.Items, new MetaData(invoker.ClientUid, id)).UnwrapThrow();
		}

		public static string UidToClientName(TsFullClient ts3Client, Uid client) {
			return ts3Client.GetClientNameFromUid(client).Unwrap().Name;
		}

		[Command("list show")]
		[Usage("<name> <index>", "Lets you specify the starting index from which songs should be listed.")]
		public static JsonValue<PlaylistInfo> CommandListShow(TsFullClient ts3Client, PlaylistManager playlistManager, ResolveContext resourceFactory, string userProvidedId, int? offset = null, int? count = null) {
			const int defaultSongCount = 50;
			var (plist, id) = playlistManager.GetPlaylist(userProvidedId).UnwrapThrow();
			int offsetV = Tools.Clamp(offset ?? 0, 0, plist.Count);
			int countV = count ?? defaultSongCount;
			if (countV == -1) {
				// All items if specifically requested
				countV = plist.Count - offsetV;
			} else {
				countV = Tools.Clamp(countV, 0, plist.Count - offsetV);
			}
			var items = plist.Items.Skip(offsetV).Take(countV).Select(resourceFactory.ToApiFormat).ToArray();
			var plInfo = new PlaylistInfo
			{
				Id = id,
				OwnerId = plist.Owner == Uid.Null ? null : UidToClientName(ts3Client, plist.Owner),
				SongCount = plist.Count,
				AdditionalEditors = plist.AdditionalEditors.Select(e => e.Value).ToList(),
				DisplayOffset = offsetV,
				Items = items,
			};

			return JsonValue.Create(plInfo, x =>
			{
				var tmb = new TextModBuilder();

				tmb.AppendFormat(strings.cmd_list_show_header, x.Id.Mod().Bold(), x.SongCount.ToString(), x.OwnerId).Append(x.Modifiable ? strings.cmd_list_show_header_modifiable : "").Append("\n");
				var index = x.DisplayOffset;
				foreach (var plitem in x.Items)
					tmb.Append((index++).ToString()).Append(": ").AppendLine(plitem.Title);
				return tmb.ToString();
			});
		}

		[Command("next")]
		public static void CommandNext(PlayManager playManager, InvokerData invoker)
			=> playManager.Next().UnwrapThrow();

		[Command("param", "_undocumented")] // TODO add documentation, when name decided
		public static object CommandParam(ExecutionInformation info, IReadOnlyList<Type> resultTypes, int index)
		{
			if (!info.TryGet<AliasContext>(out var ctx) || ctx.Arguments == null)
				throw new CommandException("No parameter available", CommandExceptionReason.CommandError);

			if (index < 0 || index >= ctx.Arguments.Count)
				return CommandManager.GetEmpty(resultTypes);

			var backup = ctx.Arguments;
			ctx.Arguments = null;
			var result = backup[index].Execute(info, Array.Empty<ICommand>(), resultTypes);
			ctx.Arguments = backup;
			return result;
		}

		[Command("pm")]
		public static string CommandPm(ClientCall invoker)
		{
			invoker.Visibiliy = TextMessageTargetMode.Private;
			return string.Format(strings.cmd_pm_hi, invoker.NickName ?? "Anonymous");
		}

		[Command("pm channel", "_undocumented")] // TODO
		public static void CommandPmChannel(Ts3Client ts3Client, string message) => ts3Client.SendChannelMessage(message).UnwrapThrow();

		[Command("pm server", "_undocumented")] // TODO
		public static void CommandPmServer(Ts3Client ts3Client, string message) => ts3Client.SendServerMessage(message).UnwrapThrow();

		[Command("pm user")]
		public static void CommandPmUser(Ts3Client ts3Client, ushort clientId, string message) => ts3Client.SendMessage(message, (ClientId)clientId).UnwrapThrow();

		[Command("pause")]
		public static void CommandPause(Player playerConnection) => playerConnection.Paused = !playerConnection.Paused;

		[Command("play")]
		public static void CommandPlay(PlayManager playManager, Player playerConnection, InvokerData invoker) {
			if (!playManager.IsPlaying)
				playManager.Play().UnwrapThrow();
			else
				playerConnection.Paused = false;
		}

		[Command("plugin list")]
		public static JsonArray<PluginStatusInfo> CommandPluginList(PluginManager pluginManager, Bot bot = null)
			=> new JsonArray<PluginStatusInfo>(pluginManager.GetPluginOverview(bot), PluginManager.FormatOverview);

		[Command("plugin unload")]
		public static void CommandPluginUnload(PluginManager pluginManager, string identifier, Bot bot = null)
		{
			var result = pluginManager.StopPlugin(identifier, bot);
			if (result != PluginResponse.Ok)
				throw new CommandException(string.Format(strings.error_plugin_error, result /*TODO*/), CommandExceptionReason.CommandError);
		}

		[Command("plugin load")]
		public static void CommandPluginLoad(PluginManager pluginManager, string identifier, Bot bot = null)
		{
			var result = pluginManager.StartPlugin(identifier, bot);
			if (result != PluginResponse.Ok)
				throw new CommandException(string.Format(strings.error_plugin_error, result /*TODO*/), CommandExceptionReason.CommandError);
		}

		[Command("previous")]
		public static void CommandPrevious(PlayManager playManager, InvokerData invoker)
			=> playManager.Previous().UnwrapThrow();

		[Command("print")]
		public static string CommandPrint(params string[] parameter)
		{
			// XXX << Design changes expected >>
			var strb = new StringBuilder();
			foreach (var param in parameter)
				strb.Append(param);
			return strb.ToString();
		}

		[Command("quiz")]
		public static JsonValue<bool> CommandQuiz(Bot bot) => new JsonValue<bool>(bot.QuizMode, string.Format(strings.info_status_quizmode, bot.QuizMode ? strings.info_on : strings.info_off));
		[Command("quiz on")]
		public static void CommandQuizOn(Bot bot)
		{
			bot.QuizMode = true;
			bot.UpdateBotStatus().UnwrapThrow();
		}
		[Command("quiz off")]
		public static void CommandQuizOff(Bot bot, ClientCall invoker = null)
		{
			if (invoker != null && invoker.Visibiliy.HasValue && invoker.Visibiliy == TextMessageTargetMode.Private)
				throw new CommandException(strings.cmd_quiz_off_no_cheating, CommandExceptionReason.CommandError);
			bot.QuizMode = false;
			bot.UpdateBotStatus().UnwrapThrow();
		}

		[Command("rights can")]
		public static JsonArray<string> CommandRightsCan(ExecutionInformation info, RightsManager rightsManager, params string[] rights)
			=> new JsonArray<string>(rightsManager.GetRightsSubset(info, rights), r => r.Count > 0 ? string.Join(", ", r) : strings.info_empty);

		[Command("rights reload")]
		public static JsonEmpty CommandRightsReload(RightsManager rightsManager)
		{
			if (rightsManager.Reload())
				return new JsonEmpty(strings.info_ok);

			// TODO: this can be done nicer by returning the errors and warnings from parsing
			throw new CommandException(strings.cmd_rights_reload_error_parsing_file, CommandExceptionReason.CommandError);
		}

		[Command("rng")]
		[Usage("", "Gets a number between 0 and 100")]
		[Usage("<max>", "Gets a number between 0 and <max>")]
		[Usage("<min> <max>", "Gets a number between <min> and <max>")]
		public static int CommandRng(int? first = null, int? second = null)
		{
			if (first.HasValue && second.HasValue)
			{
				return Tools.Random.Next(Math.Min(first.Value, second.Value), Math.Max(first.Value, second.Value));
			}
			else if (first.HasValue)
			{
				if (first.Value <= 0)
					throw new CommandException(strings.cmd_rng_value_must_be_positive, CommandExceptionReason.CommandError);
				return Tools.Random.Next(first.Value);
			}
			else
			{
				return Tools.Random.Next(0, 100);
			}
		}

		[Command("seek")]
		[Usage("<sec>", "Time in seconds")]
		[Usage("<min:sec>", "Time in Minutes:Seconds")]
		[Usage("<0h0m0s>", "Time in hours, minutes and seconds")]
		public static void CommandSeek(Player playerConnection, TimeSpan position)
		{
			//if (!parsed)
			//	throw new CommandException(strings.cmd_seek_invalid_format, CommandExceptionReason.CommandError);
			if (position < TimeSpan.Zero || position > playerConnection.Length)
				throw new CommandException(strings.cmd_seek_out_of_range, CommandExceptionReason.CommandError);

			playerConnection.Position = position;
		}

		public static AudioResource GetSearchResult(this UserSession session, int index)
		{
			if (!session.Get<IList<AudioResource>>(SessionConst.SearchResult, out var sessionList))
				throw new CommandException(strings.error_select_empty, CommandExceptionReason.CommandError);

			if (index < 0 || index >= sessionList.Count)
				throw new CommandException(string.Format(strings.error_value_not_in_range, 0, sessionList.Count), CommandExceptionReason.CommandError);

			return sessionList[index];
		}

		[Command("search add", "_undocumented")] // TODO Doc
		public static void CommandSearchAdd(PlayManager playManager, InvokerData invoker, UserSession session, int index)
			=> playManager.Enqueue(session.GetSearchResult(index), new MetaData(invoker.ClientUid)).UnwrapThrow();

		[Command("search from", "_undocumented")] // TODO Doc
		public static JsonArray<AudioResource> PropagiateSearch(UserSession session, CallerInfo callerInfo, ResolveContext resolver, string resolverName, string query)
		{
			var result = resolver.Search(resolverName, query);
			var list = result.UnwrapThrow();
			session.Set(SessionConst.SearchResult, list);

			return new JsonArray<AudioResource>(list, searchResults =>
			{
				if (searchResults.Count == 0)
					return strings.cmd_search_no_result;

				var tmb = new TextModBuilder(callerInfo.IsColor);
				tmb.AppendFormat(
					strings.cmd_search_header.Mod().Bold(),
					$"!search play <{strings.info_number}>".Mod().Italic(),
					$"!search add <{strings.info_number}>".Mod().Italic()).Append("\n");
				for (int i = 0; i < searchResults.Count; i++)
				{
					tmb.AppendFormat("{0}: {1}\n", i.ToString().Mod().Bold(), searchResults[i].ResourceTitle);
				}

				return tmb.ToString();
			});
		}

		[Command("search get", "_undocumented")] // TODO Doc
		public static void CommandSearchGet(UserSession session, int index)
			=> session.GetSearchResult(index);

		[Command("server tree", "_undocumented")]
		public static JsonValue<Connection> CommandServerTree(Connection book, ApiCall _)
		{
			return JsonValue.Create(book);
		}

		[Command("settings")]
		public static void CommandSettings()
			=> throw new CommandException(string.Format(strings.cmd_settings_empty_usage, "'rights.path', 'web.api.enabled', 'tools.*'"), CommandExceptionReason.MissingParameter);

		[Command("settings copy")]
		public static void CommandSettingsCopy(ConfRoot config, string from, string to) => config.CopyBotConfig(from, to).UnwrapThrow();

		[Command("settings create")]
		public static void CommandSettingsCreate(ConfRoot config, string name) => config.CreateBotConfig(name).UnwrapThrow();

		[Command("settings delete")]
		public static void CommandSettingsDelete(ConfRoot config, string name) => config.DeleteBotConfig(name).UnwrapThrow();

		[Command("settings get")]
		public static ConfigPart CommandSettingsGet(ConfBot config, string path = null)
			=> SettingsGet(config, path);

		[Command("settings set")]
		public static void CommandSettingsSet(ConfBot config, string path, string value = null)
		{
			SettingsSet(config, path, value);
			if (!config.SaveWhenExists())
			{
				throw new CommandException("Value was set but could not be saved to file. All changes are temporary and will be lost when the bot restarts.",
					CommandExceptionReason.CommandError);
			}
		}

		[Command("settings bot get", "cmd_settings_get_help")]
		public static ConfigPart CommandSettingsBotGet(BotManager bots, ConfRoot config, string bot, string path = null)
		{
			using (var botlock = bots.GetBotLock(bot))
			{
				var confBot = GetConf(botlock?.Bot, config, bot);
				return CommandSettingsGet(confBot, path);
			}
		}

		[Command("settings bot set", "cmd_settings_set_help")]
		public static void CommandSettingsBotSet(BotManager bots, ConfRoot config, string bot, string path, string value = null)
		{
			using (var botlock = bots.GetBotLock(bot))
			{
				var confBot = GetConf(botlock?.Bot, config, bot);
				CommandSettingsSet(confBot, path, value);
			}
		}

		[Command("settings bot reload")]
		public static void CommandSettingsReload(ConfRoot config, string name = null)
		{
			if (string.IsNullOrEmpty(name))
				config.ClearBotConfigCache();
			else
				config.ClearBotConfigCache(name);
		}

		[Command("settings global get")]
		public static ConfigPart CommandSettingsGlobalGet(ConfRoot config, string path = null)
			=> SettingsGet(config, path);

		[Command("settings global set")]
		public static void CommandSettingsGlobalSet(ConfRoot config, string path, string value = null)
		{
			SettingsSet(config, path, value);
			if (!config.Save())
			{
				throw new CommandException("Value was set but could not be saved to file. All changes are temporary and will be lost when the bot restarts.",
					CommandExceptionReason.CommandError);
			}
		}

		//[Command("settings global reload")]
		public static void CommandSettingsGlobalReload(ConfRoot config)
		{
			// TODO
			throw new NotImplementedException();
		}

		private static ConfBot GetConf(Bot bot, ConfRoot config, string name)
		{
			if (bot != null)
			{
				if (bot.Injector.TryGet<ConfBot>(out var conf))
					return conf;
				else
					throw new CommandException(strings.error_call_unexpected_error, CommandExceptionReason.CommandError);
			}
			else
			{
				var getTemplateResult = config.GetBotConfig(name);
				if (!getTemplateResult.Ok)
					throw new CommandException(strings.error_bot_does_not_exist, getTemplateResult.Error, CommandExceptionReason.CommandError);
				return getTemplateResult.Value;
			}
		}

		private static ConfigPart SettingsGet(ConfigPart config, string path = null) => config.ByPathAsArray(path ?? "").SettingsGetSingle();

		private static void SettingsSet(ConfigPart config, string path, string value)
		{
			var setConfig = config.ByPathAsArray(path).SettingsGetSingle();
			if (setConfig is IJsonSerializable jsonConfig)
			{
				var result = jsonConfig.FromJson(value ?? "");
				if (!result.Ok)
					throw new CommandException($"Failed to set the value ({result.Error}).", CommandExceptionReason.CommandError); // LOC: TODO
			}
			else
			{
				throw new CommandException("This value currently cannot be set.", CommandExceptionReason.CommandError); // LOC: TODO
			}
		}

		private static ConfigPart SettingsGetSingle(this ConfigPart[] configPartsList)
		{
			if (configPartsList.Length == 0)
			{
				throw new CommandException(strings.error_config_no_key_found, CommandExceptionReason.CommandError);
			}
			else if (configPartsList.Length == 1)
			{
				return configPartsList[0];
			}
			else
			{
				throw new CommandException(
					string.Format(
						strings.error_config_multiple_keys_found + "\n",
						string.Join("\n  ", configPartsList.Take(3).Select(kvp => kvp.Key))),
					CommandExceptionReason.CommandError);
			}
		}

		[Command("settings help")]
		public static string CommandSettingsHelp(ConfRoot config, string path)
		{
			var part = SettingsGet(config, path);
			return string.IsNullOrEmpty(part.Documentation) ? strings.info_empty : part.Documentation;
		}

		[Command("song")]
		public static JsonValue<SongInfo> CommandSong(PlayManager playManager, Player playerConnection, Bot bot, ClientCall invoker = null)
		{
			if (playManager.CurrentPlayData is null)
				throw new CommandException(strings.info_currently_not_playing, CommandExceptionReason.CommandError);
			if (bot.QuizMode && invoker != null && playManager.CurrentPlayData.Invoker != invoker.ClientUid)
				throw new CommandException(strings.info_quizmode_is_active, CommandExceptionReason.CommandError);

			return JsonValue.Create(
				new SongInfo
				{
					Title = playManager.CurrentPlayData.ResourceData.ResourceTitle,
					AudioType = playManager.CurrentPlayData.ResourceData.AudioType,
					Link = playManager.CurrentPlayData.SourceLink,
					Position = playerConnection.Position,
					Length = playerConnection.Length,
					Paused = playerConnection.Paused,
				},
				x =>
				{
					var tmb = new StringBuilder();
					tmb.Append(x.Paused ? "⏸ " : "► ");
					tmb.AppendFormat("[url={0}]{1}[/url]", x.Link, x.Title);
					tmb.Append(" [");
					tmb.Append(x.Length.TotalHours >= 1 || x.Position.TotalHours >= 1
						? $"{x.Position:hh\\:mm\\:ss}/{x.Length:hh\\:mm\\:ss}"
						: $"{x.Position:mm\\:ss}/{x.Length:mm\\:ss}");
					tmb.Append("]");
					return tmb.ToString();
				}
			);
		}

		[Command("stop")]
		public static void CommandStop(PlayManager playManager) => playManager.Stop();

		[Command("subscribe")]
		public static void CommandSubscribe(IVoiceTarget targetManager, ClientCall invoker)
		{
			if (invoker.ClientId.HasValue)
				targetManager.WhisperClientSubscribe(invoker.ClientId.Value);
		}

		[Command("subscribe tempchannel")]
		public static void CommandSubscribeTempChannel(IVoiceTarget targetManager, ClientCall invoker = null, ChannelId? channel = null)
		{
			var subChan = channel ?? invoker?.ChannelId ?? ChannelId.Null;
			if (subChan != ChannelId.Null)
				targetManager.WhisperChannelSubscribe(true, subChan);
		}

		[Command("subscribe channel")]
		public static void CommandSubscribeChannel(IVoiceTarget targetManager, ClientCall invoker = null, ChannelId? channel = null)
		{
			var subChan = channel ?? invoker?.ChannelId ?? ChannelId.Null;
			if (subChan != ChannelId.Null)
				targetManager.WhisperChannelSubscribe(false, subChan);
		}

		[Command("system info", "_undocumented")]
		public static JsonValue CommandSystemInfo(SystemMonitor systemMonitor)
		{
			var sysInfo = systemMonitor.GetReport();
			return JsonValue.Create(new
			{
				memory = sysInfo.Memory,
				cpu = sysInfo.Cpu,
				starttime = systemMonitor.StartTime,
			}, x => new TextModBuilder().AppendFormat(
				"\ncpu: {0}% \nmemory: {1} \nstartime: {2}".Mod().Bold(),
					(x.cpu.Last() * 100).ToString("0.#"),
					Util.FormatBytesHumanReadable(x.memory.Last()),
					x.starttime.ToString(Thread.CurrentThread.CurrentCulture)).ToString()
			);
		}

		[Command("system quit", "cmd_quit_help")]
		public static JsonEmpty CommandSystemQuit(Core core, CallerInfo caller, UserSession session = null, string param = null)
		{
			const string force = "force";

			if (caller.ApiCall || param == force)
			{
				core.Dispose();
				return new JsonEmpty(string.Empty);
			}

			string ResponseQuit(string message)
			{
				if (TextUtil.GetAnswer(message) == Answer.Yes)
				{
					CommandSystemQuit(core, caller, session, force);
				}
				return null;
			}

			session.SetResponse(ResponseQuit);
			return new JsonEmpty(strings.cmd_quit_confirm + YesNoOption);
		}

		[Command("take")]
		[Usage("<count> <text>", "Take only <count> parts of the text")]
		[Usage("<count> <start> <text>", "Take <count> parts, starting with the part at <start>")]
		[Usage("<count> <start> <delimiter> <text>", "Specify another delimiter for the parts than spaces")]
		public static object CommandTake(ExecutionInformation info, IReadOnlyList<ICommand> arguments, IReadOnlyList<Type> returnTypes)
		{
			if (arguments.Count < 2)
				throw new CommandException(strings.error_cmd_at_least_two_argument, CommandExceptionReason.MissingParameter);

			int start = 0;
			string delimiter = null;

			// Get count
			var res = ((IPrimitiveResult<string>)arguments[0].Execute(info, Array.Empty<ICommand>(), ReturnString)).Get();
			if (!int.TryParse(res, out int count) || count < 0)
				throw new CommandException("Count must be an integer >= 0", CommandExceptionReason.CommandError); // LOC: TODO

			if (arguments.Count > 2)
			{
				// Get start
				res = ((IPrimitiveResult<string>)arguments[1].Execute(info, Array.Empty<ICommand>(), ReturnString)).Get();
				if (!int.TryParse(res, out start) || start < 0)
					throw new CommandException("Start must be an integer >= 0", CommandExceptionReason.CommandError); // LOC: TODO
			}

			// Get delimiter if exists
			if (arguments.Count > 3)
				delimiter = ((IPrimitiveResult<string>)arguments[2].Execute(info, Array.Empty<ICommand>(), ReturnString)).Get();

			string text = ((IPrimitiveResult<string>)arguments[Math.Min(arguments.Count - 1, 3)]
				.Execute(info, Array.Empty<ICommand>(), ReturnString)).Get();

			var splitted = delimiter is null
				? text.Split()
				: text.Split(new[] { delimiter }, StringSplitOptions.None);
			if (splitted.Length < start + count)
				throw new CommandException(strings.cmd_take_not_enough_arguements, CommandExceptionReason.CommandError);
			var splittedarr = splitted.Skip(start).Take(count).ToArray();

			foreach (var returnType in returnTypes)
			{
				if (returnType == typeof(string))
					return new PrimitiveResult<string>(string.Join(delimiter ?? " ", splittedarr));
			}

			throw new CommandException(strings.error_nothing_to_return, CommandExceptionReason.NoReturnMatch);
		}

		[Command("unsubscribe")]
		public static void CommandUnsubscribe(IVoiceTarget targetManager, ClientCall invoker)
		{
			if (invoker.ClientId.HasValue)
				targetManager.WhisperClientUnsubscribe(invoker.ClientId.Value);
		}

		[Command("unsubscribe channel")]
		public static void CommandUnsubscribeChannel(IVoiceTarget targetManager, ClientCall invoker = null, ulong? channel = null)
		{
			var subChan = (ChannelId?)channel ?? invoker?.ChannelId;
			if (subChan.HasValue)
				targetManager.WhisperChannelUnsubscribe(false, subChan.Value);
		}

		[Command("unsubscribe temporary")]
		public static void CommandUnsubscribeTemporary(IVoiceTarget targetManager) => targetManager.ClearTemporary();

		[Command("version")]
		public static JsonValue<BuildData> CommandVersion() => new JsonValue<BuildData>(SystemData.AssemblyData, d => d.ToLongString());

		[Command("volume")]
		public static JsonValue<float> CommandVolume(Player playerConnection)
			=> new JsonValue<float>(playerConnection.Volume, string.Format(strings.cmd_volume_current, playerConnection.Volume.ToString("0.#")));

		[Command("volume")]
		[Usage("<level>", "A new volume level between 0 and 100.")]
		[Usage("+/-<level>", "Adds or subtracts a value from the current volume.")]
		public static JsonValue<float> CommandVolume(ExecutionInformation info, Player playerConnection, CallerInfo caller, ConfBot config, string volume, UserSession session = null)
		{
			volume = volume.Trim();
			bool relPos = volume.StartsWith("+", StringComparison.Ordinal);
			bool relNeg = volume.StartsWith("-", StringComparison.Ordinal);
			string numberString = (relPos || relNeg) ? volume.Remove(0, 1).TrimStart() : volume;

			if (!float.TryParse(numberString, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedVolume))
				throw new CommandException(strings.cmd_volume_parse_error, CommandExceptionReason.CommandError);

			float curVolume = playerConnection.Volume;
			float newVolume;
			if (relPos) newVolume = curVolume + parsedVolume;
			else if (relNeg) newVolume = curVolume - parsedVolume;
			else newVolume = parsedVolume;

			if (newVolume < AudioValues.MinVolume || newVolume > AudioValues.MaxVolume)
				throw new CommandException(string.Format(strings.cmd_volume_is_limited, AudioValues.MinVolume, AudioValues.MaxVolume), CommandExceptionReason.CommandError);

			if (newVolume <= config.Audio.MaxUserVolume || newVolume <= curVolume || caller.ApiCall)
			{
				playerConnection.Volume = newVolume;
			}
			else if (newVolume <= AudioValues.MaxVolume)
			{
				string ResponseVolume(string message)
				{
					if (TextUtil.GetAnswer(message) == Answer.Yes)
					{
						if (info.HasRights(RightHighVolume))
							playerConnection.Volume = newVolume;
						else
							return strings.cmd_volume_missing_high_volume_permission;
					}
					return null;
				}

				session.SetResponse(ResponseVolume);
				throw new CommandException(strings.cmd_volume_high_volume_confirm + YesNoOption, CommandExceptionReason.CommandError);
			}
			return null;
		}

		[Command("whisper all")]
		public static void CommandWhisperAll(IVoiceTarget targetManager) => CommandWhisperGroup(targetManager, GroupWhisperType.AllClients, GroupWhisperTarget.AllChannels);

		[Command("whisper group")]
		public static void CommandWhisperGroup(IVoiceTarget targetManager, GroupWhisperType type, GroupWhisperTarget target, ulong? targetId = null)
		{
			if (type == GroupWhisperType.ServerGroup || type == GroupWhisperType.ChannelGroup)
			{
				if (!targetId.HasValue)
					throw new CommandException(strings.cmd_whisper_group_missing_target, CommandExceptionReason.CommandError);
				targetManager.SetGroupWhisper(type, target, targetId.Value);
				targetManager.SendMode = TargetSendMode.WhisperGroup;
			}
			else
			{
				if (targetId.HasValue)
					throw new CommandException(strings.cmd_whisper_group_superfluous_target, CommandExceptionReason.CommandError);
				targetManager.SetGroupWhisper(type, target, 0);
				targetManager.SendMode = TargetSendMode.WhisperGroup;
			}
		}

		[Command("whisper list")]
		public static JsonObject CommandWhisperList(IVoiceTarget targetManager)
		{
			return JsonValue.Create(new
			{
#pragma warning disable IDE0037
				SendMode = targetManager.SendMode,
				GroupWhisper = targetManager.SendMode == TargetSendMode.WhisperGroup ?
				new
				{
					Target = targetManager.GroupWhisperTarget,
					TargetId = targetManager.GroupWhisperTargetId,
					Type = targetManager.GroupWhisperType,
				}
				: null,
				WhisperClients = targetManager.WhisperClients,
				WhisperChannel = targetManager.WhisperChannel,
#pragma warning restore IDE0037
			},
			x =>
			{
				var strb = new StringBuilder(strings.cmd_whisper_list_header);
				strb.AppendLine();
				switch (x.SendMode)
				{
				case TargetSendMode.None: strb.Append(strings.cmd_whisper_list_target_none); break;
				case TargetSendMode.Voice: strb.Append(strings.cmd_whisper_list_target_voice); break;
				case TargetSendMode.Whisper:
					strb.Append(strings.cmd_whisper_list_target_whisper_clients).Append(": [").Append(string.Join(",", x.WhisperClients)).Append("]\n");
					strb.Append(strings.cmd_whisper_list_target_whisper_channel).Append(": [").Append(string.Join(",", x.WhisperChannel)).Append("]");
					break;
				case TargetSendMode.WhisperGroup:
					strb.AppendFormat(strings.cmd_whisper_list_target_whispergroup, x.GroupWhisper.Type, x.GroupWhisper.Target, x.GroupWhisper.TargetId);
					break;
				default:
					throw new ArgumentOutOfRangeException();
				}
				return strb.ToString();
			});
		}

		[Command("whisper off")]
		public static void CommandWhisperOff(IVoiceTarget targetManager) => targetManager.SendMode = TargetSendMode.Voice;

		[Command("whisper subscription")]
		public static void CommandWhisperSubsription(IVoiceTarget targetManager) => targetManager.SendMode = TargetSendMode.Whisper;

		[Command("xecute")]
		public static void CommandXecute(ExecutionInformation info, IReadOnlyList<ICommand> arguments)
		{
			foreach (var arg in arguments)
				arg.Execute(info, Array.Empty<ICommand>(), ReturnAnyPreferNothing);
		}
		// ReSharper enable UnusedMember.Global

		//private static string GetEditPlaylist(this UserSession session)
		//{
		//	if (session is null)
		//		throw new MissingContextCommandException(strings.error_no_session_in_context, typeof(UserSession));
		//	var result = session.Get<string>(SessionConst.Playlist);
		//	if (result)
		//		return result.Value;

		//	throw new CommandException("You are currently not editing any playlist.", CommandExceptionReason.CommandError); // TODO: Loc
		//}

		public static bool HasRights(this ExecutionInformation info, params string[] rights)
		{
			if (!info.TryGet<CallerInfo>(out var caller)) caller = null;
			if (caller?.SkipRightsChecks ?? false)
				return true;
			if (!info.TryGet<RightsManager>(out var rightsManager))
				return false;
			return rightsManager.HasAllRights(info, rights);
		}

		public static E<LocalStr> Write(this ExecutionInformation info, string message)
		{
			if (!info.TryGet<Ts3Client>(out var ts3Client))
				return new LocalStr(strings.error_no_teamspeak_in_context);

			if (!info.TryGet<ClientCall>(out var invoker))
				return new LocalStr(strings.error_no_invoker_in_context);

			if (!invoker.Visibiliy.HasValue || !invoker.ClientId.HasValue)
				return new LocalStr(strings.error_invoker_not_visible);

			var behaviour = LongTextBehaviour.Split;
			var limit = 1;
			if (info.TryGet<ConfBot>(out var config))
			{
				behaviour = config.Commands.LongMessage;
				limit = config.Commands.LongMessageSplitLimit;
			}

			foreach (var msgPart in LongTextTransform.Transform(message, behaviour, limit))
			{
				E<LocalStr> result;
				switch (invoker.Visibiliy.Value)
				{
				case TextMessageTargetMode.Private:
					result = ts3Client.SendMessage(msgPart, invoker.ClientId.Value);
					break;
				case TextMessageTargetMode.Channel:
					result = ts3Client.SendChannelMessage(msgPart);
					break;
				case TextMessageTargetMode.Server:
					result = ts3Client.SendServerMessage(msgPart);
					break;
				default:
					throw Tools.UnhandledDefault(invoker.Visibiliy.Value);
				}

				if (!result.Ok)
					return result;
			}
			return R.Ok;
		}

		public static void UseComplexityTokens(this ExecutionInformation info, int count)
		{
			if (!info.TryGet<CallerInfo>(out var caller) || caller.CommandComplexityCurrent + count > caller.CommandComplexityMax)
				throw new CommandException(strings.error_cmd_complexity_reached, CommandExceptionReason.CommandError);
			caller.CommandComplexityCurrent += count;
		}
	}
}
