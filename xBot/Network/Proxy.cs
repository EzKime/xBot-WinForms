﻿using SecurityAPI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using xBot.App;
using xBot.Game;

namespace xBot.Network
{
	public class Proxy
	{
		/// <summary>
		/// Gateway connection.
		/// </summary>
		public Gateway Gateway { get { return _gateway; } }
		private Gateway _gateway;
		/// <summary>
		/// Agent connection.
		/// </summary>
		public Agent Agent { get { return _agent; } }
		private Agent _agent;
		/// <summary>
		/// Gets the current Silkroad process connected to the proxy.
		/// </summary>
		public Process SRO_Client {
			get {
				if (sro_client != null)
				{
					sro_client.Refresh();
					if (sro_client.HasExited)
						sro_client = null;
				}
				return sro_client;
			}
		}
		private Process sro_client;
		public bool ClientlessMode { get { return SRO_Client == null; } }
		/// <summary>
		/// Check the current client mode.
		/// </summary>
		public bool LoginClientlessMode { get; }

		private Thread ThreadProxyReconnection;
		private int CurrentAttemptReconnections;
		private Thread PingHandler;
		public bool isRunning { get; private set; }
		private int lastPortIndexSelected;
		private int lastHostIndexSelected;
		private List<ushort> GatewayPorts { get; }
		private List<string> GatewayHosts { get; }
		public Proxy(bool LoginClientlessMode, List<string> Hosts,List<ushort> Ports)
		{
			this.LoginClientlessMode = LoginClientlessMode;
			sro_client = null;
			GatewayHosts = Hosts;
			RandomHost = false;
			GatewayPorts = Ports;
			lastPortIndexSelected = lastHostIndexSelected = -1;
		}
		private Random rand;
		/// <summary>
		/// Gets o sets the host selection to random or sequence.
		/// </summary>
		public bool RandomHost {
			get {
				return (rand != null);
			}
			set {
				if (value && rand == null)
				{
					rand = new Random();
				}
				else if(!value && rand != null)
				{
					rand = null;
				}
			}
		}
		/// <summary>
		/// Start the connection.
		/// </summary>
		public void Start()
		{
			isRunning = true;
			Window w = Window.Get;
			WinAPI.InvokeIfRequired(w.Login_btnStart, ()=> {
				w.Login_btnStart.Text = "STOP";
				w.EnableControl(w.Login_btnStart, true);
			});

			Thread gwThread = (new Thread(ThreadGateway));
			gwThread.Priority = ThreadPriority.AboveNormal;
			gwThread.Start();
		}
		private string SelectHost()
		{
			if (RandomHost)
				lastHostIndexSelected = rand.Next(GatewayHosts.Count);
			else
				lastHostIndexSelected++;
			if (lastHostIndexSelected == GatewayHosts.Count)
				lastHostIndexSelected = 0;
			return GatewayHosts[lastHostIndexSelected];
		}
		private ushort SelectPort()
		{
			lastPortIndexSelected++;
			if (lastPortIndexSelected == GatewayPorts.Count)
				lastPortIndexSelected = 0;
			return GatewayPorts[lastPortIndexSelected];
		}
		private void ThreadGateway()
		{
			_gateway = new Gateway(this.SelectHost(), this.SelectPort());

			Window w = Window.Get;
			Socket SocketBinded = BindSocket("127.0.0.1", 20190);
			if (!LoginClientlessMode)
			{
				Gateway.Local.Socket = SocketBinded;
				try
				{
					// Loader setup
					w.LogProcess("Executing EdxLoader...");
					EdxLoader loader = new EdxLoader(Info.Get.ClientPath);
					loader.SetPatches(true, true, false);
					sro_client = loader.StartClient(false, Info.Get.Locale, 0, lastHostIndexSelected, ((IPEndPoint)Gateway.Local.Socket.LocalEndPoint).Port);
					if (sro_client == null)
						Stop();
					else
						sro_client.PriorityClass = ProcessPriorityClass.AboveNormal;

					int dummy = 0;
					w.Log("Waiting for client connection [" + Gateway.Local.Socket.LocalEndPoint.ToString() + "]");
					w.LogProcess("Waiting client connection...", Window.ProcessState.Warning);
					// Wait 2min. Infinity attempts
					ProxyReconnection(120, ref dummy, int.MaxValue);
					Gateway.Local.Socket = Gateway.Local.Socket.Accept();
					ProxyReconnectionStop();
					w.LogProcess("Connected");

					// Save client process
					sro_client.EnableRaisingEvents = true;
					sro_client.Exited += new EventHandler(this.Client_Closed);
				}
				catch{
					w.LogProcess();
					return;
				}
			}
			try
			{
				Gateway.Remote.Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				w.Log("Connecting to Gateway server [" + Gateway.Host + ":" + Gateway.Port + "]");
				w.LogProcess("Waiting server connection...");

				ProxyReconnection(30, ref CurrentAttemptReconnections, 10); // Wait 30 seconds, max. 10 attempts
				Gateway.Remote.Socket.Connect(Gateway.Host, Gateway.Port);
				ProxyReconnectionStop();
				CurrentAttemptReconnections = 0;
				w.Log("Connected");
				w.LogProcess("Connected");
			}
			catch { return; }
			try
			{
				// Handle it easily by iterating
				List<Context> gws = new List<Context>();
				gws.Add(Gateway.Remote);
				if (!ClientlessMode)
				{
					gws.Add(Gateway.Local);
				}
				PingHandler = new Thread(ThreadPing);
				PingHandler.Start();
				// Running process
				while (isRunning)
				{
					// Network input event processing
					foreach (Context context in gws)
					{
						if (context.Socket.Poll(0, SelectMode.SelectRead))
						{
							int count = context.Socket.Receive(context.Buffer.Buffer);
							if (count == 0)
							{
								throw new Exception("The remote connection has been lost.");
							}
							context.Security.Recv(context.Buffer.Buffer, 0, count);
						}
					}
					// Logic event processing
					foreach (Context context in gws)
					{
						List<Packet> packets = context.Security.TransferIncoming();
						if (packets != null)
						{
							foreach (Packet packet in packets)
							{
								// Show all incoming packets on analizer
								if (context == Gateway.Remote && w.Settings_cbxShowPacketServer.Checked)
								{
									bool opcodeFound = false;
									WinAPI.InvokeIfRequired(w.Settings_lstvOpcodes, () => {
										opcodeFound = w.Settings_lstvOpcodes.Items.ContainsKey(packet.Opcode.ToString("X4"));
									});
									if (opcodeFound && w.Settings_rbnPacketOnlyShow.Checked
										|| !opcodeFound && !w.Settings_rbnPacketOnlyShow.Checked)
									{
										w.LogPacket(string.Format("[G][{0}][{1:X4}][{2} bytes]{3}{4}{6}{5}", "S->C", packet.Opcode, packet.GetBytes().Length, packet.Encrypted ? "[Encrypted]" : "", packet.Massive ? "[Massive]" : "", Utility.HexDump(packet.GetBytes()), Environment.NewLine));
									}
								}
								// Switch from gateway to agent process
								if (packet.Opcode == Gateway.Opcode.SERVER_LOGIN_RESPONSE)
								{
									byte result = packet.ReadByte();
									if (result == 1)
									{
										_agent = new Agent(packet.ReadUInt(), packet.ReadAscii(), packet.ReadUShort());

										// Stop ping while switch
										PingHandler.Abort();

										string[] ip_port = SocketBinded.LocalEndPoint.ToString().Split(':');
										Thread agThread = new Thread((ThreadStart)delegate
										{
											ThreadAgent(ip_port[0], int.Parse(ip_port[1]) + 1);
										});
										agThread.Priority = ThreadPriority.AboveNormal;
										agThread.Start();
										
										// Send packet about client listeninig
										Packet agPacket = new Packet(Gateway.Opcode.SERVER_LOGIN_RESPONSE, true);
										agPacket.WriteByte(result);
										agPacket.WriteUInt(Agent.id);
										agPacket.WriteAscii(((IPEndPoint)SocketBinded.LocalEndPoint).Address.ToString());
										agPacket.WriteUShort(((IPEndPoint)SocketBinded.LocalEndPoint).Port + 1);
										context.RelaySecurity.Send(agPacket);
									}
									else if (result == 2)
									{
										byte error = packet.ReadByte();
										switch (error)
										{
											case 1:
												{
													uint maxAttempts = packet.ReadUInt();
													uint attempts = packet.ReadUInt();
													w.Log("Password entry has failed (" + attempts + " / " + maxAttempts + " attempts)");
													w.LogProcess("Password failed", Window.ProcessState.Warning);
													break;
												}
											case 2:
												byte blockType = packet.ReadByte();
												if (blockType == 1)
												{
													string blockedReason = packet.ReadAscii();
													ushort endYear = packet.ReadUShort();
													ushort endMonth = packet.ReadUShort();
													ushort endDay = packet.ReadUShort();
													ushort endHour = packet.ReadUShort();
													ushort endMinute = packet.ReadUShort();
													ushort endSecond = packet.ReadUShort();
													w.Log("Account banned till [" + endDay + "/" + endMonth + "/" + endYear + " " + endHour + "/" + endMinute + "/" + endSecond + "]. Reason: " + blockedReason);
													w.LogProcess("Account banned", Window.ProcessState.Error);
												}
												break;
											case 3:
												w.Log("This user is already connected. Please try again in 5 minutes");
												break;
											default:
												w.Log("Login error [" + error + "]");
												break;
										}
										// Client bugfix reset
										Bot.Get.LoggedFromBot = false;
										w.EnableControl(w.Login_btnStart, true);

										context.RelaySecurity.Send(packet);
									}
								}
								else if (!Gateway.PacketHandler(context, packet)
									&& !Gateway.IgnoreOpcode(packet.Opcode, context))
								{
									// Send normally through proxy
									context.RelaySecurity.Send(packet);
								}
							}
						}
					}
					// Network output event processing
					foreach (Context context in gws)
					{
						if (context.Socket.Poll(0, SelectMode.SelectWrite))
						{
							List<KeyValuePair<TransferBuffer, Packet>> buffers = context.Security.TransferOutgoing();
							if (buffers != null)
							{
								foreach (KeyValuePair<TransferBuffer, Packet> kvp in buffers)
								{
									TransferBuffer buffer = kvp.Key;
									Packet packet = kvp.Value;

									byte[] packet_bytes = packet.GetBytes();
									// Show outcoming packets on analizer
									if (context == Gateway.Remote && w.Settings_cbxShowPacketClient.Checked)
									{
										bool opcodeFound = false;
										WinAPI.InvokeIfRequired(w.Settings_lstvOpcodes, () =>
										{
											opcodeFound = w.Settings_lstvOpcodes.Items.ContainsKey(packet.Opcode.ToString("X4"));
										});
										if (opcodeFound && w.Settings_rbnPacketOnlyShow.Checked
											|| !opcodeFound && !w.Settings_rbnPacketOnlyShow.Checked)
										{
											w.LogPacket(string.Format("[G][{0}][{1:X4}][{2} bytes]{3}{4}{6}{5}", "C->S", packet.Opcode, packet.GetBytes().Length, packet.Encrypted ? "[Encrypted]" : "", packet.Massive ? "[Massive]" : "", Utility.HexDump(packet.GetBytes()), Environment.NewLine));
										}
									}
									while (true)
									{
										int count = context.Socket.Send(buffer.Buffer, buffer.Offset, buffer.Size, SocketFlags.None);
										buffer.Offset += count;
										if (buffer.Offset == buffer.Size)
										{
											break;
										}
										Thread.Sleep(1);
									}
								}
							}
						}
					}
					Thread.Sleep(1); // Cycle complete, prevent 100% CPU usage
				}
			}
			catch (Exception ex)
			{
				CloseGateway();
				w.LogPacket("[G] Error: " + ex.Message);
				if (_agent == null)
				{
					// Reset proxy
					Bot.Get.LogError(ex.ToString());
					Stop();
				}
			}
		}
		public void CloseClient()
		{
			if (SRO_Client != null)
			{
				sro_client.Kill();
				sro_client = null;
			}
		}
		private void Client_Closed(object sender, EventArgs e)
		{
			try
			{
				if (Bot.Get.inGame)
					Window.Get.Log("Switched to clientless mode");
			}
			catch { /* Window closed probably.. */ }
		}
		private void ThreadAgent(string Host, int Port)
		{
			Window w = Window.Get;
			// Connect AgentClient to Proxy
			if (!ClientlessMode)
			{
				Agent.Local.Socket = BindSocket(Host, Port);
				try
				{
					int dummy = 0;
					w.LogProcess("Waiting client connection...", Window.ProcessState.Warning);
					ProxyReconnection(10, ref dummy, int.MaxValue);
					Agent.Local.Socket = Agent.Local.Socket.Accept();
					ProxyReconnectionStop();
					w.LogProcess("Connected");
				}
				catch { return; }
			}
			Agent.Remote.Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			try
			{
				int dummy = 0;
				w.Log("Connecting to Agent server [" + Agent.Host + ":" + Agent.Port + "]");
				w.LogProcess("Waiting server connection...");
				ProxyReconnection(10, ref dummy, int.MaxValue);
				Agent.Remote.Socket.Connect(Agent.Host, Agent.Port);
				ProxyReconnectionStop();
				w.Log("Connected");
				w.LogProcess("Connected");
			}
			catch
			{
				w.Log("Failed to connect to the server");
				w.LogProcess();
				return;
			}
			try
			{
				// Handle it easily by iterating
				List<Context> ags = new List<Context>();
				ags.Add(Agent.Remote);
				if (!ClientlessMode)
				{
					ags.Add(Agent.Local);
				}
				PingHandler = new Thread(ThreadPing);
				PingHandler.Start();
				while (isRunning)
				{
					// Network input event processing
					foreach (Context context in ags)
					{
						if (context.Socket.Poll(0, SelectMode.SelectRead))
						{
							try
							{
								int count = context.Socket.Receive(context.Buffer.Buffer);
								if (count == 0)
								{
									throw new Exception("The remote connection has been lost.");
								}
								context.Security.Recv(context.Buffer.Buffer, 0, count);
							}
							catch(Exception ex) {
								if (context == Agent.Local)
								{
									// Try to continue without client
									ags.Remove(context);
									break;
								}
								else throw ex;
							}
						}
					}
					// Logic event processing
					foreach (Context context in ags)
					{
						List<Packet> packets = context.Security.TransferIncoming();
						if (packets != null)
						{
							foreach (Packet packet in packets)
							{
								// Show all incoming packets on analizer
								if (context == Agent.Remote && w.Settings_cbxShowPacketServer.Checked)
								{
									bool opcodeFound = false;
									WinAPI.InvokeIfRequired(w.Settings_lstvOpcodes, () =>
									{
										opcodeFound = w.Settings_lstvOpcodes.Items.ContainsKey(packet.Opcode.ToString("X4"));
									});
									if (opcodeFound && w.Settings_rbnPacketOnlyShow.Checked
										|| !opcodeFound && !w.Settings_rbnPacketOnlyShow.Checked)
									{
										w.LogPacket(string.Format("[A][{0}][{1:X4}][{2} bytes]{3}{4}{6}{5}", "S->C", packet.Opcode, packet.GetBytes().Length, packet.Encrypted ? "[Encrypted]" : "", packet.Massive ? "[Massive]" : "", Utility.HexDump(packet.GetBytes()), Environment.NewLine));
									}
								}
								if (!Agent.PacketHandler(context, packet) && !Agent.IgnoreOpcode(packet.Opcode, context))
								{
									// Send normally through proxy
									context.RelaySecurity.Send(packet);
								}
							}
						}
					}
					// Network output event processing
					foreach (Context context in ags)
					{
						if (context.Socket.Poll(0, SelectMode.SelectWrite))
						{
							List<KeyValuePair<TransferBuffer, Packet>> buffers = context.Security.TransferOutgoing();
							if (buffers != null)
							{
								foreach (KeyValuePair<TransferBuffer, Packet> kvp in buffers)
								{
									TransferBuffer buffer = kvp.Key;
									Packet packet = kvp.Value;

									byte[] packet_bytes = packet.GetBytes();
									// Show outcoming packets on analizer
									if (context == Agent.Remote && w.Settings_cbxShowPacketClient.Checked)
									{
										bool opcodeFound = false;
										WinAPI.InvokeIfRequired(w.Settings_lstvOpcodes, () =>
										{
											opcodeFound = w.Settings_lstvOpcodes.Items.ContainsKey(packet.Opcode.ToString("X4"));
										});
										if (opcodeFound && w.Settings_rbnPacketOnlyShow.Checked
											|| !opcodeFound && !w.Settings_rbnPacketOnlyShow.Checked)
										{
											w.LogPacket(string.Format("[A][{0}][{1:X4}][{2} bytes]{3}{4}{6}{5}", "C->S", packet.Opcode, packet.GetBytes().Length, packet.Encrypted ? "[Encrypted]" : "", packet.Massive ? "[Massive]" : "", Utility.HexDump(packet.GetBytes()), Environment.NewLine));
										}
									}
									
									while (true)
									{
										int count;
										try
										{
											count = context.Socket.Send(buffer.Buffer, buffer.Offset, buffer.Size, SocketFlags.None);
										}
										catch (Exception ex)
										{
											if (context == Agent.Local)
											{
												// Try to continue without send to client
												break;
											}
											else throw ex;
										}
										buffer.Offset += count;
										if (buffer.Offset == buffer.Size)
											break;
										Thread.Sleep(1);
									}
								}
							}
						}
					}
					Thread.Sleep(1); // Cycle complete, prevent 100% CPU usage
				}
			}
			catch (Exception ex)
			{
				w.LogPacket("[A] Error: " + ex.Message + Environment.NewLine);
				Bot.Get.LogError(ex.ToString());
				Stop();
			}
		}
		private Socket BindSocket(string ip, int port)
		{
			Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			for (int i = port; i < ushort.MaxValue; i += 2)
			{
				// Check if agent port is available
				if(availableSocket(port+1)){
					try
					{
						s.Bind(new IPEndPoint(IPAddress.Parse(ip), i));
						s.Listen(1);
						return s;
					}
					catch (SocketException) { /* ignore and continue */ }
				}
			}
			return null;
		}
		private bool availableSocket(int port){
			TcpConnectionInformation[] tcpConnInfoArray = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
			foreach (TcpConnectionInformation tcpi in tcpConnInfoArray)
			{
				if (tcpi.LocalEndPoint.Port == port)
				{
					return false;
				}
			}
			return true;
		}
		/// <summary>
		/// Wait the time specified and try to reconnect the proxy if is necessary.
		/// </summary>
		/// <param name="Seconds">Maximum time for waiting</param>
		/// <param name="CurrentAttempts">Current connection counter</param>
		/// <param name="MaxAttempts">Max connections to stop</param>
		private void ProxyReconnection(int Seconds,ref int CurrentAttempts,int MaxAttempts)
		{
			int refCurrentAttempts = CurrentAttempts;
			int refMaxAttempts = MaxAttempts;
			ThreadProxyReconnection = (new Thread((ThreadStart)delegate {
				while (true)
				{
					if (Seconds == 0)
					{
						if(refCurrentAttempts < refMaxAttempts)
						{
							Reset();
							Start();
							refCurrentAttempts++;
						}
						return;
					}
					Thread.Sleep(1000);
					Seconds--;
				}
			}));
			ThreadProxyReconnection.Start();
		}
		private void ProxyReconnectionStop()
		{
			if (ThreadProxyReconnection != null)
				ThreadProxyReconnection.Abort();
		}
		private void ThreadPing()
		{
			while (isRunning)
			{
				Thread.Sleep(6666);
				// Keep only one connection alive at clientless mode
				if (Agent != null)
				{
					try
					{
						Packet p = new Packet(Agent.Opcode.GLOBAL_PING);
						Agent.InjectToServer(p);
					}
					catch { /*Connection closed*/}
				}
			  else if (Gateway != null)
				{
					try
					{
						Packet p = new Packet(Gateway.Opcode.GLOBAL_PING);
						Gateway.InjectToServer(p);
					}
					catch { /*Connection closed*/ }
				}
			}
		}
		private void CloseGateway()
		{
			if (Gateway != null)
			{
				if (Gateway.Local.Socket != null)
				{
					try
					{
						Gateway.Local.Socket.Close();
					}
					catch { }
				}
				if (Gateway.Remote.Socket != null)
				{
					try
					{
						Gateway.Remote.Socket.Close();
					}
					catch { }
				}
			}
		}
		private void CloseAgent()
		{
			if (Agent != null)
			{
				if (Agent.Local.Socket != null)
				{
					try
					{
						Agent.Local.Socket.Close();
					}
					catch { }
				}
				if (Agent.Remote.Socket != null)
				{
					try
					{
						Agent.Remote.Socket.Close();
					}
					catch { }
				}
			}
		}
		private void Reset()
		{
			isRunning = false;
			if (PingHandler != null)
				PingHandler.Abort();
			CloseClient();
			CloseGateway();
			CloseAgent();
		}
		public void Stop()
		{
			ProxyReconnectionStop();
			Reset();
			Window w = Window.Get;
			// Reset locket controls
			WinAPI.InvokeIfRequired(w.Login_cmbxSilkroad, () => {
				w.Login_cmbxSilkroad.Enabled = true;
			});
			WinAPI.InvokeIfRequired(w.Login_btnStart, () => {
				w.Login_btnStart.Text = "START";
				w.EnableControl(w.Login_btnStart, true);
			});
			w.EnableControl(w.Login_btnLauncher, true);
			
			WinAPI.InvokeIfRequired(w.Login_gbxCharacters, () => {
				w.Login_gbxCharacters.Visible = false;
			});
			WinAPI.InvokeIfRequired(w.Login_gbxServers, () => {
				w.Login_gbxServers.Visible = true;
			});

			if (Bot.Get.inGame)
			{
				Bot.Get._OnDisconnected();
			}
			Info.Get.Database.Close();
			w.Log("Disconnected");
			w.LogProcess("Disconnected");
			// Relogin
			if (w.Login_cbxRelogin.Checked)
			{
				System.Timers.Timer Relogin = new System.Timers.Timer(1000);
				Relogin.AutoReset = true;
				byte countdown = 10;
				Relogin.Elapsed += delegate {
					 try
					{
						if (w.Login_cbxRelogin.Checked)
						{
							if (countdown > 0)
							{
								w.LogProcess("Relogin at " + countdown + " seconds...");
								countdown = (byte)(countdown - 1);
							}
							else
							{
								WinAPI.InvokeIfRequired(w.Login_btnStart, ()=> {
									if(w.Login_btnStart.Text == "START")
									{
										w.LogProcess("Relogin...");
										WinAPI.InvokeIfRequired(w.Login_btnStart, () => {
											w.Control_Click(w.Login_btnStart, null);
										});
									}
									else
									{
										w.LogProcess("Relogin canceled...");
									}
									Relogin.Stop();
								});
							}
						}
						else
						{
							w.LogProcess("Relogin canceled...");
							Relogin.Stop();
						}
					}
					 catch { Relogin.Stop(); }
				 };
				Relogin.Start();
			}
		}
		/// <summary>
		/// Send packet to the server if exists connection (Gateway/Agent).
		/// </summary>
		/// <param name="p">Packet to inject</param>
		public void InjectToServer(Packet packet)
		{
			if (Agent != null && Agent.Remote != null && Agent.Remote.Socket.Connected)
			{
				Agent.InjectToServer(packet);
			}
			else if (Gateway != null && Gateway.Remote != null && Gateway.Remote.Socket.Connected)
			{
				Gateway.InjectToServer(packet);
			}
		}
		/// <summary>
		/// Send packet to the client if exists connection (Gateway/Agent).
		/// </summary>
		/// <param name="p">Packet to inject</param>
		public void InjectToClient(Packet packet)
		{
			if (Agent != null && Agent.Local != null && Agent.Local.Socket.Connected)
			{
				Agent.InjectToClient(packet);
			}
			else if (Gateway != null && Gateway.Local != null && Gateway.Local.Socket.Connected)
			{
				Gateway.InjectToClient(packet);
			}
		}
	}
}