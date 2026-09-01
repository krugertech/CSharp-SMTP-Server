using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using MimeKit;

namespace CSharp_SMTP_Server
{
	/// <summary>
	/// Backing store for a message body, holding it as bytes rather than as a .NET string.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Small bodies stay in memory; past <see cref="SpillThresholdBytes"/> the accumulated bytes are
	/// moved to a temp file and everything after is written straight through to it. Either way the
	/// consumer reads a <see cref="Stream"/>, so a delivery handler that only needs to persist the
	/// message never materializes it.
	/// </para>
	/// <para>
	/// This replaces the <c>StringBuilder</c> + <c>RawBody</c> string the DATA path used to
	/// accumulate into, which cost roughly 11x the message size at peak: the builder's chunked slack,
	/// <c>ToString()</c>, the <c>Clone()</c> before delivery, and MimeKit's re-encode to bytes — each
	/// of those a full copy, and each copy doubled because .NET strings are UTF-16 while the message
	/// on the wire is bytes. Writing the bytes once, as they arrive, makes peak memory O(buffer)
	/// rather than O(message).
	/// </para>
	/// <para>
	/// Not thread-safe. A body is written by one connection's receive loop and read afterwards, which
	/// is the only access pattern the server produces.
	/// </para>
	/// </remarks>
	public sealed class MessageBody : IDisposable
	{
		/// <summary>
		/// Size past which the body moves from memory to a temp file, in bytes.
		/// </summary>
		/// <remarks>
		/// Chosen so ordinary mail — which is overwhelmingly under this — never touches the disk,
		/// while the large journal reports that motivated the streaming path always do. The cost of
		/// getting it wrong is asymmetric: too high risks the OOM this class exists to prevent, too
		/// low costs a temp file for a message that would have fit in memory anyway.
		/// </remarks>
		internal const int SpillThresholdBytes = 4 * 1024 * 1024;

		/// <summary>
		/// Buffer size for stream copies and for the temp file's own buffer.
		/// </summary>
		private const int CopyBufferSize = 81920;

		/// <summary>
		/// Headers prepended by the server, in the order they will appear, outermost last.
		/// </summary>
		/// <remarks>
		/// Kept separate from the body bytes so that <see cref="MailTransaction.AddHeader"/> costs
		/// O(header) instead of rewriting the whole message. They are spliced in front of the body
		/// when it is read — see <see cref="OpenRead"/>.
		/// </remarks>
		private readonly List<string> _prependedHeaders = new();

		private MemoryStream? _memory;
		private FileStream? _file;
		private string? _filePath;
		private long _length;
		private bool _disposed;

		/// <summary>
		/// The parsed form of this message, once something has asked for it.
		/// </summary>
		/// <remarks>
		/// Cached here rather than on the transaction so that a clone — which shares the body rather
		/// than copying it — shares the parse too, without either side having to force one. That keeps
		/// <c>Clone()</c> free of a parse on the delivery path while preserving the documented
		/// behaviour that a clone reuses the original's <c>MimeMessage</c> instance.
		/// </remarks>
		internal MimeMessage? ParsedMessageCache;

		/// <summary>
		/// Creates an empty body.
		/// </summary>
		public MessageBody() => _memory = new MemoryStream();

		/// <summary>
		/// Creates a body holding the given text. Intended for tests and for consumers constructing a
		/// transaction by hand; the server's DATA path writes incrementally instead.
		/// </summary>
		/// <param name="text">Body text, encoded as UTF-8.</param>
		public MessageBody(string text) : this()
		{
			if (string.IsNullOrEmpty(text)) return;

			var bytes = Encoding.UTF8.GetBytes(text);
			Write(bytes, 0, bytes.Length);
		}

		/// <summary>
		/// Total length of the body in bytes, including any headers prepended by
		/// <see cref="PrependHeader"/>.
		/// </summary>
		public long Length => _length + PrependedHeaderByteCount;

		/// <summary>
		/// Whether the body has spilled to a temp file rather than staying in memory.
		/// </summary>
		internal bool IsSpilled => _file != null;

		private long PrependedHeaderByteCount
		{
			get
			{
				long total = 0;

				foreach (var header in _prependedHeaders)
					total += Encoding.UTF8.GetByteCount(header);

				return total;
			}
		}

		/// <summary>
		/// Appends bytes to the body, spilling to a temp file if it grows past the threshold.
		/// </summary>
		/// <param name="buffer">Source buffer.</param>
		/// <param name="offset">Offset within <paramref name="buffer"/>.</param>
		/// <param name="count">Number of bytes to append.</param>
		internal void Write(byte[] buffer, int offset, int count)
		{
			if (_disposed)
				throw new ObjectDisposedException(nameof(MessageBody));

			if (count <= 0) return;

			if (_file == null && _length + count > SpillThresholdBytes)
				SpillToFile();

			if (_file != null)
				_file.Write(buffer, offset, count);
			else
				_memory!.Write(buffer, offset, count);

			_length += count;
		}

