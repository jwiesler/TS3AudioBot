using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;

namespace TS3AudioBot.Search {
	public class ResourceSearchInstance : IDisposable {
		private static readonly Logger Log = LogManager.GetCurrentClassLogger();

		private readonly StrSearchWrapper sa;
		private readonly List<PlaylistSearchItemInfo> items;

		public static PlaylistSearchItemInfo Convert(IReadonlyUniqueResourceInfo info) {
			var r = new PlaylistSearchItemInfo();
			r.ResourceTitle = info.Resource.ResourceTitle;
			r.ResourceId = info.Resource.ResourceId;
			r.ResourceType = info.Resource.AudioType;
			r.ContainingLists = info.ContainingLists.Select(kv => new ContainingListInfo {Id = kv.Key, Index = kv.Value}).ToList();
			return r;
		}

		public ResourceSearchInstance(IEnumerable<IReadonlyUniqueResourceInfo> uniqueItems) {
			items = uniqueItems.Select(Convert).ToList();
			sa = new StrSearchWrapper(items.Select(i => i.ResourceTitle.ToLowerInvariant()).ToList());
		}

		private List<PlaylistSearchItemInfo> LookupItems(int[] offsets, int count) {
			var res = new List<PlaylistSearchItemInfo>(count);
			for (int i = 0; i < count; ++i) {
				res.Add(items[offsets[i]]);
			}

			return res;
		}

		public class Result {
			public List<PlaylistSearchItemInfo> Items { get; set; }
			public int ConsumedResults { get; set; }
			public int TotalResults { get; set; }
		}

		private Result MakeResult(int[] items, StrSearch.FindUniqueItemsResult result) {
			return new Result {
				ConsumedResults = (int) result.Consumed,
				Items = LookupItems(items, (int) result.Count),
				TotalResults = (int) result.TotalResults
			};
		}

		public R<Result, LocalStr> Find(string query, int maxItems, uint offset) {
			var res = sa.FindUniqueItems(query.ToLowerInvariant().ToCharArray(), maxItems, offset);
			if (!res.Ok)
				return res.Error;

			return MakeResult(res.Value.items, res.Value.result);
		}

		public R<Result, LocalStr> FindKeywords(string query, int maxItems, StrSearch.KeywordsMatch matching, uint offset) {
			var res = sa.FindUniqueItemsKeywords(query.ToLowerInvariant().ToCharArray(), maxItems, matching, offset);
			if (!res.Ok)
				return res.Error;

			return MakeResult(res.Value.items, res.Value.result);
		}

		public void Dispose() {
			sa?.Dispose();
		}
	}

	public class ResourceSearch {
		private static readonly Logger Log = LogManager.GetCurrentClassLogger();

		private ResourceSearchInstance instance;
		private readonly ReaderWriterLockSlim instanceLock = new ReaderWriterLockSlim();

		private Task CurrentUpdateTask { get; set; }
		private int Version { get; set; }
		private PlaylistDatabase Database { get; }

		public ResourceSearch(PlaylistDatabase database) {
			Database = database;
			Exchange(Build(database));
		}

		private static ResourceSearchInstance Build(PlaylistDatabase database) {
			Stopwatch timer = new Stopwatch();
			timer.Start();
			var items = database.UniqueResources;
			var loadMs = timer.ElapsedMilliseconds;
			timer.Restart();
			var inst = new ResourceSearchInstance(items);
			Log.Info($"Built suffix array (loading playlists {loadMs}ms, build {timer.ElapsedMilliseconds}ms)");
			return inst;
		}

		public R<ResourceSearchInstance.Result, LocalStr> Find(string query, int maxItems, uint offset) {
			instanceLock.EnterReadLock();
			var r = instance.Find(query, maxItems, offset);
			instanceLock.ExitReadLock();
			return r;
		}

		public R<ResourceSearchInstance.Result, LocalStr> FindKeywords(string query, int maxItems, StrSearch.KeywordsMatch matching, uint offset) {
			instanceLock.EnterReadLock();
			var r = instance.FindKeywords(query, maxItems, matching, offset);
			instanceLock.ExitReadLock();
			return r;
		}

		private void Exchange(ResourceSearchInstance value) {
			instanceLock.EnterWriteLock();
			instance?.Dispose();
			instance = value;
			instanceLock.ExitWriteLock();
		}

		private Task StartRebuildTask(int version) {
			return Task.Run(() => {
				while (true) {
					Log.Info($"Rebuilding suffix array (version {version})...");

					var inst = Build(Database);
					lock (this) {
						if (version < Version) {
							// Rebuild was called in the background, rebuild
							inst.Dispose();
							version = Version;
							continue;
						}

						Exchange(inst);
						CurrentUpdateTask = null;
						return;
					}
				}
			});
		}

		public void Rebuild() {
			lock (this) {
				if (CurrentUpdateTask == null) {
					// not running => start
					CurrentUpdateTask = StartRebuildTask(Version);
				} else {
					// Already running => announce version change
					++Version;
				}
			}
		}
	}
}
