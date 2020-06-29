using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using TS3AudioBot.Audio;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;

namespace TS3ABotUnitTests {
	[TestFixture]
	public class PlayManagerTest {
		public static PlayManager CreateDefaultPlayManager() {
			var player = new Player();
			var loader = new LoaderContext();
			var playlistManager = new PlaylistManager(null);
			return new PlayManager(Values.VolumeConfig, player, loader, playlistManager);
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
			var manager = CreateDefaultPlayManager();
			manager.AutoStartPlaying = false;

			var queueItem = new QueueItem(Values.Resource1AYoutubeGain, new MetaData(Values.TestUid, Values.ListId));
			var queueItem2 = new QueueItem(Values.Resource2BYoutubeGain, new MetaData(Values.TestUid, Values.ListId));
			Assert.IsTrue(manager.Enqueue(queueItem).Ok);
			Assert.IsTrue(manager.Enqueue(queueItem2).Ok);
			Assert.AreEqual(manager.Queue.Items.Count, 2);
			Assert.AreSame(manager.Queue.TryGetItem(0), queueItem);
			Assert.AreSame(manager.Queue.TryGetItem(1), queueItem2);

			Assert.IsFalse(manager.IsPlaying);
		}

		[Test]
		public void ClearTest() {
			var manager = CreateDefaultPlayManager();

			var queueItem = new QueueItem(Values.Resource1AYoutubeGain, new MetaData(Values.TestUid, Values.ListId));
			var queueItem2 = new QueueItem(Values.Resource2BYoutubeGain, new MetaData(Values.TestUid, Values.ListId));
			Assert.IsTrue(manager.Enqueue(queueItem).Ok);
			Assert.IsTrue(manager.Enqueue(queueItem2).Ok);
			manager.Clear();
			Assert.IsTrue(manager.Queue.Items.Count == 0);
			Assert.IsFalse(manager.IsPlaying);
		}

		public class WaitUntilEventFired<TEventArgs> where TEventArgs : EventArgs  {
			public EventWaitHandle WaitHandle { get; } = new EventWaitHandle(false, EventResetMode.AutoReset);

			public void SetHandle(object sender, TEventArgs e) { WaitHandle.Set(); }

			public void WaitForHandle(object sender, TEventArgs e) { WaitHandle.WaitOne(); }
		}

		public class MarkOnEventFired<TEventArgs> where TEventArgs : EventArgs {
			public bool WasHit { get; private set; }

			public void Set(object sender, TEventArgs e) {
				WasHit = true;
			}

			public bool WasHitReset() {
				var value = WasHit;
				WasHit = false;
				return value;
			}
		}

		public static void AssertIsLoaded(PlayResource res, int gain, QueueItem item, int loaderIndex) {
			Assert.AreSame(res.BaseData, item.AudioResource);
			Assert.AreSame(res.Meta, item.MetaData);
			Assert.AreEqual(gain, item.AudioResource.Gain);
			Assert.AreEqual(res.PlayUri, LoaderContext.MakeResourceURI(item.AudioResource.ResourceId, loaderIndex));
		}

		public static void AssertIsPlaying(PlayInfoEventArgs args, QueueItem item, int loaderIndex) {
			Assert.AreSame(args.PlayResource.BaseData, item.AudioResource);
			Assert.AreSame(args.PlayResource.Meta, item.MetaData);
			Assert.AreEqual(args.Invoker, item.MetaData.ResourceOwnerUid);
			Assert.AreSame(args.ResourceData, item.AudioResource);
			Assert.AreEqual(args.PlayResource.PlayUri, LoaderContext.MakeResourceURI(item.AudioResource.ResourceId, loaderIndex));
		}

		class PlayTestHelper {
			public Player Player { get; } = new Player();
			public LoaderContext Loader { get; } = new LoaderContext();
			public PlaylistManager PlaylistManager { get; }

			public PlayManager Manager { get; }

			public WaitUntilEventFired<PlayInfoEventArgs> AfterStartedEventWaiter { get; } = new WaitUntilEventFired<PlayInfoEventArgs>();
			public WaitUntilEventFired<PlayInfoEventArgs> BeforeStartedEventWaiter { get; } = new WaitUntilEventFired<PlayInfoEventArgs>();

			public MarkOnEventFired<PlayInfoEventArgs> BeforeStartedFired { get; } = new MarkOnEventFired<PlayInfoEventArgs>();
			public MarkOnEventFired<PlayInfoEventArgs> AfterStartedFired { get; } = new MarkOnEventFired<PlayInfoEventArgs>();

			public MarkOnEventFired<SongEndEventArgs> ResourceStoppedFired { get; } = new MarkOnEventFired<SongEndEventArgs>();

