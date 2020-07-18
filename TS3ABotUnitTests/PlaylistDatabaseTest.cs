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
				var kvp = info.ContainingLists.First(kv => kv.Key == id);
				Assert.IsNotNull(kvp);
				Assert.AreEqual(index, kvp.Value);
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

			public void CheckIoChange(int expectedChange, Action action) {
				var countBefore = Io.ChangeCount;
				action();
				var newExpectedCount = countBefore + expectedChange;
				Assert.AreEqual(newExpectedCount, Io.ChangeCount);
			}

			public void ChangeItemAtDeep(
				string id, int index, AudioResource resource, int expectChange,
				PlaylistDatabase.ChangeItemReplacement replacement, bool shouldHandleDuplicates) {
				if(shouldHandleDuplicates)
					Assert.AreNotEqual(PlaylistDatabase.ChangeItemResult.Success, Database.ChangeItemAtDeep(id, index, resource, replacement));

				CheckIoChange(expectChange, () => {
					Assert.AreEqual(PlaylistDatabase.ChangeItemResult.Success, Database.ChangeItemAtDeep(id, index, resource, replacement, shouldHandleDuplicates));
				});
			}

			// Expects that the replacement item is at `index` in all lists
			public void ChangeItemAtDeep(string id, int index, AudioResource resource, int expectChange, PlaylistDatabase.ChangeItemReplacement replacement, bool shouldHandleDuplicates, params string[] containingLists) {
				ChangeItemAtDeep(id, index, resource, expectChange, replacement, shouldHandleDuplicates);

				foreach (var containingList in containingLists) {
					Assert.IsTrue(Database.TryGet(containingList, out _, out var list));
					Assert.AreEqual(resource, list[index]);
					Assert.AreSame(resource, list[index]);
				}
			}

			public void CheckUniqueSongsExactly(string id, params AudioResource[] resources) {
				CheckUniqueSongsExactly(id, (IEnumerable<AudioResource>)resources);
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
					Assert.AreEqual(resource, editor.RemoveItemAt(i1));
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
						Assert.IsFalse(editor.ChangeItemAt(index, lastResource));
					}

					Assert.IsTrue(editor.ChangeItemAt(index, resource));
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

		[Test]
		public void ChangeItemAtDeepTest() {
			var helper = new Helper();
			var resources = Constants.GenerateAudioResources(2);
			helper.CreatePlaylist(Constants.ListId, Constants.TestUid);
			helper.EditPlaylist(Constants.ListId, editor => {
				editor.Add(resources[0]);
			});
			
			helper.CreatePlaylist(Constants.AnotherListId, Constants.TestUid);
			helper.EditPlaylist(Constants.AnotherListId, editor => {
				editor.Add(resources[0]);
			});

			helper.ChangeItemAtDeep(Constants.AnotherListId, 0, resources[0], 0, PlaylistDatabase.ChangeItemReplacement.Database, false, Constants.ListId, Constants.AnotherListId);

			// Does not produce a duplicate (was not even contained in the database before)
			helper.ChangeItemAtDeep(Constants.AnotherListId, 0, resources[1], 2, PlaylistDatabase.ChangeItemReplacement.Database, false, Constants.ListId, Constants.AnotherListId);
			helper.CheckUniqueSongsExactly(Constants.ListId, resources[1]);
			helper.CheckUniqueSongsExactly(Constants.AnotherListId, resources[1]);

			helper.ChangeItemAtDeep(Constants.AnotherListId, 0, resources[0], 2, PlaylistDatabase.ChangeItemReplacement.Database, false, Constants.ListId, Constants.AnotherListId);
			helper.CheckUniqueSongsExactly(Constants.ListId, resources[0]);
			helper.CheckUniqueSongsExactly(Constants.AnotherListId, resources[0]);

			// Does not produce a duplicate (is equal but not reallyEqual to the item)
			var itemEqualButNotReally = resources[0].WithGain(20);

			helper.ChangeItemAtDeep(Constants.AnotherListId, 0, itemEqualButNotReally, 2, PlaylistDatabase.ChangeItemReplacement.Database, false, Constants.ListId, Constants.AnotherListId);
			helper.CheckUniqueSongsExactly(Constants.ListId, itemEqualButNotReally);
			helper.CheckUniqueSongsExactly(Constants.AnotherListId, itemEqualButNotReally);

			helper.ChangeItemAtDeep(Constants.AnotherListId, 0, resources[0], 2, PlaylistDatabase.ChangeItemReplacement.Database, false, Constants.ListId, Constants.AnotherListId);
			helper.CheckUniqueSongsExactly(Constants.ListId, resources[0]);
			helper.CheckUniqueSongsExactly(Constants.AnotherListId, resources[0]);
		}

		[Test]
		public void ChangeItemAtDeepTestDuplicate() {
			{
				var helper = new Helper();
				var resources = Constants.GenerateAudioResources(2);
				helper.CreatePlaylist(Constants.ListId, Constants.TestUid);
				helper.EditPlaylist(Constants.ListId, editor => {
					editor.Add(resources[0]);
					editor.Add(resources[1]);
				});

				helper.CreatePlaylist(Constants.AnotherListId, Constants.TestUid);
				helper.EditPlaylist(Constants.AnotherListId, editor => { editor.Add(resources[0]); });

				// Change 0 to item at 1 => playlist 2 replaced and playlist 1 item 0 deleted
				helper.ChangeItemAtDeep(Constants.AnotherListId, 0, resources[1], 2, PlaylistDatabase.ChangeItemReplacement.Database, true, Constants.ListId,
					Constants.AnotherListId);
				helper.Database.TryGet(Constants.ListId, out _, out var list);
				helper.CheckUniqueSongsExactly(Constants.ListId, resources[1]);
				helper.CheckUniqueSongsExactly(Constants.AnotherListId, resources[1]);
				Assert.AreEqual(1, list.Count);
			}

			{
				var helper = new Helper();
				var resources = Constants.GenerateAudioResources(2);
				helper.CreatePlaylist(Constants.ListId, Constants.TestUid);
				helper.EditPlaylist(Constants.ListId, editor => {
					editor.Add(resources[0]);
					editor.Add(resources[1]);
				});

				helper.CreatePlaylist(Constants.AnotherListId, Constants.TestUid);
				helper.EditPlaylist(Constants.AnotherListId, editor => { editor.Add(resources[0]); });

				var itemEqualButNotReally = resources[1].WithGain(20);

				// Change 0 to item at 1 => playlist 2 replaced and playlist 1 item 0 deleted AND both items are really equal
				helper.ChangeItemAtDeep(Constants.AnotherListId, 0, itemEqualButNotReally, 2, PlaylistDatabase.ChangeItemReplacement.Input, true, Constants.ListId,
					Constants.AnotherListId);
				helper.Database.TryGet(Constants.ListId, out _, out var list);
				helper.CheckUniqueSongsExactly(Constants.ListId, itemEqualButNotReally);
				helper.CheckUniqueSongsExactly(Constants.AnotherListId, itemEqualButNotReally);
				Assert.AreEqual(1, list.Count);
			}

			{
				var helper = new Helper();
				var resources = Constants.GenerateAudioResources(2);
				helper.CreatePlaylist(Constants.ListId, Constants.TestUid);
				helper.EditPlaylist(Constants.ListId, editor => {
					editor.Add(resources[0]);
					editor.Add(resources[1]);
				});

				helper.CreatePlaylist(Constants.AnotherListId, Constants.TestUid);
				helper.EditPlaylist(Constants.AnotherListId, editor => {
					editor.Add(resources[0]);
				});

				var itemEqualButNotReally = resources[0].WithGain(20);
				helper.ChangeItemAtDeep(Constants.ListId, 1, itemEqualButNotReally, 2, PlaylistDatabase.ChangeItemReplacement.Input, true);

				// Check that the unrelated playlist is safed as well
				helper.Database.TryGet(Constants.ListId, out _, out var list);
				Assert.AreSame(itemEqualButNotReally, list[0]);
				helper.Database.TryGet(Constants.AnotherListId, out _, out var anotherList);
				Assert.AreSame(itemEqualButNotReally, anotherList[0]);

				helper.CheckUniqueSongsExactly(Constants.ListId, resources[0]);
				helper.CheckUniqueSongsExactly(Constants.AnotherListId, resources[0]);
			}
		}
	}
}
