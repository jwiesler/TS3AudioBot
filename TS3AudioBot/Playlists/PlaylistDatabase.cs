using System;
using System.Collections.Generic;
using System.Linq;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Web.Model;
using TSLib;

namespace TS3AudioBot.Playlists {
	public interface IReadonlyUniqueResourceInfo {
		AudioResource Resource { get; }
		IReadOnlyDictionary<string, int> ContainingLists { get; }
	}

	public class UniqueResourceInfo : IReadonlyUniqueResourceInfo {
		public AudioResource Resource { get; }

		private Dictionary<string, int> ContainingListInstances { get; } = new Dictionary<string, int>();

		public IReadOnlyDictionary<string, int> ContainingLists => ContainingListInstances;

		public UniqueResourceInfo(AudioResource resource) { Resource = resource; }

		public bool TryAdd(string id, int offset) {
			if (ContainingListInstances.ContainsKey(id))
				return false;
			ContainingListInstances.Add(id, offset);
			return true;
		}

		public bool RemoveList(string id) {
			return ContainingListInstances.Remove(id);
		}

		public void UpdateIndex(string id, int offset) {
			if (!ContainingListInstances.ContainsKey(id))
				throw new ArgumentException();
			ContainingListInstances[id] = offset;
		} 

		public bool IsContainedIn(string id) { return ContainingListInstances.ContainsKey(id); }

		public bool IsContainedInAList => ContainingListInstances.Count > 0;
	}
	
	public class DatabasePlaylist : PlaylistEditorsBase, IPlaylist {
		public List<UniqueResourceInfo> InfoItems { get; }
		public IEnumerable<AudioResource> Items => InfoItems.Select(i => i.Resource);
		public AudioResource this[int i] => InfoItems[i].Resource;
		public int Count => InfoItems.Count;

		public DatabasePlaylist(Uid owner) :
			this(owner, Enumerable.Empty<Uid>())
		{ }

		public DatabasePlaylist(Uid owner, IEnumerable<Uid> editors) :
			this(owner, editors, new List<UniqueResourceInfo>())
		{ }

		public DatabasePlaylist(Uid owner, IEnumerable<Uid> editors, List<UniqueResourceInfo> items)  : base(owner, editors)
		{
			InfoItems = items ?? throw new ArgumentNullException(nameof(items));
		}
	}

	public class PlaylistResourcesDatabase {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private readonly Dictionary<AudioResource, UniqueResourceInfo> uniqueSongs =
			new Dictionary<AudioResource, UniqueResourceInfo>();

		public IEnumerable<IReadonlyUniqueResourceInfo> UniqueResources => uniqueSongs.Values.Select(v => (IReadonlyUniqueResourceInfo) v);

		public bool TryGetUniqueResourceInfo(AudioResource resource, out IReadonlyUniqueResourceInfo info) {
			if (uniqueSongs.TryGetValue(resource, out var v)) {
				info = v;
				return true;
			}

			info = null;
			return false;
		}

		public bool GetOrCreateForListItem(AudioResource resource, string id, int index, out UniqueResourceInfo info) {
			if (uniqueSongs.TryGetValue(resource, out info)) {
				return info.TryAdd(id, index);
			}

			info = new UniqueResourceInfo(resource);
			info.TryAdd(id, index);
			uniqueSongs.Add(resource, info);
			return true;
		}

		public void RemoveListFromItem(string id, UniqueResourceInfo info) {
			if(!info.RemoveList(id))
				Log.Warn("Failed to remove song from database");

			if (!info.IsContainedInAList)
				uniqueSongs.Remove(info.Resource);
		}

		public void Clear() {
			uniqueSongs.Clear();
		}
	}

	public class PlaylistDatabase {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private readonly IPlaylistIO io;
		private readonly Dictionary<string, DatabasePlaylist> playlistCache = new Dictionary<string, DatabasePlaylist>(16);
		private readonly PlaylistResourcesDatabase resourcesDatabase = new PlaylistResourcesDatabase();
		
		private readonly object myLock = new object();

		public class PlaylistEditor {
			private readonly DatabasePlaylist playlist;
			private readonly PlaylistDatabase database;
			public IPlaylist Playlist => playlist;
			public string Id { get; }

			public PlaylistEditor(string id, DatabasePlaylist playlist, PlaylistDatabase database) {
				Id = id;
				this.playlist = playlist;
				this.database = database;
			}

			// Adds an item, returns false if the item is already contained
			// O(log d)
			public bool Add(AudioResource resource) {
				if (!database.resourcesDatabase.GetOrCreateForListItem(resource, Id, playlist.Count, out var info))
					return false;
				playlist.InfoItems.Add(info);
				return true;
			}

			// Adds every item in the list blindly, i.e. does not check whether the item was actually added
			public void AddRange(IEnumerable<AudioResource> items) {
				foreach (var item in items)
					Add(item);
			}

