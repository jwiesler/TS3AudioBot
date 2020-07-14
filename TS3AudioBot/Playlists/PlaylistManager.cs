// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Search;
using TS3AudioBot.Web.Model;
using TSLib;

namespace TS3AudioBot.Playlists
{
	public sealed class PlaylistManager
	{
		private readonly PlaylistDatabase database;
		private readonly ResourceSearch resourceSearch;
		private readonly object listLock = new object();

		public PlaylistManager(IPlaylistIO playlistPool, ResourceSearch resourceSearch) {
			database = new PlaylistDatabase(playlistPool);
			this.resourceSearch = resourceSearch;
		}

		public PlaylistManager(PlaylistIO playlistPool, ResourceSearch resourceSearch) : this((IPlaylistIO) playlistPool, resourceSearch) {}

		private static LocalStr ErrorListNotFound(string list) {
			return new LocalStr($"Could not find playlist {list}");
		}

		public R<(IPlaylist list, string id), LocalStr> GetPlaylist(string listId)
		{
			var checkName = Util.IsSafeFileName(listId);
			if (!checkName.Ok)
				return checkName.Error;

			if (!database.TryGet(listId, out var id, out var list))
				return ErrorListNotFound(listId);
			return (list, id);
		}

		public E<LocalStr> CreatePlaylist(string listId, Uid owner)
		{
			var checkName = Util.IsSafeFileName(listId);
			if (!checkName.Ok)
				return checkName;
			if (!database.CreatePlaylist(listId, owner))
				return new LocalStr($"Playlist {listId} already exists");
			
			return R.Ok;
		}

		public bool ExistsPlaylist(string listId)
		{
			var checkName = Util.IsSafeFileName(listId);
			if (!checkName.Ok)
				return false;
			return database.ContainsPlaylist(listId);
		}

		public E<LocalStr> ModifyPlaylist(string listId, Action<PlaylistDatabase.PlaylistEditor> action)
		{
			var checkName = Util.IsSafeFileName(listId);
			if (!checkName.Ok)
				return checkName.Error;

			lock (listLock)
			{
				if(!database.EditPlaylist(listId, action))
					return ErrorListNotFound(listId);
			}

			resourceSearch?.Rebuild();
			return E<LocalStr>.OkR;
		}

		public E<LocalStr> ModifyPlaylistEditors(string listId, Action<string, IPlaylistEditors> action) {
			lock (listLock)
			{
				if(!database.EditPlaylistEditorsBase(listId, action))
					return ErrorListNotFound(listId);
			}
			return E<LocalStr>.OkR;
		}

		public E<LocalStr> DeletePlaylist(string listId)
		{
			var checkName = Util.IsSafeFileName(listId);
			if (!checkName.Ok)
				return checkName.Error;

			if (!database.Remove(listId))
				return new LocalStr($"Failed to delete list {listId}");
			resourceSearch?.Rebuild();
			return R.Ok;
		}

		public PlaylistInfo[] GetAvailablePlaylists() => database.GetInfos();

		/*public bool TryGetPlaylistId(string listId, out string id) {
			var checkName = Util.IsSafeFileName(listId);
			if (!checkName.Ok) {
				id = null;
				return false;
			}

			return database.TryGetPlaylistId(listId, out id);
		}*/

		/*public bool TryGetUniqueItem(UniqueResource resource, out UniqueResourceInfo info) {
			return playlistPool.TryGetUniqueItem(resource, out info);
		}

		public bool GetAllOccurences(UniqueResource resource, out IReadOnlyCollection<KeyValuePair<string, List<int>>> list) {
			return playlistPool.GetAllOccurences(resource, out list);
		}*/

		// Replaces all occurences of `resource` with `with` or removes `resource` if `with` is already in the playlist
		/*public void ChangeAllOccurences(UniqueResource resource, AudioResource with) {
			playlistPool.ChangeAllOccurences(resource, with);
			resourceSearch?.Rebuild();
		}*/

		public bool TryGetUniqueResourceInfo(AudioResource resource, out IReadonlyUniqueResourceInfo info) {
			return database.TryGetUniqueResourceInfo(resource, out info);
		}

		public void ReloadFromDisk() {
			database.Reload();
		}
	}
}
