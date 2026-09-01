using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CSharp_SMTP_Server.Protocol;
using CSharp_SMTP_Server.Protocol.Commands;
using CSharp_SMTP_Server.Protocol.Responses;
using static System.FormattableString;

namespace CSharp_SMTP_Server.Networking
{
	internal class ClientProcessor : IDisposable
	{
		private static readonly LingerOption Reset = new (true, 0);

		internal ClientProcessor(TcpClient c, Listener l, bool secure)
		{
			_t = _ts.Token;
			_listener = l;
			_client = c;
			_innerStream = c.GetStream();
			_stream = _innerStream;
			if (_client.Client.RemoteEndPoint is IPEndPoint ipe)
				RemoteEndPoint = ipe;
			Encryption = ConnectionEncryption.Plaintext;
			Secure = secure && Server.Certificate != null;

			if (Server.SpfValidator != null)
				SpfResultsCache = new();

			// Run Init on the thread pool. If the greeting write completes synchronously (typical for a
			// small write into an empty socket buffer), Init would otherwise run inline in this ctor and
			// block the caller inside Receive()'s EndOfStream check until the client sends data or goes
			// away. For connections accepted by Listener that parks the accept loop, so a second client
			// can never be greeted — the server would handle one connection at a time.
			_ = Task.Run(Init);
		}

		internal readonly Dictionary<string, ValidationResult>? SpfResultsCache;

		private readonly CancellationTokenSource _ts = new();
		private readonly CancellationToken _t;
		internal CancellationToken ConnectionToken => _t;

		internal readonly IPEndPoint? RemoteEndPoint;

		private readonly TcpClient _client;
		private readonly NetworkStream _innerStream;
		private Stream _stream;
		private BoundedLineReader? _reader;
		private readonly Listener _listener;
		private bool _greetSent;
		private int _fails;

		internal MailTransaction? Transaction;
		internal ulong Counter;

		/// <summary>
		/// Whether any line of the current message exceeded <see cref="BoundedLineReader.MaxLineLength"/>
		/// and had its tail discarded.
		/// </summary>
		/// <remarks>
		/// <para>
		/// The reader truncates an over-long line to bound memory against a client that never sends a
		/// terminator, and reports it through <see cref="BoundedLineReader.LastLineTruncated"/>. That
		/// signal has to be acted on: <c>ProcessData</c> only ever sees the retained prefix, so it
		/// counts the prefix against <see cref="ServerOptions.MessageCharactersLimit"/> and stores the
		/// prefix. Left unconsumed, a 5 MB DATA line was silently delivered as its first 1 MB with a
		/// <c>250</c> — an acknowledged, corrupted message, which for a journaling relay is worse than
		/// a refusal because nothing signals the loss.
		/// </para>
		/// <para>
		/// Reset when DATA capture begins, and cleared with the transaction.
		/// </para>
		/// </remarks>
		internal bool DataTruncated;

		/// <summary>
		/// Ends the current transaction, releasing the storage its body holds.
		/// </summary>
		/// <remarks>
		/// Clearing <see cref="Transaction"/> alone is no longer enough: a body past the spill
		/// threshold owns a temp file, and dropping the reference would leave it open until
		/// finalization. Every path that abandons a transaction — an over-limit message, a policy
		/// rejection, RSET, connection teardown — goes through here. The delivery path is the one
		/// exception: it hands the body to the clone, which disposes it once the handler returns.
		/// </remarks>
		internal void DiscardTransaction()
		{
			var transaction = Transaction;
			Transaction = null;
			transaction?.Body.Dispose();
		}

		internal bool Secure { get; private set; }
		internal ConnectionEncryption Encryption { get; private set; }
		internal ushort CaptureData;
		internal string? Username, TempUsername;
		private ushort _protocolVersion;