			public PlayTestHelper() {
				PlaylistManager = new PlaylistManager(null);
				Manager = new PlayManager(Values.VolumeConfig, Player, Loader, PlaylistManager);

				Manager.BeforeResourceStarted += BeforeStartedEventWaiter.WaitForHandle;
				Manager.AfterResourceStarted += AfterStartedEventWaiter.SetHandle;

				Manager.BeforeResourceStarted += BeforeStartedFired.Set;
				Manager.AfterResourceStarted += AfterStartedFired.Set;

				Manager.ResourceStopped += ResourceStoppedFired.Set;
			}

			public void CheckBeforePlayNextSong(int queueIndex) {
				Assert.IsFalse(BeforeStartedFired.WasHit);
				Assert.IsFalse(AfterStartedFired.WasHit);

				Assert.AreEqual(Manager.Queue.Index, queueIndex);

				Assert.IsFalse(ResourceStoppedFired.WasHit);
			}

			public void AllowPlayNextSong() {
				BeforeStartedEventWaiter.WaitHandle.Set();
				AfterStartedEventWaiter.WaitHandle.WaitOne();
			}

			public void CheckAfterLoadFailure(int loaderIndex) {
				Assert.AreEqual(Loader.LoadedResources - 1, loaderIndex);
			}

			public void CheckAfterPlayStarted(int loaderIndex, QueueItem queueItem, int queueIndex) {
				var (res, gain) = Player.PlayArgs;
				AssertIsLoaded(res, gain, queueItem, loaderIndex);
				Assert.IsTrue(Manager.IsPlaying);
				AssertIsPlaying(Manager.CurrentPlayData, queueItem, loaderIndex);

				Assert.IsTrue(BeforeStartedFired.WasHitReset());
				Assert.IsTrue(AfterStartedFired.WasHitReset());

				Assert.AreEqual(Manager.Queue.Index, queueIndex);
			}

			public void InvokeSongEnd() {
				Assert.IsFalse(ResourceStoppedFired.WasHit);
				Player.InvokeOnSongEnd();
				Assert.IsTrue(ResourceStoppedFired.WasHitReset());
			}

			public void Enqueue(QueueItem item) {
				var index = Manager.Queue.Items.Count;
				Assert.IsTrue(Manager.Enqueue(item).Ok);
				Assert.AreSame(Manager.Queue.TryGetItem(index), item);
			}

			public void AssertAtEndOfQueue() {
				Assert.AreEqual(Manager.Queue.Index, Manager.Queue.Items.Count);
			}
		}

		public const int QueueItemGain = 10;

		public static QueueItem QueueItemWithIndex(int index, MetaData meta) {
			return new QueueItem(new AudioResource("id_" + index, "Title " + index, "youtube", null, null, QueueItemGain), meta);
		}

		public static QueueItem[] GenerateQueueItems(int count) {
			var queueItems = new QueueItem[count];

			var meta = new MetaData(Values.TestUid, Values.ListId);
			for (var i = 0; i < count; ++i) {
				var item = QueueItemWithIndex(i, meta);
				queueItems[i] = item;
			}

			return queueItems;
		}

		[Test]
		public void SimplePlayTest() {
			var helper = new PlayTestHelper();

			var meta = new MetaData(Values.TestUid, Values.ListId);
			var queueItem = new QueueItem(Values.Resource1AYoutubeGain, meta);

			helper.Enqueue(queueItem);

			helper.CheckBeforePlayNextSong(0);
			helper.AllowPlayNextSong();
			helper.CheckAfterPlayStarted(0, queueItem, 0);
			helper.InvokeSongEnd();
			helper.AssertAtEndOfQueue();
			Assert.IsFalse(helper.Manager.IsPlaying);
		}

		[Test]
		public void PlayTestMultipleSongs() {
			var helper = new PlayTestHelper();

			const int iterations = 50;

			var queueItems = GenerateQueueItems(iterations);

			foreach(var item in queueItems)
				helper.Enqueue(item);

			for (var i = 0; i < iterations; ++i) {
				var queueItem = queueItems[i];
				helper.CheckBeforePlayNextSong(i);
				helper.AllowPlayNextSong();
				helper.CheckAfterPlayStarted(i, queueItem, i);
				helper.InvokeSongEnd();
			}
		}

		[Test]
		public void PlayTestMultipleSongsWithFailures() {
			var helper = new PlayTestHelper();

			const int iterations = 50;

			var queueItems = GenerateQueueItems(iterations);

			foreach(var item in queueItems)
				helper.Enqueue(item);

			bool ShouldFail(int i) => i > 0 && ((i % 5) == 0 || (i % 3) == 0);

			// Let a few songs fail
			for (var i = 0; i < iterations; ++i) {
				var queueItem = queueItems[i];

				if (ShouldFail(i))
					continue;
				helper.CheckBeforePlayNextSong(i);
				helper.AllowPlayNextSong();
				helper.CheckAfterPlayStarted(i, queueItem, i);
				helper.InvokeSongEnd();
			}
		}
	}
}
