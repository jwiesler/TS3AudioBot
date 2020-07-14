using NUnit.Framework;
using TS3ABotUnitTests.Mocks;
using TS3AudioBot.Playlists;

namespace TS3ABotUnitTests
{
	[TestFixture]
	public class PlaylistDatabaseTest {
		[Test]
		public void CreatePlaylistTest() {
			var io = new PlaylistIOMock();
			var database = new PlaylistDatabase(io);
			database.CreatePlaylist(Constants.ListId, Constants.TestUid);

			Assert.IsTrue(database.TryGet(Constants.ListId, out var list));
			Assert.AreSame(Constants.TestUid, list.Owner);

			Assert.IsNotNull(list);
			Assert.AreEqual(0, list.Count);
			Assert.IsEmpty(list.Items);
			Assert.IsEmpty(list.AdditionalEditors);
		}

		[Test]
		public void DifferentCasePlaylistTest() {
			var io = new PlaylistIOMock();
			var database = new PlaylistDatabase(io);
			database.CreatePlaylist(Constants.ListId, Constants.TestUid);

			Assert.IsTrue(database.TryGet(Constants.ListId, out var listA));
			Assert.IsTrue(database.TryGet(Constants.ListIdMixedCase, out var listB));
			Assert.AreSame(listA, listB);
		}
	}
}