		/// <summary>
		/// The domain the client gave in its EHLO/HELO, or null if it gave none usable.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Retained because RFC 7208 §2.4 defines the SPF check for a null reverse-path
		/// (<c>MAIL FROM:&lt;&gt;</c>) against <c>postmaster@&lt;HELO domain&gt;</c> — with the argument
		/// discarded there was no identity to check in either direction, so a null sender was not
		/// authenticated at all and a spoofed From under <c>p=reject</c> was accepted.
		/// </para>
		/// <para>
		/// Only a plausible DNS domain is kept. An address literal ("[192.0.2.1]", "[IPv6:fe80::1]") is
		/// not a domain and cannot be looked up, and a bare label with no dot cannot carry an SPF
		/// record; storing null for those makes "no checkable identity" explicit at the point of
		/// capture rather than something every consumer has to re-derive.
		/// </para>
		/// </remarks>
		internal string? HeloDomain { get; private set; }

		/// <summary>
		/// Extracts a checkable DNS domain from an EHLO/HELO argument.
		/// </summary>
		/// <param name="argument">The command argument, as sent.</param>
		/// <returns>The domain, or null if it is absent, an address literal, or not a DNS name.</returns>
		internal static string? ParseHeloDomain(string? argument)
		{
			if (string.IsNullOrWhiteSpace(argument))
				return null;

			var domain = argument.Trim();

			// Address literals are the RFC 5321 §4.1.3 alternative to a domain and are not resolvable.
			if (domain[0] == '[')
				return null;

			// A trailing '.' is a legal absolute form; strip it so lookups and alignment comparisons
			// see the same string a MAIL FROM domain would produce.
			domain = domain.TrimEnd('.');

			// Must look like a DNS name: at least one dot, and nothing that belongs to other grammar.
			if (!domain.Contains('.', StringComparison.Ordinal))
				return null;

			foreach (var c in domain)
			{
				if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-')
					continue;

				return null;
			}

			return domain;
		}

		/// <summary>
		/// Converts <see cref="ServerOptions.MessageCharactersLimit"/> (characters, excluding CRLF) into
		/// an octet count safe to advertise as RFC 1870 SIZE (octets, including CRLF).
		/// </summary>
		/// <remarks>
		/// <para>
		/// The limit passes through unchanged, because it is already a safe understatement. The counter
		/// in <c>ProcessData</c> adds each line's character count after CRLF has been stripped, so for
		/// any message: octets = sum(line bytes) + 2 * lines >= sum(line chars) = counted characters.
		/// Every difference between the two runs one way — CRLF adds two octets per line, UTF-8
		/// multibyte adds octets per character, dot-stuffing adds an octet to a line that already
		/// carries two of CRLF — so a message accepted by the character limit can never exceed the same
		/// number of octets. Advertising the limit therefore promises no more than the server honours.
		/// </para>
		/// <para>
		/// An earlier version halved this on the reasoning that an N-octet message could be as few as
		/// N/2 characters (every line empty). That is the wrong extremum: the question is how many
		/// octets a message at the character limit can reach, and that direction only ever inflates.
		/// Halving would have discarded half the relay's usable capacity, telling Office 365 that
		/// journal reports it could in fact deliver were too large.
		/// </para>
		/// <para>
		/// 0 means "no limit" here and is preserved as "SIZE 0", which RFC 1870 §6 independently reads
		/// as "no fixed maximum". Only a genuinely unlimited configuration may advertise 0 — a small
		/// finite limit must not round down into it.
		/// </para>
		/// </remarks>
		internal static uint AdvertisedSizeLimit(uint messageCharactersLimit) => messageCharactersLimit;
		private bool _dispose;

