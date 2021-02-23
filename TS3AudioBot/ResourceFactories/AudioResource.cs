// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using Newtonsoft.Json;
using System.Collections.Generic;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem.CommandResults;
namespace TS3AudioBot.ResourceFactories
{
	public class PlayResource
	{
		public AudioResource BaseData { get; set; }
		public string PlayUri { get; }
		public MetaData Meta { get; set; }

		public PlayResource(string uri, AudioResource baseData, MetaData meta = null)
		{
			BaseData = baseData;
			PlayUri = uri;
			Meta = meta;
		}

		public override string ToString() => BaseData.ToString();
	}

	public class UniqueResource {
		[JsonProperty(PropertyName = "type")]
		public string AudioType { get; }

		[JsonProperty(PropertyName = "resid")]
		public string ResourceId { get; }

		[JsonProperty(PropertyName = "title")]
		public string ResourceTitle { get; }

		public UniqueResource(string resourceId, string resourceTitle, string audioType) {
			AudioType = audioType;
			ResourceId = resourceId;
			ResourceTitle = resourceTitle;
		}

		public override bool Equals(object obj) {
			if (ReferenceEquals(null, obj)) return false;
			if (ReferenceEquals(this, obj)) return true;
			if (obj.GetType() != this.GetType()) return false;
			return Equals((UniqueResource) obj);
		}

		protected bool Equals(UniqueResource other) {
			return AudioType == other.AudioType && ResourceId == other.ResourceId && ResourceTitle == other.ResourceTitle;
		}

		public override int GetHashCode() {
			unchecked {
				var hashCode = (AudioType != null ? AudioType.GetHashCode() : 0);
				hashCode = (hashCode * 397) ^ (ResourceId != null ? ResourceId.GetHashCode() : 0);
				hashCode = (hashCode * 397) ^ (ResourceTitle != null ? ResourceTitle.GetHashCode() : 0);
				return hashCode;
			}
		}

		public override string ToString() { return $"{AudioType} ID:{ResourceId}"; }
	}

	public class AudioResource : UniqueResource, IAudioResourceResult
	{
		[JsonProperty(PropertyName = "isusertitle", NullValueHandling = NullValueHandling.Ignore)]
		public bool? TitleIsUserSet { get; }

		[JsonProperty(PropertyName = "gain", NullValueHandling = NullValueHandling.Ignore)]
		public int? Gain { get; }

		/// <summary>Additional data to resolve the link.</summary>
		[JsonProperty(PropertyName = "add", NullValueHandling = NullValueHandling.Ignore)]
		public Dictionary<string, string> AdditionalData { get; }

		/// <summary>An identifier which is unique among all <see cref="AudioResource"/> and resource type string of a factory.</summary>
		[JsonIgnore]
		public string UniqueId => ResourceId + AudioType;

		[JsonIgnore]
		AudioResource IAudioResourceResult.AudioResource => this;

		[JsonConstructor]
		public AudioResource(
			string resourceId, string resourceTitle = null, string audioType = null,
			Dictionary<string, string> additionalData = null,
			bool? titleIsUserSet = null, int? gain = null
		) : base(resourceId, resourceTitle, audioType) {
			AdditionalData = additionalData;
			TitleIsUserSet = titleIsUserSet;
			Gain = gain;
		}

		public string Get(string key)
		{
			if (AdditionalData == null)
				return null;
			return AdditionalData.TryGetValue(key, out var value) ? value : null;
		}

		public AudioResource DeepCopy() {
			return new AudioResource(
				ResourceId, ResourceTitle, AudioType,
				AdditionalData != null ? new Dictionary<string, string>(AdditionalData) : null,
				TitleIsUserSet,
				Gain
			);
		}

		public AudioResource WithTitle(string newInfoTitle) {
			return new AudioResource(ResourceId, newInfoTitle, AudioType, AdditionalData, TitleIsUserSet, Gain);
		}

		public AudioResource WithUserTitle(string title) {
			return new AudioResource(ResourceId, title, AudioType, AdditionalData, true, Gain);
		}

		public AudioResource WithAudioType(string audioType) {
			return new AudioResource(ResourceId, ResourceTitle, audioType, AdditionalData, TitleIsUserSet, Gain);
		}

		public AudioResource WithGain(int? gain) {
			return new AudioResource(ResourceId, ResourceTitle, AudioType, AdditionalData, TitleIsUserSet, gain);
		}

		public AudioResource WithNewAdditionalData() {
			return new AudioResource(ResourceId, ResourceTitle, AudioType, new Dictionary<string, string>(), TitleIsUserSet, Gain);
		}

		public bool ReallyEquals(AudioResource other) {
			if (ReferenceEquals(null, other)) {
				return false;
			}

			if (ReferenceEquals(this, other)) {
				return true;
			}

			if (!Equals(other) || Gain != other.Gain || TitleIsUserSet != other.TitleIsUserSet) {
				return false;
			}

			if (AdditionalData == null && other.AdditionalData != null) {
				return false;
			}

			if (AdditionalData != null && other.AdditionalData == null) {
				return false;
			}

			// Both are null (see checks above). If I use &&, static code analysis is unhappy for some weird reason.
			if (AdditionalData == null || other.AdditionalData == null) {
				return true;
			}

			if (AdditionalData.Count != other.AdditionalData.Count) {
				return false;
			}

			if (new HashSet<string>(AdditionalData.Keys).Equals(new HashSet<string>(other.AdditionalData.Keys))) {
				// Key sets are not the same.
				return false;
			}

			foreach (var key in AdditionalData.Keys) {
				if (AdditionalData[key] != other.AdditionalData[key]) {
					// There is a key-value-pair that does not match.
					return false;
				}
			}

			// No need to compare values in the other direction as the key sets are the same.
			return true;
		}
	}
}
