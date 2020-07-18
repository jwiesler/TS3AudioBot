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
		public AudioResource Resource { get; set; }

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

		// Partitions ContainingListInstances into contained and not contained
		public (List<KeyValuePair<string, int>> contained, List<KeyValuePair<string, int>> notContained) PartitionContainingLists(UniqueResourceInfo o) {
			var contained = new List<KeyValuePair<string, int>>();
			var notContained = new List<KeyValuePair<string, int>>();
			foreach (var containingListInstance in ContainingListInstances) {
				(o.IsContainedIn(containingListInstance.Key) ? contained : notContained).Add(containingListInstance);
			}
			return (contained, notContained);
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

		public bool TryGet(AudioResource resource, out UniqueResourceInfo info) {
			return uniqueSongs.TryGetValue(resource, out info);
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

		public void ReplaceResource(UniqueResourceInfo info, AudioResource resource) {
			uniqueSongs.Remove(info.Resource);
			info.Resource = resource;
			uniqueSongs.Add(resource, info);
		}

		public bool Remove(AudioResource resource) {
			return uniqueSongs.Remove(resource);
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
		
		public object Lock { get; } = new object();

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

			// Replaces the item at `index`, returns false if the item at `index` is not equal to `resource` afterwards (i.e. already contained)
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

			public bool TryGetIndexOf(AudioResource resource, out int index) {
				return database.TryGetIndexOfInternal(Id, resource, out index);
			}
		}

		public PlaylistDatabase(PlaylistIO io) : this((IPlaylistIO) io) {}

		public PlaylistDatabase(IPlaylistIO io) {
			this.io = io;
			Reload();
		}

		private void EditPlaylistAndWriteInteral(string id, DatabasePlaylist list, Action<PlaylistEditor> editor) {
			editor(new PlaylistEditor(id, list, this));
			AfterPlaylistChanged(id, list);
		}

		public bool EditPlaylist(string listId, Action<PlaylistEditor> editor) {
			lock (Lock) {
				if (!io.TryGetRealId(listId, out var id) || !TryGetInternal(id, out var list))
					return false;

				EditPlaylistAndWriteInteral(id, list, editor);
				return true;
			}
		}

		public bool EditPlaylistEditorsBase(string listId, Action<string, IPlaylistEditors> editors) {
			lock (Lock) {
				if (!io.TryGetRealId(listId, out var id) || !TryGetInternal(id, out var list))
					return false;

				editors(id, list);
				AfterPlaylistChanged(id, list);
				return true;
			}
		}

		// Returns the index of the resource in the list
		// O(log d)
		private bool TryGetIndexOfInternal(string id, AudioResource resource, out int index) {
			if (resourcesDatabase.TryGetUniqueResourceInfo(resource, out var info) &&
			    info.ContainingLists.TryGetValue(id, out index))
				return true;
			index = 0;
			return false;
		}

		public bool TryGetIndexOf(string listId, AudioResource resource, out int index) {
			lock (Lock) {
				if (io.TryGetRealId(listId, out var id))
					return TryGetIndexOfInternal(id, resource, out index);
				index = 0;
				return false;
			}
		}

		private void LogPlaylistIdNotFoundThatShouldExist(string id) {
			Log.Error($"Not existing playlist with id {id} that should exist contained in a PlaylistDatabase object");
		}

		private void SaveAll(IEnumerable<string> lists) {
			foreach (var listId in lists) {
				if (TryGetInternal(listId, out var containingList)) {
					AfterPlaylistChanged(listId, containingList);
				} else {
					LogPlaylistIdNotFoundThatShouldExist(listId);
				}
			}
		}

		public enum ChangeItemResult {
			Success,
			ErrorListNotFound,
			ErrorIntroducesDuplicate
		}

		public enum ChangeItemReplacement {
			Database,
			Input
		}

		// Replaces the item at `index` and all its occurences, returns false if the item at `index` is not exactly the same as `resource` afterwards
		// `replacement` handles the replacement priority if `resource` is already contained in the database but not exactly equal. ChangeItemReplacement.Input may involve changing unrelated playlists!!
		// `shouldHandleDuplicates` if false fails if the change would introduce a duplicate
		// O(1) + io time for each containing playlist if the replacement does not produce duplicates
		// Higher else
		public ChangeItemResult ChangeItemAtDeep(string listId, int index, AudioResource resource, ChangeItemReplacement replacement = ChangeItemReplacement.Database, bool shouldHandleDuplicates = false) {
			lock (Lock) {
				if (!io.TryGetRealId(listId, out var id) || !TryGetInternal(id, out var list))
					return ChangeItemResult.ErrorListNotFound;

				var item = list.InfoItems[index];
				if (item.Resource.ReallyEquals(resource))
					return ChangeItemResult.Success;

				if (!resourcesDatabase.TryGet(resource, out var resourceInfo) || ReferenceEquals(item, resourceInfo)) {
					// Replacement is not contained or will be mapped to the same item
					// Replacement will not produce a duplicate if added to all already containing playlists
					// => just change the item we got
					resourcesDatabase.ReplaceResource(item, resource);

					SaveAll(item.ContainingLists.Keys);
				} else {
					// Replacement is contained and will not be mapped to the same item
					// Replacement might produce a duplicate if added to all already containing playlists
					// => remove from all playlists that already contain the duplicate and add to the rest, remove the old item from the database

					var (contained, notContained) = item.PartitionContainingLists(resourceInfo);

					if (!shouldHandleDuplicates && contained.Count > 0)
						return ChangeItemResult.ErrorIntroducesDuplicate;

					// Replacement is different from the version we have stored => change the other instances as well
					if (replacement == ChangeItemReplacement.Input && !resourceInfo.Resource.ReallyEquals(resource)) {
						resourcesDatabase.ReplaceResource(resourceInfo, resource);

						// Save only the ones that don't contain the initial item as the rest will be saved later
						var containedMap = new Dictionary<string, int>(contained.Count);
						foreach (var c in contained)
							containedMap.Add(c.Key, c.Value);

						SaveAll(resourceInfo.ContainingLists.Where(x => !containedMap.ContainsKey(x.Key))
							.Select(x => x.Key));
					}

					// remove from all playlists that already contain the replacement
					foreach (var kvp in contained) {
						if (TryGetInternal(kvp.Key, out var containingList)) {
							EditPlaylistAndWriteInteral(kvp.Key, containingList, editor => {
								editor.RemoveItemAt(kvp.Value);
							});
						} else {
							LogPlaylistIdNotFoundThatShouldExist(kvp.Key);
						}
					}

					// add to the rest
					foreach (var kvp in notContained) {
						if (TryGetInternal(kvp.Key, out var containingList)) {
							resourceInfo.TryAdd(kvp.Key, kvp.Value);
							containingList.InfoItems[kvp.Value] = resourceInfo;
							AfterPlaylistChanged(kvp.Key, containingList);
						} else {
							LogPlaylistIdNotFoundThatShouldExist(kvp.Key);
						}
					}
					resourcesDatabase.Remove(item.Resource);
				}

				return ChangeItemResult.Success;
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
			lock (Lock) {
				if (!io.TryGetRealId(listId, out id) || !TryGetInternal(id, out var list)) {
					value = null;
					return false;
				}

				value = list;
				return true;
			}
		}

		public PlaylistInfo[] GetInfos() {
			lock (Lock) {
				return playlistCache.Select(kvp => new PlaylistInfo {
					Id = kvp.Key,
					SongCount = kvp.Value.Count,
					OwnerId = kvp.Value.Owner.Value,
					AdditionalEditors = kvp.Value.AdditionalEditors.Select(k => k.Value).ToList()
				}).ToArray();
			}
		}

		public bool CreatePlaylist(string listId, Uid owner) {
			lock (Lock) {
				var list = new DatabasePlaylist(owner);
				if (io.TryGetRealId(listId, out _))
					return false;

				playlistCache.Add(listId, list);
				io.Write(listId, list);
				return true;
			}
		}

		public bool ContainsPlaylist(string id) {
			lock (Lock) {
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
			lock (Lock) {
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
			lock (Lock) {
				playlistCache.Clear();
				resourcesDatabase.Clear();
				io.Clear();
			}
		}

		public void Reload() {
			lock (Lock) {
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
				lock (Lock) {
					return resourcesDatabase.UniqueResources;
				}
			}
		}

		public bool TryGetUniqueResourceInfo(AudioResource resource, out IReadonlyUniqueResourceInfo info) {
			return resourcesDatabase.TryGetUniqueResourceInfo(resource, out info);
		}
	}
}
