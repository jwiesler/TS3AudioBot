using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TS3ABotUnitTests.Mocks;
using TS3AudioBot.Audio;
using TS3AudioBot.Audio.Preparation;
using TS3AudioBot.Config;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;
using TSLib;

namespace TS3ABotUnitTests {
	[TestFixture]
	public class StartSongTaskTest {
		[Test]
		public void ShouldCreateTaskTest() {
			var handler = new NextSongHandler();

			var queueItem1 = new QueueItem(Constants.Resource1AC, new MetaData(Constants.TestUid));
			var queueItem2 = new QueueItem(Constants.Resource1AC, new MetaData(Constants.TestUid));
			Assert.IsTrue(handler.IsPreparingCurrentSong(queueItem1));
			Assert.IsFalse(handler.IsPreparingNextSong(queueItem1));
			Assert.IsFalse(NextSongHandler.ShouldBeReplaced(queueItem1, queueItem1)); // Same item

			handler.NextSongPreparing = queueItem1;
			Assert.IsFalse(handler.IsPreparingCurrentSong(queueItem1));
			Assert.IsTrue(handler.IsPreparingNextSong(queueItem1));
			Assert.IsFalse(handler.ShouldBeReplacedNext(queueItem1,
				queueItem1)); // Preparing next song and same QueueItem => no new task
			Assert.IsTrue(handler.ShouldBeReplacedNext(queueItem1,
				queueItem2)); // Preparing next song and new QueueItem => new task
		}

		public class InformingEventWaitHandle : EventWaitHandle {
			public EventWaitHandle OutputHandle { get; }

			public InformingEventWaitHandle(bool initialState, EventResetMode mode) : base(initialState, mode) {
				OutputHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
			}

			public override bool WaitOne() {
				OutputHandle.Set();
				return base.WaitOne();
			}

			public override bool WaitOne(int millisecondsTimeout) {
				OutputHandle.Set();
				return base.WaitOne(millisecondsTimeout);
			}
		}

		private static void TestSongAnalyzerExpectErrorMessage(QueueItem queueItem, ILoaderContext loaderContext, string message) {
			var volumeDetector = new VolumeDetectorMock();

			var task = new SongAnalyzerTask(queueItem, loaderContext, volumeDetector);

			var t = Task.Run(() => task.Run(CancellationToken.None));
			var res = t.Result;
			Assert.IsFalse(res.Ok);
			Assert.AreSame(res.Error.Str, message);
		}

		private static SongAnalyzerResult TestSongAnalyzerExpectOk(QueueItem queueItem) {
			var loaderContext = new LoaderContextMock();
			var volumeDetector = new VolumeDetectorMock();
			
			var task = new SongAnalyzerTask(queueItem, loaderContext, volumeDetector);

			var t = Task.Run(() => task.Run(CancellationToken.None));
			var res = t.Result;
			Assert.IsTrue(res.Ok);
			return res.Value;
		}

		[Test]
		public void RunSongAnalyzerTaskTest() {
			var queueItem = new QueueItem(Constants.Resource1AYoutube, new MetaData(null));
			var queueItemGain = new QueueItem(Constants.Resource1AYoutubeGain, new MetaData(null));

			{
				// Item without gain gets the gain set
				var res = TestSongAnalyzerExpectOk(queueItem);
				var resource = res.Resource.BaseData;
				var gain = resource.Gain;
				Assert.IsTrue(gain.HasValue);
				Assert.AreEqual(gain.Value, VolumeDetectorMock.VolumeSet);
			}

			{
				// Item with gain set should not be changed
				var res = TestSongAnalyzerExpectOk(queueItemGain);
				var resource = res.Resource.BaseData;
				Assert.AreSame(queueItemGain.AudioResource, resource);
			}

			{
				// Failing load gets propagated
				var loaderContext = new LoaderContextMock {ShouldFailLoad = true};
				TestSongAnalyzerExpectErrorMessage(queueItem, loaderContext, LoaderContextMock.LoadFailedMessage);
			}

			{
				// No restored link gets propagated
				var loaderContext = new LoaderContextMock {ShouldReturnNoRestoredLink = true};
				TestSongAnalyzerExpectErrorMessage(queueItem, loaderContext, LoaderContextMock.NoRestoredLinkMessage);
			}

			{
				// Cancelling throws cancelled exception of volume detector
				var loaderContext = new LoaderContextMock();
				var volumeDetector = new VolumeDetectorMock();

				var task = new SongAnalyzerTask(queueItem, loaderContext, volumeDetector);

				Assert.Throws<TaskCanceledException>(() => task.Run(new CancellationToken(true)));
			}
		}

		private static E<LocalStr> RunStartSongTaskExpectWaited(StartSongTask task, CancellationToken token) {
			var waitHandle = new InformingEventWaitHandle(false, EventResetMode.AutoReset);
			
			var t = Task.Run(() => task.RunInternal(waitHandle, token));
			waitHandle.OutputHandle.WaitOne();
			waitHandle.Set();
			return t.Result;
		}

		private static TInner AssertThrowsInnerException<TOuter, TInner>(TestDelegate code) where TOuter : Exception where TInner : Exception {
			var ex = Assert.Throws<TOuter>(code);
			Assert.IsNotNull(ex.InnerException);
			Assert.IsInstanceOf<TInner>(ex.InnerException);
			return (TInner) ex.InnerException;
		}

