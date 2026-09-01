using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CSharp_SMTP_Server.Networking
{
	/// <summary>
	/// Reads CRLF-terminated lines from a stream with a hard ceiling on how much a single line may
	/// buffer.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Replaces <c>StreamReader.ReadLineAsync</c>, which materializes a complete line before returning
	/// and so grows its buffer without limit while a client sends bytes and no terminator. That was
	/// reachable unauthenticated on an internet-facing listener, and
	/// <see cref="ServerOptions.MessageCharactersLimit"/> did not bound it: the limit is applied per
	/// line, after the line exists, so a client that never sends CRLF never reaches it. The size limit
	/// bounds a message made of terminated lines; this bounds the line itself.
	/// </para>
	/// <para>
	/// Over-long lines are truncated at the cap and the remainder discarded up to the next terminator,
	/// rather than dropping the connection. RFC 5321 §4.5.3.1 sets a 1000-octet line limit, so
	/// anything past a cap far above that is already non-conforming; truncating keeps the session's
	/// framing intact — the next line still starts where the client thinks it does — and leaves an
	/// oversized message to be refused at the terminating dot by the size limit, which for a
	/// journaling relay is a deliberate 552 rather than a dropped connection.
	/// </para>
	/// </remarks>
	internal sealed class BoundedLineReader
	{
		/// <summary>
		/// Maximum number of characters buffered for a single line.
		/// </summary>
		/// <remarks>
		/// RFC 5321 §4.5.3.1.6 sets the text line limit at 1000 octets including CRLF. This sits three
		/// orders of magnitude above that: generous enough that no real sender — including the long
		/// base64 lines and folded headers Exchange emits — is ever truncated, while still bounding a
		/// hostile client to a megabyte per connection instead of all available memory.
		/// </remarks>
		internal const int MaxLineLength = 1024 * 1024;

		private const int ReadBufferSize = 8192;

		private readonly Stream _stream;
		private readonly Decoder _decoder;
		private readonly byte[] _byteBuffer = new byte[ReadBufferSize];
		private readonly char[] _charBuffer = new char[ReadBufferSize];

		private int _charStart;
		private int _charCount;
		private bool _endOfStream;

		/// <summary>
		/// Whether the last read consumed a line longer than <see cref="MaxLineLength"/>, whose tail
		/// was discarded.
		/// </summary>
		internal bool LastLineTruncated { get; private set; }

		internal BoundedLineReader(Stream stream)
		{
			_stream = stream;

			// UTF-8 without a BOM preamble, matching what StreamReader's default decoding produced for
			// the wire. A stateful Decoder is required rather than Encoding.GetString: a multi-byte
			// sequence can straddle two socket reads, and decoding each read independently would
			// corrupt it.
			_decoder = new UTF8Encoding(false).GetDecoder();
		}

		/// <summary>
		/// Whether the stream is exhausted and no buffered characters remain.
		/// </summary>
		internal bool EndOfStream => _endOfStream && _charCount == 0;

		/// <summary>
		/// Reads the next line, without its terminator.
		/// </summary>
		/// <remarks>
		/// Accepts both CRLF and a bare LF as a terminator, as the <c>StreamReader</c> it replaces did
		/// — a lone CR is not a terminator, matching the DATA path's own <c>Replace("\r", "")</c>
		/// handling.
		/// </remarks>
		/// <param name="cancellationToken">Cancels a pending socket read.</param>
		/// <returns>The line, or null at end of stream.</returns>
		internal async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
		{
			LastLineTruncated = false;

			StringBuilder? builder = null;
			var length = 0;
			var discarding = false;

			while (true)
			{
				if (_charCount == 0)
				{
					if (!await FillAsync(cancellationToken).ConfigureAwait(false))
					{
						// End of stream. A trailing unterminated fragment is still a line, as
						// StreamReader treated it; nothing buffered means there is no line at all.
						if (builder == null || builder.Length == 0)
							return discarding ? string.Empty : null;

						return builder.ToString();
					}
				}

				// Scan the buffered characters for a terminator.
				var end = _charStart + _charCount;
				var newlineIndex = -1;

				for (var i = _charStart; i < end; i++)
				{
					if (_charBuffer[i] != '\n') continue;

					newlineIndex = i;
					break;
				}

				var available = newlineIndex == -1 ? _charCount : newlineIndex - _charStart;

				if (!discarding && available > 0)
				{
					var take = Math.Min(available, MaxLineLength - length);

					if (take > 0)
					{
						builder ??= new StringBuilder();
						builder.Append(_charBuffer, _charStart, take);
						length += take;
					}

					if (length >= MaxLineLength && (newlineIndex == -1 || take < available))
					{
						// The cap is reached with more of this line still to come: stop buffering and
						// drain the rest. This is the branch that bounds memory — everything past the cap
						// is read and thrown away, so the connection stays framed without the line ever
						// being held.
						discarding = true;
						LastLineTruncated = true;
					}
				}

				if (newlineIndex == -1)
				{
					// No terminator in this bufferful; consume it and read more.
					_charStart = 0;
					_charCount = 0;
					continue;
				}

				// Consume through the terminator.
				_charCount -= newlineIndex - _charStart + 1;
				_charStart = newlineIndex + 1;

				if (builder == null)
					return string.Empty;

				// Trim the CR of a CRLF pair; a lone LF leaves nothing to trim.
				if (builder.Length > 0 && builder[builder.Length - 1] == '\r')
					builder.Length--;

				return builder.ToString();
			}
		}

		private async Task<bool> FillAsync(CancellationToken cancellationToken)
		{
			_charStart = 0;
			_charCount = 0;

			while (_charCount == 0)
			{
				var read = await _stream.ReadAsync(_byteBuffer, 0, _byteBuffer.Length, cancellationToken).ConfigureAwait(false);

				if (read == 0)
				{
					_endOfStream = true;

					// Flush whatever the decoder still holds, so a truncated multi-byte sequence at end
					// of stream surfaces as replacement characters rather than vanishing.
					_charCount = _decoder.GetChars(_byteBuffer, 0, 0, _charBuffer, 0, true);

					return _charCount > 0;
				}

				// A read can yield only the leading bytes of a multi-byte character, producing zero
				// characters; the loop reads again rather than reporting end of stream.
				_charCount = _decoder.GetChars(_byteBuffer, 0, read, _charBuffer, 0, false);
			}

			return true;
		}
	}
}
