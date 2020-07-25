using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NLog;
using TS3AudioBot.Localization;

namespace TS3AudioBot.Search
{
	public class StrSearch : IDisposable {
		private static readonly Logger Log = LogManager.GetCurrentClassLogger();
		private const string StrSearchLibrary = "strsearch";

		private enum Result {
			Ok = 0,
			InvalidInstance = 1,
			NullPointer = 2,
			OffsetOutOfBounds
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct FindUniqueItemsResult {
			public readonly ulong TotalResults;
			public readonly ulong Count;
			public readonly ulong Consumed;
		};

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate void LogCallbackFunction(string msg);

		[DllImport(StrSearchLibrary, CallingConvention = CallingConvention.Cdecl)]
		private static extern unsafe IntPtr CreateSearchInstanceFromText(char *charactersBegin, ulong count, LogCallbackFunction callback);

		[DllImport(StrSearchLibrary, CallingConvention = CallingConvention.Cdecl)]
		public static extern void DestroySearchInstance(IntPtr instance);


		[DllImport(StrSearchLibrary, CallingConvention = CallingConvention.Cdecl)]
		private static extern unsafe Result FindUniqueItems(IntPtr instance, char *patternBegin, ulong count, int *output, ulong outputCount, ref FindUniqueItemsResult itemsResult, uint offset);
		
		private static void LogCallback(string msg) { Log.Info("strsearch: " + msg); }

		public static unsafe IntPtr CreateSearchInstanceFromText(char[] characters) {
			fixed (char* charactersPtr = characters) {
				return CreateSearchInstanceFromText(charactersPtr, (ulong) characters.LongLength, LogCallback);
			}
		}

		private static LocalStr FormatError(Result result) {
			return new LocalStr($"strsearch returned non success value {result}");
		}

		public static unsafe R<FindUniqueItemsResult, LocalStr> FindUniqueItems(IntPtr instance, char[] pattern, int[] output, uint offset) {
			var result = new FindUniqueItemsResult();
			fixed (char* patternPtr = pattern)
			fixed (int* outputPtr = output) {
				var res = FindUniqueItems(instance, patternPtr, (ulong) pattern.LongLength, outputPtr, (ulong) output.LongLength, ref result, offset);
				if (res == Result.Ok)
					return result;

				if (res == Result.OffsetOutOfBounds)
					return new LocalStr("Offset was out of bounds");
				return FormatError(res);
			}
		}

		private readonly IntPtr instance;

		public StrSearch(char[] characters) { instance = CreateSearchInstanceFromText(characters); }

		public R<(int[] items, FindUniqueItemsResult result), LocalStr> FindUniqueItems(char[] pattern, int count, uint offset) {
			var items = new int[count];
			var res = FindUniqueItems(instance, pattern, items, offset);
			if (!res.Ok)
				return res.Error;
			return (items, res.Value);
		}

		public void Dispose() {
			DestroySearchInstance(instance);
		}
	}

	public class StrSearchWrapper : IDisposable {
		public int CharacterCount { get; }
		public char[] Characters { get; }

		private readonly StrSearch instance;

		public StrSearchWrapper(ICollection<string> strings) {
			CharacterCount = strings.Sum(w => w.Length + 1);
			Characters = new char[CharacterCount];
			{
				int offset = 0;
				foreach (var str in strings) {
					foreach (var c in str) {
						Characters[offset++] = c;
					}

					++offset;
				}
			}

			instance = new StrSearch(Characters);
		}

		public R<(int[] items, StrSearch.FindUniqueItemsResult result), LocalStr> FindUniqueItems(char[] pattern, int count, uint offset) {
			return instance.FindUniqueItems(pattern, count, offset);
		}

		public void Dispose() {
			instance.Dispose();
		}
	}
}
