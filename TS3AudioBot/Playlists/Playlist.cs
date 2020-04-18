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
using TS3AudioBot.Localization;
using TSLib;

namespace TS3AudioBot.Playlists
{
	public class Playlist : IReadOnlyPlaylist
	{
		private const int MaxSongs = 1000;
		private string title;
		public string Title { get => title; set => SetTitle(value); }
		public bool Modifiable { get; set; } = false;

		private readonly HashSet<Uid> additionalEditors = new HashSet<Uid>();
		public IReadOnlyCollection<Uid> AdditionalEditors => additionalEditors;
		public Uid Owner { get; }

		private readonly List<PlaylistItem> items;
		public IReadOnlyList<PlaylistItem> Items => items;

		public PlaylistItem this[int i] => items[i];

		public Playlist(string title, Uid owner) :
			this(title, owner, Enumerable.Empty<Uid>())
		{ }

		public Playlist(string title, Uid owner, IEnumerable<Uid> editors) :
			this(title, owner, editors, new List<PlaylistItem>())
		{ }

		public Playlist(string title, Uid owner, IEnumerable<Uid> editors, List<PlaylistItem> items)
		{
			this.items = items ?? throw new ArgumentNullException(nameof(items));
			this.title = TransformTitleString(title);
			Owner = owner;
			additionalEditors = new HashSet<Uid>(editors);
		}

		public static string TransformTitleString(string title)
		{
			title = title.Replace("\r", "").Replace("\n", "");
			return title.Substring(0, Math.Min(title.Length, 256));
		}

		public Playlist SetTitle(string newTitle)
		{
			title = TransformTitleString(newTitle);
			return this;
		}

		private int GetMaxAdd(int amount)
		{
			int remainingSlots = Math.Max(MaxSongs - items.Count, 0);
			return Math.Min(amount, remainingSlots);
		}

		// Returns true if the specified editor is now an additional editor
		public bool ToggleAdditionalEditor(Uid editor) {
			if(editor == Owner)
				throw new ArgumentException("Owner can't be an additional editor");
			if (additionalEditors.Add(editor))
				return true;
			additionalEditors.Remove(editor);
			return false;
		}

		public bool HasAdditionalEditor(Uid editor) { return additionalEditors.Contains(editor); }

		public E<LocalStr> Add(PlaylistItem song)
		{
			if (GetMaxAdd(1) > 0)
			{
				items.Add(song);
				return R.Ok;
			}
			return ErrorFull;
		}

		public E<LocalStr> AddRange(IEnumerable<PlaylistItem> songs)
		{
			var maxAddCount = GetMaxAdd(MaxSongs);
			if (maxAddCount > 0)
			{
				items.AddRange(songs.Take(maxAddCount));
				return R.Ok;
			}
			return ErrorFull;
		}

		public void RemoveAt(int index) => items.RemoveAt(index);

		public E<LocalStr> Insert(int index, PlaylistItem song)
		{
			if (GetMaxAdd(1) > 0)
			{
				items.Insert(index, song);
				return R.Ok;
			}
			return ErrorFull;
		}

		public void Clear() => items.Clear();

		private static readonly E<LocalStr> ErrorFull = new LocalStr("Playlist is full");
	}

	public interface IReadOnlyPlaylist
	{
		PlaylistItem this[int i] { get; }
		string Title { get; }
		Uid Owner { get; }
		bool Modifiable { get; }
		IReadOnlyCollection<Uid> AdditionalEditors { get; }
		IReadOnlyList<PlaylistItem> Items { get; }
	}
}
