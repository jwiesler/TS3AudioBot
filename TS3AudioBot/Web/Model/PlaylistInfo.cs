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
using Newtonsoft.Json;

namespace TS3AudioBot.Web.Model
{
	public class PlaylistInfo : IComparable
	{
		// TODO better names
		[JsonProperty(PropertyName = "Id")]
		public string Id { get; set; }
		[JsonProperty(PropertyName = "Owner")]
		public string OwnerId { get; set; }
		[JsonProperty(PropertyName = "Modifiable")]
		public bool Modifiable { get; set; }
		[JsonProperty(PropertyName = "AdditionalEditors")]
		public List<string> AdditionalEditors { get; set; }

		/// <summary>How many songs are in the entire playlist</summary>
		[JsonProperty(PropertyName = "SongCount")]
		public int SongCount { get; set; }
		/// <summary>From which index the itemization begins.</summary>
		[JsonProperty(PropertyName = "DisplayOffset")]
		public int DisplayOffset { get; set; }
		/// <summary>The playlist items for the request.
		/// This might only be a part of the entire playlist.
		/// Check <see cref="SongCount"> for the entire count.</summary>
		[JsonProperty(PropertyName = "Items", NullValueHandling = NullValueHandling.Ignore)]
		public PlaylistItemGetData[] Items { get; set; }

		public int CompareTo(object obj) {
			if (obj == null) {
				return 1;
			}

			if (!(obj is PlaylistInfo info)) {
				throw new ArgumentException("Can't compare to a non-PlaylistInfo object.");
			}

			return StringComparer.OrdinalIgnoreCase.Compare(Id, info.Id);
		}
	}
}
