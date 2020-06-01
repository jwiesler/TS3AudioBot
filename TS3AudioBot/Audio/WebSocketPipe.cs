// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using TSLib.Audio;

namespace TS3AudioBot.Audio
{
	public class WebSocketPipe : IAudioPassiveConsumer
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		public bool Active => OutStream?.Active ?? false;
		public IAudioPassiveConsumer OutStream { get; set; }

		private int clientCounter;
		private readonly ConcurrentDictionary<int, WebSocketConnection> connectedClients;

		public WebSocketPipe() {
			clientCounter = 0;
			connectedClients = new ConcurrentDictionary<int, WebSocketConnection>();
			var websocketServerThread = new Thread(NewConnectionHandler) {
				IsBackground = true
			};
			websocketServerThread.Start();

			var clientWatcherThread = new Thread(() => {
				while (true) {
					IList<int> keysToRemove = new List<int>();

					foreach (var pair in connectedClients) {
						if (!pair.Value.IsRunning()) {
							keysToRemove.Add(pair.Key);
						}
					}

					foreach (var key in keysToRemove) {
						connectedClients.TryRemove(key, out var client);
						client.Stop();
					}

					Thread.Sleep(1000);
				}
			}) {
				IsBackground = true
			};
			clientWatcherThread.Start();
		}

