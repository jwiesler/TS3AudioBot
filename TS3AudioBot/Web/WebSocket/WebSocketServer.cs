using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using JsonWebToken;
using Newtonsoft.Json;
using TS3AudioBot.Config;

namespace TS3AudioBot.Web.WebSocket {
	public class WebSocketServer : IDisposable {
		private static int clientCounter;
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private static readonly	byte[] AccessDeniedResponse = Encoding.UTF8.GetBytes(
			"HTTP/1.1 401\r\n" +
			"status: 401 Unauthorized\r\n" +
			"content-length: 52\r\n\r\n" +
			"You are not authorized to connect to this websocket."
		);

		public readonly ConcurrentDictionary<int, WebSocketConnection> ConnectedClients;

		private readonly ConfWebSocket confWebSocket;
		private readonly IPAddress ip;
		private readonly ushort port;
		private readonly Thread newConnectionHandlerThread;

		private bool running;

		public WebSocketServer(IPAddress ip, ushort port, ConfWebSocket confWebSocket) {
			this.ip = ip ?? throw new ArgumentException("No IP address provided.");

			if (port == 0) {
				throw new ArgumentException("Invalid port 0 provided.");
			}
			this.port = port;

			this.confWebSocket = confWebSocket;
			if (this.confWebSocket.JwsKey == "DefaultKey") {
				Log.Warn("Default key in use for websocket JWS. This might not even work and is at least a bad idea.");
			}

			running = true;
			ConnectedClients = new ConcurrentDictionary<int, WebSocketConnection>();
			newConnectionHandlerThread = new Thread(NewConnectionHandler) {
				IsBackground = true
			};
			newConnectionHandlerThread.Start();

			var clientWatcherThread = new Thread(() => {
				while (running) {
					IList<int> keysToRemove = new List<int>();

					foreach (var pair in ConnectedClients) {
						if (!pair.Value.IsRunning()) {
							keysToRemove.Add(pair.Key);
						}
					}

					foreach (var key in keysToRemove) {
						ConnectedClients.TryRemove(key, out var client);
						client?.Stop();
					}

					Thread.Sleep(1000);
				}
			}) {
				IsBackground = true
			};
			clientWatcherThread.Start();
		}

		private void InvalidMatchNumber(Match match, Stream stream) {
			Log.Error("While trying to find the Sec-WebSocket-Key, there was a wrong number of groups as result of the regex.");
			for (int i = 0; i < match.Groups.Count; i++) {
				Log.Trace($"Group {i}: " + match.Groups[i]);
			}
			stream.Write(AccessDeniedResponse, 0, AccessDeniedResponse.Length);
		}

		private void NewConnectionHandler() {
			var server = new TcpListener(ip, port);
			server.Start();

			var policy = new TokenValidationPolicyBuilder()
				.RequireSignature(SymmetricJwk.FromBase64Url(confWebSocket.JwsKey), SignatureAlgorithm.HmacSha512)
				.RequireIssuer("Leierkasten Backend")
				.Build();
			var reader = new JwtReader();

			while (running) {
				TcpClient client;
				if (server.Pending()) {
					client = server.AcceptTcpClient();
				} else {
					Thread.Sleep(100);
					continue;
				}

				var stream = client.GetStream();
				stream.WriteTimeout = 10;

				while (client.Available < 3) {
					Thread.Sleep(100);
				}

				var bytes = new byte[client.Available];
				stream.Read(bytes, 0, bytes.Length);

				// This is a textual request, decode it
				var request = Encoding.UTF8.GetString(bytes);

				// Check if this is a websocket handshake. If no, skip.
				if (!new Regex("^GET").IsMatch(request)) {
					stream.Write(AccessDeniedResponse, 0, AccessDeniedResponse.Length);
					continue;
				}

				const string eol = "\r\n";
				const string guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

				// HTML headers are case insensitive
				var match = new Regex("Sec-WebSocket-Key: (.*)", RegexOptions.IgnoreCase).Match(request);
				if (!match.Success) {
					Log.Error("Sec-WebSocket-Key was not found in request.");
					Log.Trace("Request was (base64-encoded): " + Convert.ToBase64String(Encoding.ASCII.GetBytes(request)));
					stream.Write(AccessDeniedResponse, 0, AccessDeniedResponse.Length);
					continue;
				}

				if (match.Groups.Count != 2) {
					InvalidMatchNumber(match, stream);
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

				// Get cookie
				match = new Regex("lk-session=([^;\r\n]*)", RegexOptions.IgnoreCase).Match(request);
				if (!match.Success) {
					stream.Write(AccessDeniedResponse, 0, AccessDeniedResponse.Length);
					continue;
				}

				if (match.Groups.Count != 2) {
					InvalidMatchNumber(match, stream);
					continue;
				}

				// Validate session cookie of user
				var sessionCookie = match.Groups[1].ToString();
				var result = reader.TryReadToken(sessionCookie, policy);
				if (!result.Succedeed) {
					stream.Write(AccessDeniedResponse, 0, AccessDeniedResponse.Length);
					continue;
				}

				if (result.Token?.Payload == null) {
					stream.Write(AccessDeniedResponse, 0, AccessDeniedResponse.Length);
					continue;
				}

				var uid = JsonConvert.DeserializeObject<Dictionary<string, string>>(result.Token.Payload.ToString())["uid"];

				// Send handshake response
				stream.Write(response, 0, response.Length);

				// Start handler for the connection
				var handler = new WebSocketConnection(client, uid);

				if (!ConnectedClients.TryAdd(clientCounter++, handler)) {
					handler.Stop();
				}
			}

			server.Stop();
		}

		public void Dispose() {
			running = false;
			newConnectionHandlerThread.Join();
			Log.Trace($"Joined WebSocket server {ip}:{port}.");

			foreach (var client in ConnectedClients) {
				client.Value.Stop();
			}
			Log.Trace("Stopped all remaining client connections.");
		}
	}
}
