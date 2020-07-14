using System.Collections.Generic;

namespace TS3AudioBot.Algorithm
{
	public static class Collections {
		public static int Move<T>(IList<T> items, int begin, int end, int target) {
			while (begin != end) {
				items[target++] = items[begin++];
			}

			return target;
		}

		public static void RemoveIndices<T>(List<T> items, IList<int> indices, int ibegin, int iend) {
			if(iend == ibegin)
				return;

			int count = items.Count;
			int it = indices[ibegin];
			for (int iit = ibegin; iit < iend; iit++) {
				int next = (iit + 1 == iend ? count : indices[iit + 1]);
				int moveBegin = indices[iit] + 1;
				int moveEnd = next;

				it = Move(items, moveBegin, moveEnd, it);
			}

			items.RemoveRange(it, items.Count - it);
		}

		public static void RemoveIndices<T>(List<T> items, IList<int> indices) {
			RemoveIndices(items, indices, 0, indices.Count);
		}
	}
}