			// Moves and updates the moved items' index, ignores values at target
			// O(n)
			private static int Move(string id, List<UniqueResourceInfo> items, int begin, int end, int target) {
				while (begin != end) {
					var item = items[target] = items[begin];
					item.UpdateIndex(id, target);
					target++;
					begin++;
				}

				return target;
			}

			// Moves and updates the moved items' index, ignores values at target
			// O(n)
			private static int MoveBackwards(string id, List<UniqueResourceInfo> items, int begin, int end, int targetEnd) {
				while (begin != end) {
					var item = items[--targetEnd] = items[--end];
					item.UpdateIndex(id, targetEnd);
				}

				return targetEnd;
			}

			// Replaces the item at `index`, returns false if the item at `index` is not equal to resource afterwards (i.e. already contained)
			// O(log d)
			public bool ChangeItemAt(int index, AudioResource resource) {
				var item = playlist.InfoItems[index];
				if (Equals(item.Resource, resource))
					return true;

				database.resourcesDatabase.RemoveListFromItem(Id, item);
				if (!database.resourcesDatabase.GetOrCreateForListItem(resource, Id, index, out var info))
					return false;
				playlist.InfoItems[index] = info;
				return true;
			}

			// Removes the item at `index` and returns it. Shifts all items with a higher index down
			// O(n), O(1) if last, additionally O(log d) if this was the last list containing this item
			public AudioResource RemoveItemAt(int index) {
				var resource = playlist.InfoItems[index].Resource;
				database.resourcesDatabase.RemoveListFromItem(Id, playlist.InfoItems[index]);
				Move(Id, playlist.InfoItems, index + 1, playlist.Count, index);
				playlist.InfoItems.RemoveAt(playlist.Count - 1);
				return resource;
			}

			private static void RemoveIndices(string id, List<UniqueResourceInfo> items, IList<int> indices, int ibegin, int iend) {
				if(iend == ibegin)
					return;

				int count = items.Count;
				int it = indices[ibegin];
				for (int iit = ibegin; iit < iend; iit++) {
					int next = (iit + 1 == iend ? count : indices[iit + 1]);
					int moveBegin = indices[iit] + 1;
					int moveEnd = next;

					it = Move(id, items, moveBegin, moveEnd, it);
				}

				items.RemoveRange(it, items.Count - it);
			}

			// Removes all items specified by `indices`. Shifts the remaining items. Indices has to be sorted in ascending order.
			// O(n), additionally for every item O(log d) if this was the last list containing this item
			public void RemoveIndices(IList<int> indices) {
				RemoveIndices(Id, playlist.InfoItems, indices, 0, indices.Count);
			}

			// Moves an item from `index` to `to`, shifting other items to make/fill space
			// O(n)
			public void MoveItem(int index, int to) {
				if (index == to)
					return;

				var item = playlist.InfoItems[index];
				if (index < to) {
					Move(Id, playlist.InfoItems, index + 1, to + 1, index);
				} else {
					MoveBackwards(Id, playlist.InfoItems, to, index, index + 1);
				}

				item.UpdateIndex(Id, to);
				playlist.InfoItems[to] = item;
			}

			// Returns the index of the resource in the list
			// O(log d)
			public bool TryGetIndexOf(AudioResource resource, out int index) {
				if (database.resourcesDatabase.TryGetUniqueResourceInfo(resource, out var info) &&
				    info.ContainingLists.TryGetValue(Id, out index))
					return true;
				index = 0;
				return false;

			}
		}

		public PlaylistDatabase(PlaylistIO io) : this((IPlaylistIO) io) {}

		public PlaylistDatabase(IPlaylistIO io) {
			this.io = io;
			Reload();
		}

		public bool EditPlaylist(string listId, Action<PlaylistEditor> editor) {
			lock (myLock) {
				if (!io.TryGetRealId(listId, out var id) || !TryGetInternal(id, out var list))
					return false;

				editor(new PlaylistEditor(id, list, this));
				AfterPlaylistChanged(id, list);
				return true;
			}
		}

		public bool EditPlaylistEditorsBase(string listId, Action<string, IPlaylistEditors> editors) {
			lock (myLock) {
				if (!io.TryGetRealId(listId, out var id) || !TryGetInternal(id, out var list))
					return false;

				editors(id, list);
				AfterPlaylistChanged(id, list);
				return true;
			}
		}

		private void AfterPlaylistChanged(string id, DatabasePlaylist playlist) {
			io.Write(id, playlist);
		}

		private bool TryGetInternal(string id, out DatabasePlaylist value) {
			if (playlistCache.TryGetValue(id, out var data)) {
				value = data;
				return true;
			}

			value = null;
			return false;
		}

		public bool TryGet(string listId, out string id, out IPlaylist value) {
			lock (myLock) {
				if (!io.TryGetRealId(listId, out id) || !TryGetInternal(id, out var list)) {
					value = null;
					return false;
				}

				value = list;
				return true;
			}
		}

