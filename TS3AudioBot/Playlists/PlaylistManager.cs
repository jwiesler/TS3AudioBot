// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections.Generic;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Search;
using TS3AudioBot.Web.Model;
using TSLib;

namespace TS3AudioBot.Playlists
{
	public sealed class PlaylistManager
	{
		private readonly PlaylistIO playlistPool;
		public ResourceSearch ResourceSearch { get; set; }
		private readonly object listLock = new object();

		public bool Random
		{
			get;
			set;
		}

		public int Seed { get; set; }

		/// <summary>Loop mode for the current playlist.</summary>
		public LoopMode Loop { get; set; } = LoopMode.Off;

		public PlaylistManager(PlaylistIO playlistPool)
		{
			this.playlistPool = playlistPool;
		}

		public R<IReadOnlyPlaylist, LocalStr> LoadPlaylist(string listId)
		{
			var checkName = Util.IsSafeFileName(listId);
			if (!checkName.Ok)
				return checkName.Error;

			var res = playlistPool.ReadFull(listId);

			if (!res.Ok)
				return res.Error;
			return res.Value;
		}

		public E<LocalStr> CreatePlaylist(string listId, Uid owner, string title = null)
		{
			var checkName = Util.IsSafeFileName(listId);
			if (!checkName.Ok)
				return checkName;
			if (playlistPool.Exists(listId))
				return new LocalStr("Already exists");
			
			var ret = playlistPool.Write(listId, new Playlist(title ?? listId, owner));
			ResourceSearch?.Rebuild();
			return ret;
		}

		public bool ExistsPlaylist(string listId)
		{
			var checkName = Util.IsSafeFileName(listId);
			if (!checkName.Ok)
				return false;
			return playlistPool.Exists(listId);
		}

		public E<LocalStr> ModifyPlaylist(string listId, Action<Playlist> action)
		{
			var checkName = Util.IsSafeFileName(listId);
			if (!checkName.Ok)
				return checkName.Error;
			var res = playlistPool.ReadFull(listId);
			if (!res.Ok) {
				return res.Error;
			}

			var plist = res.Value;
			lock (listLock)
			{
				action(plist);
			}

			var ret = playlistPool.Write(listId, plist);
			ResourceSearch?.Rebuild();
			return ret;
		}

		public E<LocalStr> DeletePlaylist(string listId)
		{
			var checkName = Util.IsSafeFileName(listId);
			if (!checkName.Ok)
				return checkName.Error;

			var ret = playlistPool.Delete(listId);
			ResourceSearch?.Rebuild();
			return ret;
		}

		public R<PlaylistInfo[], LocalStr> GetAvailablePlaylists(string pattern = null) => playlistPool.ListPlaylists();

		public bool TryGetPlaylistId(string listId, out string id) {
			var checkName = Util.IsSafeFileName(listId);
			if (!checkName.Ok) {
				id = null;
				return false;
			}

			return playlistPool.TryGetPlaylistId(listId, out id);
		}
	}
}
