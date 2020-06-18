using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.DataCollection;
using NUnit.Framework;
using TS3AudioBot.Audio;
using TS3AudioBot.Config;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;
using TSLib;

namespace TS3ABotUnitTests {
	[TestFixture]
	public class StartSongTaskTest {
		private static readonly AudioResource Resource1AC = new AudioResource("1", "A", "C");
		private static readonly AudioResource Resource1AYoutube = new AudioResource("1", "A", "youtube");
		private static readonly AudioResource Resource1AYoutubeGain = Resource1AYoutube.WithGain(5);
		private static readonly Uid TestUid = Uid.To("Test");

		[Test]
		public void ShouldCreateTaskTest() {
			var handler = new NextSongHandler();

			var queueItem1 = new QueueItem(Resource1AC, new MetaData(TestUid));
			var queueItem2 = new QueueItem(Resource1AC, new MetaData(TestUid));
			Assert.IsTrue(handler.IsPreparingCurrentSong(queueItem1));
			Assert.IsFalse(handler.ShouldCreateNewTask(queueItem1, queueItem1)); // Same item
			Assert.IsFalse(handler.ShouldCreateNewTask(queueItem1,
				queueItem2)); // Preparing current song => no new task

			handler.NextSongToPrepare = queueItem1;
			Assert.IsFalse(handler.IsPreparingCurrentSong(queueItem1));
			Assert.IsFalse(handler.ShouldCreateNewTask(queueItem1,
				queueItem1)); // Preparing next song and same QueueItem => no new task
			Assert.IsTrue(handler.ShouldCreateNewTask(queueItem1,
				queueItem2)); // Preparing next song and new QueueItem => new task
		}

		private class LoaderContext : ILoaderContext {
			public bool ShouldReturnNoRestoredLink { get; set; }
			public bool ShouldFailLoad { get; set; }

			public const string NoRestoredLinkMessage = "NoRestoredLinkMessage";
			public const string LoadFailedMessage = "LoadFailedMessage";
			public const string RestoredLink = "Restored link";

			public R<string, LocalStr> RestoreLink(AudioResource res) {
				if (ShouldReturnNoRestoredLink)
					return new LocalStr(NoRestoredLinkMessage);
				return RestoredLink;
			}

			public R<PlayResource, LocalStr> Load(AudioResource resource) {
				if (ShouldFailLoad)
					return new LocalStr(LoadFailedMessage);
				return new PlayResource(resource.ResourceId, resource);
			}
		}

		private class VolumeDetector : IVolumeDetector {
			public const int VolumeSet = 10;

			public int RunVolumeDetection(string url, CancellationToken token) {
				if (token.IsCancellationRequested)
					throw new TaskCanceledException();

				return VolumeSet;
			}
		}

		private class Player : VolumeDetector, IPlayer {
			public float Volume { get; set; } = 70;
			public TimeSpan Length { get; set; } = TimeSpan.Zero;
			public TimeSpan Position { get; set; } = TimeSpan.Zero;

			public bool ShouldFailPlay { get; set; }

			public bool StopCalled { get; set; }
			public (PlayResource res, int gain) PlayArgs { get; set; }

			public E<string> Play(PlayResource res, int gain) {
				PlayArgs = (res, gain);
				if (ShouldFailPlay)
					return "";
				return R.Ok;
			}

			public void Stop() { StopCalled = true; }
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
			var volumeDetector = new VolumeDetector();

			var task = new SongAnalyzerTask(queueItem, loaderContext, volumeDetector);

			var t = Task.Run(() => task.Run(CancellationToken.None));
			var res = t.Result;
			Assert.IsFalse(res.Ok);
			Assert.AreSame(res.Error.Str, message);
		}

		private static SongAnalyzerResult TestSongAnalyzerExpectOk(QueueItem queueItem) {
			var loaderContext = new LoaderContext();
			var volumeDetector = new VolumeDetector();
			
			var task = new SongAnalyzerTask(queueItem, loaderContext, volumeDetector);

			var t = Task.Run(() => task.Run(CancellationToken.None));
			var res = t.Result;
			Assert.IsTrue(res.Ok);
			return res.Value;
		}