		/// <summary>
		/// Appends one line of body text plus its CRLF terminator.
		/// </summary>
		/// <remarks>
		/// CRLF is written explicitly rather than via <c>StringBuilder.AppendLine</c>, which the old
		/// path used: that emitted <c>Environment.NewLine</c>, so the stored message had bare LF line
		/// endings on Linux — wrong for SMTP, and a difference between what the same server produced
		/// on Windows and in a Linux container.
		/// </remarks>
		/// <param name="line">The line, without its terminator.</param>
		internal void WriteLine(string line)
		{
			var count = Encoding.UTF8.GetByteCount(line);
			var buffer = new byte[count + 2];

			Encoding.UTF8.GetBytes(line, 0, line.Length, buffer, 0);
			buffer[count] = (byte)'\r';
			buffer[count + 1] = (byte)'\n';

			Write(buffer, 0, buffer.Length);
		}

		/// <summary>
		/// Appends one line of body bytes plus its CRLF terminator, without transcoding them.
		/// </summary>
		/// <remarks>
		/// The DATA path's line writer. Taking bytes rather than a string is what makes the stored
		/// message byte-identical to the wire: a body is an octet stream that may be in any charset or
		/// none, and decoding it to UTF-16 and re-encoding to UTF-8 replaced every byte that was not
		/// valid UTF-8 with U+FFFD. Whatever arrived is what is stored, so a downstream DKIM verifier
		/// hashes the same octets the sender signed.
		/// </remarks>
		/// <param name="buffer">Source buffer holding the line.</param>
		/// <param name="offset">Offset of the line within <paramref name="buffer"/>.</param>
		/// <param name="count">Length of the line in bytes, excluding its terminator.</param>
		internal void WriteLine(byte[] buffer, int offset, int count)
		{
			if (count > 0)
				Write(buffer, offset, count);

			Write(Crlf, 0, 2);
		}

		/// <summary>The line terminator written after every body line.</summary>
		private static readonly byte[] Crlf = { (byte)'\r', (byte)'\n' };

		/// <summary>
		/// Records a header to appear before the body, ahead of any header added earlier.
		/// </summary>
		/// <remarks>
		/// The header is stored, not written: prepending to a 150 MB body by rewriting it is exactly
		/// the copy this class exists to avoid. <see cref="OpenRead"/> splices the accumulated headers
		/// in front of the body bytes as they are read.
		/// </remarks>
		/// <param name="name">Header name.</param>
		/// <param name="value">Header value.</param>
		internal void PrependHeader(string name, string value) =>
			_prependedHeaders.Add($"{name}: {value}\r\n");

		private void SpillToFile()
		{
			_filePath = Path.Combine(Path.GetTempPath(), "csharp-smtp-" + Guid.NewGuid().ToString("N") + ".eml");

			// DeleteOnClose is the primary lifetime guarantee: the file goes away when the handle is
			// closed, including when the process is killed, so a pod OOM-killed mid-transaction cannot
			// leave the temp directory filling up. Dispose() deletes explicitly as well, for the
			// netstandard2.1 consumers where the flag is honoured less consistently.
			_file = new FileStream(_filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
				CopyBufferSize, FileOptions.DeleteOnClose);

			if (_memory != null && _memory.Length > 0)
			{
				_memory.Position = 0;
				_memory.CopyTo(_file, CopyBufferSize);
			}

			_memory?.Dispose();
			_memory = null;
		}

		/// <summary>
		/// Opens a forward-only stream over the whole message: the prepended headers followed by the
		/// body bytes.
		/// </summary>
		/// <remarks>
		/// The returned stream is independent — it may be read while the body is read again elsewhere —
		/// but it borrows this instance's storage, so it must be disposed before the body is, and it
		/// becomes invalid once the body is disposed.
		/// </remarks>
		/// <returns>A readable stream positioned at the start of the message.</returns>
		public Stream OpenRead()
		{
			if (_disposed)
				throw new ObjectDisposedException(nameof(MessageBody));

			return new BodyReadStream(this);
		}

		/// <summary>
		/// Reads the whole message into a string, headers included.
		/// </summary>
		/// <remarks>
		/// This materializes the entire message in memory as UTF-16 — two bytes per character — which
		/// is what the streaming path exists to avoid. Prefer <see cref="OpenRead"/>.
		/// </remarks>
		/// <returns>The message as text.</returns>
		internal string ReadAsString()
		{
			using var stream = OpenRead();
			using var reader = new StreamReader(stream, Encoding.UTF8);

			return reader.ReadToEnd();
		}

