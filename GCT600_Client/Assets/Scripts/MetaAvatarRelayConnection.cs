using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace GCT600.AvatarNetworking
{
    // No Unity API calls on this worker. Queues hold at most one complete pose.
    public sealed class MetaAvatarRelayConnection : IDisposable
    {
        private readonly object gate = new object();
        private readonly string host, channel;
        private readonly int port;
        private readonly bool publisher;
        private readonly Thread worker;
        private TcpClient client;
        private byte[] outgoing, incoming;
        private bool stopping, connected;
        private string status = "Connecting";

        public string Status { get { lock (gate) return status; } }
        public bool Connected { get { lock (gate) return connected; } }

        public MetaAvatarRelayConnection(string host, int port, string channel, bool publisher)
        {
            if (string.IsNullOrWhiteSpace(host) || port < 1 || port > 65535 ||
                channel == null || !Regex.IsMatch(channel, @"\A[A-Za-z0-9_-]{1,64}\z"))
                throw new ArgumentException("Check relay host, port and channel (ASCII letters/digits/_/-)");
            this.host = host;
            this.port = port;
            this.channel = channel;
            this.publisher = publisher;
            worker = new Thread(Run) { IsBackground = true, Name = "Meta Avatar Relay" };
            worker.Start();
        }

        public void Publish(byte[] packet)
        {
            lock (gate)
            {
                if (stopping || !connected) return;
                outgoing = packet;
                Monitor.PulseAll(gate);
            }
        }

        public bool TryTake(out byte[] packet)
        {
            lock (gate)
            {
                packet = incoming;
                incoming = null;
                return packet != null;
            }
        }

        private void Run()
        {
            while (true)
            {
                TcpClient socket;
                lock (gate)
                {
                    if (stopping) return;
                    socket = new TcpClient { NoDelay = true, SendTimeout = 5000, ReceiveTimeout = 5000 };
                    client = socket;
                    status = "Connecting to " + host + ":" + port;
                }
                try
                {
                    var attempt = socket.BeginConnect(host, port, null, null);
                    using (var wait = attempt.AsyncWaitHandle)
                        if (!wait.WaitOne(5000)) throw new IOException("Connection timed out");
                    socket.EndConnect(attempt);
                    using (NetworkStream stream = socket.GetStream())
                    {
                        string role = publisher ? "publisher" : "subscriber";
                        string hello = "{\"version\":1,\"role\":\"" + role + "\",\"channel\":\"" + channel + "\"}";
                        MetaAvatarNetworkProtocol.WriteFrame(stream, Encoding.UTF8.GetBytes(hello));
                        string ack = Encoding.UTF8.GetString(MetaAvatarNetworkProtocol.ReadFrame(stream, 1024));
                        if (!Regex.IsMatch(ack, "\"ok\"\\s*:\\s*true")) throw new IOException("Relay rejected connection: " + ack);
                        socket.ReceiveTimeout = 0; // Subscribers may wait for an avatar to finish loading.
                        lock (gate)
                        {
                            if (stopping) return;
                            connected = true;
                            status = "Connected (" + role + ", " + channel + ")";
                        }
                        if (publisher) SendPoses(stream);
                        else ReceivePoses(stream);
                    }
                }
                catch (Exception ex)
                {
                    lock (gate) status = stopping ? "Stopped" : "Retrying: " + ex.Message;
                }
                finally
                {
                    socket.Close();
                    lock (gate)
                    {
                        connected = false;
                        incoming = outgoing = null;
                        if (client == socket) client = null;
                    }
                }
                lock (gate)
                {
                    if (stopping) return;
                    Monitor.Wait(gate, 2000);
                }
            }
        }

        private void SendPoses(NetworkStream stream)
        {
            while (true)
            {
                byte[] packet;
                lock (gate)
                {
                    while (!stopping && outgoing == null) Monitor.Wait(gate);
                    if (stopping) return;
                    packet = outgoing;
                    outgoing = null;
                }
                MetaAvatarNetworkProtocol.WriteFrame(stream, packet);
            }
        }

        private void ReceivePoses(NetworkStream stream)
        {
            while (true)
            {
                byte[] packet = MetaAvatarNetworkProtocol.ReadFrame(stream);
                lock (gate)
                {
                    if (stopping) return;
                    incoming = packet;
                }
            }
        }

        public void Dispose()
        {
            TcpClient socket;
            lock (gate)
            {
                if (stopping) return;
                stopping = true;
                connected = false;
                status = "Stopped";
                incoming = outgoing = null;
                socket = client;
                Monitor.PulseAll(gate);
            }
            socket?.Close(); // Interrupt blocking read/connect/write before waiting.
            worker.Join(250);
        }
    }
}
