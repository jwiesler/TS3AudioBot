using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NLog;
using NLog.Fluent;
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

		public enum KeywordsMatch {
			All,
			AtLeastOne
		};

		[StructLayout(LayoutKind.Sequential)]
		public struct FindUniqueItemsResult {
			public readonly ulong TotalResults;
			public readonly ulong Count;
			public readonly ulong Consumed;
		};

		[StructLayout(LayoutKind.Sequential)]
		public struct FindUniqueItemsTimings {
			public readonly long Find;
			public readonly long Unique;
		};

		[StructLayout(LayoutKind.Sequential)]
		public struct FindUniqueItemsKeywordsTimings {
			public readonly long Find;
			public readonly long Unique;
			public readonly long Parse;
		};

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		public delegate void LogCallbackFunction(string msg);

		[DllImport(StrSearchLibrary, CallingConvention = CallingConvention.Cdecl)]
		private static extern unsafe IntPtr CreateSearchInstanceFromText(char *charactersBegin, ulong count, LogCallbackFunction callback);

		[DllImport(StrSearchLibrary, CallingConvention = CallingConvention.Cdecl)]
		public static extern void DestroySearchInstance(IntPtr instance);


		[DllImport(StrSearchLibrary, CallingConvention = CallingConvention.Cdecl)]
		private static extern unsafe Result FindUniqueItems(IntPtr instance, char *patternBegin, ulong count, int *output, ulong outputCount, ref FindUniqueItemsResult itemsResult, uint offset, ref FindUniqueItemsTimings timing);

		[DllImport(StrSearchLibrary, CallingConvention = CallingConvention.Cdecl)]
		private static extern unsafe Result FindUniqueItemsKeywords(IntPtr instance, char *patternBegin, ulong count, int *output, ulong outputCount, KeywordsMatch matching, uint offset, ref FindUniqueItemsResult itemsResult, ref FindUniqueItemsKeywordsTimings timings);

		private static void LogCallback(string msg) { Log.Info("strsearch: " + msg); }

		public static unsafe IntPtr CreateSearchInstanceFromText(char[] characters, LogCallbackFunction callback) {
			fixed (char* charactersPtr = characters) {
				return CreateSearchInstanceFromText(charactersPtr, (ulong) characters.LongLength, callback);
			}
		}

		private static LocalStr FormatError(Result result) {
			if (result == Result.OffsetOutOfBounds)
				return new LocalStr("Offset was out of bounds");
			return new LocalStr($"strsearch returned non success value {result}");
		}

		public static unsafe R<FindUniqueItemsResult, LocalStr> FindUniqueItemsKeywords(IntPtr instance, char[] pattern, int[] output, KeywordsMatch matching, uint offset) {
			var result = new FindUniqueItemsResult();
			var timings = new FindUniqueItemsKeywordsTimings();
			fixed (char* patternPtr = pattern)
			fixed (int* outputPtr = output) {
				var res = FindUniqueItemsKeywords(instance, patternPtr, (ulong) pattern.LongLength, outputPtr, (ulong) output.LongLength, matching, offset, ref result, ref timings);
				if (res != Result.Ok)
					return FormatError(res);

				Log.Info($"Find unique items keywords timings: parse: {timings.Parse}ns, find: {timings.Find}ns, unique: {timings.Unique}ns");
				return result;
			}
		}

		public static unsafe R<FindUniqueItemsResult, LocalStr> FindUniqueItems(IntPtr instance, char[] pattern, int[] output, uint offset) {
			var result = new FindUniqueItemsResult();
			var timings = new FindUniqueItemsTimings();
			fixed (char* patternPtr = pattern)
			fixed (int* outputPtr = output) {
				var res = FindUniqueItems(instance, patternPtr, (ulong) pattern.LongLength, outputPtr, (ulong) output.LongLength, ref result, offset, ref timings);
				if (res != Result.Ok)
					return FormatError(res);

				Log.Info($"Find unique items timings: find: {timings.Find}ns, unique: {timings.Unique}ns");
				return result;
			}
		}

		private readonly LogCallbackFunction log;
		private readonly IntPtr instance;

		public StrSearch(char[] characters) {
			log = LogCallback;
			instance = CreateSearchInstanceFromText(characters, log);
		}

		public R<(int[] items, FindUniqueItemsResult result), LocalStr> FindUniqueItems(char[] pattern, int count, uint offset) {
			var items = new int[count];
			var res = FindUniqueItems(instance, pattern, items, offset);
			if (!res.Ok)
				return res.Error;
			return (items, res.Value);
		}

		public R<(int[] items, FindUniqueItemsResult result), LocalStr> FindUniqueItemsKeywords(char[] pattern, int count, KeywordsMatch matching, uint offset) {
			var items = new int[count];
			var res = FindUniqueItemsKeywords(instance, pattern, items, matching, offset);
			if (!res.Ok)
				return res.Error;
			return (items, res.Value);
		}

		public void Dispose() {
			DestroySearchInstance(instance);
		}
	}

	public class StrSearchWrapper : IDisposable {
		private static readonly Logger Log = LogManager.GetCurrentClassLogger();

		public int CharacterCount { get; }
		public char[] Characters { get; }

		private readonly StrSearch instance;

		public StrSearchWrapper(ICollection<string> strings) {
			CharacterCount = strings.Sum(w => w.Length + 1);

			Log.Trace($"Creating character array with {CharacterCount} characters...");
			Characters = new char[CharacterCount];

			Log.Trace("Filling character array...");
			int offset = 0;
			foreach (var str in strings) {
				foreach (var c in str) {
					Characters[offset++] = c;
				}

				++offset;
			}

			Log.Trace("Creating StrSearch object...");
			instance = new StrSearch(Characters);
		}

		public R<(int[] items, StrSearch.FindUniqueItemsResult result), LocalStr> FindUniqueItems(char[] pattern, int count, uint offset) {
			return instance.FindUniqueItems(pattern, count, offset);
		}

		public R<(int[] items, StrSearch.FindUniqueItemsResult result), LocalStr> FindUniqueItemsKeywords(char[] pattern, int count, StrSearch.KeywordsMatch matching, uint offset) {
			return instance.FindUniqueItemsKeywords(pattern, count, matching, offset);
		}

		public void Dispose() {
			instance.Dispose();
		}
	}
}