		[Test]
		public void RunSongAnalyzerTaskTest() {
			var queueItem = new QueueItem(Resource1AYoutube, new MetaData(null));
			var queueItemGain = new QueueItem(Resource1AYoutubeGain, new MetaData(null));

			{
				// Item without gain gets the gain set
				var res = TestSongAnalyzerExpectOk(queueItem);
				var resource = res.Resource.BaseData;
				var gain = resource.Gain;
				Assert.IsTrue(gain.HasValue);
				Assert.AreEqual(gain.Value, VolumeDetector.VolumeSet);
			}

			{
				// Item with gain set should not be changed
				var res = TestSongAnalyzerExpectOk(queueItemGain);
				var resource = res.Resource.BaseData;
				Assert.AreSame(queueItemGain.AudioResource, resource);
			}

			{
				// Failing load gets propagated
				var loaderContext = new LoaderContext {ShouldFailLoad = true};
				TestSongAnalyzerExpectErrorMessage(queueItem, loaderContext, LoaderContext.LoadFailedMessage);
			}

			{
				// No restored link gets propagated
				var loaderContext = new LoaderContext {ShouldReturnNoRestoredLink = true};
				TestSongAnalyzerExpectErrorMessage(queueItem, loaderContext, LoaderContext.NoRestoredLinkMessage);
			}

			{
				// Cancelling throws cancelled exception of volume detector
				var loaderContext = new LoaderContext();
				var volumeDetector = new VolumeDetector();

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
			var volume = new ConfAudioVolume();
			volume.Min.Value = 0;
			volume.Max.Value = 10;
			var lck = new object();
			const string listId = "CoolPlaylist";
			var queueItem = new QueueItem(Resource1AYoutube, new MetaData(TestUid, listId));
			var queueItemGain = new QueueItem(Resource1AYoutubeGain, new MetaData(TestUid, listId));
			var playResource = new PlayResource(queueItem.AudioResource.ResourceId, queueItem.AudioResource, queueItem.MetaData);
			var playResourceGain = new PlayResource(queueItemGain.AudioResource.ResourceId, queueItemGain.AudioResource, queueItemGain.MetaData);

			{
				// Queue item without gain gets it set and update gets invoked
				var loaderContext = new LoaderContext();
				var player = new Player();
				
				var task = new StartSongTask(loaderContext, player, volume, lck, queueItem);

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
				Assert.AreEqual(gain.Value, VolumeDetector.VolumeSet);
			}

			{
				var loaderContext = new LoaderContext();
				var player = new Player();
				
				var task = new StartSongTask(loaderContext, player, volume, lck, queueItem);

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
				var player = new Player();
				
				var task = new StartSongTask(null, player, volume, null, null);
				PlayInfoEventArgs argsBefore = null;
				PlayInfoEventArgs argsAfter = null;
				
				task.BeforeResourceStarted += (sender, args) => {
					Assert.IsNotNull(args);
					Assert.AreEqual(args.Invoker, TestUid);
					Assert.AreSame(args.MetaData, queueItemGain.MetaData);
					Assert.AreSame(args.ResourceData, queueItemGain.AudioResource);
					Assert.AreSame(args.SourceLink, LoaderContext.RestoredLink);
					argsBefore = args;
				};

				task.AfterResourceStarted += (sender, args) => {
					Assert.IsNotNull(args);
					Assert.AreEqual(args.Invoker, TestUid);
					Assert.AreSame(args.MetaData, queueItemGain.MetaData);
					Assert.AreSame(args.ResourceData, queueItemGain.AudioResource);
					Assert.AreSame(args.SourceLink, LoaderContext.RestoredLink);
					argsAfter = args;
				};
				
				var t = Task.Run(() => task.StartResource(new SongAnalyzerResult {Resource = playResourceGain, RestoredLink = LoaderContext.RestoredLink}));

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
				var player = new Player();
				
				var task = new StartSongTask(null, player, volume, null, null);
				var t = Task.Run(() => task.StartResource(new SongAnalyzerResult {Resource = playResource, RestoredLink = LoaderContext.RestoredLink}));

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