		public PlaylistInfo[] GetInfos() {
			lock (myLock) {
				return playlistCache.Select(kvp => new PlaylistInfo {
					Id = kvp.Key,
					SongCount = kvp.Value.Count,
					OwnerId = kvp.Value.Owner.Value,
					AdditionalEditors = kvp.Value.AdditionalEditors.Select(k => k.Value).ToList()
				}).ToArray();
			}
		}

		public bool CreatePlaylist(string listId, Uid owner) {
			lock (myLock) {
				var list = new DatabasePlaylist(owner);
				if (io.TryGetRealId(listId, out _))
					return false;

				playlistCache.Add(listId, list);
				io.Write(listId, list);
				return true;
			}
		}

		public bool ContainsPlaylist(string id) {
			lock (myLock) {
				return playlistCache.ContainsKey(id);
			}
		}

		

		private void RemovePlaylistItemsInternal(string id, DatabasePlaylist list) {
			foreach (var info in list.InfoItems) {
				if(!info.RemoveList(id))
					Log.Warn("Failed to remove song from database");

				resourcesDatabase.RemoveListFromItem(id, info);
			}
		}

		public bool Remove(string id) {
			lock (myLock) {
				if (!playlistCache.TryGetValue(id, out var list))
					return false;

				RemovePlaylistItemsInternal(id, list);
				playlistCache.Remove(id);
				io.Delete(id);
				return true;
			}
		}

		/*public bool GetAllOccurences(AudioResource resource, out IReadOnlyCollection<KeyValuePair<string, int>> list) {
			if (UniqueResourcesDictionary.TryGetValue(resource, out var info)) {
				list = info.ContainingLists;
				return true;
			}

			list = null;
			return false;
		}*/

		/*private static void OverwriteListItem(string listId, PlaylistData playlistData, List<UniqueResourceInfo> songs, int index, AudioResource with, UniqueResourceInfo withInfo) {
			withInfo.AddInstance(listId, index);
			songs[index] = withInfo;
					
			playlistData.Playlist.ItemsW[index] = with;
		}*/

		/*public void ChangeAllOccurences(UniqueResource resource, AudioResource with) {
			if (!uniqueSongs.TryGetValue(resource, out var info))
				return;

			
			if (GetOrCreate(with, out var withInfo)) {
				if (Equals(resource, withInfo.Resource))
					return;
			}

			foreach (var listKeyValuePair in info.ContainingLists) {
				var listId = listKeyValuePair.Key;
				var indices = listKeyValuePair.Value;
				if (!playlistCache.TryGetValue(listId, out var playlistData))
					continue;

				indices.Sort();
				var songs = playlistData.Songs;

				int startRemoveIndex;
				if (withInfo.IsContainedIn(listId)) {
					// Already contained, remove all
					Log.Debug($"{resource.ResourceTitle} is already in {listId}, removing entries");
					startRemoveIndex = 0;
				} else {
					// Not yet contained, replace and remove other occurences
					Log.Debug($"{resource.ResourceTitle} is not in {listId}, replacing entry");
					startRemoveIndex = 1;
					OverwriteListItem(listId, playlistData, songs, indices[0], with, withInfo);
				} 

				Collections.RemoveIndices(songs, indices, startRemoveIndex, indices.Count);
				playlistData.Playlist.RemoveIndices(indices, startRemoveIndex, indices.Count);
			}

			uniqueSongs.Remove(resource);
		}*/

		public void Clear() {
			lock (myLock) {
				playlistCache.Clear();
				resourcesDatabase.Clear();
				io.Clear();
			}
		}

		public void Reload() {
			lock (myLock) {
				Clear();
				ReloadFromIo();
			}
		}

		private void AddPlaylistInternal(string id, IPlaylist list) {
			var items = new List<UniqueResourceInfo>(list.Count);

			for (var index = 0; index < list.Count; index++) {
				var item = list[index];
				if(resourcesDatabase.GetOrCreateForListItem(item, id, index, out var info))
					items.Add(info);
				else
					Log.Info($"Song {item.ResourceTitle} in playlist {id} at index {index} is already contained in this playlist, skipping");
			}

			var plist = new DatabasePlaylist(list.Owner, list.AdditionalEditors, items);
			playlistCache.Add(id, plist);
		}

		private void ReloadFromIo() {
			var items = io.ReloadFolder();
			foreach (var (id, list) in items)
				AddPlaylistInternal(id, list);
		}

		public IEnumerable<IReadonlyUniqueResourceInfo> UniqueResources {
			get {
				lock (myLock) {
					return resourcesDatabase.UniqueResources;
				}
			}
		}

		public bool TryGetUniqueResourceInfo(AudioResource resource, out IReadonlyUniqueResourceInfo info) {
			return resourcesDatabase.TryGetUniqueResourceInfo(resource, out info);
		}
	}
}
