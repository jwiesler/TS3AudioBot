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
using System.Net;
using TS3AudioBot.Config;
using TS3AudioBot.Web.WebSocket;
using TSLib.Audio;

namespace TS3AudioBot.Audio
{
	public class WebSocketPipe : IAudioPassiveConsumer
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		public bool Active => OutStream?.Active ?? false;
		public bool HasListeners => server.ConnectedClients.Count != 0;
		public int NumListeners => server.ConnectedClients.Count;
		public IList<string> Listeners => server.ConnectedClients.Select(kvPair => kvPair.Value.Uid).Distinct().ToList();
		public IAudioPassiveConsumer OutStream { get; set; }

		private readonly WebSocketServer server;

		public WebSocketPipe(ConfWebSocket confWebSocket) {
			server = new WebSocketServer(IPAddress.Loopback, 2020, confWebSocket);
		}

		public void Write(Span<byte> data, Meta meta) {
			foreach (var pair in server.ConnectedClients) {
				pair.Value.SendBytes(data.ToArray());
			}
		}
	}
}
