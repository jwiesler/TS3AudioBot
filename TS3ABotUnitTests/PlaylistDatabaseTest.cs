using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TS3ABotUnitTests.Mocks;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TSLib;

namespace TS3ABotUnitTests
{
	[TestFixture]
	public class PlaylistDatabaseTest {
		public static void AssertIsDefaultPlaylist(Uid uid, IPlaylist list) {
			Assert.IsNotNull(list);
			Assert.AreEqual(0, list.Count);
			Assert.IsEmpty(list.Items);
			Assert.IsEmpty(list.AdditionalEditors);
			Assert.AreEqual(uid, list.Owner);
		}

		public static void AssertPlaylistsEqual(IPlaylist expected, IPlaylist value) {
			if (expected == null) {
				Assert.IsNull(value);
				return;
			}
			Assert.IsNotNull(value);
			Assert.AreEqual(expected.Owner, value.Owner);
			CollectionAssert.AreEquivalent(expected.Items, value.Items);
			CollectionAssert.AreEquivalent(expected.AdditionalEditors, value.AdditionalEditors);
		}

		public static IEnumerable<(int, T)> EnumerateWithIndex<T>(IEnumerable<T> list) {
			var index = 0;
			foreach (var item in list) {
				yield return (index++, item);
			}
		}

		public static IEnumerable<AudioResource> EnumeratePlaylist(IPlaylist list) {
			for (var i = 0; i < list.Count; ++i) {
				yield return list[i];
			}
		}

		public class Helper {
			public PlaylistIOMock Io { get; } = new PlaylistIOMock();
			public PlaylistDatabase Database { get; }

			public Helper() {
				Database = new PlaylistDatabase(Io);
			}

			public void CheckDoesContainPlaylist(string id) {
				Assert.IsTrue(Database.TryGet(id, out var lid, out _));
				Assert.AreSame(id, lid);
				Assert.IsTrue(Database.ContainsPlaylist(id));
				Assert.IsTrue(Io.Playlists.ContainsKey(id));
			}

			public void CheckDoesNotContainPlaylist(string id) {
				Assert.IsFalse(Database.TryGet(id, out _, out _));
				Assert.IsFalse(Database.ContainsPlaylist(id));
				Assert.IsFalse(Io.Playlists.ContainsKey(id));
			}

			public void CreatePlaylist(string id, Uid uid) {
				CheckDoesNotContainPlaylist(id);
				Database.CreatePlaylist(id, uid);

				CheckDoesContainPlaylist(id);
				Assert.IsTrue(Database.TryGet(id, out _, out var list));
				AssertIsDefaultPlaylist(uid, list);

				Assert.IsTrue(Io.Playlists.TryGetValue(id, out var writtenPlaylist));
				AssertIsDefaultPlaylist(uid, writtenPlaylist);
			}

			public void RemovePlaylist(string id) {
				CheckDoesContainPlaylist(id);
				Assert.IsTrue(Database.Remove(Constants.ListId));
				CheckDoesNotContainPlaylist(id);
			}

			public void CheckExistingPlaylistWritten(string listId) {
				Assert.IsTrue(Database.TryGet(listId, out _, out var list));
				Assert.IsTrue(Io.Playlists.TryGetValue(listId, out var written));
				AssertPlaylistsEqual(list, written);
			}

			public void EditPlaylistEditorsBase(string listId, Action<string, IPlaylistEditors> action) {
				Assert.IsTrue(Database.EditPlaylistEditorsBase(listId, (id, editors) => {
					Assert.AreEqual(listId, id);
					Assert.IsNotNull(editors);
					action(id, editors);
				}));
				CheckExistingPlaylistWritten(Constants.ListId);
			}

			public void EditPlaylist(string listId, Action<PlaylistDatabase.PlaylistEditor> action) {
				Assert.IsTrue(Database.EditPlaylist(listId, editor => {
					Assert.IsNotNull(editor);
					Assert.AreEqual(listId, editor.Id);
					action(editor);
				}));
				CheckExistingPlaylistWritten(Constants.ListId);
			}

