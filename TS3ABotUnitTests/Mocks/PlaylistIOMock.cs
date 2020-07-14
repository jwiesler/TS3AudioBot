using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;

namespace TS3ABotUnitTests.Mocks {
	public class PlaylistIOMock : PlaylistLowerIdToId, IPlaylistIO {
		public Dictionary<string, IPlaylist> Playlists { get; } = new Dictionary<string, IPlaylist>();

		public void Write(string listId, IPlaylist list) {
			Assert.IsTrue(Playlists.ContainsKey(listId));
			Playlists[listId] = list;
		}

		public E<LocalStr> Delete(string id) {
			if(!Playlists.ContainsKey(id))
				return new LocalStr();
			Playlists.Remove(id);
			return R.Ok;
		}

		public List<(string, IPlaylist)> ReloadFolder() {
			return new List<(string, IPlaylist)>(Playlists.Select(kv => (kv.Key, kv.Value)));
		}
	}
}
