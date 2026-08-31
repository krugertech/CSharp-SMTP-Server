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
		private bool _dispose;

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
				while (!_dispose)
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
						if (!_dispose)
							Server.LoggerInterface?.LogError("[Listening inner loop] Exception: " + e.Message);
					}
				}
			}
			catch (Exception e)
			{
				Server.LoggerInterface?.LogError("[Listening] Exception: " + e.Message);
			}
		}

		public void Dispose()
		{
			_dispose = true;

			_listener.Stop();

			// Set the flag and take the snapshot in ONE critical section, using the same lock
			// AddProcessor takes. That ordering is what closes the accept/registration window: a
			// processor accepted just before shutdown either registers before this block — and is
			// disposed from the snapshot — or finds _dispose already set and is refused, and the
			// accept loop disposes it. It can no longer be added after the snapshot and survive
			// shutdown unnoticed, still holding a socket and using the certificate that
			// SMTPServer.Dispose() disposes next.
			//
			// (_dispose is also assigned above, unsynchronised, so the accept loop's `while (!_dispose)`
			// and the exception filter observe shutdown promptly. The assignment here is the one this
			// invariant relies on; the plain-bool visibility issue on that other read is R7.)
			ClientProcessor[] snapshot;
			lock (_processorsLock)
			{
				_dispose = true;
				snapshot = ClientProcessors.ToArray();
			}

			foreach (var processor in snapshot)
				processor.Dispose(true, true);
		}
	}
}