			public void CheckUniqueSong(AudioResource resource, string id, int index) {
				Assert.IsTrue(Database.TryGetUniqueResourceInfo(resource, out var info));
				Assert.IsTrue(info.ContainingLists.TryGetValue(id, out var idx));
				Assert.AreEqual(index, idx);
			}

			public void CheckPlaylistContainsExactly(string id, IEnumerable<AudioResource> resources) {
				Assert.IsTrue(Database.TryGet(id, out _, out var list));
				foreach(var (i, item) in EnumerateWithIndex(resources)) {
					Assert.AreEqual(item, list[i]);
				}
			}

			public void CheckUniqueSongs(string id, IEnumerable<AudioResource> resources) {
				foreach(var (i, item) in EnumerateWithIndex(resources)) {
					CheckUniqueSong(item, id, i);
				}
			}

			public void CheckUniqueSongsExactly(string id, IEnumerable<AudioResource> resources) {
				var count = 0;
				foreach(var (i, item) in EnumerateWithIndex(resources)) {
					CheckUniqueSong(item, id, i);
					++count;
				}
				Assert.AreEqual(Database.UniqueResources.Count(), count);
			}
		}

		[Test]
		public void CreatePlaylistTest() {
			var helper = new Helper();
			helper.CreatePlaylist(Constants.ListId, Constants.TestUid);
		}

		[Test]
		public void DifferentCasePlaylistTest() {
			var helper = new Helper();
			helper.CreatePlaylist(Constants.ListId, Constants.TestUid);
			Assert.IsTrue(helper.Database.TryGet(Constants.ListIdMixedCase, out var lid, out _));
			Assert.AreEqual(Constants.ListId, lid);
		}

		[Test]
		public void RemovePlaylistTest() {
			var helper = new Helper();
			helper.CreatePlaylist(Constants.ListId, Constants.TestUid);
			helper.RemovePlaylist(Constants.ListId);
		}

		[Test]
		public void ModifyPlaylistEditorsTest() {
			var helper = new Helper();
			helper.CreatePlaylist(Constants.ListId, Constants.TestUid);
			helper.EditPlaylistEditorsBase(Constants.ListId, (id, editors) => {
				Assert.IsTrue(editors.ToggleAdditionalEditor(Constants.UidA));
			});

			helper.EditPlaylistEditorsBase(Constants.ListId, (id, editors) => {
				Assert.IsFalse(editors.ToggleAdditionalEditor(Constants.UidA));
			});
		}

		[Test]
		public void ModifyPlaylistEditAddItemTest() {
			var helper = new Helper();
			helper.CreatePlaylist(Constants.ListId, Constants.TestUid);

			var resources = Constants.GenerateAudioResources(10);
			helper.EditPlaylist(Constants.ListId, editor => {
				foreach(var res in resources)
					Assert.IsTrue(editor.Add(res));
			});
			helper.CheckUniqueSongsExactly(Constants.ListId, resources);

			helper.EditPlaylist(Constants.ListId, editor => {
				foreach(var res in resources)
					Assert.IsFalse(editor.Add(res));
			});
			helper.CheckUniqueSongsExactly(Constants.ListId, resources);
		}

		[Test]
		public void ModifyPlaylistEditIndexOfTest() {
			var helper = new Helper();
			helper.CreatePlaylist(Constants.ListId, Constants.TestUid);

			const int count = 100;
			var resources = Constants.GenerateAudioResources(count);
			helper.EditPlaylist(Constants.ListId, editor => {
				editor.AddRange(resources);
			});

			helper.EditPlaylist(Constants.ListId, editor => {
				for (var i = 0; i < count; ++i) {
					Assert.IsTrue(editor.TryGetIndexOf(resources[i], out var index));
					Assert.AreEqual(i, index);
				}
			});
		}

