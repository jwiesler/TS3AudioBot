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
		public delegate void LogCallbackFunction(string msg);

		[DllImport(StrSearchLibrary, CallingConvention = CallingConvention.Cdecl)]
		private static extern unsafe IntPtr CreateSearchInstanceFromText(char *charactersBegin, ulong count, LogCallbackFunction callback);

		[DllImport(StrSearchLibrary, CallingConvention = CallingConvention.Cdecl)]
		public static extern void DestroySearchInstance(IntPtr instance);


		[DllImport(StrSearchLibrary, CallingConvention = CallingConvention.Cdecl)]
		private static extern unsafe Result FindUniqueItems(IntPtr instance, char *patternBegin, ulong count, int *output, ulong outputCount, ref FindUniqueItemsResult itemsResult, uint offset);

		[DllImport(StrSearchLibrary, CallingConvention = CallingConvention.Cdecl)]
		private static extern unsafe Result FindUniqueItemsKeywords(IntPtr instance, char *patternBegin, ulong count, int *output, ulong outputCount, ref FindUniqueItemsResult itemsResult);
		
		private static void LogCallback(string msg) { Log.Info("strsearch: " + msg); }

		public static unsafe IntPtr CreateSearchInstanceFromText(char[] characters, LogCallbackFunction callback) {
			fixed (char* charactersPtr = characters) {
				return CreateSearchInstanceFromText(charactersPtr, (ulong) characters.LongLength, callback);
			}
		}

		private static LocalStr FormatError(Result result) {
			return new LocalStr($"strsearch returned non success value {result}");
		}

		public static unsafe R<FindUniqueItemsResult, LocalStr> FindUniqueItemsKeywords(IntPtr instance, char[] pattern, int[] output) {
			var result = new FindUniqueItemsResult();
			fixed (char* patternPtr = pattern)
			fixed (int* outputPtr = output) {
				var res = FindUniqueItemsKeywords(instance, patternPtr, (ulong) pattern.LongLength, outputPtr, (ulong) output.LongLength, ref result);
				if (res == Result.Ok)
					return result;

				if (res == Result.OffsetOutOfBounds)
					return new LocalStr("Offset was out of bounds");
				return FormatError(res);
			}
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

		private readonly LogCallbackFunction log;
		private readonly IntPtr instance;
		
		public StrSearch(char[] characters) {
			log = LogCallback;
			instance = CreateSearchInstanceFromText(characters, log); 
		}

		public R<(int[] items, FindUniqueItemsResult result), LocalStr> FindUniqueItems(char[] pattern, int count, uint offset) {
			var items = new int[count];
			var res = FindUniqueItemsKeywords(instance, pattern, items);
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
