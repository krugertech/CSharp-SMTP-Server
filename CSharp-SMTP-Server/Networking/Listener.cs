using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace CSharp_SMTP_Server.Networking
{
	internal class Listener : IDisposable
	{
		// TODO (thread-safety): ClientProcessors is a plain List<> that is written from three distinct
		// execution contexts without synchronisation:
		//   1. The listener thread (Listen loop) calls Add() when a new TCP connection is accepted.
		//   2. Each ClientProcessor's async receive loop calls Remove() via ClientProcessor.Dispose()
		//      when that connection closes normally or due to an error.
		//   3. Listener.Dispose() iterates the list to dispose all active processors on shutdown.
		//
		// Under load this can produce InvalidOperationException ("collection was modified during
		// enumeration") in Dispose(), lost Add/Remove updates, or duplicate disposals.
		//
		// Recommended fix: protect every access with a shared lock object, and in Dispose() snapshot
		// the list to an array *after* setting _dispose = true and stopping the listener (so no new
		// Add() calls can race), then iterate the snapshot:
		//
		//   private readonly object _processorsLock = new();
		//
		//   // In Listen():      lock (_processorsLock) { ClientProcessors.Add(...); }
		//   // In Dispose():     ClientProcessor[] snapshot;
		//                        lock (_processorsLock) { snapshot = ClientProcessors.ToArray(); }
		//                        foreach (var p in snapshot) p.Dispose(true, true);
		//   // In CP.Dispose():  lock (_listener._processorsLock) { _listener.ClientProcessors.Remove(this); }
		//                        (requires exposing the lock or a helper method on Listener)
		//
		// This was a pre-existing issue in the original library and has been left intentionally
		// unchanged to minimise the diff scope of the ACK-gating fork. Fix before high-concurrency use.
		internal readonly List<ClientProcessor> ClientProcessors;
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
						ClientProcessors.Add(new ClientProcessor(client, this, _secure));
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

			foreach (var processor in ClientProcessors)
				processor.Dispose(true, true);
		}
	}
}