		[Test]
		public void ModifyPlaylistEditRemoveItemTest() {
			var helper = new Helper();
			helper.CreatePlaylist(Constants.ListId, Constants.TestUid);

			const int count = 100;
			var resources = Constants.GenerateAudioResources(count);
			helper.EditPlaylist(Constants.ListId, editor => {
				editor.AddRange(resources);
			});

			var random = new Random(42);
			var indices = new int[count];
			for (var i = 0; i < count; ++i) {
				indices[i] = random.Next(count - i);
			}

			for (var i = 0; i < count; ++i) {
				var i1 = indices[i];
				helper.EditPlaylist(Constants.ListId, editor => {
					var resource = resources[i1];
					Assert.AreEqual(resource, editor.RemoveItem(i1));
					resources.RemoveAt(i1);
				});

				helper.CheckUniqueSongsExactly(Constants.ListId, resources);
			}
		}

		public static void Shuffle<T>(Random rng, T[] array) {
			int n = array.Length;
			while (n > 1) {
				int k = rng.Next(n--);
				T temp = array[n];
				array[n] = array[k];
				array[k] = temp;
			}
		}

		[Test]
		public void ModifyPlaylistEditChangeItemTest() {
			var helper = new Helper();
			helper.CreatePlaylist(Constants.ListId, Constants.TestUid);

			const int count = 100;
			var resources = Constants.GenerateAudioResources(count);
			helper.EditPlaylist(Constants.ListId, editor => {
				editor.AddRange(resources);
			});

			var random = new Random(42);
			var indices = new int[count];
			for (var i = 0; i < count; ++i) {
				indices[i] = i;
			}
			Shuffle(random, indices);

			var replacementResources = Constants.GenerateAudioResources(count, "1");
			for (var i = 0; i < count; ++i) {
				var i1 = i;
				helper.EditPlaylist(Constants.ListId, editor => {
					var index = indices[i1];
					var resource = replacementResources[index];
					if (i1 > 0) {
						var lastResource = replacementResources[indices[i1 - 1]];
						Assert.IsFalse(editor.ChangeItem(index, lastResource));
					}

					Assert.IsTrue(editor.ChangeItem(index, resource));
					resources[index] = resource;
				});

				helper.CheckUniqueSongsExactly(Constants.ListId, resources);
			}
		}

		[Test]
		public void ModifyPlaylistEditMoveItemTest() {
			var helper = new Helper();
			helper.CreatePlaylist(Constants.ListId, Constants.TestUid);

			const int count = 100;
			var resources = Constants.GenerateAudioResources(count);
			helper.EditPlaylist(Constants.ListId, editor => {
				editor.AddRange(resources);
			});

			var random = new Random(42);
			var indices = new int[count];
			for (var i = 0; i < count; ++i) {
				indices[i] = i;
			}
			Shuffle(random, indices);

			for (var i = 0; i < count; ++i) {
				var i1 = i;
				var index = indices[i1];
				helper.EditPlaylist(Constants.ListId, editor => {
					editor.MoveItem(i1, index);
					var resource = resources[i1];
					resources.RemoveAt(i1);
					resources.Insert(index, resource);
				});

				helper.CheckUniqueSongsExactly(Constants.ListId, resources);
			}
		}

		[Test]
		public void ModifyPlaylistsEditMoveItemTest() {
			var helper = new Helper();
			helper.CreatePlaylist(Constants.ListId, Constants.TestUid);
			helper.CreatePlaylist(Constants.AnotherListId, Constants.TestUid);

			const int count = 100;
			var resources = Constants.GenerateAudioResources(count);
			helper.EditPlaylist(Constants.ListId, editor => {
				editor.AddRange(resources);
			});

			helper.EditPlaylist(Constants.AnotherListId, editor => {
				editor.AddRange(resources);
			});

			var random = new Random(42);
			var indices = new int[count];
			for (var i = 0; i < count; ++i) {
				indices[i] = i;
			}
			Shuffle(random, indices);

			for (var i = 0; i < count; ++i) {
				var i1 = i;
				var index = indices[i1];
				helper.EditPlaylist(Constants.ListId, editor => {
					editor.MoveItem(i1, index);
					var resource = resources[i1];
					resources.RemoveAt(i1);
					resources.Insert(index, resource);
				});

				helper.CheckUniqueSongsExactly(Constants.ListId, resources);
			}
		}
	}
}
