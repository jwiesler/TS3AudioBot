// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections.Generic;
using System.Linq;

namespace TS3AudioBot.Algorithm
{
	public interface IFilter
	{
		IEnumerable<KeyValuePair<string, T>> Filter<T>(IEnumerable<KeyValuePair<string, T>> list, string filter);
	}

	public static class Filter
	{
		public static IFilter DefaultFilter { get; } = Ic3Filter.Instance;

		public static IFilter GetFilterByName(string filter)
		{
			switch (filter)
			{
			case "exact": return ExactFilter.Instance;
			case "substring": return SubstringFilter.Instance;
			case "ic3": return Ic3Filter.Instance;
			case "hamming": return HammingFilter.Instance;
			case "levenshtein": return LevenshteinFilter.Instance;
			default: return null;
			}
		}

		public static IFilter GetFilterByNameOrDefault(string filter) => GetFilterByName(filter) ?? DefaultFilter;
	}

	/// <summary>Interleaved continuous character chain.</summary>
	internal sealed class Ic3Filter : IFilter
	{
		private Ic3Filter() { }

		public static IFilter Instance { get; } = new Ic3Filter();

		IEnumerable<KeyValuePair<string, T>> IFilter.Filter<T>(IEnumerable<KeyValuePair<string, T>> list, string filter)
		{
			// Convert result to list because it can be enumerated multiple times
			var possibilities = list.Select(t => (Name: t.Key, Value: t.Value, Index: 0)).ToList();
			// Filter matching commands
			foreach (var c in filter.ToLowerInvariant())
			{
				var newPossibilities = (from p in possibilities
										let pos = p.Name.ToLowerInvariant().IndexOf(c, p.Index)
										where pos != -1
										select (p.Name, p.Value, pos + 1)).ToList();
				if (newPossibilities.Count > 0)
					possibilities = newPossibilities;
			}
			// Take command with lowest index
			int minIndex = possibilities.Min(t => t.Index);
			var cmds = possibilities.Where(t => t.Index == minIndex).ToArray();
			// Take the smallest command
			int minLength = cmds.Min(c => c.Name.Length);

			return cmds.Where(c => c.Name.Length == minLength).Select(fi => new KeyValuePair<string, T>(fi.Name, fi.Value));
		}
	}

	internal class LevenshteinFilter : IFilter {
		private LevenshteinFilter() { }

		public static IFilter Instance { get; } = new LevenshteinFilter();

		private static int CalcLevenshteinDistance(string a, string b) {
			if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) {
				return 0;
			}

			if (string.IsNullOrEmpty(a)) {
				return b.Length;
			}

			if (string.IsNullOrEmpty(b)) {
				return a.Length;
			}

			int lengthA = a.Length;
			int lengthB = b.Length;
			var distances = new int[lengthA + 1, lengthB + 1];
			for (int i = 0; i <= lengthA; distances[i, 0] = i++) ;
			for (int j = 0; j <= lengthB; distances[0, j] = j++) ;

			for (int i = 1; i <= lengthA; i++)
			for (int j = 1; j <= lengthB; j++) {
				int cost = b[j - 1] == a[i - 1] ? 0 : 1;
				distances[i, j] = Math.Min
				(
					Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
					distances[i - 1, j - 1] + cost
				);
			}

			return distances[lengthA, lengthB];
		}

		IEnumerable<KeyValuePair<string, T>> IFilter.Filter<T>(IEnumerable<KeyValuePair<string, T>> list, string filter)
		{
			int min = Int32.MaxValue;
			List<KeyValuePair<string, T>> mins = new List<KeyValuePair<string, T>>();
			foreach(var pair in list) {
				int value = CalcLevenshteinDistance(filter, pair.Key);
				if (min < value)
					continue;
				if (value < min) {
					min = value;
					mins.Clear();
				}
				mins.Add(pair);
			}
			return 3 < min ? Enumerable.Empty<KeyValuePair<string, T>>() : mins;
		}
	}

	internal sealed class ExactFilter : IFilter
	{
		private ExactFilter() { }

		public static IFilter Instance { get; } = new ExactFilter();

		IEnumerable<KeyValuePair<string, T>> IFilter.Filter<T>(IEnumerable<KeyValuePair<string, T>> list, string filter)
		{
			return list.Where(x => x.Key == filter);
		}
	}

	internal sealed class HammingFilter : IFilter
	{
		private HammingFilter() { }

		public static IFilter Instance { get; } = new HammingFilter();

		IEnumerable<KeyValuePair<string, T>> IFilter.Filter<T>(IEnumerable<KeyValuePair<string, T>> list, string filter)
		{
			throw new System.NotImplementedException();
		}
	}

	internal sealed class SubstringFilter : IFilter
	{
		private SubstringFilter() { }

		public static IFilter Instance { get; } = new SubstringFilter();

		IEnumerable<KeyValuePair<string, T>> IFilter.Filter<T>(IEnumerable<KeyValuePair<string, T>> list, string filter)
		{
			var result = list.Where(x => x.Key.StartsWith(filter));
			using (var enu = result.GetEnumerator())
			{
				if (!enu.MoveNext())
					yield break;
				yield return enu.Current;
				if (enu.Current.Key == filter)
					yield break;
				while (enu.MoveNext())
					yield return enu.Current;
			}
		}
	}
}
