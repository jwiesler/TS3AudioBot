// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using TS3AudioBot.Algorithm;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Web.Model;
using TSLib;
using TSLib.Helper;

namespace TS3AudioBot.Playlists
{
	public class PlaylistIO : IDisposable
	{
		private readonly ConfBot confBot;
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private readonly PlaylistDatabase playlistCache = new PlaylistDatabase();
		private readonly Dictionary<string, string> lowerIdToId = new Dictionary<string, string>();
		private const int FileVersion = 3;
		private readonly object ioLock = new object();

		public PlaylistIO(ConfBot confBot)
		{
			this.confBot = confBot;
			ReloadFolder();
		}

		private FileInfo IdToFile(string realId) {
			return new FileInfo(Path.Combine(confBot.LocalConfigDir, BotPaths.Playlists, realId));
		}

		private string ToRealId(string listId) {
			string lower = listId.ToLower();
			return lowerIdToId.TryGetValue(lower, out var id) ? id : listId;
		}

		public R<(Playlist list, string id), LocalStr> ReadFull(string listId) {
			lock (ioLock) {
				var id = ToRealId(listId);
				
				if (playlistCache.TryGet(id, out Playlist list))
					return (list, id);

				var result = ReadFullFromFile(IdToFile(id));

				if (!result.Ok)
					return result.Error;

				playlistCache.Update(id, result.Value.list);
				return (result.Value.list, id);
			}
		}

		private static R<PlaylistMeta, LocalStr> ReadMetaFromFile(FileInfo fi) {
			if (!fi.Exists)
				return new LocalStr(strings.error_playlist_not_found);

			using (var sr = new StreamReader(fi.Open(FileMode.Open, FileAccess.Read, FileShare.Read), Tools.Utf8Encoder)) {
				var metaRes = ReadHeadStream(sr);
				if (!metaRes.Ok)
					return metaRes.Error;
				var meta = metaRes.Value;

				return meta;
			}
		}

