using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;

namespace TS3AudioBot.Audio {
	public class SongAnalyzerResult {
		public PlayResource Resource { get; set; }

		public R<string, LocalStr> RestoredLink { get; set; }
	}

	public class SongAnalyzerTask {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		public QueueItem Source { get; }

		public FfmpegProducer FfmpegProducer { get; }

		public ResolveContext ResourceResolver { get; }

		public SongAnalyzerTask(QueueItem source, ResolveContext resourceResolver, FfmpegProducer ffmpegProducer) {
			Source = source;
			FfmpegProducer = ffmpegProducer;
			ResourceResolver = resourceResolver;
		}

		public R<SongAnalyzerResult, LocalStr> Run(CancellationToken cancellationToken) {
			Log.Info("Started analysis for \"{0}\"", SongAnalyzer.GetItemDescription(Source));
			Stopwatch timer = new Stopwatch();
			timer.Start();

			var resource = ResourceResolver.Load(Source.AudioResource);
			if (!resource.Ok)
				return resource.Error;
			var res = resource.Value;
			var restoredLink = ResourceResolver.RestoreLink(res.BaseData);
			if (!restoredLink.Ok)
				return restoredLink.Error;

			Log.Debug("Song resolve took {0}ms", timer.ElapsedMilliseconds);

			if(!(Source.AudioResource.Gain.HasValue || Source.AudioResource.AudioType != "youtube")) {
				timer.Restart();

				var gain = FfmpegProducer.VolumeDetect(res.PlayUri, cancellationToken);
				res.BaseData = res.BaseData.WithGain(gain);
				Log.Debug("Song volume detect took {0}ms", timer.ElapsedMilliseconds);
			}

			return new SongAnalyzerResult {
				Resource = res,
				RestoredLink = restoredLink
			};
		}
	}

	public class SongAnalyzerTaskHost {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		public Task<R<SongAnalyzerResult, LocalStr>> Current { get; private set; }

		private CancellationTokenSource CancelTokenSource { get; } = new CancellationTokenSource();
		private WaitTask waitTask;

		public SongAnalyzerTask Data { get; }

		public SongAnalyzerTaskHost(SongAnalyzerTask data) { Data = data; }

		private Task<R<SongAnalyzerResult, LocalStr>> CreateTask(CancellationToken token) {
			return new Task<R<SongAnalyzerResult, LocalStr>>(() => {
				try {
					if(!waitTask.Run())
						return new LocalStr("Canceled");

					Log.Trace("SongAnalyzerTask finished waiting, running...");

					return Data.Run(token);
				} catch (OperationCanceledException) {
					return null;
				}
			});
		}

		public void ChangeWaitTime(int seconds) {
			waitTask.UpdateWaitTime(seconds * 1000);
		}

		public R<SongAnalyzerResult, LocalStr> Result {
			get {
				if (Current == null)
					throw new InvalidOperationException();

				return Current.Result;
			}
		}

		public void StartRun(int inSeconds) {
			if (Current != null) {
				Log.Info($"SongAnalyzerTask was already working, updating wait time to {inSeconds}s");
				ChangeWaitTime(inSeconds);
				return;
			}

			Log.Info("Starting SongAnalyzerTask for \"{0}\", starting in {1}s",
				SongAnalyzer.GetItemDescription(Data.Source), inSeconds);

			waitTask = new WaitTask(inSeconds * 1000, CancelTokenSource.Token);
			Current = CreateTask(CancelTokenSource.Token);
			Current.Start();
		}

		public void Cancel() {
			CancelTokenSource.Cancel();
			waitTask.UpdateWaitTime(0);
		}
	}

	public class SongAnalyzer {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private const int MaxSecondsBeforeNextSong = 30;

		public FfmpegProducer FfmpegProducer { get; }

		private ResolveContext ResourceResolver { get; }

//		public SongAnalyzerTaskHost Instance { get; private set; }

		public SongAnalyzer(ResolveContext resourceResolver, FfmpegProducer ffmpegProducer) {
			ResourceResolver = resourceResolver;
			FfmpegProducer = ffmpegProducer;
		}

//		public void Prepare(int inSeconds) {
//			if (Instance == null)
//				throw new InvalidOperationException("instance null");
//
//			Instance.StartRun(inSeconds);
//		}

		private SongAnalyzerTask CreateTask(QueueItem item) {
			return new SongAnalyzerTask(item, ResourceResolver, FfmpegProducer);
		}

//		public void SetNextSong(QueueItem item) {
//			Instance?.Cancel();
//			Instance = new SongAnalyzerTaskHost(CreateTask(item));
//		}

		public static string GetItemDescription(QueueItem item) { return item.AudioResource.ResourceTitle; }

		public R<SongAnalyzerResult, LocalStr> RunSync(QueueItem item, CancellationToken token) {
			return CreateTask(item).Run(token);
		}

//		public R<SongAnalyzerResult, LocalStr> TryGetResult(QueueItem item) {
//			R<SongAnalyzerResult, LocalStr> res;
//			if (Instance?.Current == null || !ReferenceEquals(Instance.Data.Source, item)) {
//				Log.Info("Song {0} was not prepared, running synchronously", GetItemDescription(item));
//				Instance?.Cancel();
//				res = CreateTask(item).Run(new CancellationToken());
//			} else {
//				Instance.ChangeWaitTime(0);
//				res = Instance.Result;
//			}
//
//			Clear();
//			return res;
//		}

//		public bool IsPreparing(QueueItem item) { return Instance != null && ReferenceEquals(Instance.Data.Source, item); }

		public static int GetTaskStartTime(TimeSpan remainingSongTime) {
			int remainingTime = (int) remainingSongTime.TotalSeconds;
			return Math.Max(remainingTime - MaxSecondsBeforeNextSong, 0);
		}

//		public void Clear() {
//			Instance?.Cancel();
//			Instance = null;
//		}
	}
}
