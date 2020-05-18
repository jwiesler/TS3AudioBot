using System;
using System.Collections.Generic;
using System.Linq;
using NLog;

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

		public StringView(string underlyingArray) : this(underlyingArray.ToCharArray()) {}

		public int CompareTo(StringView o) { return string.Compare(ToString(), o.ToString(), StringComparison.Ordinal); }

		public StringView Slice(int count) {
			return new StringView(UnderlyingArray, Offset, Math.Min(count, Length));
		}

		public override string ToString() { return new string(UnderlyingArray, Offset, Length); }

		public char AtAbsolute(int i) { return UnderlyingArray[i]; }

		public char At(int i) { return AtAbsolute(Offset + i); }
		
		public char this[int i] => AtAbsolute(i);
	}

	public static class Algorithm {
		public static void Swap<T>(ref T a, ref T b) {
			var v = a;
			a = b;
			b = v;
		}

		public static bool LexicographicalCompare(StringView a, StringView b) {
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

		public delegate bool LessDelegate<in T>(T a, T b);
		public static readonly LessDelegate<int> LessInt = (u, v) => u < v;

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

		public static int UpperBoundLinearScan(int[] array, int value, int begin, int end) {
			for (int i = begin; i < end; ++i) {
				if (array[i] > value)
					return i;
			}

			return end;
		}
	}

	public class SuffixArray {
		private static readonly Logger Log = LogManager.GetCurrentClassLogger();
		public int[] WordStarts { get; }
		public int CharacterCount { get; }
		public int[] SA { get; }
		public char[] Characters { get; }

		public StringView GetSuffix(int i) {
			return new StringView(Characters, i);
		}

		public StringView GetSuffix(int i, int count) {
			return new StringView(Characters, i).Slice(count);
		}

		private int Compare(int a, int b) {
			var sufa = GetSuffix(a);
			var sufb = GetSuffix(b);
			if (Algorithm.LexicographicalCompare(sufa, sufb))
				return -1;
			if (Algorithm.LexicographicalCompare(sufb, sufa))
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

		private int LowerBound(StringView pattern, int start, int end) {
			while (start < end) {
				int mid = start + (end - start) / 2;
				var suffix = GetSuffix(SA[mid], pattern.Length);
				if (Algorithm.LexicographicalCompare(suffix, pattern))
					start = mid + 1;
				else
					end = mid;
			}
			return start;
		}

		private int UpperBound(StringView pattern, int start, int end) {
			while (start < end) {
				int mid = start + (end - start) / 2;
				var suffix = GetSuffix(SA[mid], pattern.Length);
				if (!Algorithm.LexicographicalCompare(pattern, suffix))
					start = mid + 1;
				else
					end = mid;
			}

			return start;
		}

		private static int EfficientUpperBoundIndexSearch(int[] array, int value, int begin, int end) {
			if (begin - value > 200)
				return Algorithm.UpperBound(array, value, begin, end, Algorithm.LessInt);
			return Algorithm.UpperBoundLinearScan(array, value, begin, end);
		}

		private int WordEndOf(int offset) {
			return offset + 1 < WordStarts.Length ? WordStarts[offset + 1] : Characters.Length;
		}

		public int FindWordFromCharacter(int index, int begin) {
			return EfficientUpperBoundIndexSearch(WordStarts, index, begin, WordStarts.Length) - 1;
		}

		private int MakeUniqueFull(int[] array, int[] wordIndices) {
			int lastOffset = 0;
			int lastWordEnd = 0;
			int write = 0;
			for (int i = 0; i < array.Length; ++i) {
				int index = array[i];
				if (index < lastWordEnd) {
					// duplicate
					continue;
				}

				int offset = FindWordFromCharacter(index, lastOffset);
				lastOffset = offset;
				lastWordEnd = WordEndOf(offset);

				array[write] = index;
				wordIndices[write] = offset;
				++write;
			}

			return write;
		}

		private void UpdateBothHalfs(int value, int word, out int lastOffset, out int lastWordEnd, ref int write, int[] arrayOut, int[] wordIndicesOut) {
			lastOffset = word;
			lastWordEnd = WordEndOf(word);
			wordIndicesOut[write] = word;
			arrayOut[write++] = value;
		}

		private void UpdateFromFirstHalf(int v1, ref int lastOffset, int[] wordIndices, int i1, ref int lastWordEnd, ref int write, int[] arrayOut, int[] wordIndicesOut) {
			if (v1 >= lastWordEnd) {
				int word = wordIndices[i1];
				UpdateBothHalfs(v1, word, out lastOffset, out lastWordEnd, ref write, arrayOut, wordIndicesOut);
			}
		}

		private void UpdateFromSecondHalf(int v2, ref int lastOffset, ref int lastWordEnd, ref int write, int[] arrayOut, int[] wordIndicesOut) {
			if (v2 >= lastWordEnd) {
				int word = FindWordFromCharacter(v2, lastOffset);
				UpdateBothHalfs(v2, word, out lastOffset, out lastWordEnd, ref write, arrayOut, wordIndicesOut);
			}
		}

		private int MakeUniqueMerge(int[] array, int[] wordIndices, int[] arrayOut, int[] wordIndicesOut, int secondHalfStart) {
			int i1 = 0;
			int i2 = secondHalfStart;
			int write = 0;

			int lastOffset = 0;
			int lastWordEnd = 0;
			for (int i = 0; i < array.Length; ++i) {
				if (i1 == secondHalfStart) {
					UpdateFromSecondHalf(array[i2], ref lastOffset, ref lastWordEnd, ref write, arrayOut, wordIndicesOut);
					i2++;
				} else if (i2 == array.Length) {
					UpdateFromFirstHalf(array[i1], ref lastOffset, wordIndices, i1, ref lastWordEnd, ref write, arrayOut, wordIndicesOut);
					i1++;
				} else {
					int v1 = array[i1];
					int v2 = array[i2];

					if (v1 < v2) {
						UpdateFromFirstHalf(v1, ref lastOffset, wordIndices, i1, ref lastWordEnd, ref write, arrayOut, wordIndicesOut);
						i1++;
					} else {
						UpdateFromSecondHalf(v2, ref lastOffset, ref lastWordEnd, ref write, arrayOut, wordIndicesOut);
						i2++;
					}
				}
			}

			return write;
		}

		public (int[], int count, int consumed) GetUniqueItemsFromRange(int begin, int end, int count) {
			int[] arr = new int[count];
			int[] words = new int[count];
			int[] arrBuffer = new int[count];
			int[] wordsBuffer = new int[count];
			int goodItems = 0;
			int takenItems = 0;

			int iteration = 0;

			while (true) {
				int itemsThisIt = count - goodItems;
				Array.Copy(SA, begin + takenItems, arr, goodItems, itemsThisIt);
				takenItems += itemsThisIt;
				Array.Sort(arr, goodItems, arr.Length - goodItems);
				
//				Console.WriteLine();
//				for (int i = 0; i < arr.Length; ++i) {
//					int suffix = arr[i];
//					Console.WriteLine($"{suffix}: {new StringView(Characters, suffix).Slice(20)}");
//				}

				if (goodItems > 0) {
//					int[] checkArr = new int[arr.Length];
//					int[] checkWords = new int[words.Length];
//					Array.Copy(arr, checkArr, arr.Length);
//					Array.Copy(words, checkWords, words.Length);
//					Array.Sort(checkArr);
//					int checkItems = MakeUniqueFull(checkArr, checkWords);
					
					goodItems = MakeUniqueMerge(arr, words, arrBuffer, wordsBuffer, goodItems);

					Algorithm.Swap(ref arr, ref arrBuffer);
					Algorithm.Swap(ref words, ref wordsBuffer);

//					if (checkItems != goodItems) {
//						int j = 0;
//					}

//					for (int i = 0; i < goodItems; ++i) {
//						if (checkArr[i] != arr[i]) {
//							Console.WriteLine($"Idx  {i}: {arr[i]} should be {checkArr[i]}");
//							for (int j = 0; j < goodItems; ++j) {
//								Console.WriteLine($"{j}: {arr[j]}   {checkArr[j]}");
//							}
//							int k = 0;
//						}
//
//						if (checkWords[i] != words[i]) {
//							Console.WriteLine($"Word {i}: {words[i]} should be {checkWords[i]}");
//							int j = 0;
//						}
//					}

				} else {
					goodItems = MakeUniqueFull(arr, words);
				}

//				Console.WriteLine();
//				Console.WriteLine("After unique");
//				for (int i = 0; i < goodItems; ++i) {
//					int suffix = arr[i];
//					Console.WriteLine($"{suffix}: {new StringView(Characters, suffix).Slice(20)}");
//				}
//				for (int i = 0; i < goodItems; ++i) {
//					Console.WriteLine($"\"{new StringView(Characters, WordStarts[words[i]]).Slice(20)}\"");
//				}
				int remainingItems = count - goodItems;
				++iteration;
				if (goodItems >= count || begin + takenItems + remainingItems > end)
					break;
			}

			if(iteration >= 10)
				Log.Info($"This query took many ({iteration}) iterations!");
			return (words, goodItems, takenItems);
		}

		public (int begin, int end) Find(string query) {
			var p = new StringView(query);
			int l = LowerBound(p, 0, CharacterCount);
			int u = UpperBound(p, 0, CharacterCount);
			return (l, u);
		}
	}
}
