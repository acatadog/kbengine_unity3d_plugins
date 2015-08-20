﻿namespace KBEngine
{
	using UnityEngine;
	using System;
	using System.Net.Sockets;
	using System.Net;
	using System.Collections;
	using System.Collections.Generic;
	using System.Text;
	using System.Text.RegularExpressions;
	using System.Threading;
	using System.Runtime.Remoting.Messaging;

	using MessageID = System.UInt16;
	using MessageLength = System.UInt16;

	/// <summary>
	/// 网络模块
	/// 处理连接、收发数据
	/// </summary>
	public class NetworkInterface2 : NetworkInterface
	{
		public delegate void AsyncConnectMethod(ConnectState state);

		/// <summary>
		/// 包接收模块(与服务端网络部分的名称对应)
		/// 处理网络数据的接收
		/// </summary>
		public class PacketReceiver
		{
			public delegate void AsyncReceiveMethod(); 

			private MessageReader messageReader = null;
			private NetworkInterface _networkInterface = null;

			private byte[] _buffer;

			// socket向缓冲区写的起始位置
			int _wpos = 0;

			// 主线程读取数据的起始位置
			int _rpos = 0;

			public PacketReceiver(NetworkInterface networkInterface)
			{
				_init(networkInterface);
			}

			~PacketReceiver()
			{
				Dbg.DEBUG_MSG("PacketReceiver::~PacketReceiver(), destroyed!");
			}

			void _init(NetworkInterface networkInterface)
			{
				_networkInterface = networkInterface;
				_buffer = new byte[KBEngineApp.app.getInitArgs().RECV_BUFFER_MAX];

				messageReader = new MessageReader();
			}

			public NetworkInterface networkInterface()
			{
				return _networkInterface;
			}

			public void process()
			{
				int t_wpos = Interlocked.Add(ref _wpos, 0);

				if (_rpos < t_wpos)
				{
					messageReader.process(_buffer, (UInt32)_rpos, (UInt32)(t_wpos - _rpos));
					Interlocked.Exchange(ref _rpos, t_wpos);
				}
				else if (t_wpos < _rpos)
				{
					messageReader.process(_buffer, (UInt32)_rpos, (UInt32)(_buffer.Length - _rpos));
					messageReader.process(_buffer, (UInt32)0, (UInt32)t_wpos);
					Interlocked.Exchange(ref _rpos, t_wpos);
				}
				else
				{
					// 没有可读数据
				}
			}

			int _free()
			{
				int t_rpos = Interlocked.Add(ref _rpos, 0);

				if (_wpos == _buffer.Length)
				{
					if (t_rpos == 0)
					{
						return 0;
					}

					Interlocked.Exchange(ref _wpos, 0);
				}

				if (t_rpos <= _wpos)
				{
					return _buffer.Length - _wpos;
				}

				return t_rpos - _wpos - 1;
			}

			public void startRecv()
			{

				var v = new AsyncReceiveMethod(this._asyncReceive);
				v.BeginInvoke(new AsyncCallback(_onRecv), null);
			}

			private void _asyncReceive()
			{
				if (_networkInterface == null || !_networkInterface.valid())
				{
					Dbg.WARNING_MSG("PacketReceiver::_asyncReceive(): network interface invalid!");
					return;
				}

				var socket = _networkInterface.sock();

				while (true)
				{
					// 必须有空间可写，否则我们阻塞在线程中直到有空间为止
					int first = 0;
					int space = _free();

					while (space == 0)
					{
						if (first > 0)
						{
							if (first > 1000)
							{
								Dbg.ERROR_MSG("PacketReceiver::_asyncReceive(): no space!");
								Event.fireIn("_closeNetwork", new object[] { _networkInterface });
								return;
							}

							Dbg.WARNING_MSG("PacketReceiver::_asyncReceive(): waiting for space, Please adjust 'RECV_BUFFER_MAX'! retries=" + first);
							System.Threading.Thread.Sleep(5);
						}

						first += 1;
						space = _free();
					}

					int bytesRead = 0;
					try
					{
						bytesRead = socket.Receive(_buffer, _wpos, space, 0);
					}
					catch (SocketException se)
					{
						Dbg.ERROR_MSG(string.Format("PacketReceiver::_asyncReceive(): receive error, disconnect from '{0}'! error = '{1}'", socket.RemoteEndPoint, se));
						Event.fireIn("_closeNetwork", new object[] { _networkInterface });
						return;
					}

					if (bytesRead > 0)
					{
						// 更新写位置
						Interlocked.Add(ref _wpos, bytesRead);
					}
					else
					{
						Dbg.WARNING_MSG(string.Format("PacketReceiver::_asyncReceive(): receive 0 bytes, disconnect from '{0}'!", socket.RemoteEndPoint));
						Event.fireIn("_closeNetwork", new object[] { _networkInterface });
						return;
					}
				}
			}

			private void _onRecv(IAsyncResult ar)
			{
				AsyncResult result = (AsyncResult)ar;
				AsyncReceiveMethod caller = (AsyncReceiveMethod)result.AsyncDelegate;
				caller.EndInvoke(ar);
			}
		}




		/// <summary>
		/// 包发送模块(与服务端网络部分的名称对应)
		/// 处理网络数据的发送
		/// </summary>
		public class PacketSender
		{
			public delegate void AsyncSendMethod();
			
			private byte[] _buffer;

			int _wpos = 0;				// 写入的数据位置
			int _spos = 0;				// 发送完毕的数据位置
			int _sending = 0;

			private NetworkInterface _networkInterface = null;

			public PacketSender(NetworkInterface networkInterface)
			{
				_init(networkInterface);
			}

			~PacketSender()
			{
				Dbg.DEBUG_MSG("PacketSender::~PacketSender(), destroyed!");
			}

			void _init(NetworkInterface networkInterface)
			{
				_networkInterface = networkInterface;

				_buffer = new byte[KBEngineApp.app.getInitArgs().SEND_BUFFER_MAX];

				_wpos = 0;
				_spos = 0;
				_sending = 0;
			}

			public NetworkInterface networkInterface()
			{
				return _networkInterface;
			}

			public bool send(byte[] datas)
			{
				if (datas.Length <= 0)
					return true;

				bool startSend = false;
				if (Interlocked.CompareExchange(ref _sending, 1, 0) == 0)
				{
					startSend = true;
					if (_wpos == _spos)
					{
						_wpos = 0;
						_spos = 0;
					}
				}

				int t_spos = Interlocked.Add(ref _spos, 0);
				int space = 0;
				int tt_wpos = _wpos % _buffer.Length;
				int tt_spos = t_spos % _buffer.Length;

				if (tt_wpos >= tt_spos)
					space = _buffer.Length - tt_wpos + tt_spos - 1;
				else
					space = tt_spos - tt_wpos - 1;

				if (datas.Length > space)
				{
					Dbg.ERROR_MSG("PacketSender::send(): no space, Please adjust 'SEND_BUFFER_MAX'! data(" + datas.Length
						+ ") > space(" + space + "), wpos=" + _wpos + ", spos=" + t_spos);

					return false;
				}

				int expect_total = tt_wpos + datas.Length;
				if (expect_total <= _buffer.Length)
				{
					Array.Copy(datas, 0, _buffer, tt_wpos, datas.Length);
				}
				else
				{
					int remain = _buffer.Length - tt_wpos;
					Array.Copy(datas, 0, _buffer, tt_wpos, remain);
					Array.Copy(datas, remain, _buffer, 0, expect_total - _buffer.Length);
				}

				Interlocked.Add(ref _wpos, datas.Length);

				if (startSend)
				{
					_startSend();
				}

				return true;
			}

			void _startSend()
			{
				var v = new AsyncSendMethod(this._asyncSend);
				v.BeginInvoke(new AsyncCallback(_onSent), null);
			}

			void _asyncSend()
			{
				if (_networkInterface == null || !_networkInterface.valid())
				{
					Dbg.WARNING_MSG("PacketReceiver::_asyncReceive(): network interface invalid!");
					return;
				}

				var socket = _networkInterface.sock();

				while (true)
				{
					int sendSize = Interlocked.Add(ref _wpos, 0) - _spos;
					int t_spos = _spos % _buffer.Length;
					if (t_spos == 0)
						t_spos = sendSize;

					if (sendSize > _buffer.Length - t_spos)
						sendSize = _buffer.Length - t_spos;

					int bytesSent = 0;
					try
					{
						bytesSent = socket.Send(_buffer, _spos % _buffer.Length, sendSize, 0);
					}
					catch (SocketException se)
					{
						Dbg.ERROR_MSG(string.Format("PacketReceiver::_asyncSend(): send data error, disconnect from '{0}'! error = '{1}'", socket.RemoteEndPoint, se));
						Event.fireIn("_closeNetwork", new object[] { _networkInterface });
						return;
					}

					int spos = Interlocked.Add(ref _spos, bytesSent);

					// 所有数据发送完毕了
					if (spos == Interlocked.Add(ref _wpos, 0))
					{
						Interlocked.Exchange(ref _sending, 0);
						return;
					}
				}
			}

			private static void _onSent(IAsyncResult ar)
			{
				AsyncResult result = (AsyncResult)ar;
				AsyncSendMethod caller = (AsyncSendMethod)result.AsyncDelegate;
				caller.EndInvoke(ar);
			}
		}



		
		
		
		PacketReceiver _packetReceiver = null;
		PacketSender _packetSender = null;

		public NetworkInterface2()
		{
			reset();
		}

		~NetworkInterface2()
		{
			Dbg.DEBUG_MSG("NetworkInterface::~NetworkInterface(), destructed!!!");
			reset();
		}

		public override void reset()
		{
			base.reset();
			_packetReceiver = null;
			_packetSender = null;
		}

		public override void close()
		{
			base.close();
			_packetReceiver = null;
			_packetSender = null;
		}

		public override void _onConnectStatus(ConnectState state)
		{
			KBEngine.Event.deregisterIn(this);

			bool success = (state.error == "" && valid());
			if (success)
			{
				Dbg.DEBUG_MSG(string.Format("NetworkInterface::_onConnectStatus(), connect to {0} is success!", state.socket.RemoteEndPoint.ToString()));
				_packetReceiver = new PacketReceiver(this);
				_packetReceiver.startRecv();
			}
			else
			{
				Dbg.ERROR_MSG(string.Format("NetworkInterface::_onConnectStatus(), connect is error! ip: {0}:{1}, err: {2}", state.connectIP, state.connectPort, state.error));
			}

			Event.fireAll("onConnectStatus", new object[] { success });

			if (state.connectCB != null)
				state.connectCB(state.connectIP, state.connectPort, success, state.userData);
		}

		private void _asyncConnect(ConnectState state)
		{
			Dbg.DEBUG_MSG(string.Format("NetWorkInterface::_asyncConnect(), will connect to '{0}:{1}' ...", state.connectIP, state.connectPort));
			try
			{
				state.socket.Connect(state.connectIP, state.connectPort);
			}
			catch (Exception e)
			{
				Dbg.ERROR_MSG(string.Format("NetWorkInterface::_asyncConnect(), connect to '{0}:{1}' fault! error = '{2}'", state.connectIP, state.connectPort, e));
				state.error = e.ToString();
			}
		}

		private void _asyncConnectCB(IAsyncResult ar)
		{
			ConnectState state = (ConnectState)ar.AsyncState;
			AsyncResult result = (AsyncResult)ar;
			AsyncConnectMethod caller = (AsyncConnectMethod)result.AsyncDelegate;

			Dbg.DEBUG_MSG(string.Format("NetWorkInterface::_asyncConnectCB(), connect to '{0}:{1}' finish. error = '{2}'", state.connectIP, state.connectPort, state.error));

			// Call EndInvoke to retrieve the results.
			caller.EndInvoke(ar);
			Event.fireIn("_onConnectStatus", new object[] { state });
		}

		public override void connectTo(string ip, int port, ConnectCallback callback, object userData)
		{
			if (valid())
				throw new InvalidOperationException("Have already connected!");

			if (!(new Regex(@"((?:(?:25[0-5]|2[0-4]\d|((1\d{2})|([1-9]?\d)))\.){3}(?:25[0-5]|2[0-4]\d|((1\d{2})|([1-9]?\d))))")).IsMatch(ip))
			{
				IPHostEntry ipHost = Dns.GetHostEntry(ip);
				ip = ipHost.AddressList[0].ToString();
			}

			// Security.PrefetchSocketPolicy(ip, 843);
			_socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			_socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, KBEngineApp.app.getInitArgs().getRecvBufferSize() * 2);

			ConnectState state = new ConnectState();
			state.connectIP = ip;
			state.connectPort = port;
			state.connectCB = callback;
			state.userData = userData;
			state.socket = _socket;
			state.networkInterface = this;

			Dbg.DEBUG_MSG("connect to " + ip + ":" + port + " ...");

			// 先注册一个事件回调，该事件在当前线程触发
			Event.registerIn("_onConnectStatus", this, "_onConnectStatus");

			var v = new AsyncConnectMethod(this._asyncConnect);
			v.BeginInvoke(state, new AsyncCallback(this._asyncConnectCB), state);
		}

		public override bool send(byte[] datas)
		{
			if (!valid())
			{
				throw new ArgumentException("invalid socket!");
			}

			if (_packetSender == null)
				_packetSender = new PacketSender(this);

			return _packetSender.send(datas);
		}

		public override void process()
		{
			if (!valid())
				return;

			if (_packetReceiver != null)
				_packetReceiver.process();
		}
	}
}