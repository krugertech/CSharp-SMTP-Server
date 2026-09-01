using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace CSharp_SMTP_Server.Networking
{
	internal class Listener : IDisposable
	{
		// Thread-safety: ClientProcessors is written from three execution contexts — the accept loop
		// (Add), each connection's dispose path (Remove) and Dispose() below. All mutations go through
		// _processorsLock, and Dispose() snapshots the list to an array *after* stopping the listener,
		// so no new Add can race with the enumeration.
		//
		// This was a pre-existing upstream bug: unsynchronised concurrent Add/Remove corrupts List<>
		// internals (a removed slot is nulled and a racing append can leave it inside the live range),
		// so Dispose() could hit a null entry and throw NullReferenceException under load. Reproduced
		// deterministically by ConcurrencyStress_ParallelSessions_AllDeliveriesSucceed in the test suite.
		private readonly object _processorsLock = new();
		private readonly List<ClientProcessor> ClientProcessors;
		internal readonly SMTPServer Server;

		private readonly TcpListener _listener;
		private readonly Thread _listenerThread;
		private readonly bool _secure;

		// Shutdown signal. A CancellationTokenSource rather than a plain bool: the flag is written by
		// Dispose() on one thread and read by the accept loop on another, and a non-volatile bool gives
		// no visibility guarantee for that read (R7). IsCancellationRequested does, and the same object
		// also gives Dispose() something to wait on — see _stopped.
		private readonly CancellationTokenSource _cts = new();

		// Signalled by the accept loop as it exits, so Dispose() can confirm the thread is actually
		// gone instead of returning while it may still be inside AcceptTcpClient. That matters because
		// SMTPServer.Dispose() disposes the TLS certificate immediately after disposing listeners.
		private readonly ManualResetEventSlim _stopped = new(false);

		// Guarded by _processorsLock. _dispose is the R11 registration gate (may a processor still be
		// added?); _disposeStarted makes Dispose() itself idempotent (has teardown already run?).
		private bool _dispose;
		private bool _disposeStarted;

		internal Listener(IPAddress address, ushort port, SMTPServer s, bool secure, bool dualMode)
		{
			Server = s;
			_secure = secure;
			ClientProcessors = new List<ClientProcessor>();

			var ipEndPoint = new IPEndPoint(address, port);
			_listener = new TcpListener(ipEndPoint);
			if (dualMode && address.AddressFamily == AddressFamily.InterNetworkV6)
				_listener.Server.DualMode = true;
			_listenerThread = new Thread(Listen)
			{
				Name = "Listening on port " + port,
				IsBackground = true
			};
		}

		internal void Start() => _listenerThread.Start();

		/// <summary>
		/// Registers a freshly accepted connection, unless the listener is already shutting down.
		/// </summary>
		/// <returns><c>true</c> if registered; <c>false</c> if the listener is disposed.</returns>
		internal bool AddProcessor(ClientProcessor processor)
		{
			// The disposed-check and the registration must be atomic with respect to Dispose()'s
			// snapshot: otherwise a connection accepted just before Dispose() could be added *after*
			// the snapshot was taken and never be disposed at all (see the comment in Dispose()).
			lock (_processorsLock)
			{
				if (_dispose)
					return false;

				ClientProcessors.Add(processor);
				return true;
			}
		}

		internal void RemoveProcessor(ClientProcessor processor)
		{
			lock (_processorsLock) ClientProcessors.Remove(processor);
		}

		private void Listen()
		{
			try
			{
				_listener.Start(200);
				while (!_cts.IsCancellationRequested)
				{
					try
					{
						var client = _listener.AcceptTcpClient();

						var processor = new ClientProcessor(client, this, _secure);

						// Dispose() may have run between the accept above and this registration. If so
						// the processor is not in its snapshot and nothing else will ever dispose it,
						// so drop it here — otherwise it keeps serving a client after shutdown, still
						// using the TLS certificate that SMTPServer.Dispose() is about to dispose.
						if (!AddProcessor(processor))
							processor.Dispose(true, true);
					}
					catch (Exception e)
					{
						// Once shutdown has begun, AcceptTcpClient throws on every iteration because the
						// socket is closed. Reading the cancellation token (not a plain bool) is what
						// keeps that from becoming a log flood on a stale read.
						if (_cts.IsCancellationRequested)
							break;

						Server.LoggerInterface?.LogError("[Listening inner loop] Exception: " + e.Message);
					}
				}
			}
			catch (Exception e)
			{
				Server.LoggerInterface?.LogError("[Listening] Exception: " + e.Message);
			}
			finally
			{
				// Always signal, including on the outer catch path — Dispose() waits on this, and a
				// missed signal would turn its join into a guaranteed timeout.
				_stopped.Set();
			}
		}

		/// <summary>How long Dispose() waits for the accept thread to exit before giving up on it.</summary>
		private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

		public void Dispose()
		{
			// Dispose() is called more than once in practice (SMTPServer.Dispose plus a test's own
			// `using`), and the pre-CTS version tolerated that because setting a bool twice is
			// harmless. Disposing _cts is not, so guard the whole method and keep it idempotent.
			lock (_processorsLock)
			{
				if (_disposeStarted)
					return;

				_disposeStarted = true;
			}

			// Signal first, then break the blocking accept: the loop re-checks the token after
			// AcceptTcpClient throws, so it sees cancellation already set and exits instead of logging.
			_cts.Cancel();
			_listener.Stop();

			// Wait for the accept thread to actually leave the loop before tearing anything down. Without
			// this, Dispose() could return while the thread was still mid-accept — and SMTPServer.Dispose()
			// disposes the TLS certificate immediately after disposing listeners. Bounded so a wedged
			// thread degrades to the old behavior rather than hanging shutdown forever.
			if (_listenerThread.IsAlive && !_stopped.Wait(StopTimeout))
				Server.LoggerInterface?.LogError(
					"[Listener] Accept thread did not exit within " + StopTimeout.TotalSeconds + "s; continuing shutdown.");

			// Set the flag and take the snapshot in ONE critical section, using the same lock
			// AddProcessor takes. That ordering is what closes the accept/registration window: a
			// processor accepted just before shutdown either registers before this block — and is
			// disposed from the snapshot — or finds _dispose already set and is refused, and the
			// accept loop disposes it. It can no longer be added after the snapshot and survive
			// shutdown unnoticed, still holding a socket and using the certificate that
			// SMTPServer.Dispose() disposes next.
			//
			// The join above makes this strictly stronger than before: in the normal case no accept is
			// even in flight by the time the snapshot is taken. The lock still matters for the timeout
			// path and for connections' own dispose paths calling RemoveProcessor concurrently.
			ClientProcessor[] snapshot;
			lock (_processorsLock)
			{
				_dispose = true;
				snapshot = ClientProcessors.ToArray();
			}

			foreach (var processor in snapshot)
				processor.Dispose(true, true);

			_cts.Dispose();
			_stopped.Dispose();
		}
	}
}