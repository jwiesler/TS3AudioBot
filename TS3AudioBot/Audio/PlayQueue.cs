using System;
using System.Collections.Generic;
using TS3AudioBot.ResourceFactories;
using TSLib.Helper;

namespace TS3AudioBot.Audio {
	public class QueueItem {
		public AudioResource AudioResource { get; }
		public MetaData MetaData { get; }

		public QueueItem(AudioResource audioResource, MetaData metaData) {
			AudioResource = audioResource;
			MetaData = metaData;
		}

		public override string ToString() { return $"{AudioResource} ({MetaData})"; }
	}

	public class PlayQueueCurrentChangedEventArgs : EventArgs {
		public int Index { get; }

		public PlayQueueCurrentChangedEventArgs(int index) {
			Index = index;
		}
	}

	public class PlayQueue {
		private readonly List<QueueItem> items;

		public int Index { get; set; } = 0;

		public QueueItem Current => TryGetItem(Index);

		public QueueItem Next => TryGetItem(Index + 1);

		public IReadOnlyList<QueueItem> Items => items;

		public int Count => Items.Count;

		public event EventHandler<EventArgs> OnQueueChange;
		public event EventHandler<EventArgs> OnQueueIndexChange;

		public PlayQueue() { items = new List<QueueItem>(); }

		public QueueItem TryGetItem(int index) { return index < items.Count ? items[index] : null; }

		public void Enqueue(QueueItem item) {
			items.Add(item);
			OnQueueChange?.Invoke(this, null);
		}

		public void Enqueue(IEnumerable<QueueItem> list) {
			items.AddRange(list);
			OnQueueChange?.Invoke(this, null);
		}

		public void InsertAfter(QueueItem item, int index) {
			if(!Tools.IsBetweenExcludingUpper(index, 0, items.Count))
				throw new ArgumentException();
			items.Insert(index + 1, item);
			OnQueueChange?.Invoke(this, null);
		}

		public void Remove(int at) {
			if (!Tools.IsBetweenExcludingUpper(at, 0, items.Count))
				throw new ArgumentException();
			if (Index == at)
				throw new InvalidOperationException("Can't remove the current item");

			items.RemoveAt(at);
			if (at < Index) {
				--Index;
				OnQueueIndexChange?.Invoke(this, null);
			}

			OnQueueChange?.Invoke(this, null);
		}

		public void RemoveRange(int from, int to) {
			if (!Tools.IsBetweenExcludingUpper(from, 0, items.Count) ||
			    !Tools.IsBetweenExcludingUpper(to, 0, items.Count) || to < from)
				throw new ArgumentException();
			if (Tools.IsBetween(Index, from, to))
				throw new InvalidOperationException("Can't remove the current item");

			int count = to - from + 1;
			items.RemoveRange(from, count);
			if (to < Index) {
				Index -= count;
				OnQueueIndexChange?.Invoke(this, null);
			}

			OnQueueChange?.Invoke(this, null);
		}

		public bool CanSkip(int count) {
			int targetIndex = Index + count;
			return 0 < count && Tools.IsBetween(targetIndex, 0, items.Count);
		}

		public bool Skip(int count) {
			if(count <= 0)
				throw new ArgumentException("count too small");

			int targetIndex = Index + count;
			if (!Tools.IsBetween(targetIndex, 0, items.Count))
				throw new ArgumentException("count too large");

			Index = targetIndex;
			OnQueueChange?.Invoke(this, null);
			OnQueueIndexChange?.Invoke(this, null);
			return Index != items.Count;
		}

		public bool TryNext() {
			if (Index == items.Count)
				return false;

			if (++Index != items.Count) {
				OnQueueChange?.Invoke(this, null);
				OnQueueIndexChange?.Invoke(this, null);
				return true;
			}
			return false;
		}

		public bool TryPrevious() {
			if (0 == Index)
				return false;
			--Index;
			OnQueueChange?.Invoke(this, null);
			OnQueueIndexChange?.Invoke(this, null);
			return true;
		}

		public void Clear() {
			items.Clear();
			Index = 0;
			OnQueueChange?.Invoke(this, null);
			OnQueueIndexChange?.Invoke(this, null);
		}
	}
}
