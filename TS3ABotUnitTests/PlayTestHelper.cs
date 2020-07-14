using NUnit.Framework;
using TS3ABotUnitTests.Mocks;
using TS3AudioBot.Audio;
using TS3AudioBot.Playlists;

namespace TS3ABotUnitTests
{
	class PlayTestHelper {
		public PlayerMock Player { get; } = new PlayerMock();
		public StartSongTaskHostMock TaskHost { get; } = new StartSongTaskHostMock();
		public PlaylistManager PlaylistManager { get; }
		
		public PlayManager Manager { get; }

		public PlayTestHelper() {
			PlaylistManager = new PlaylistManager(new PlaylistIOMock(), null);
			Manager = new PlayManager(Player,  PlaylistManager, TaskHost);
		}

		public void CheckIsPreparing(QueueItem item) { Assert.AreSame(item, TaskHost.PreparingItem); }

		public static void CheckPlayInfoEventArgs(PlayInfoEventArgs args, QueueItem item) {
			Assert.AreSame(item.AudioResource, args.PlayResource.BaseData);
			Assert.AreSame(item.MetaData, args.PlayResource.Meta);
			Assert.AreEqual(item.MetaData.ResourceOwnerUid, args.Invoker);
			Assert.AreSame(args.ResourceData, item.AudioResource);
		}

		private void CheckQueueIndex(int queueIndex) {
			Assert.AreEqual(queueIndex, Manager.Queue.Index, "Queue index");
		}

		public void CheckNextSongIndex(int queueIndex) {
			Assert.AreEqual(Manager.NextSongIndex, queueIndex, "Next song index");
		}

		private void CheckBeforePlayNextSong(QueueItem nextItem, int? queueIndex) {
			if (queueIndex.HasValue) {
				CheckQueueIndex(queueIndex.Value);
				CheckNextSongIndex(queueIndex.Value);
			}

			Assert.AreEqual(nextItem, Manager.NextSong, "Next item");
			CheckIsPreparing(nextItem);
		}

		private void CheckAfterPlayStarted(QueueItem queueItem, int? queueIndex) {
			if (queueIndex.HasValue)
				CheckQueueIndex(queueIndex.Value);
			CheckPlayInfoEventArgs(Manager.CurrentPlayData, queueItem);
		}

		public void FailLoad(QueueItem item) {
			CheckIsPreparing(item);
			TaskHost.FailLoad();
		}

		public void Play(QueueItem item, int? queueIndex) {
			Assert.IsTrue(TaskHost.GetPlayRequestedReset(), "Play requested");
			CheckBeforePlayNextSong(item, queueIndex);
			TaskHost.Play();
			Assert.IsTrue(Manager.IsPlaying, "Is playing");
			CheckAfterPlayStarted(item, queueIndex);
		}

		public void InvokeSongEnd() {
			Player.InvokeOnSongEnd();
		}

		public void Enqueue(QueueItem item) {
			var index = Manager.Queue.Items.Count;
			Assert.IsTrue(Manager.Enqueue(item).Ok, "Enqueue succeeds");
			Assert.AreSame(item, Manager.Queue.TryGetItem(index), "Added item");
		}

		public void Clear() {
			var hasTask = TaskHost.HasTask;
			var canceledBefore = TaskHost.CanceledTasks;

			Manager.Clear();

			Assert.AreEqual(0, Manager.NextSongIndex);
			AssertAtEndOfPlay();
			
			if (hasTask)
				++canceledBefore;
			CheckTasksCanceled(canceledBefore);
		}

		public void CheckTasksCanceled(int canceled) {
			Assert.AreEqual(canceled, TaskHost.CanceledTasks);
		}

		public void AssertAtEndOfQueue() {
			Assert.AreEqual(Manager.Queue.Items.Count, Manager.Queue.Index);
		}

		public void AssertAtEndOfPlay() {
			AssertAtEndOfQueue();
			Assert.IsFalse(Manager.IsPlaying);
			Assert.IsNull(Manager.NextSongShadow);
			Assert.IsNull(Manager.NextSong);
			Assert.IsNull(Manager.CurrentPlayData);
			
			Assert.IsFalse(TaskHost.HasTask);
		}
	}
}