		private async void Init()
		{
			// Init runs as async void with no other handler, so ANY exception escaping it crashes the
			// whole process rather than the connection. The TLS handshake was the first case found
			// (B5); Greet() is another — it awaits IMailFilter.IsConnectionAllowed, and a filter that
			// throws (a database timeout in a real deployment is enough) is ordinary consumer code, not
			// a hostile scanner. The outer catch covers the whole body, including anything added later
			// on this path.
			try
			{
				if (Secure)
				{
					Encryption = ConnectionEncryption.Tls;
					_stream = new SslStream(_innerStream, false);
					try
					{
						await ((SslStream)_stream).AuthenticateAsServerAsync(Server.Certificate!, false, Server.Options.Protocols, true);
					}
					catch (Exception e)
					{
						// Kept distinct from the outer handler for its specific log prefix: a client that
						// drops mid-handshake, rejects our certificate, or sends plaintext to an
						// implicit-TLS port is routine and worth identifying as such in the log.
						Server.LoggerInterface?.LogError("[Client TLS handshake] Exception: " + e.GetType().FullName + ", " + e.Message);

						Dispose();
						return;
					}
				}
				else
					await Greet();

				if (!_dispose)
					_reader = new BoundedLineReader(_stream);

				_ = Receive();
			}
			catch (Exception e)
			{
				Server.LoggerInterface?.LogError("[Client init] Exception: " + e.GetType().FullName + ", " + e.Message);

				Dispose();
			}
		}

		private async Task Greet()
		{
			if (Server.Filter != null)
			{
				var filterResult = await Server.Filter.IsConnectionAllowed(RemoteEndPoint);

				if (filterResult.Type != SmtpResultType.Success)
				{
					await WriteCode(550,
						filterResult.Type == SmtpResultType.PermanentFail ? "5.7.1" : "4.7.1",
						string.IsNullOrWhiteSpace(filterResult.FailMessage)
							? "Delivery not authorized, connection refused"
							: filterResult.FailMessage);

					Dispose();
					return;
				}
			}

			_greetSent = true;
			await WriteText($"220 {Server.Options.ServerName} ESMTP");
		}

		private async Task Receive()
		{
			// Receive() is started as `_ = Receive()`, so like Init() it has no caller to observe its
			// exceptions. On a TLS connection the greeting — and with it the IMailFilter call — happens
			// HERE rather than in Init(), so this pre-greeting section needs the same guard: without it
			// a throwing filter still takes the process down on exactly the TLS path (R6).
			try
			{
				if (Secure)
				{
					while (!_t.IsCancellationRequested && !((SslStream)_stream).IsAuthenticated)
						await Task.Delay(5, _t);

					if (!_greetSent)
						await Greet();
				}
			}
			catch (Exception e)
			{
				Server.LoggerInterface?.LogError("[Client greeting] Exception: " + e.GetType().FullName + ", " + e.Message);

				Dispose();
				return;
			}

			if (!_greetSent)
				return;

			while (!_t.IsCancellationRequested && !_reader!.EndOfStream && _client.Connected && _stream.CanRead)
			{
				try
				{
					// While capturing a message body, read the line as raw bytes and hand it straight to
					// the DATA path. Decoding it to a string here and re-encoding it into the body store
					// silently rewrote every byte that was not valid UTF-8 — a body is an octet stream,
					// and the archive has to hold what arrived. Command lines still come back as text.
					if (CaptureData == 1)
					{
						var line = await _reader.ReadLineBytesAsync(_t);

						if (line == null)
							continue;

						// Latch truncation for the whole message: the tail of an over-long line has been
						// discarded, so the body can no longer be stored intact and the transaction is
						// refused at the terminating dot rather than acknowledged as good.
						if (_reader.LastLineTruncated)
							DataTruncated = true;

						if (_greetSent)
							await TransactionCommands.ProcessData(this, line.Value.Buffer, line.Value.Length);

						continue;
					}

					var read = await _reader.ReadLineAsync(_t);

					if (read == null)
						continue;

					await ProcessResponse(read);
				}
				catch (ObjectDisposedException)
				{
					break;
				}
				catch (IOException)
				{
					break;
				}
				catch (Exception e)
				{
					Server.LoggerInterface?.LogError("[Client receive loop] Exception: " + e.GetType().FullName + ", " + e.StackTrace + ", " + e.Message);

					_fails++;
					if (_fails <= 3) continue;
					break;
				}
			}

			if (!_dispose)
				Dispose();
		}

