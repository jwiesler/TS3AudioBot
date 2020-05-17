using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;

namespace TS3AudioBot.Search {
	class ResourceSearchInstance {
		private static readonly Logger Log = LogManager.GetCurrentClassLogger();

		private readonly SuffixArray sa;
		private readonly List<PlaylistSearchItemInfo> items;

		public static PlaylistSearchItemInfo Convert(UniqueResourceInfo info) {
			var r = new PlaylistSearchItemInfo();
			r.ResourceTitle = info.Resource.ResourceTitle;
			r.ResourceId = info.Resource.ResourceId;
			r.ContainingLists = info.ContainingLists.Select(kv => new ContainingListInfo {Id = kv.Key, Index = kv.Value.First()}).ToList();
			return r;
		}

		public ResourceSearchInstance(List<UniqueResourceInfo> uniqueItems) {
			items = uniqueItems.Select(Convert).ToList();
			sa = new SuffixArray(items.Select(i => i.ResourceTitle.ToLowerInvariant()).ToList());
		}

		private List<PlaylistSearchItemInfo> LookupItems(int begin, int end) {
			var res = new List<PlaylistSearchItemInfo>(end - begin);
			for (int i = begin; i < end; ++i) {
				var idx = sa.LookupItemAtSAIndex(i);
				if (!idx.Ok) {
					Log.Warn("LookupItemAtSAIndex returned error");
					continue;
				}
				res.Add(items[idx.Value]);
			}

			return res;
		}

		public R<(int totalResults, List<PlaylistSearchItemInfo> results), LocalStr> Find(string query, int offset, int maxItems) {
			var (begin, end) = sa.Find(query.ToLowerInvariant());
			if (end < begin)
				return new LocalStr("Search failed.");
			
			Log.Info($"Found {end - begin} items for query \"{query}\"");

			int count = end - begin;
			begin += offset;
			if (end < begin)
				return new LocalStr("Offset was out of bounds");

			if (maxItems < end - begin)
				end = begin + maxItems;

			var res = LookupItems(begin, end);
			return (count, res);
		}
	}

	public class ResourceSearch {
		private static readonly Logger Log = LogManager.GetCurrentClassLogger();
		private ResourceSearchInstance Instance { get; set; }
		private Task CurrentUpdateTask { get; set; }
		private int Version { get; set; }
		private PlaylistIO PlaylistIO { get; }

		public ResourceSearch(PlaylistIO playlistIO) {
			PlaylistIO = playlistIO;
			Instance = Build(playlistIO);
		}

		private static ResourceSearchInstance Build(PlaylistIO io) {
			Stopwatch timer = new Stopwatch();
			timer.Start();
			var items = io.ListItems();
			var loadMs = timer.ElapsedMilliseconds;
			timer.Restart();
			var inst = new ResourceSearchInstance(items);
			Log.Info($"Built suffix array (loading playlists {loadMs}ms, build {timer.ElapsedMilliseconds}ms)");
			return inst;
		}

		public R<(int totalResults, List<PlaylistSearchItemInfo> results), LocalStr> Find(string query, int offset, int maxItems) {
			return Instance.Find(query, offset, maxItems);
		}

		private Task StartRebuildTask(int version) {
			return Task.Run(() => {
				while (true) {
					Log.Info($"Rebuilding suffix array (version {version})...");
					
					var inst = Build(PlaylistIO);
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
