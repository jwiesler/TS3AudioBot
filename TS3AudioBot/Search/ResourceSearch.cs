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

		public R<Result, LocalStr> Find(string query, uint offset, int maxItems) {
			var res = sa.FindUniqueItems(query.ToLowerInvariant().ToCharArray(), maxItems, offset);
			if (!res.Ok)
				return res.Error;

			var (ints, result) = res.Value;
			return new Result {
				ConsumedResults = (int) result.Consumed,
				Items = LookupItems(ints, (int) result.Count),
				TotalResults = (int) result.TotalResults
			};
		}

		public void Dispose() {
			sa?.Dispose();
		}
	}

	public class ResourceSearch {
		private static readonly Logger Log = LogManager.GetCurrentClassLogger();

		private ResourceSearchInstance instance;
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

		public R<ResourceSearchInstance.Result, LocalStr> Find(string query, uint offset, int maxItems) {
			return instance.Find(query, offset, maxItems);
		}

		private void Exchange(ResourceSearchInstance value) {
			var old = Interlocked.Exchange(ref instance, value);
			old?.Dispose();
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
