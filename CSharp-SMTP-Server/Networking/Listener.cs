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

		internal void AddProcessor(ClientProcessor processor)
		{
			lock (_processorsLock) ClientProcessors.Add(processor);
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
						AddProcessor(new ClientProcessor(client, this, _secure));
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

			// Snapshot after Stop(): no new Add can race with the enumeration (an accept that slipped
			// in just before Stop is either in the snapshot — and gets disposed here — or outlives it,
			// in which case its own dispose path removes itself safely).
			ClientProcessor[] snapshot;
			lock (_processorsLock) snapshot = ClientProcessors.ToArray();

			foreach (var processor in snapshot)
				processor.Dispose(true, true);
		}
	}
}