		private void NewConnectionHandler() {
			TcpListener server = new TcpListener(IPAddress.Loopback, 2020);
			server.Start();

			while (true) {
				TcpClient client = server.AcceptTcpClient();

				var stream = client.GetStream();
				while (client.Available < 3) {
					Thread.Sleep(100);
				}

				var bytes = new byte[client.Available];
				stream.Read(bytes, 0, bytes.Length);

				// This is a textual request, decode it
				var request = Encoding.UTF8.GetString(bytes);

				// Check if this is a websocket handshake. If yes, respond.
				if (new Regex("^GET").IsMatch(request)) {
					const string eol = "\r\n";
					const string guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

					// HTML headers are case insensitive
					var match = new Regex("Sec-WebSocket-Key: (.*)", RegexOptions.IgnoreCase).Match(request);
					if (!match.Success) {
						Log.Error("Sec-WebSocket-Key was not found in request.");
						Log.Trace("Request was (base64-encoded): " + Convert.ToBase64String(Encoding.ASCII.GetBytes(request)));
						continue;
					}

					if (match.Groups.Count != 2) {
						Log.Error("While trying to find the Sec-WebSocket-Key, there was a wrong number of groups as result of the regex.");
						for (int i = 0; i < match.Groups.Count; i++) {
							Log.Trace($"Group {i}: " + new Regex("Sec-WebSocket-Key: (.*)").Match(request).Groups[i]);
						}
						continue;
					}
					string key = match.Groups[1].Value.Trim();
					byte[] hashedAcceptKey = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(key + guid));
					string base64AcceptKey = Convert.ToBase64String(hashedAcceptKey);

					Log.Trace("Received Sec-WebSocket-Key: " + key);
					if (Log.IsTraceEnabled) {
						StringBuilder hex = new StringBuilder(hashedAcceptKey.Length * 2);
						foreach (byte b in hashedAcceptKey) {
							hex.AppendFormat("{0:x2}", b);
						}
						Log.Trace("Hashed Sec-WebSocket-Key: " + hex);
					}

					Log.Trace("Base64 if Sec-WebSocket-Key Hash: " + base64AcceptKey);
					string responseStr = "HTTP/1.1 101 Switching Protocols" + eol
											+ "Connection: Upgrade" + eol
	                                        + "Upgrade: websocket" + eol
	                                        + "Sec-WebSocket-Accept: " + base64AcceptKey + eol + eol;
					byte[] response = Encoding.UTF8.GetBytes(responseStr);

					// Send handshake response
					stream.WriteTimeout = 10;
					stream.Write(response, 0, response.Length);

					// Start handler for the connection
					var handler = new WebSocketConnection(client);

					if (!connectedClients.TryAdd(clientCounter++, handler)) {
						handler.Stop();
					}
				}
			}
		}

		public void Write(Span<byte> data, Meta meta) {
			foreach (var pair in connectedClients) {
				pair.Value.Send(data.ToArray());
			}
		}

		private class WebSocketConnection {
			private const int KeepaliveTimeout = 5; // in seconds

			private readonly Thread receivedMessageHandlerThread;
			private readonly Thread keepaliveThread;
			private bool running;

			private readonly TcpClient client;
			private long waitingForPongSince;

			private readonly string logPrefix;

			public WebSocketConnection(TcpClient client) {
				running = true;

                this.client = client;
				receivedMessageHandlerThread = new Thread(ReceivedMessageHandler) {
					IsBackground = true
				};
				receivedMessageHandlerThread.Start();

				keepaliveThread = new Thread(KeepaliveHandler) {
					IsBackground = true
				};
				keepaliveThread.Start();
				waitingForPongSince = -1;

				logPrefix = $"[{GetClientId()}] ";
				Log.Info(logPrefix + "Established connection to new WebSocket client.");
			}

			public bool IsRunning() {
				return running;
			}

			public void Send(byte[] data) {
				if (!client.Connected) {
					return;
				}

				if (running) {
					ComposeAndSend(2, data);
				}
			}

			private bool ComposeAndSend(uint opcode, byte[] payload) {
				byte[] packet = ComposePacket(opcode, payload);
				if (packet == null) {
					return false;
				}

				try {
					var stream = client.GetStream();
					stream.WriteTimeout = 10;
					stream.Write(packet, 0, packet.Length);
				} catch (IOException) {
					// Connection reset by peer
					running = false;
					return false;
				}

				return true;
			}

			private byte[] ComposePacket(uint opcode, byte[] payload) {
				if (opcode > 15) {
					return null;
				}

				// Prepare websocket header
				var firstByte = Convert.ToByte(128 + opcode); // FIN + OpCode

				// Just the length, no mask bit set
				byte secondByte;
				byte[] lengthBytes = null;
				if (payload == null) {
					secondByte = 0;
				} else if (payload.Length < 126) {
					secondByte = (byte) payload.Length;
				} else if (payload.Length < Math.Pow(2, 16)) {
					secondByte = 126;
					lengthBytes = BitConverter.GetBytes((ushort) payload.Length);
				} else if (payload.Length < Math.Pow(2, 64)) {
					secondByte = 127;
					lengthBytes = BitConverter.GetBytes((ulong) payload.Length);
				} else {
					Log.Error("Audio chunk too long to send via websocket.");
					running = false;
					return null;
				}

				byte[] completeData = new byte[2 + (lengthBytes?.Length ?? 0) + (payload?.Length ?? 0)];
				completeData[0] = firstByte;
				completeData[1] = secondByte;

				int pos = 2;
				if (lengthBytes != null) {
					// To network byte order (BigEndian)
					if (BitConverter.IsLittleEndian) {
						Array.Reverse(lengthBytes);
					}

					for (int i = 0; i < lengthBytes.Length; i++) {
						completeData[i + pos] = lengthBytes[i];
					}

					pos += lengthBytes.Length;
				}

				if (payload != null) {
					for (uint i = 0; i < payload.Length; i++) {
						completeData[i + pos] = payload[i];
					}
				}

				return completeData;
			}

			private string GetClientId() {
				return ((IPEndPoint) client.Client.RemoteEndPoint).Port.ToString();
			}

			private void ReceivedMessageHandler() {
				while (running) {
					if (!client.Connected) {
						break;
					}

					// If there are less than 7 byte available, it cannot be a text message
					// (1 byte FIN + Opcode, 1 byte MASK + payload, 4 byte decoding mask, no payload)
					if (client.Available < 6) {
						Thread.Sleep(100);
						continue;
					}

					var stream = client.GetStream();
					var data = new byte[client.Available];
					stream.Read(data, 0, data.Length);

					while (data != null) {
						data = ProcessMessage(data);
					}
				}

                Log.Trace(logPrefix + "ReceivedMessageHandler exited");
			}

			private byte[] ProcessMessage(byte[] data) {
				if (data == null || data.Length < 6) {
					Log.Error("Tried to process null or length < 6 packet, that should not happen.");
					return null;
				}

				Log.Trace(logPrefix + $"{data.Length} {data[0]} {data[0] & 0b1111} {data[1]} {data[1] - 128}");
				Span<byte> dataAsSpan = data;

				// Check if fin bit (MSB) is set. If not, ignore the message.
				if ((data[0] & 128) == 0) {
					Log.Warn(logPrefix + "Fragmented WebSocket message received, ignoring...");
					return null;
				}

				// Check if the opcode (bits 4-7) is "text" (0x1). If not, ignore the message.
				int opcode = data[0] & 0b1111;
				Log.Trace(logPrefix + $"Message received, opcode {opcode}");
				switch (opcode) {
					case 0x1:
						// Text message, we can work with that
						break;
					case 0x8:
						// Connection close, answer and stop all concurrent client activity
						ComposeAndSend(8, null);
						running = false;
						return null;
					case 0xA:
						// Pong received, mark as done
						waitingForPongSince = -1;
						break;
					default:
						Log.Warn(logPrefix + $"Client sent unexpected message opcode {opcode}, ignoring...");
						return null;
				}

				// Check if the mask bit (MSB) is 1. If not, disconnect the client as it has misbehaved.
				if ((data[1] & 128) == 0) {
					Log.Error(logPrefix + "Client sent message with wrong mask bit, disconnecting...");
					running = false;
					return null;
				}

				// Get length
				ulong length;
				int position;
				switch (data[1] - 128) {
				case 0:
					// No payload. Nothing more to do.
					// Strip 6 bytes (FIN + OpCode, MASK + length, 4 byte decoding mask) and return remaining data.
					byte[] remainingData = dataAsSpan.Slice(6).ToArray();
					return remainingData.Length != 0 ? remainingData : null;
				case 126:
					if (data.Length < 9) {
						Log.Error(logPrefix + "Client sent message that is too short, disconnecting...");
						running = false;
						return null;
					}

					length = MemoryMarshal.Read<ulong>(dataAsSpan.Slice(2, 2));
					position = 4;
					break;
				case 127:
					if (data.Length < 15) {
						Log.Error(logPrefix + "Client sent message that is too short, disconnecting...");
						running = false;
						return null;
					}

					length = MemoryMarshal.Read<ulong>(dataAsSpan.Slice(2, 8));
					position = 10;
					break;
				default:
					length = Convert.ToUInt64(data[1] - 128); // Subtract the mask bit
					position = 2;
					break;
				}

				if (length > Math.Pow(2, 31) - 1) {
					Log.Error(logPrefix + "Client send message that has over 2^31 - 1 bytes of data, disconnecting...");
					running = false;
					return null;
				}
				int intLength = (int) length;

				// Get decoding mask
				byte[] decodingMask = dataAsSpan.Slice(position, 4).ToArray();
				position += 4;

				// Check payload length information
				byte[] encodedMessage = dataAsSpan.Slice(position, intLength).ToArray();
				if ((ulong) encodedMessage.Length != length) {
					Log.Error(logPrefix + "Client sent wrong length information, disconnecting...");
					running = false;
					return null;
				}

				// Decode message
				byte[] decodedMessage = new byte[length];
				for (uint i = 0; i < length; i++) {
					decodedMessage[i] = (byte) (encodedMessage[i] ^ decodingMask[i % 4]);
				}

				Log.Info(logPrefix + $"Recieved text message: {Encoding.UTF8.GetString(decodedMessage)}");

				// Return remaining bytes if any
				if (position + intLength < dataAsSpan.Length) {
					return dataAsSpan.Slice(position + intLength).ToArray();
				}
				return null;
			}

			private void KeepaliveHandler() {
				long lastKeepalive = 0;
				while (running) {
					var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
					if (currentTime - lastKeepalive > 30) {
						if (ComposeAndSend(9, null)) {
							waitingForPongSince = currentTime;
						} else {
							Log.Error(logPrefix + "Sending keepalive failed, disconnecting...");
							running = false;
							return;
						}

						lastKeepalive = currentTime;
					}

					if (waitingForPongSince > 0 && currentTime - waitingForPongSince > KeepaliveTimeout) {
						// Timeout
						Log.Info(logPrefix + "WebSocket client did not respond to PING in time, disconnecting...");
						running = false;
						return;
					}

					Thread.Sleep(1000);
				}

                Log.Trace(logPrefix + "KeepaliveHandler exited");
			}

			public void Stop() {
				running = false;
				Log.Trace(logPrefix + "Joining threads...");
				receivedMessageHandlerThread.Join();
				Log.Trace(logPrefix + "Joined receiver thread");
				keepaliveThread.Join();
				Log.Trace(logPrefix + "Joined keepalive thread");
				client.Close();
				Log.Info(logPrefix + "WebSocket client disconnected.");
			}
		}
	}
}