		private Stream OpenBodyOnly()
		{
			if (_file != null)
			{
				// A second handle on the same path would fail against FileShare.None, and would not be
				// covered by DeleteOnClose; reading through the write handle keeps one owner of the file.
				// Concurrent readers are not supported, which matches the server's single-writer,
				// read-afterwards access pattern.
				_file.Flush();
				_file.Position = 0;
				return new NonClosingStreamWrapper(_file);
			}

			return new MemoryStream(_memory!.GetBuffer(), 0, (int)_memory.Length, false, true);
		}

		/// <summary>
		/// Releases the temp file backing this body, if it has one.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Deliberately a no-op for a body that never spilled. A temp file is an OS resource that has
		/// to be handed back promptly — leaving one per message for as long as a backend outage lasts
		/// fills the disk — but an in-memory body is only garbage, and the GC reclaims it without
		/// help.
		/// </para>
		/// <para>
		/// The asymmetry is what keeps this from being a trap. Retaining a transaction past the
		/// delivery call and reading it later is a reasonable thing for a handler to do, and it is what
		/// the server's own tests do; disposing unconditionally would turn that into an
		/// <see cref="ObjectDisposedException"/> at runtime rather than a compile-time break. Ordinary
		/// mail therefore keeps working exactly as before, and only a message large enough to have
		/// spilled — where holding the bytes was never viable anyway — becomes unreadable after
		/// delivery returns. That case is documented on <see cref="MailTransaction.GetBodyStream"/>.
		/// </para>
		/// </remarks>
		public void Dispose()
		{
			if (_disposed) return;

			// An in-memory body stays readable; there is no resource here worth breaking consumers for.
			if (_file == null) return;

			_disposed = true;

			_memory?.Dispose();
			_memory = null;

			var file = _file;
			_file = null;

			if (file != null)
			{
				var path = _filePath;
				_filePath = null;

				try
				{
					file.Dispose();
				}
				catch (IOException)
				{
					// Disposal must not throw out of a transaction teardown or a finalizer-like path.
				}

				if (path != null)
				{
					try
					{
						// Normally already gone via FileOptions.DeleteOnClose; this covers the platforms
						// and failure modes where it is not.
						if (File.Exists(path))
							File.Delete(path);
					}
					catch (IOException)
					{
					}
					catch (UnauthorizedAccessException)
					{
					}
				}
			}
		}

		/// <summary>
		/// Presents the prepended headers followed by the body as one continuous read-only stream.
		/// </summary>
		/// <remarks>
		/// Concatenating rather than copying is the point: feeding this to
		/// <c>MimeMessage.Load</c> parses the message with its <c>Received:</c> header in place without
		/// ever holding headers and body together in one buffer.
		/// </remarks>
		private sealed class BodyReadStream : Stream
		{
			private readonly MemoryStream _headers;
			private readonly Stream _body;
			private bool _headersDone;

			internal BodyReadStream(MessageBody owner)
			{
				var builder = new StringBuilder();

				// Later calls to PrependHeader put their header nearer the top, matching the old
				// string-prepend behaviour that Received:/Authentication-Results: ordering depends on.
				for (var i = owner._prependedHeaders.Count - 1; i >= 0; i--)
					builder.Append(owner._prependedHeaders[i]);

				_headers = new MemoryStream(Encoding.UTF8.GetBytes(builder.ToString()), false);
				_body = owner.OpenBodyOnly();
			}

			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => _headers.Length + _body.Length;

			public override long Position
			{
				get => _headers.Position + (_headersDone ? _body.Position : 0);
				set => throw new NotSupportedException();
			}

			public override int Read(byte[] buffer, int offset, int count)
			{
				if (!_headersDone)
				{
					var read = _headers.Read(buffer, offset, count);

					if (read > 0)
						return read;

					_headersDone = true;
				}

				return _body.Read(buffer, offset, count);
			}

			public override void Flush()
			{
			}

			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

			protected override void Dispose(bool disposing)
			{
				if (disposing)
				{
					_headers.Dispose();
					_body.Dispose();
				}

				base.Dispose(disposing);
			}
		}

		/// <summary>
		/// Wraps a stream so that disposing the wrapper does not dispose the underlying stream.
		/// </summary>
		/// <remarks>
		/// Lets a reader be handed the body's own file handle — avoiding a second handle on a
		/// <see cref="FileShare.None"/> file — while still being safe to wrap in a <c>using</c>.
		/// </remarks>
		private sealed class NonClosingStreamWrapper : Stream
		{
			private readonly Stream _inner;

			internal NonClosingStreamWrapper(Stream inner) => _inner = inner;

			public override bool CanRead => _inner.CanRead;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => _inner.Length - _inner.Position;

			public override long Position
			{
				get => _inner.Position;
				set => throw new NotSupportedException();
			}

			public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

			public override void Flush()
			{
			}

			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

			protected override void Dispose(bool disposing)
			{
				// Deliberately does not dispose _inner.
				base.Dispose(disposing);
			}
		}
	}
}