		internal async Task WriteText(string text)
		{
			try
			{
				if (!_stream.CanWrite) return;
				var encoded = Encoding.UTF8.GetBytes(text + "\r\n");
				await _stream.WriteAsync(encoded, _t);
			}
			catch (IOException)
			{
				Dispose(false, true);
			}
			catch (Exception e)
			{
				Server.LoggerInterface?.LogError("[Client write] Exception: " + e.GetType().FullName + ", " + e.StackTrace + ", " + e.Message);

				_fails++;
				if (_fails > 3) Dispose(false, true);
			}
		}

		internal async Task WriteCode(ushort code) => await SMTPCodes.SendCode(this, code);
		internal async Task WriteCode(ushort code, string enhanced) => await SMTPCodes.SendCode(this, code, enhanced);
		internal async Task WriteCode(ushort code, string enhanced, string text) => await SMTPCodes.SendCode(this, code, enhanced, text);
		internal async Task WriteCode(int code, string message)
		{
			// Sanitize to prevent SMTP response-splitting: CR/LF in a message field would let a
			// delivery handler inject spurious response lines visible to the remote client.
			var safe = message.Replace('\r', ' ').Replace('\n', ' ');
			await WriteText($"{code} {safe}");
		}

		internal SMTPServer Server => _listener.Server;

