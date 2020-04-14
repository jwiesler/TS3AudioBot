using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
	}

	public class PlayQueueCurrentChangedEventArgs : EventArgs {
		public int Index { get; }

		public PlayQueueCurrentChangedEventArgs(int index) {
			Index = index;
		}
	}

//	public class PlayQueue {
//		private readonly List<QueueItem> items;
//		private readonly object listLock = new object();
//		private int index = -1;
//
//		public event EventHandler<PlayQueueCurrentChangedEventArgs> OnCurrentItemChanged;
//
//		public QueueItem Current {
//			get {
//				lock (listLock) {
//					return index == -1 || index == items.Count ? null : items[index];
//				}
//			}
//		}
//
//		public IReadOnlyList<QueueItem> Items => items;
//
//		public PlayQueue() { items = new List<QueueItem>(); }
//
//		public void Enqueue(QueueItem item) {
//			lock (listLock) {
//				items.Add(item);
//			}
//		}
//
//		public void Enqueue(IEnumerable<QueueItem> items) {
//			lock (listLock) {
//				items.AddRange(items);
//			}
//		}
//
//		public void Remove(int at) {
//			lock (listLock) {
//				if (index == at)
//					throw new InvalidOperationException("Can't remove the current item");
//
//				items.RemoveAt(at);
//			}
//		}
//
//		public void RemoveRange(int from, int to) {
//			lock (listLock) {
//				if (!Tools.IsBetweenExcludingUpper(from, 0, items.Capacity) ||
//				    !Tools.IsBetweenExcludingUpper(to, 0, items.Capacity) || to < from)
//					throw new ArgumentException();
//				if (Tools.IsBetween(index, from, to))
//					throw new InvalidOperationException("Can't remove the current item");
//				items.RemoveRange(from, to - from + 1);
//			}
//		}
//
//		private void InvokeCurrentItemChanged(int idx) {
//			OnCurrentItemChanged?.Invoke(this, new PlayQueueCurrentChangedEventArgs(idx));
//		}
//
//		public bool TryNext() {
//			int idx;
//			lock (listLock) {
//				if (items.Count == index - 1)
//					return false;
//				idx = ++index;
//			}
//			InvokeCurrentItemChanged(idx);
//			return true;
//		}
//
//		public bool TryPrevious() {
//			int idx;
//			lock (listLock) {
//				if (0 == index)
//					return false;
//				idx = --index;
//			}
//			InvokeCurrentItemChanged(idx);
//			return true;
//		}
//
//		public void Clear() {
//			lock (listLock) {
//				items.Clear();
//				index = -1;
//			}
//			InvokeCurrentItemChanged(-1);
//		}
//	}

	public class PlayQueue {
		private readonly List<QueueItem> items;

		//public event EventHandler<PlayQueueCurrentChangedEventArgs> OnCurrentItemChanged;

		public int Index { get; private set; } = 0;

		public QueueItem Current => Index == items.Count ? null : items[Index];

		public IReadOnlyList<QueueItem> Items => items;

		public PlayQueue() { items = new List<QueueItem>(); }

		public void Enqueue(QueueItem item) { items.Add(item); }

		public void Enqueue(IEnumerable<QueueItem> list) { items.AddRange(list); }

		public void InsertAfter(QueueItem item, int index) {
			if(Tools.IsBetweenExcludingUpper(index, 0, items.Capacity))
				throw new ArgumentException();
			items.Insert(index + 1, item);
		}

		public void Remove(int at) {
			if (Index == at)
				throw new InvalidOperationException("Can't remove the current item");

			items.RemoveAt(at);
			if (at < Index)
				--Index;
		}

		public void RemoveRange(int from, int to) {
			if (!Tools.IsBetweenExcludingUpper(from, 0, items.Capacity) ||
			    !Tools.IsBetweenExcludingUpper(to, 0, items.Capacity) || to < from)
				throw new ArgumentException();
			if (Tools.IsBetween(Index, from, to))
				throw new InvalidOperationException("Can't remove the current item");

			int count = to - from + 1;
			items.RemoveRange(from, count);
			if (to < Index)
				Index -= count;
		}

		public void Skip(int count) {
			if(count <= 0)
				throw new ArgumentException("count too small");

			int targetIndex = Index + count;
			if (!Tools.IsBetweenExcludingUpper(targetIndex, 0, items.Capacity))
				throw new ArgumentException("count too large");

			Index = targetIndex;
		}

		public bool TryNext() {
			if (Index == items.Count)
				return false;

			return ++Index != items.Count;
		}

		public bool TryPrevious() {
			if (0 == Index)
				return false;
			--Index;
			return true;
		}

		public void Clear() {
			items.Clear();
			Index = 0;
		}
	}
}
