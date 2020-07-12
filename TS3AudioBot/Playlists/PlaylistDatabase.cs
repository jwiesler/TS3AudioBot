using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Web.Model;

namespace TS3AudioBot.Playlists {
	

	public class UniqueResourceInfo {
		public UniqueResource Resource { get; }

		private Dictionary<string, List<int>> ContainingListInstances { get; } = new Dictionary<string, List<int>>();

		public IReadOnlyCollection<KeyValuePair<string, List<int>>> ContainingLists => ContainingListInstances;

		public UniqueResourceInfo(UniqueResource resource) { Resource = resource; }

		public void Add(string id, int offset) {
			List<int> indices = new List<int>();
			indices.Add(offset);
			ContainingListInstances.Add(id, indices);
		}

		public void AddInstance(string id, int offset) {
			if (ContainingListInstances.TryGetValue(id, out var indices)) {
				indices.Add(offset);
			} else {
				Add(id, offset);
			}
		}

		public void RemoveList(string id) {
			ContainingListInstances.Remove(id);
		}

		public bool IsContainedInAList => ContainingListInstances.Count > 0;
	}

	public class PlaylistDatabase {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		class PlaylistData {
			public Playlist Playlist { get; set; }
			public PlaylistMeta Meta { get; set; }
			public List<UniqueResourceInfo> Songs { get; set; }

			public void Update(Playlist list) {
				Playlist = list;
				PlaylistIO.UpdateMeta(Meta, list);
			}
		}

		private readonly Dictionary<string, PlaylistData> playlistCache = new Dictionary<string, PlaylistData>(16);

		private readonly Dictionary<UniqueResource, UniqueResourceInfo> uniqueSongs =
			new Dictionary<UniqueResource, UniqueResourceInfo>();

		public bool TryGet(string id, out Playlist value) {
			if (playlistCache.TryGetValue(id, out var data)) {
				value = data.Playlist;
				return data.Playlist != null;
			}

			value = null;
			return false;
		}

		private UniqueResourceInfo GetOrCreate(UniqueResource resource) {
			if (!uniqueSongs.TryGetValue(resource, out var info)) {
				info = new UniqueResourceInfo(resource);
				uniqueSongs.Add(resource, info);
			}

			return info;
		}

		private List<UniqueResourceInfo> CreateSongsInfo(string id, Playlist list) {
			List<UniqueResourceInfo> res = new List<UniqueResourceInfo>();

			for (var i = 0; i < list.Items.Count; i++) {
				var item = list.Items[i];
				var info = GetOrCreate(item.AudioResource);
				info.AddInstance(id, i);
				res.Add(info);
			}

			return res;
		}

		private void RemoveResourceFromList(UniqueResourceInfo info, string listId) {
			info.RemoveList(listId);
			if (!info.IsContainedInAList)
				uniqueSongs.Remove(info.Resource);
		}

		public void Add(string id, Playlist list, PlaylistMeta meta) {
			playlistCache.Add(id, new PlaylistData {
				Meta = meta,
				Playlist = list,
				Songs = CreateSongsInfo(id, list)
			});
		}

		public void Add(string id, PlaylistMeta meta) {
			playlistCache.Add(id, new PlaylistData {
				Meta = meta
			});
		}

		public PlaylistInfo[] GetInfos() {
			return playlistCache.Select(kvp => new PlaylistInfo {
				Id = kvp.Key,
				Title = kvp.Value.Meta.Title,
				SongCount = kvp.Value.Meta.Count,
				OwnerId = kvp.Value.Meta.OwnerId,
				AdditionalEditors = kvp.Value.Meta.AdditionalEditors
			}).ToArray();
		}

		public ICollection<string> Ids => playlistCache.Keys;

		public bool Contains(string id) { return playlistCache.ContainsKey(id); }

		public bool Remove(string id) {
			if (playlistCache.TryGetValue(id, out var data)) {
				foreach (var s in data.Songs) {
					RemoveResourceFromList(s, id);
				}

				playlistCache.Remove(id);
				return true;
			}

			return false;
		}

		public PlaylistMeta Update(string id, Playlist list) {
			if (!playlistCache.TryGetValue(id, out var data)) {
				var meta = new PlaylistMeta();
				PlaylistIO.UpdateMeta(meta, list);
				Add(id, list, meta);
				return meta;
			}

			var wasNull = data.Playlist == null;
			data.Update(list);
			if (!wasNull) {
				foreach (var s in data.Songs) {
					RemoveResourceFromList(s, id);
				}
			}
			data.Songs = CreateSongsInfo(id, list);

			return data.Meta;
		}

		public bool ChangeAllOccurences(UniqueResource resource, AudioResource with) {
			if (!UniqueResourcesDictionary.TryGetValue(resource, out var info))
				return false;

			var withInfo = GetOrCreate(with);

			foreach (var listKeyValuePair in info.ContainingLists) {
				var listId = listKeyValuePair.Key;
				var indices = listKeyValuePair.Value;
				if (!playlistCache.TryGetValue(listId, out var playlistData))
					continue;

				foreach (var index in indices) {
					var songs = playlistData.Songs;
					RemoveResourceFromList(songs[index], listId);
					withInfo.Add(listId, index);
					songs[index] = withInfo;
					
					playlistData.Playlist[index] = new PlaylistItem(with);
				}
			}
			return true;
		}

		public void Clear() {
			playlistCache.Clear();
			uniqueSongs.Clear();
		}

		public IReadOnlyDictionary<UniqueResource, UniqueResourceInfo> UniqueResourcesDictionary => uniqueSongs;

		public IReadOnlyCollection<UniqueResourceInfo> GetUniqueResources() { return uniqueSongs.Values; }
	}
}
