using System;
using System.Collections.Generic;
using System.Linq;

namespace TS3AudioBot.Search
{
	public class StringView : IComparable<StringView> {
		private char[] UnderlyingArray { get; }
		public int Offset { get; }
		public int Length { get; }

		public StringView(char[] underlyingArray, int offset, int length) {
			UnderlyingArray = underlyingArray;
			Offset = offset;
			Length = length;
		}

		public StringView(char[] underlyingArray, int offset) : this(underlyingArray, offset, underlyingArray.Length - offset) {}

		public StringView(char[] underlyingArray) : this(underlyingArray, 0, underlyingArray.Length) {}

		public StringView(string underlyingArray) : this(underlyingArray.ToCharArray(), 0, underlyingArray.Length) {}

		public int CompareTo(StringView o) { return string.Compare(ToString(), o.ToString(), StringComparison.Ordinal); }

		public StringView Slice(int count) {
			return new StringView(UnderlyingArray, Offset, Math.Min(count, Length));
		}

		public override string ToString() { return new string(UnderlyingArray, Offset, Length); }

		public char AtAbsolute(int i) { return UnderlyingArray[i]; }

		public char At(int i) { return AtAbsolute(Offset + i); }
		
		public char this[int i] => AtAbsolute(i);
	}

	public class SuffixArray {
		public int[] WordStarts { get; private set; }
		public int CharacterCount { get; private set; }
		public int[] SA { get; private set; }
		public char[] Characters { get; private set; }

		public StringView GetSuffix(int i) {
			return new StringView(Characters, i);
		}

		public StringView GetSuffix(int i, int count) {
			return new StringView(Characters, i).Slice(count);
		}

		private static bool LexicographicalCompare(StringView a, StringView b) {
			int first1 = a.Offset;
			int first2 = b.Offset;
			int last1 = a.Offset + a.Length;
			int last2 = b.Offset + b.Length;

			while (first1 != last1) {
				if (first2 == last2 || b.AtAbsolute(first2) < a.AtAbsolute(first1)) 
					return false;
				if (a.AtAbsolute(first1) < b.AtAbsolute(first2)) 
					return true;
				++first1;
				++first2;
			}

			return (first2 != last2);
		}

		private int Compare(int a, int b) {
			var sufa = GetSuffix(a);
			var sufb = GetSuffix(b);
			if (LexicographicalCompare(sufa, sufb))
				return -1;
			if (LexicographicalCompare(sufb, sufa))
				return 1;
			return 0;
		}

		public static void PrintSuffixArray(int[] sa, char[] text, int begin, int end) {
			for (int i = begin; i < end; ++i) {
				Console.WriteLine($"{i}: {new StringView(text, sa[i]).Slice(20)}");
			}
		}

		public SuffixArray(ICollection<string> strings) {
			CharacterCount = strings.Sum(w => w.Length + 1);
			Characters = new char[CharacterCount];
			WordStarts = new int[strings.Count];
			{
				int offset = 0;
				int word = 0;
				foreach (var str in strings) {
					WordStarts[word] = offset;
					foreach (var c in str) {
						Characters[offset++] = c;
					}

					++word;
					++offset;
				}
			}

			SA = new int[CharacterCount];
			for (int i = 0; i < CharacterCount; ++i) {
				SA[i] = i;
			}

			Array.Sort(SA, Compare);
		}

		public delegate bool LessDelegate<in T>(T a, T b);

		public static int LowerBound<T>(T[] array, T value, int start, int end, LessDelegate<T> less) {
			while (start < end) {
				int mid = start + (end - start) / 2;
				
				if (less(array[mid], value))
					start = mid + 1;
				else
					end = mid;
			}
			return start;
		}

		public static int UpperBound<T>(T[] array, T value, int start, int end, LessDelegate<T> less) {
			while (start < end) {
				int mid = start + (end - start) / 2;
				
				if (!less(value, array[mid]))
					start = mid + 1;
				else
					end = mid;
			}
			return start;
		}

		public int LowerBound(StringView pattern, int start, int end) {
			while (start < end) {
				int mid = start + (end - start) / 2;
				var suffix = GetSuffix(SA[mid], pattern.Length);
				if (LexicographicalCompare(suffix, pattern))
					start = mid + 1;
				else
					end = mid;
			}
			return start;
		}
		
		public int UpperBound(StringView pattern, int start, int end) {
			while (start < end) {
				int mid = start + (end - start) / 2;
				var suffix = GetSuffix(SA[mid], pattern.Length);
				if (!LexicographicalCompare(pattern, suffix))
					start = mid + 1;
				else
					end = mid;
			}

			return start;
		}

		public R<int> LookupItemAtIndex(int index) {
			int l = UpperBound(WordStarts, index, 0, WordStarts.Length, (i, i1) => i < i1);
			if (l == 0)
				return R.Err;
			return l - 1;
		}

		public R<int> LookupItemAtSAIndex(int index) { return LookupItemAtIndex(SA[index]); }

		public (int begin, int end) Find(string query) {
			var p = new StringView(query);
			int l = LowerBound(p, 0, CharacterCount);
			int u = UpperBound(p, 0, CharacterCount);
			return (l, u);
		}
	}
}
