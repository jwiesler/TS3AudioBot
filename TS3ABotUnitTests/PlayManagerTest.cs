using System.Collections.Generic;
using NUnit.Framework;
using TS3ABotUnitTests.Mocks;
using TS3AudioBot.Audio;
using TS3AudioBot.Playlists;

namespace TS3ABotUnitTests {
	[TestFixture]
	public class PlayManagerTest {
		private const int Iterations = 50;

		public static readonly List<QueueItem> QueueItems = Constants.GenerateQueueItems(Iterations);

		public static PlayManager CreateDefaultPlayManager() {
			var player = new PlayerMock();
			var playlistManager = new PlaylistManager(new PlaylistDatabase(new PlaylistIOMock()), null);
			var taskHost = new StartSongTaskHostMock();
			return new PlayManager(player, playlistManager, taskHost);
		}

		[Test]
		public void BasicTest() {
			var manager = CreateDefaultPlayManager();

			Assert.IsFalse(manager.IsPlaying);
			Assert.IsNull(manager.NextSongShadow);

			manager.Play();
			Assert.IsFalse(manager.IsPlaying);

			Assert.IsFalse(manager.Next().Ok);
			Assert.IsFalse(manager.Next(10).Ok);
			Assert.IsFalse(manager.Previous().Ok);
		}

		[Test]
		public void EnqueueTest() {
			var helper = new PlayTestHelper();
			helper.Manager.AutoStartPlaying = false;
			helper.Enqueue(QueueItems[0]);
			helper.Enqueue(QueueItems[1]);

			Assert.IsFalse(helper.Manager.IsPlaying);
		}

		[Test]
		public void ClearTest() {
			{
				var helper = new PlayTestHelper();

				helper.Enqueue(QueueItems[0]);
				helper.Clear();
			}

			{
				var helper = new PlayTestHelper();
				foreach (var item in QueueItems)
					helper.Enqueue(item);

				helper.Play(QueueItems[0], 0);
				helper.InvokeSongEnd();
				helper.Clear();
			}

			{
				var helper = new PlayTestHelper();
				foreach (var item in QueueItems)
					helper.Enqueue(item);

				helper.Play(QueueItems[0], 0);
				helper.Clear();
			}

			{
				var helper = new PlayTestHelper();
				helper.Enqueue(QueueItems[0]);
				helper.Manager.NextSongShadow = QueueItems[1];
				helper.Play(QueueItems[0], 0);

				helper.Clear();
			}
		}

		[Test]
		public void SimplePlayTest() {
			var helper = new PlayTestHelper();

			var queueItem = QueueItems[0];

			helper.Enqueue(queueItem);
			helper.CheckIsPreparing(queueItem);
			helper.Play(queueItem, 0);
			helper.InvokeSongEnd();

			helper.AssertAtEndOfPlay();
			helper.CheckTasksCanceled(0);
		}

		[Test]
		public void PlayTestMultipleSongs() {
			var helper = new PlayTestHelper();

			foreach(var item in QueueItems)
				helper.Enqueue(item);

			for (var i = 0; i < Iterations; ++i) {
				var queueItem = QueueItems[i];
				helper.Play(queueItem, i);
				helper.InvokeSongEnd();
			}

			helper.AssertAtEndOfPlay();
			helper.CheckTasksCanceled(0);
		}

		private static bool ShouldFailSong(int i) => i > 0 && ((i % 5) == 0 || (i % 3) == 0);
		
		[Test]
		public void PlayTestMultipleSongsWithFailures() {
			var helper = new PlayTestHelper();

			foreach(var item in QueueItems)
				helper.Enqueue(item);

			// Let a few songs fail
			for (var i = 0; i < Iterations; ++i) {
				var queueItem = QueueItems[i];

				if (ShouldFailSong(i)) {
					helper.FailLoad(queueItem);
					continue;
				}

				helper.Play(queueItem, i);
				helper.InvokeSongEnd();
			}

			helper.AssertAtEndOfPlay();
			helper.CheckTasksCanceled(0);
		}

		[Test]
		public void PlayTestMultipleSongsWithFailuresLoadBeforeSongEnd() {
			var helper = new PlayTestHelper();

			foreach(var item in QueueItems)
				helper.Enqueue(item);

			if (ShouldFailSong(0))
				helper.FailLoad(QueueItems[0]);

			// Let a few songs fail
			// current song is already prepared
			for (var i = 0; i < Iterations; ++i) {
				var queueItem = QueueItems[i];

				if (ShouldFailSong(i))
					continue;

				helper.Play(queueItem, i);

				for (var j = i + 1; j < Iterations; ++j) {
					if (ShouldFailSong(j))
						helper.FailLoad(QueueItems[j]);
					else
						break;
				}
				
				helper.InvokeSongEnd();
			}

			helper.AssertAtEndOfPlay();
			helper.CheckTasksCanceled(0);
		}

		[Test]
		public void PlayTestWithShadows() {
			var helper = new PlayTestHelper();

			// queue with one item, add shadow
			helper.Enqueue(QueueItems[0]);
			helper.Manager.NextSongShadow = QueueItems[1];
			helper.Play(QueueItems[0], 0);

			// first song is playing, check that the shadow is preparing
			helper.CheckIsPreparing(QueueItems[1]);
			helper.Enqueue(QueueItems[2]);

			// a new song is enqueued, check that it is preparing
			helper.CheckIsPreparing(QueueItems[2]);
			helper.CheckTasksCanceled(1);

			// next song index is valid
			helper.CheckNextSongIndex(1);

			helper.InvokeSongEnd();
			helper.Play(QueueItems[2], 1);

			// shadow is kept
			helper.CheckIsPreparing(QueueItems[1]);

			// next song index is still valid
			helper.CheckNextSongIndex(2);

			helper.InvokeSongEnd();
			helper.AssertAtEndOfQueue();

			// Add the shadow song when not playing and at end of queue
			helper.Enqueue(QueueItems[1]);
			helper.Play(QueueItems[1], 2);

			// shadow gets cleared after play
			Assert.AreSame(helper.Manager.NextSongShadow, null);
			helper.CheckIsPreparing(null);
			helper.CheckNextSongIndex(3);

			helper.InvokeSongEnd();

			helper.AssertAtEndOfPlay();
			helper.CheckTasksCanceled(1);
		}

		[Test]
		public void PlayTestWithShadowsPlaybackStopped() {
			var helper = new PlayTestHelper();
			helper.Manager.PlaybackStopped += (sender, args) => {
				if (args.NextShadow == null)
					return;
				args.Item = args.NextShadow;
				args.NextShadow = null;
			};

			// queue with one item, add shadow
			helper.Enqueue(QueueItems[0]);
			helper.Manager.NextSongShadow = QueueItems[1];
			helper.Play(QueueItems[0], 0);

			// first song is playing, check that the shadow is preparing
			helper.CheckIsPreparing(QueueItems[1]);
			helper.InvokeSongEnd();

			// item gets filled in by event handler, is added to queue and plays now
			helper.Play(QueueItems[1], 1);
			helper.InvokeSongEnd();

			helper.AssertAtEndOfPlay();
			helper.CheckTasksCanceled(0);
		}
	}
}