		[Test]
		public void RunStartSongTaskTest() {
			var lck = new object();
			var queueItem = new QueueItem(Constants.Resource1AYoutube, new MetaData(Constants.TestUid, Constants.ListId));
			var queueItemGain = new QueueItem(Constants.Resource1AYoutubeGain, new MetaData(Constants.TestUid, Constants.ListId));
			var playResource = new PlayResource(queueItem.AudioResource.ResourceId, queueItem.AudioResource, queueItem.MetaData);
			var playResourceGain = new PlayResource(queueItemGain.AudioResource.ResourceId, queueItemGain.AudioResource, queueItemGain.MetaData);

			{
				// Queue item without gain gets it set and update gets invoked
				var loaderContext = new LoaderContextMock();
				var player = new PlayerMock();
				
				var task = new StartSongTask(loaderContext, player, Constants.VolumeConfig, lck, queueItem);

				AudioResource changedResource = null;
				QueueItem containingQueueItem = null;
				task.OnAudioResourceUpdated += (sender, args) => {
					changedResource = args.Resource;
					containingQueueItem = args.QueueItem;
				};

				var waitHandle = new InformingEventWaitHandle(false, EventResetMode.AutoReset);
				var tokenSource = new CancellationTokenSource();
				var t = Task.Run(() => task.RunInternal(waitHandle, tokenSource.Token));

				// Wait that the task reached the first point, cancel
				waitHandle.OutputHandle.WaitOne();
				tokenSource.Cancel();
				waitHandle.Set();

				// Check that it actually failed
				AssertThrowsInnerException<AggregateException, TaskCanceledException>(() => {
					var _ = t.Result;
				});

				Assert.NotNull(changedResource);
				Assert.NotNull(containingQueueItem);
				Assert.AreSame(containingQueueItem, queueItem);
				
				var gain = changedResource.Gain;
				Assert.IsTrue(gain.HasValue);
				Assert.AreEqual(gain.Value, VolumeDetectorMock.VolumeSet);
			}

			{
				var loaderContext = new LoaderContextMock();
				var player = new PlayerMock();
				
				var task = new StartSongTask(loaderContext, player, Constants.VolumeConfig, lck, queueItem);

				var waitHandle = new InformingEventWaitHandle(false, EventResetMode.AutoReset);
				var tokenSource = new CancellationTokenSource();
				var t = Task.Run(() => task.RunInternal(waitHandle, tokenSource.Token));

				// Wait that the task reached the first point, cancel
				waitHandle.OutputHandle.WaitOne();
				lock (lck) {
					waitHandle.Set();
					Task.Delay(100);
					tokenSource.Cancel();
				}

				AssertThrowsInnerException<AggregateException, TaskCanceledException>(() => {
					var _ = t.Result;
				});
			}

			{
				var player = new PlayerMock();
				
				var task = new StartSongTask(null, player, Constants.VolumeConfig, null, null);
				PlayInfoEventArgs argsBefore = null;
				PlayInfoEventArgs argsAfter = null;
				
				task.BeforeResourceStarted += (sender, args) => {
					Assert.IsNotNull(args);
					Assert.AreEqual(args.Invoker, Constants.TestUid);
					Assert.AreSame(args.MetaData, queueItemGain.MetaData);
					Assert.AreSame(args.ResourceData, queueItemGain.AudioResource);
					Assert.AreSame(args.SourceLink, LoaderContextMock.RestoredLink);
					argsBefore = args;
				};

				task.AfterResourceStarted += (sender, args) => {
					Assert.IsNotNull(args);
					Assert.AreEqual(args.Invoker, Constants.TestUid);
					Assert.AreSame(args.MetaData, queueItemGain.MetaData);
					Assert.AreSame(args.ResourceData, queueItemGain.AudioResource);
					Assert.AreSame(args.SourceLink, LoaderContextMock.RestoredLink);
					argsAfter = args;
				};
				
				var t = Task.Run(() => task.StartResource(new SongAnalyzerResult {Resource = playResourceGain, RestoredLink = LoaderContextMock.RestoredLink}));

				var res = t.Result;

				Assert.IsTrue(res.Ok);
				Assert.AreSame(argsBefore, argsAfter);

				Assert.NotNull(player.PlayArgs.res);
				Assert.NotNull(player.PlayArgs.gain);
				Assert.AreSame(player.PlayArgs.res, playResourceGain);
				Assert.AreEqual(player.PlayArgs.gain, queueItemGain.AudioResource.Gain);
				Assert.AreEqual(player.Volume, 10.0f);
			}

			{
				var player = new PlayerMock();
				
				var task = new StartSongTask(null, player, Constants.VolumeConfig, null, null);
				var t = Task.Run(() => task.StartResource(new SongAnalyzerResult {Resource = playResource, RestoredLink = LoaderContextMock.RestoredLink}));

				var res = t.Result;

				Assert.IsTrue(res.Ok);

				Assert.NotNull(player.PlayArgs.res);
				Assert.NotNull(player.PlayArgs.gain);
				Assert.AreSame(player.PlayArgs.res, playResource);
				Assert.AreEqual(player.PlayArgs.gain, 0);
				Assert.AreEqual(player.Volume, 10.0f);
			}
		}
	}
}
