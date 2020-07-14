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
using System.IO;
using System.Linq;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;
using TSLib;
using TSLib.Helper;

namespace TS3AudioBot.Playlists
{
	public interface IPlaylistIO {
		bool TryGetRealId(string listId, out string id);
		void Write(string listId, IPlaylist list);
		E<LocalStr> Delete(string id);
		void Clear();
		List<(string, IPlaylist)> ReloadFolder();
	}

	public class PlaylistLowerIdToId {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private readonly Dictionary<string, string> lowerIdToId = new Dictionary<string, string>();

		protected bool RegisterPlaylistId(string listId) {
			var lower = listId.ToLowerInvariant();
			if (lowerIdToId.ContainsKey(lower)) {
				Log.Warn($"A file with the lowercase name \"{lower}\" already exists, \"{listId}\" will be ignored.");
				return false;
			}

			lowerIdToId.Add(lower, listId);
			return true;
		}

		protected void UnregisterPlaylistId(string listId) {
			var lower = listId.ToLowerInvariant();
			lowerIdToId.Remove(lower);
		}

		public bool TryGetRealId(string listId, out string id) {
			var lower = listId.ToLower();
			return lowerIdToId.TryGetValue(lower, out id);
		}

		public void Clear() {
			lowerIdToId.Clear();
		}
	}

	public class PlaylistIO : PlaylistLowerIdToId, IPlaylistIO
	{
		private readonly ConfBot confBot;
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		
		private const int FileVersion = 3;
		private readonly object ioLock = new object();

		public PlaylistIO(ConfBot confBot)
		{
			this.confBot = confBot;
		}

		private FileInfo IdToFile(string realId) {
			return new FileInfo(Path.Combine(confBot.LocalConfigDir, BotPaths.Playlists, realId));
		}

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

		private static R<Playlist, LocalStr> ReadFullFromFile(FileInfo fi) {
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
				return plist;
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

			return new LocalStr("Could not find the header.");
		}

		public void Write(string listId, IPlaylist list)
		{
			lock (ioLock) {
				WriteToFile(listId, list);
			}
		}

		public static PlaylistMeta CreateMeta(IPlaylist list) {
			return new PlaylistMeta {
				Count = list.Count,
				OwnerId = list.Owner.Value,
				AdditionalEditors = new List<string>(list.AdditionalEditors.Select(uid => uid.Value)),
				Version = FileVersion
			};
		}

		private void WriteToFile(string id, IPlaylist list) {
			WriteToFile(IdToFile(id), list);
		}

		private static void WriteToFile(FileInfo fi, IPlaylist list)
		{
			var dir = fi.Directory;
			if (dir != null && !dir.Exists)
				dir.Create();

			using (var sw = new StreamWriter(fi.Open(FileMode.Create, FileAccess.Write, FileShare.Read), Tools.Utf8Encoder))
			{
				var serializer = new JsonSerializer
				{
					Formatting = Formatting.None,
				};

				sw.WriteLine("version:" + FileVersion);
				sw.Write("meta:");
				serializer.Serialize(sw, CreateMeta(list));
				sw.WriteLine();

				sw.WriteLine();

				for (int i = 0; i < list.Count; ++i) {
					sw.Write("rsj:");
					serializer.Serialize(sw, list[i]);
					sw.WriteLine();
				}
			}
		}

		public E<LocalStr> Delete(string id)
		{
			lock (ioLock) {
				var file = IdToFile(id);
				if(!file.Exists)
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

		public List<(string, IPlaylist)> ReloadFolder() {
			lock (ioLock) {
				Clear();
				return ReloadFolderInternal();
			}
		}

		private List<(string, IPlaylist)> ReloadFolderInternal() {
			var di = new DirectoryInfo(Path.Combine(confBot.LocalConfigDir, BotPaths.Playlists));
			if (!di.Exists)
				return null;

			var fileEnu = di.EnumerateFiles();

			var result = new List<(string, IPlaylist)>();
			foreach (var fi in fileEnu) {
				var list = ReadFullFromFile(IdToFile(fi.Name));
				if (!list.Ok)
					continue;

				if(RegisterPlaylistId(fi.Name))
					result.Add((fi.Name, list.Value));
				
			}

			return result;
		}

		/*public R<PlaylistInfo[], LocalStr> ListPlaylists()
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
		}*/

		/*public bool Exists(string listId) {
			lock (ioLock) {
				var id = ToRealId(listId);
				return playlistCache.Contains(id);
			}
		}*/

		/*public List<UniqueResourceInfo> ListItems() {
			List<UniqueResourceInfo> items;
			lock (playlistCache) {
				items = new List<UniqueResourceInfo>(playlistCache.GetUniqueResources());
			}

			return items;
		}*/

		/*public bool TryGetUniqueItem(UniqueResource resource, out UniqueResourceInfo info) {
			lock (playlistCache) {
				return playlistCache.UniqueResourcesDictionary.TryGetValue(resource, out info);
			}
		}

		public bool GetAllOccurences(UniqueResource resource, out IReadOnlyCollection<KeyValuePair<string, List<int>>> list) {
			lock (playlistCache) {
				return playlistCache.GetAllOccurences(resource, out list);
			}
		}

		public void ChangeAllOccurences(UniqueResource resource, AudioResource with) {
			lock (playlistCache) {
				if (!GetAllOccurences(resource, out var occurences))
					return;
				var copy = occurences.Select(kv => kv.Key).ToList();
				playlistCache.ChangeAllOccurences(resource, with);

				foreach (var listId in copy) {
					var r = WriteInternal(listId);
					if (r.Ok)
						continue;
					Log.Error($"Failed to write playlist {listId}");
				}
					
			}
		}*/
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
