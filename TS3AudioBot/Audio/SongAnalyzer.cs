using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;

namespace TS3AudioBot.Audio {
	public class SongAnalyzerTask : IDisposable {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		public QueueItem Source { get; }
		public FfmpegProducer FfmpegProducer { get; }
		public ResolveContext ResourceResolver { get; }
		public Task Task { get; private set; }

		public R<PlayResource, LocalStr>? Resource { get; private set; }
		public int Gain { get; private set; }
		
		private CancellationTokenSource TokenSource { get; } = new CancellationTokenSource();

		public SongAnalyzerTask(QueueItem source, ResolveContext resourceResolver, FfmpegProducer ffmpegProducer) {
			Source = source;
			FfmpegProducer = ffmpegProducer;
			ResourceResolver = resourceResolver;
		}

		private void Run(CancellationToken cancellationToken) {
			Stopwatch timer = new Stopwatch();
			timer.Start();

			Resource = ResourceResolver.Load(Source.AudioResource);
			if (!Resource.Value.Ok)
				return;
			var res = Resource.Value.Value;
			ResourceResolver.RestoreLink(res.BaseData);

			Log.Debug("Song resolve took {0}ms", timer.ElapsedMilliseconds);
			timer.Restart();

			Gain = FfmpegProducer.VolumeDetect(res.PlayUri, cancellationToken);

			Log.Debug("Song volume detect took {0}ms", timer.ElapsedMilliseconds);
		}

		private Task CreateTask(int inSeconds, CancellationToken token) {
			return new Task(() => {
				Task.Delay(inSeconds * 1000, token).Wait();
				Log.Info("Started analyze for \"{0}\"", SongAnalyzer.GetItemDescription(Source));
				Run(token);
			});
		}

		public R<(PlayResource resource, int gain), LocalStr> TryGetResult() {
			if(Task == null)
				throw new InvalidOperationException();

			bool ended = Task.Wait(2000);

			if (!Resource.HasValue) {
				Cancel();
				Log.Warn("Song analyze task is hanging or didn't start yet...");

				Run(new CancellationToken());
				if(!Resource.HasValue)
					throw new InvalidOperationException();

				if (!Resource.Value.Ok)
					return Resource.Value.Error;
			} else {
				if (!Resource.Value.Ok)
					return Resource.Value.Error;

				if (!ended) {
					Log.Warn("Song analyze task is taking very long...");
					Task.Wait();
				}
			}

			return (Resource.Value.Value, Gain);
		}

		public void PrepareRun(int inSeconds) {
			if (Task != null) {
				Log.Warn("SongAnalyzerTask was already working");
				return;
			}

			Log.Debug("Preparing background analyzer for \"{0}\", starting in {1}s", SongAnalyzer.GetItemDescription(Source), inSeconds);

			Task = CreateTask(inSeconds, TokenSource.Token);
		}

		public void Start() { Task.Start(); }

		public void RunSynchronously() { Task.RunSynchronously(); }

		public void Cancel() { TokenSource.Cancel(); }

		public void Dispose() {
			Cancel();
			TokenSource?.Dispose();
		}
	}

	public class SongAnalyzer {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private const int MaxSecondsBeforeNextSong = 10;

		public FfmpegProducer FfmpegProducer { get; }

		private ResolveContext ResourceResolver { get; }

		public SongAnalyzerTask Instance { get; private set; }

		public SongAnalyzer(ResolveContext resourceResolver, FfmpegProducer ffmpegProducer) {
			ResourceResolver = resourceResolver;
			FfmpegProducer = ffmpegProducer;
		}

		public void Prepare(int inSeconds) {
			if(Instance == null)
				throw new InvalidOperationException("instance null");

			Instance.PrepareRun(inSeconds);
			Instance.Start();
		}

		public void SetNextSong(QueueItem item) {
			Instance?.Dispose();
			Instance = new SongAnalyzerTask(item, ResourceResolver, FfmpegProducer);
		}

		public static string GetItemDescription(QueueItem item) { return item.AudioResource.ResourceTitle; }

		public R<(PlayResource resource, int gain), LocalStr> TryGetResult(QueueItem item) {
			if (Instance?.Task == null || !ReferenceEquals(Instance.Source, item)) {
				Instance?.Cancel();
				Log.Warn("Song {0} was not prepared", SongAnalyzer.GetItemDescription(item));
				SetNextSong(item);
				Instance.PrepareRun(0);
				Instance.RunSynchronously();
			}
			var res = Instance.TryGetResult();
			Clear();
			return res;
		}

		public bool IsPreparing(QueueItem item) { return Instance != null && ReferenceEquals(Instance.Source, item); }

		public static int GetTaskStartTime(TimeSpan remainingSongTime) {
			int remainingTime = (int)remainingSongTime.TotalSeconds;
			return Math.Max(remainingTime - MaxSecondsBeforeNextSong, 0);
		}

		public void Clear() {
			Instance?.Dispose();
			Instance = null;
		}
	}
}