		private static List<PlaylistItem> ReadListItems(StreamReader reader) {
			List<PlaylistItem> items = new List<PlaylistItem>();
		private static List<AudioResource> ReadListItems(StreamReader reader) {
			var items = new List<AudioResource>();

			string line;
			while ((line = reader.ReadLine()) != null)
			{
				var kvp = line.Split(new[] { ':' }, 2);
				if (kvp.Length < 2) continue;

				string key = kvp[0];
				string value = kvp[1];

				switch (key)
				{
				// Legacy entry
				case "rs":
				{
					var rskvp = value.Split(new[] { ':' }, 2);
					if (kvp.Length < 2)
					{
						Log.Warn("Erroneus playlist split count: {0}", line);
						continue;
					}
					string optOwner = rskvp[0];
					string content = rskvp[1];

					var rsSplit = content.Split(new[] { ',' }, 3);
					if (rsSplit.Length < 3)
						goto default;
					if (!string.IsNullOrWhiteSpace(rsSplit[0])) {
						var resource = new AudioResource(Uri.UnescapeDataString(rsSplit[1]),
							StringNormalize.Normalize(Uri.UnescapeDataString(rsSplit[2])), rsSplit[0]);
						items.Add(resource);
					} else {
						goto default;
					}

					break;
				}

				case "rsj":
					var res = JsonConvert.DeserializeObject<AudioResource>(value);
					// This can be commented out if all playlist have been written once
					res = res.WithTitle(StringNormalize.Normalize(res.ResourceTitle));
					items.Add(res);
					break;

				case "id":
				case "ln":
					Log.Warn("Deprecated playlist data block: {0}", line);
					break;

				default:
					Log.Warn("Erroneus playlist data block: {0}", line);
					break;
				}
			}

			return items;
		}

		private static R<(Playlist list, PlaylistMeta meta), LocalStr> ReadFullFromFile(FileInfo fi) {
			if (!fi.Exists)
				return new LocalStr(strings.error_playlist_not_found);

			using (var sr = new StreamReader(fi.Open(FileMode.Open, FileAccess.Read, FileShare.Read), Tools.Utf8Encoder))
			{
				var metaRes = ReadHeadStream(sr);
				if (!metaRes.Ok)
					return metaRes.Error;

				var items = ReadListItems(sr);

				var meta = metaRes.Value;
				meta.Count = items.Count;
				var plist = new Playlist(
					meta.OwnerId == null ? Uid.Null : new Uid(meta.OwnerId),
					meta.AdditionalEditors == null
						? Enumerable.Empty<Uid>()
						: meta.AdditionalEditors.Select(e => new Uid(e)),
					items);
				return (plist, meta);
			}
		}

		private static R<PlaylistMeta, LocalStr> ReadHeadStream(StreamReader sr)
		{
			string line;
			int version = -1;

			// read header
			while ((line = sr.ReadLine()) != null)
			{
				if (string.IsNullOrEmpty(line))
					break;

				var kvp = line.Split(new[] { ':' }, 2);
				if (kvp.Length < 2) continue;

				string key = kvp[0];
				string value = kvp[1];

				switch (key)
				{
				case "version":
					version = int.Parse(value);
					if (version > FileVersion)
						return new LocalStr("The file version is too new and can't be read."); // LOC: TODO
					break;
				case "meta":
					var meta = JsonConvert.DeserializeObject<PlaylistMeta>(value);
					meta.Version = version;
					return meta;
				}
			}

			return new PlaylistMeta { Title = "", Count = 0, Version = version };
		}

		public E<LocalStr> Write(string listId, Playlist list)
		{
			lock (ioLock) {
				var id = ToRealId(listId);

				var meta = playlistCache.Update(id, list);
				return WriteToFile(IdToFile(id), meta, list.Items);
			}
		}

		public static void UpdateMeta(PlaylistMeta meta, IReadOnlyPlaylist list) {
			meta.Count = list.Items.Count;
			meta.OwnerId = list.Owner.Value;
			meta.Version = FileVersion;
			meta.AdditionalEditors = new List<string>(list.AdditionalEditors.Select(uid => uid.Value));
		}

		private static E<LocalStr> WriteToFile(FileInfo fi, PlaylistMeta meta, IReadOnlyCollection<PlaylistItem> items)
		{
			var dir = fi.Directory;
			if (!dir.Exists)
				dir.Create();

			using (var sw = new StreamWriter(fi.Open(FileMode.Create, FileAccess.Write, FileShare.Read), Tools.Utf8Encoder))
			{
				var serializer = new JsonSerializer
				{
					Formatting = Formatting.None,
				};

				sw.WriteLine("version:" + FileVersion);
				sw.Write("meta:");
				serializer.Serialize(sw, meta);
				sw.WriteLine();

				sw.WriteLine();

				for (int i = 0; i < list.Count; ++i) {
					sw.Write("rsj:");
					serializer.Serialize(sw, list[i]);
					sw.WriteLine();
				}
			}
			return R.Ok;
		}

		public E<LocalStr> Delete(string listId)
		{
			lock (ioLock) {
				var id = ToRealId(listId);
				var file = IdToFile(id);
				if(!playlistCache.Remove(id) && !file.Exists)
					return new LocalStr(strings.error_playlist_not_found);

				return DeleteFile(file);
			}
		}

		private static E<LocalStr> DeleteFile(FileInfo fi)
		{
			try
			{
				fi.Delete();
				return R.Ok;
			}
			catch (IOException) { return new LocalStr(strings.error_io_in_use); }
			catch (System.Security.SecurityException) { return new LocalStr(strings.error_io_missing_permission); }
		}

		public void ReloadFolder() {
			lock (ioLock) {
				ReloadFolderInternal();
				LoadAll();
			}
		}

		private void ReloadFolderInternal() {
			var di = new DirectoryInfo(Path.Combine(confBot.LocalConfigDir, BotPaths.Playlists));
			if (!di.Exists)
				return;

			var fileEnu = di.EnumerateFiles();

			playlistCache.Clear();
			lowerIdToId.Clear();
			foreach (var fi in fileEnu) {
				var meta = ReadMetaFromFile(IdToFile(fi.Name));
				if (!meta.Ok)
					continue;

				var lower = fi.Name.ToLower();
				if (lowerIdToId.ContainsKey(lower)) {
					Log.Warn($"A file with the lowercase name \"{lower}\" already exists, \"{fi.Name}\" will be ignored.");
					continue;
				}

				lowerIdToId.Add(lower, fi.Name);
				playlistCache.Add(fi.Name, meta.Value);
			}
		}

		public R<PlaylistInfo[], LocalStr> ListPlaylists()
		{
			if (confBot.LocalConfigDir is null)
				return new LocalStr("Temporary bots cannot have playlists"); // TODO do this for all other methods too

			lock (ioLock) {
				return playlistCache.GetInfos();
			}
		}

		public bool TryGetPlaylistId(string listId, out string outId)
		{
			lock (ioLock) {
				var id = ToRealId(listId);
				if (!playlistCache.Contains(id)) {
					outId = null;
					return false;
				}

				outId = id;
				return true;
			}
		}

		public void Dispose()
		{
		}

		public bool Exists(string listId) {
			lock (ioLock) {
				var id = ToRealId(listId);
				return playlistCache.Contains(id);
			}
		}

		public List<UniqueResourceInfo> ListItems() {
			List<UniqueResourceInfo> items;
			lock (playlistCache) {
				items = new List<UniqueResourceInfo>(playlistCache.GetUniqueResources());
			}

			return items;
		}

		public bool TryGetUniqueItem(UniqueResource resource, out UniqueResourceInfo info) {
			lock (playlistCache) {
				return playlistCache.UniqueResourcesDictionary.TryGetValue(resource, out info);
			}
		}

		public bool ChangeAllOccurences(UniqueResource resource, AudioResource with) {
			lock (playlistCache) {
				return playlistCache.ChangeAllOccurences(resource, with);
			}
		}

		public void LoadAll() {
			var res = new List<PlaylistSearchItemInfo>();

			List<string> ids;
			lock (playlistCache) {
				ids = new List<string>(playlistCache.Ids);
			}

			foreach (var id in ids) {
				ReadFull(id);
			}
		}
	}

	public class ContainingListInfo {
		[JsonProperty(PropertyName = "index")]
		public int Index { get; set; }
		[JsonProperty(PropertyName = "id")]
		public string Id { get; set; }
	}

	public class PlaylistSearchItemInfo {
		[JsonProperty(PropertyName = "title")]
		public string ResourceTitle { get; set; }

		[JsonProperty(PropertyName = "resid")]
		public string ResourceId { get; set; }

		[JsonProperty(PropertyName = "containinglists")]
		public List<ContainingListInfo> ContainingLists { get; set; }
	}

	public class PlaylistMeta
	{
		[JsonProperty(PropertyName = "count")]
		public int Count { get; set; }
		[JsonProperty(PropertyName = "id")]
		public string Id { get; set; }
		[JsonProperty(PropertyName = "owner")]
		public string OwnerId { get; set; }
		[JsonProperty(PropertyName = "additional-editors")]
		public List<string> AdditionalEditors { get; set; }
		[JsonIgnore]
		public int Version { get; set; }
	}
}
