// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using TSLib;

namespace TS3AudioBot.Audio
{
	public sealed class MetaData
	{
		/// <summary>Defaults to: invoker.Uid - Can be set if the owner of a song differs from the invoker.</summary>
		public Uid? ResourceOwnerUid { get; }
		public string ContainingPlaylistId { get; }
		/// <summary></summary>
		public TimeSpan? StartOffset { get; }

		public MetaData(Uid? resourceOwnerUid, string containingPlaylistId = null, TimeSpan? startOffset = null) {
			ResourceOwnerUid = resourceOwnerUid;
			ContainingPlaylistId = containingPlaylistId;
			StartOffset = startOffset;

		}

		public override string ToString() { return $"{ResourceOwnerUid}-{ContainingPlaylistId}@{StartOffset}"; }
	}
}
