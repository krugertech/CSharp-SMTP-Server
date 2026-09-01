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
	/// <b>Byte-primary.</b> Lines are accumulated as the bytes that arrived on the wire and decoded to
	/// text only when a caller asks for text. <see cref="ReadLineBytesAsync"/> hands back the raw
	/// octets, which is what the DATA path consumes: decoding each body line to UTF-16 and re-encoding
	/// it to UTF-8 on the way into the body store silently rewrote any byte sequence that was not
	/// valid UTF-8 — every invalid byte became U+FFFD and came back out as the three bytes EF BF BD.
	/// A message is a byte stream, not text: it may carry another charset entirely, or be an unlabelled
	/// 8-bit body, and a downstream DKIM verifier hashes the octets it is given. Round-tripping through
	/// a string put a lossy transcode between the wire and the archive.
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
		/// Maximum number of bytes buffered for a single line.
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
		private readonly UTF8Encoding _encoding = new(false);
		private readonly byte[] _byteBuffer = new byte[ReadBufferSize];

		/// <summary>
		/// Accumulates the current line's bytes. Reused across lines so an ordinary session does not
		/// allocate a buffer per line; it grows to the largest line seen and is capped by
		/// <see cref="MaxLineLength"/>.
		/// </summary>
		private byte[] _line = new byte[256];
		private int _lineLength;

		private int _byteStart;
		private int _byteCount;
		private bool _endOfStream;

		/// <summary>
		/// Whether the last read consumed a line longer than <see cref="MaxLineLength"/>, whose tail
		/// was discarded.
		/// </summary>
		internal bool LastLineTruncated { get; private set; }

		internal BoundedLineReader(Stream stream) => _stream = stream;

		/// <summary>
		/// Whether the stream is exhausted and no buffered bytes remain.
		/// </summary>
		internal bool EndOfStream => _endOfStream && _byteCount == 0;

		/// <summary>
		/// Reads the next line as text, without its terminator.
		/// </summary>
		/// <remarks>
		/// <para>
		/// For command lines, where the protocol is ASCII and a string is what the parser wants.
		/// Message bodies go through <see cref="ReadLineBytesAsync"/> instead — see the class remarks
		/// for why decoding those is lossy.
		/// </para>
		/// <para>
		/// Accepts both CRLF and a bare LF as a terminator, as the <c>StreamReader</c> this replaced
		/// did — a lone CR is not a terminator.
		/// </para>
		/// <para>
		/// Decoding is per line rather than through a stateful <c>Decoder</c>. That is correct here and
		/// was not a behaviour change when it was introduced: a UTF-8 sequence can straddle two socket
		/// reads, which is what a stateful decoder exists to handle, but it cannot straddle a line
		/// terminator — no byte of a multi-byte sequence can be 0x0A — so decoding a whole line at once
		/// sees every sequence entire.
		/// </para>
		/// </remarks>
		/// <param name="cancellationToken">Cancels a pending socket read.</param>
		/// <returns>The line, or null at end of stream.</returns>
		internal async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
		{
			var read = await ReadLineBytesAsync(cancellationToken).ConfigureAwait(false);

			if (read == null)
				return null;

			var (buffer, length) = read.Value;

			return _encoding.GetString(buffer, 0, length);
		}

		/// <summary>
		/// Reads the next line as the raw bytes that arrived on the wire, without its terminator.
		/// </summary>
		/// <remarks>
		/// The returned array is an internal buffer that the NEXT read overwrites: consume it — copy it
		/// into the body store — before reading again. Handing back a borrowed buffer rather than a
		/// fresh array is what keeps the DATA path from allocating once per body line, which for a
		/// 150 MB message is millions of arrays the streaming work exists to avoid.
		/// </remarks>
		/// <param name="cancellationToken">Cancels a pending socket read.</param>
		/// <returns>The buffer and the number of valid bytes in it, or null at end of stream.</returns>
		internal async Task<(byte[] Buffer, int Length)?> ReadLineBytesAsync(CancellationToken cancellationToken = default)
		{
			LastLineTruncated = false;

			_lineLength = 0;

			var sawAny = false;
			var discarding = false;

			while (true)
			{
				if (_byteCount == 0)
				{
					if (!await FillAsync(cancellationToken).ConfigureAwait(false))
					{
						// End of stream. A trailing unterminated fragment is still a line, as
						// StreamReader treated it; nothing buffered means there is no line at all.
						if (!sawAny)
							return null;

						return (_line, _lineLength);
					}
				}

				// Scan the buffered bytes for a terminator.
				var end = _byteStart + _byteCount;
				var newlineIndex = -1;

				for (var i = _byteStart; i < end; i++)
				{
					if (_byteBuffer[i] != (byte)'\n') continue;

					newlineIndex = i;
					break;
				}

				var available = newlineIndex == -1 ? _byteCount : newlineIndex - _byteStart;

				if (available > 0)
					sawAny = true;

				if (!discarding && available > 0)
				{
					var take = Math.Min(available, MaxLineLength - _lineLength);

					if (take > 0)
					{
						EnsureLineCapacity(_lineLength + take);
						Buffer.BlockCopy(_byteBuffer, _byteStart, _line, _lineLength, take);
						_lineLength += take;
					}

					if (_lineLength >= MaxLineLength && (newlineIndex == -1 || take < available))
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
					_byteStart = 0;
					_byteCount = 0;
					continue;
				}

				// Consume through the terminator.
				_byteCount -= newlineIndex - _byteStart + 1;
				_byteStart = newlineIndex + 1;

				// Trim the CR of a CRLF pair; a lone LF leaves nothing to trim.
				if (_lineLength > 0 && _line[_lineLength - 1] == (byte)'\r')
					_lineLength--;

				return (_line, _lineLength);
			}
		}

		private void EnsureLineCapacity(int required)
		{
			if (_line.Length >= required) return;

			var capacity = _line.Length;

			while (capacity < required)
				capacity *= 2;

			// Never grow past the cap: the caller only ever asks for at most MaxLineLength bytes, and
			// clamping here keeps the doubling from overshooting it.
			if (capacity > MaxLineLength)
				capacity = MaxLineLength;

			Array.Resize(ref _line, capacity);
		}

		private async Task<bool> FillAsync(CancellationToken cancellationToken)
		{
			_byteStart = 0;
			_byteCount = 0;

			var read = await _stream.ReadAsync(_byteBuffer, 0, _byteBuffer.Length, cancellationToken).ConfigureAwait(false);

			if (read == 0)
			{
				_endOfStream = true;
				return false;
			}

			_byteCount = read;

			return true;
		}
	}
}
