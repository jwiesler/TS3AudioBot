using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;

namespace TS3AudioBot.Search {
	public class ResourceSearchInstance {
		private static readonly Logger Log = LogManager.GetCurrentClassLogger();

		private readonly SuffixArray sa;
		private readonly List<PlaylistSearchItemInfo> items;

		public static PlaylistSearchItemInfo Convert(IReadonlyUniqueResourceInfo info) {
			var r = new PlaylistSearchItemInfo();
			r.ResourceTitle = info.Resource.ResourceTitle;
			r.ResourceId = info.Resource.ResourceId;
			r.ContainingLists = info.ContainingLists.Select(kv => new ContainingListInfo {Id = kv.Key, Index = kv.Value}).ToList();
			return r;
		}

		public ResourceSearchInstance(IEnumerable<IReadonlyUniqueResourceInfo> uniqueItems) {
			items = uniqueItems.Select(Convert).ToList();
			sa = new SuffixArray(items.Select(i => i.ResourceTitle.ToLowerInvariant()).ToList());
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

		private Result FindAtMostItems(int begin, int end, int offset, int count) {
			var timer = new Stopwatch();
			timer.Start();
			var (uniqueItems, okCount, totalConsumed) = sa.GetUniqueItemsFromRange(begin + offset, end, count);
			Log.Info($"Unique took {timer.Elapsed.TotalMilliseconds}ms");
			
			return new Result {
				ConsumedResults = totalConsumed,
				Items = LookupItems(uniqueItems, okCount),
				TotalResults = end - begin
			};
		}

		public R<Result, LocalStr> Find(string query, int offset, int maxItems) {
			var (begin, end) = sa.Find(query.ToLowerInvariant());
			if (end < begin)
				return new LocalStr("Search failed.");
			
			Log.Info($"Found {end - begin} items for query \"{query}\"");

			if (end < begin + offset)
				return new LocalStr("Offset was out of bounds");

			int count = end - begin - offset;
			if (maxItems < count)
				count = maxItems;

			return FindAtMostItems(begin, end, offset, count);
		}
	}

	public class ResourceSearch {
		private static readonly Logger Log = LogManager.GetCurrentClassLogger();
		private ResourceSearchInstance Instance { get; set; }
		private Task CurrentUpdateTask { get; set; }
		private int Version { get; set; }
		private PlaylistDatabase Database { get; }

		public ResourceSearch(PlaylistDatabase database) {
			Database = database;
			Instance = Build(database);
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

		public R<ResourceSearchInstance.Result, LocalStr> Find(string query, int offset, int maxItems) {
			return Instance.Find(query, offset, maxItems);
		}

		private Task StartRebuildTask(int version) {
			return Task.Run(() => {
				while (true) {
					Log.Info($"Rebuilding suffix array (version {version})...");
					
					var inst = Build(Database);
					lock (this) {
						if (version < Version) {
							// Rebuild was called in the background, rebuild
							version = Version;
							continue;
						}

						Instance = inst;
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