		private async Task ProcessResponse(string response)
		{
			if (!_greetSent)
				return;

			switch (CaptureData)
			{
				// CaptureData == 1 (message body) never reaches here: the receive loop reads those lines
				// as bytes and dispatches them directly, so the body is never decoded to a string.
				case 2:
				case 3:
				case 4:
					await AuthenticationCommands.ProcessData(this, response.Trim());
					return;
			}

			response = response.Trim();

			string command;
			var data = string.Empty;

			// Find the first ':' outside square brackets so that bracketed IPv6 literals in EHLO/HELO
			// (e.g. "EHLO [IPv6:fe80::1]", sent by Thunderbird) are not misparsed as a command
			// separator, which previously made the server answer 503 "EHLO/HELO first".
			// Upstream issue #18 (https://github.com/zabszk/CSharp-SMTP-Server/issues/18).
			int colonIndex = -1;
			bool inBrackets = false;

			for (var i = 0; i < response.Length; i++)
			{
				var c = response[i];

				if (inBrackets)
				{
					if (c == ']') inBrackets = false;
				}
				else if (c == '[') inBrackets = true;
				else if (c == ':') { colonIndex = i; break; }
			}

			if (colonIndex >= 0) command = response[..colonIndex].ToUpperInvariant().TrimEnd();
			else if (response.Contains(' ', StringComparison.Ordinal))
				command = response[..response.IndexOf(" ", StringComparison.Ordinal)].ToUpper(CultureInfo.InvariantCulture).TrimEnd();
			else command = response.ToUpperInvariant();

			if (command.Length != response.Length)
				data = response[command.Length..].TrimStart();

			switch (command.Trim())
			{
				case "EHLO":
					DiscardTransaction();
					_protocolVersion = 2;
					HeloDomain = ParseHeloDomain(data);
					await WriteText($"250-{Server.Options.ServerName} at your service");
					if (Server.AuthLogin != null) await WriteText("250-AUTH LOGIN PLAIN");
					if (!Secure && Server.Certificate != null) await WriteText("250-STARTTLS");
					await WriteText("250-8BITMIME");

					// RFC 1870. The advertised value must be one the server will never reject, so it is
					// derived conservatively rather than published as-is: SIZE is an octet count INCLUDING
					// the CRLF of each line, while MessageCharactersLimit counts characters EXCLUDING them.
					// A message of N octets can therefore be as few as N/2 counted characters, in the
					// degenerate case of empty lines costing 2 octets of CRLF each. Halving the limit is the
					// floor that holds for any line-length distribution. (UTF-8 multibyte input only makes
					// the character limit bind sooner, so it cannot breach this floor either.)
					//
					// Advertising the raw limit would publish a contract the server does not honour: a
					// many-short-line message could exceed the advertised maximum and still be accepted.
					// Understating is the safe direction — a sender may send less than it could, but is
					// never told it may send something that would then be refused.
					//
					// A limit of 0 means "no limit" here and is advertised as "SIZE 0", which RFC 1870 §6
					// independently defines as "no fixed maximum" — the same meaning.
					//
					// This advertises only; the declared SIZE= on MAIL FROM is deliberately NOT acted upon.
					// A sender that over-declares would otherwise be refused before its data arrives, and for
					// journaling a refused report is a compliance record that no longer exists anywhere.
					// Oversized messages are still caught at the terminating dot.
					await WriteText(Invariant($"250 SIZE {AdvertisedSizeLimit(Server.Options.MessageCharactersLimit)}"));
					break;

				case "HELO":
					DiscardTransaction();
					_protocolVersion = 1;
					HeloDomain = ParseHeloDomain(data);
					await WriteText($"250 {Server.Options.ServerName} at your service");
					break;

				case "STARTTLS":
					if (Secure)
					{
						await WriteCode(503, "5.5.1");
						return;
					}

					if (Server.Certificate == null)
					{
						await WriteCode(502, "5.5.1");
						return;
					}

					await WriteCode(220, "2.0.0", "Ready for TLS");

					_stream = new SslStream(_innerStream, false);
					Secure = true;
					Encryption = ConnectionEncryption.StartTls;
					await ((SslStream)_stream).AuthenticateAsServerAsync(Server.Certificate, false, Server.Options.Protocols, true);
					_reader = new BoundedLineReader(_stream);
					break;

				case "HELP":
					await WriteCode(214, "2.0.0");
					break;

				case "AUTH":
					if (_protocolVersion == 0)
					{
						await WriteCode(503, "5.5.1", "EHLO/HELO first.");
						return;
					}
					await AuthenticationCommands.ProcessCommand(this, data);
					break;

				case "NOOP":
					await WriteCode(250, "2.0.0");
					break;

				case "QUIT":
					await WriteCode(221, "2.0.0");
					Dispose();
					break;

				case "RSET":
				case "MAIL FROM":
				case "RCPT TO":
				case "DATA":
					if (_protocolVersion == 0)
					{
						await WriteCode(503, "5.5.1", "EHLO/HELO first.");
						return;
					}
					await TransactionCommands.ProcessCommand(this, command, data);
					break;

				case "VRFY":
					if (_protocolVersion == 0)
					{
						await WriteCode(503, "5.5.1", "EHLO/HELO first.");
						return;
					}
					await WriteCode(252, "5.5.1");
					break;

				default:
					if (_protocolVersion == 0)
					{
						await WriteCode(503, "5.5.1", "EHLO/HELO first.");
						return;
					}
					await WriteCode(502, "5.5.1");
					break;
			}
		}

		public void Dispose() => Dispose(false, false);

		public void Dispose(bool dontRemove, bool reset)
		{
			if (_dispose) return;
			_dispose = true;

			if (reset)
				_client.LingerState = Reset;

			_ts.Cancel();

			DiscardTransaction();

			// BoundedLineReader owns nothing but its own buffers — it borrows _stream, which is closed
			// just below — so there is nothing to dispose, unlike the StreamReader it replaced.
			_reader = null;

			_stream.Close();
			_stream.Dispose();

			_innerStream.Close();
			_innerStream.Dispose();

			_client.Close();
			_client.Dispose();

			if (!dontRemove)
				_listener.RemoveProcessor(this);
		}
	}
}