// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TS3AudioBot.Algorithm;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;
using TSLib;

namespace TS3AudioBot.Playlists
{
	public interface IPlaylistEditors {
		Uid Owner { get; }
		IReadOnlyCollection<Uid> AdditionalEditors { get; }
		bool ToggleAdditionalEditor(Uid uid);
	}

	public interface IPlaylist : IPlaylistEditors, IReadOnlyList<AudioResource> {}

	public class PlaylistEditorsBase : IPlaylistEditors {
		public Uid Owner { get; }

		private readonly HashSet<Uid> additionalEditors;
		public IReadOnlyCollection<Uid> AdditionalEditors => additionalEditors;
		
		public PlaylistEditorsBase(Uid owner, HashSet<Uid> additionalEditors) {
			Owner = owner;
			this.additionalEditors = additionalEditors;
		}

		public PlaylistEditorsBase(Uid owner, IEnumerable<Uid> additionalEditors) : this(owner, new HashSet<Uid>(additionalEditors)) {}

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
	}

	public class Playlist : PlaylistEditorsBase, IPlaylist
	{
		private const int MaxSongs = 2500;

		public List<AudioResource> ItemsW { get; }
		public IEnumerable<AudioResource> Items => ItemsW;
		public AudioResource this[int i] => ItemsW[i];
		public int Count => ItemsW.Count;

		public Playlist(Uid owner) :
			this(owner, Enumerable.Empty<Uid>())
		{ }

		public Playlist(Uid owner, IEnumerable<Uid> editors) :
			this(owner, editors, new List<AudioResource>())
		{ }

		public Playlist(Uid owner, IEnumerable<Uid> editors, List<AudioResource> items)  : base(owner, editors)
		{
			ItemsW = items ?? throw new ArgumentNullException(nameof(items));
		}

		private int GetMaxAdd(int amount)
		{
			int remainingSlots = Math.Max(MaxSongs - Count, 0);
			return Math.Min(amount, remainingSlots);
		}

		public E<LocalStr> Add(AudioResource song)
		{
			if (GetMaxAdd(1) > 0)
			{
				ItemsW.Add(song);
				return R.Ok;
			}
			return ErrorFull;
		}

		public E<LocalStr> AddRange(IEnumerable<AudioResource> songs)
		{
			var maxAddCount = GetMaxAdd(MaxSongs);
			if (maxAddCount > 0)
			{
				ItemsW.AddRange(songs.Take(maxAddCount));
				return R.Ok;
			}
			return ErrorFull;
		}

		public void RemoveAt(int index) => ItemsW.RemoveAt(index);

		public void RemoveIndices(IList<int> indices) => Collections.RemoveIndices(ItemsW, indices);

		public void RemoveIndices(IList<int> indices, int ibegin, int iend) => Collections.RemoveIndices(ItemsW, indices, ibegin, iend);

		public E<LocalStr> Insert(int index, AudioResource song)
		{
			if (GetMaxAdd(1) > 0)
			{
				ItemsW.Insert(index, song);
				return R.Ok;
			}
			return ErrorFull;
		}

		private static readonly E<LocalStr> ErrorFull = new LocalStr("Playlist is full");
		public IEnumerator<AudioResource> GetEnumerator() { return Items.GetEnumerator(); }
		IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
	}
}
