// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Linq;
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
		public object Lock => database.Lock;
		public int UniqueCount => database.UniqueResources.Count();
		public int Count => database.UniqueResources.Select(r => r.ContainingLists.Count()).Sum();

		public PlaylistManager(PlaylistDatabase database, ResourceSearch resourceSearch) {
			this.database = database;
			this.resourceSearch = resourceSearch;
		}

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

		public bool ContainsPlaylist(string listId) { return database.ContainsPlaylist(listId); }

		public bool TryGetPlaylistId(string listId, out string id) { return database.TryGet(listId, out id, out _); }

		public bool TryGetIndexOf(string listId, AudioResource resource, out int index) {
			return database.TryGetIndexOf(listId, resource, out index);
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

			if(!database.EditPlaylist(listId, action))
				return ErrorListNotFound(listId);

			resourceSearch?.Rebuild();
			return E<LocalStr>.OkR;
		}

		public E<LocalStr> ModifyPlaylistEditors(string listId, Action<string, IPlaylistEditors> action) {
			if(!database.EditPlaylistEditorsBase(listId, action))
				return ErrorListNotFound(listId);
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

		// Replaces all occurences of the resource in `listId` at `index` with `with` or removes `resource` if `with` is already in the playlist
		public E<LocalStr> ChangeItemAtDeep(string listId, int index, AudioResource with, PlaylistDatabase.ChangeItemReplacement replacement = PlaylistDatabase.ChangeItemReplacement.Database, bool shouldHandleDuplicates = false) {
			switch (database.ChangeItemAtDeep(listId, index, with, replacement, shouldHandleDuplicates)) {
			case PlaylistDatabase.ChangeItemResult.Success:
				resourceSearch?.Rebuild();
				return R.Ok;
			case PlaylistDatabase.ChangeItemResult.ErrorListNotFound:
				return ErrorListNotFound(listId);
			case PlaylistDatabase.ChangeItemResult.ErrorIntroducesDuplicate:
				return new LocalStr("This change would involve creating a duplicate because the replacement is already contained in one of the playlists.");
			default:
				throw new ArgumentOutOfRangeException();
			}
		}

		public bool TryGetUniqueResourceInfo(AudioResource resource, out IReadonlyUniqueResourceInfo info) {
			return database.TryGetUniqueResourceInfo(resource, out info);
		}

		public void ReloadFromDisk() {
			database.Reload();
		}
	}
}
