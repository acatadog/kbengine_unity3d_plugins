namespace KBEngine
{
	using System; 
	using System.Net.Sockets; 
	using System.Net;
    using System.Text.RegularExpressions;
    using System.Threading;

    /*
		����ģ��
		�������ӡ��շ�����
	*/
    public class NetworkInterface3 : NetworkInterface
    {

		/*
			������ģ��(���������粿�ֵ����ƶ�Ӧ)
			�����������ݵĽ���
		*/
		public class PacketReceiver
		{
			private MessageReader messageReader = null;

			private byte[] _buffer;

			// socket�򻺳���д����ʼλ��
			int _wpos = 0;

			// ���̶߳�ȡ���ݵ���ʼλ��
			int _rpos = 0;

			public PacketReceiver()
			{
				_buffer = new byte[KBEngineApp.app.getInitArgs().RECV_BUFFER_MAX];

				messageReader = new MessageReader();
			}

			~PacketReceiver()
			{
				Dbg.DEBUG_MSG("PacketReceiver::~PacketReceiver(), destroyed!");
			}

			public void processMessage()
			{
				int t_wpos = _wpos;

				if (_rpos < t_wpos)
				{
					messageReader.process(_buffer, (UInt32)_rpos, (UInt32)(t_wpos - _rpos));
					_rpos = t_wpos;
				}
				else if (t_wpos < _rpos)
				{
					messageReader.process(_buffer, (UInt32)_rpos, (UInt32)(_buffer.Length - _rpos));
					messageReader.process(_buffer, (UInt32)0, (UInt32)t_wpos);
					_rpos = t_wpos;
				}
				else
				{
					// û�пɶ�����
				}
			}

			int _free()
			{
				int t_rpos = _rpos;

				if (_wpos == _buffer.Length)
				{
					if (t_rpos == 0)
					{
						return 0;
					}

                    _wpos = 0;
                }

				if (t_rpos <= _wpos)
				{
					return _buffer.Length - _wpos;
				}

				return t_rpos - _wpos - 1;
			}

			public void process(NetworkInterface3 networkInterface)
			{
				var socket = networkInterface.sock();

				try
				{
					if (!socket.Poll(0, SelectMode.SelectRead))
						return;
				}
				catch (Exception e)
				{
					Dbg.ERROR_MSG(string.Format("PacketReceiver::process(): socket error! {0}", e.ToString()));
					return;
				}

				// �����пռ��д�����������������߳���ֱ���пռ�Ϊֹ
				int space = _free();
                if (space <= 0)
                {
                    // �ڵ��̰߳汾�У�����������������˵��ĳ�����Է���������Ϣ�ĳ���ԶԶ���ڽ��ջ������Ĵ�С
                    Dbg.DEBUG_MSG("PacketReceiver::process(), no space to receive data, ignore.");
                    return;
                }

				int bytesRead = 0;
				try
				{
					// Read data from the remote device.
					bytesRead = socket.Receive(_buffer, _wpos, space, 0);
				}
				catch (Exception e)
				{
					Dbg.ERROR_MSG("PacketReceiver::process(): call Receive() is err: " + e.ToString());
					Event.asyncFireIn("_closeNetwork", new object[] { networkInterface });
					return;
				}

				if (bytesRead > 0)
				{
					// ����дλ��
					_wpos += bytesRead;
				}
				else
				{
					Dbg.WARNING_MSG(string.Format("PacketReceiver::process(): disconnect! bytesRead = '{0}'", bytesRead));
					Event.asyncFireIn("_closeNetwork", new object[] { networkInterface });
					return;
				}
			}
		}



		/*
			������ģ��(���������粿�ֵ����ƶ�Ӧ)
			�����������ݵķ���
		*/
		public class PacketSender
		{
			private byte[] _buffer;

			int _wpos = 0;				// д�������λ��
			int _spos = 0;				// ������ϵ�����λ��

			public PacketSender()
			{
				_buffer = new byte[KBEngineApp.app.getInitArgs().SEND_BUFFER_MAX];

				_wpos = 0;
				_spos = 0;
			}

			~PacketSender()
			{
				Dbg.DEBUG_MSG("PacketSender::~PacketSender(), destroyed!");
			}

			public bool send(MemoryStream stream)
			{
				int dataLength = (int)stream.length();
				if (dataLength <= 0)
					return true;

				if (_wpos == _spos)
				{
					_wpos = 0;
					_spos = 0;
				}

				int t_spos = _spos;
				int space = 0;
				int tt_wpos = _wpos % _buffer.Length;
				int tt_spos = t_spos % _buffer.Length;

				if (tt_wpos >= tt_spos)
					space = _buffer.Length - tt_wpos + tt_spos - 1;
				else
					space = tt_spos - tt_wpos - 1;

				if (dataLength > space)
				{
					Dbg.ERROR_MSG("PacketSender::send(): no space, Please adjust 'SEND_BUFFER_MAX'! data(" + dataLength
						+ ") > space(" + space + "), wpos=" + _wpos + ", spos=" + t_spos);

					return false;
				}

				int expect_total = tt_wpos + dataLength;
				if (expect_total <= _buffer.Length)
				{
					Array.Copy(stream.data(), stream.rpos, _buffer, tt_wpos, dataLength);
				}
				else
				{
					int remain = _buffer.Length - tt_wpos;
					Array.Copy(stream.data(), stream.rpos, _buffer, tt_wpos, remain);
					Array.Copy(stream.data(), stream.rpos + remain, _buffer, 0, expect_total - _buffer.Length);
				}

                _wpos += dataLength;

				return true;
			}

			public void process(NetworkInterface3 networkInterface)
			{
				int sendSize = _wpos - _spos;
				if (sendSize <= 0)
					return;

				var socket = networkInterface.sock();

				try
				{
					if (!socket.Poll(0, SelectMode.SelectWrite))
						return;
				}
				catch (Exception e)
				{
					Dbg.ERROR_MSG(string.Format("PacketSender::process(): socket error! {0}", e.ToString()));
					return;
				}

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
				catch (Exception e)
				{
					Dbg.ERROR_MSG("PacketSender::process(): is err: " + e.ToString());
					Event.asyncFireIn("_closeNetwork", new object[] { networkInterface });
				}

                _spos += bytesSent;

                //Dbg.DEBUG_MSG(string.Format("PacketSender:process(), send '{0}' bytes, spos = '{1}'", bytesSent, spos));

                // �������ݷ��������
                if (_spos == _wpos)
				{
                    _spos = _wpos = 0;
                }
            }
		}

	
		
		public interface Status_Base
		{
			void process();
		}
		
		public class Status_Connecting : Status_Base
		{
            Thread _worker = null;

            public NetworkInterface3 networkInterface;

			ConnectState _state = new ConnectState();

            DateTime connectTime;
            bool connecting = false;

            public Status_Connecting(NetworkInterface3 networkInterface_)
			{
				this.networkInterface = networkInterface_;
			}

            public void connectTo(string ip, int port, ConnectCallback callback, object userData)
            {
                _state.connectIP = ip;
                _state.connectPort = port;
                _state.connectCB = callback;
                _state.userData = userData;
                _state.socket = networkInterface.makeDefaultSocket();
                _state.networkInterface = networkInterface;

                _worker = new Thread(new ThreadStart(this._connecting));
                _worker.Name = "NetWorkInterfaceThread3";
                _worker.Start();

                connectTime = DateTime.Now;
            }

            public void _connecting()
            { 
                Dbg.DEBUG_MSG("connect to " + _state.connectIP + ":" + _state.connectPort + " ...");

				try
				{
					_state.socket.Connect(_state.connectIP, _state.connectPort);
				}
				catch (SocketException se)
				{
					if (se.SocketErrorCode == SocketError.WouldBlock)
					{
						// do nothing
					}
					else
					{
						Dbg.ERROR_MSG(string.Format("connect to '{0}:{1}' fault!!! error = '{2}'", _state.connectIP, _state.connectPort, se));
						_state.error = se.ToString();
						Event.asyncFireIn("_onConnectionState", new object[] { _state });
                        return;
					}
				}
				catch (Exception e)
				{
					Dbg.ERROR_MSG(string.Format("connect to '{0}:{1}' fault!!! error = '{2}'", _state.connectIP, _state.connectPort, e));
					_state.error = e.ToString();
					Event.asyncFireIn("_onConnectionState", new object[] { _state });
                    return;
				}

                while (true)
                {
                    bool result = false;

                    try
                    {
                        // ÿtick���һ�Σ����Բ�����
                        result = _state.socket.Poll(0, SelectMode.SelectWrite);
                    }
                    catch (Exception e)
                    {
                        networkInterface._network_status = null;
                        _state.error = e.ToString();
                        Event.asyncFireIn("_onConnectionState", new object[] { _state });
                        return;
                    }

                    if (result)
                    {
                        // �ГQ��������״̬
                        networkInterface._network_status = networkInterface._status_connected;

                        // �ص�֪ͨ
                        Event.asyncFireIn("_onConnectionState", new object[] { _state });
                        return;
                    }

                    Thread.Sleep(100);  // ˯��0.1��
                }
            }

            public virtual void process()
            {
                //Dbg.WARNING_MSG("Status_Connecting::process(), step = " + step);
                if (_state.socket != null &&
                    !_state.socket.Connected &&
                    (DateTime.Now - connectTime).Seconds >= 3)
                {
                    Dbg.WARNING_MSG(string.Format("Status_Connecting::process(), connect to '{0}:{1}' fault!!! timeout!!!", _state.connectIP, _state.connectPort));
                    _state.socket.Close();
                    _state.socket = null;
                }
            }
		}

		public class Status_Connected : Status_Base
		{
			public NetworkInterface3 networkInterface;


			public Status_Connected(NetworkInterface3 networkInterface_)
			{
				this.networkInterface = networkInterface_;
			}

			public virtual void process()
			{
                if (!networkInterface.valid())
                    return;

                if (networkInterface._packetReceiver != null)
					networkInterface._packetReceiver.process(networkInterface);

				if (networkInterface._packetSender != null)
					networkInterface._packetSender.process(networkInterface);

			}

		}



		PacketReceiver _packetReceiver = null;
		PacketSender _packetSender = null;

		Status_Base _network_status = null;
		Status_Connecting _status_connecting = null;
		Status_Connected _status_connected = null;



        public NetworkInterface3()
        {
			reset();
        }

		~NetworkInterface3()
		{
			Dbg.DEBUG_MSG("NetworkInterface3::~NetworkInterface3(), destructed!!!");
			reset();
		}

		public Socket makeDefaultSocket()
		{
			if (_socket != null)
				return _socket;

			_socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			_socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, KBEngineApp.app.getInitArgs().getRecvBufferSize() * 2);
			_socket.NoDelay = true;
			_socket.Blocking = false;
			//SetKeepAlive(_socket, 5000, 10000);
			return _socket;
		}

		public override void reset()
		{
			_packetReceiver = null;
			_packetSender = null;
			_network_status = null;
			_status_connecting = new Status_Connecting(this);
			_status_connected = new Status_Connected(this);
			base.reset();
		}

		public override void close()
		{
			_packetReceiver = null;
			_packetSender = null;
			_network_status = null;
			base.close();
		}

		public void _onConnectionState(ConnectState state)
		{
			KBEngine.Event.deregisterIn(this);
			
			bool success = (state.error == "" && valid());
			if(success)
			{
                // ���ڴ˺����Ǳ��첽����������ڼ��˵�����£�socket�п��������ӳɹ�����һ�̱���ǰ�ͷ�
                if (_socket != null && _socket.Connected)
                {
                    Dbg.DEBUG_MSG(string.Format("NetworkInterface::_onConnectionState(), connect to {0} is success!", _socket.RemoteEndPoint.ToString()));
                    _packetReceiver = new PacketReceiver();
                    _packetSender = new PacketSender();
                }
                else
                {
                    // ������ߵ������˵�����Ӻܿ���socket����ǰ�ͷŻ�ر���
                    Dbg.WARNING_MSG(string.Format("NetworkInterface::_onConnectionState(), connect sucess, but socket invalid! ip: {0}:{1}, socket state: {2}", state.connectIP, state.connectPort, _socket == null ? "NULL" : (_socket.Connected ? "CONNECTED" : "NOT CONNECTED")));
                    success = false;  // ��Ϊ����ʧ��
                }
			}
			else
			{
				Dbg.ERROR_MSG(string.Format("NetworkInterface::_onConnectionState(), connect is error! ip: {0}:{1}, err: {2}", state.connectIP, state.connectPort, state.error));
			}

			Event.asyncFireAll("onConnectionState", new object[] { success });
			
			if (state.connectCB != null)
				state.connectCB(state.connectIP, state.connectPort, success, state.userData);
		}
		
		public override void connectTo(string ip, int port, ConnectCallback callback, object userData) 
		{
			if (valid())
				throw new InvalidOperationException( "Have already connected!" );
			
			if(!(new Regex( @"((?:(?:25[0-5]|2[0-4]\d|((1\d{2})|([1-9]?\d)))\.){3}(?:25[0-5]|2[0-4]\d|((1\d{2})|([1-9]?\d))))")).IsMatch(ip))
			{
				IPHostEntry ipHost = Dns.GetHostEntry (ip);
				ip = ipHost.AddressList[0].ToString();
			}

			// ��ע��һ���¼��ص������¼��ڵ�ǰ�̴߳���
			Event.registerIn("_onConnectionState", this, "_onConnectionState");

			_network_status = _status_connecting;
			_status_connecting.connectTo(ip, port, callback, userData);
		}

		public override bool send(MemoryStream stream)
        {
			if(!valid()) 
			{
			   throw new ArgumentException ("invalid socket!");
			}

			return _packetSender.send(stream);
        }

		public override void process()
        {
            if (_network_status != null)
            {
                _network_status.process();
            }

            if (!valid())
                return;

            if (_packetReceiver != null)
				_packetReceiver.processMessage();
        }


		public static void SetKeepAlive(Socket socket, ulong keepalive_time, ulong keepalive_interval)
		{
			int bytes_per_long = 32 / 8;
			byte[] keep_alive = new byte[3 * bytes_per_long];
			ulong[] input_params = new ulong[3];
			int i1;
			int bits_per_byte = 8;

			if (keepalive_time == 0 || keepalive_interval == 0)
				input_params[0] = 0;
			else
				input_params[0] = 1;
			input_params[1] = keepalive_time;
			input_params[2] = keepalive_interval;
			for (i1 = 0; i1 < input_params.Length; i1++)
			{
				keep_alive[i1 * bytes_per_long + 3] = (byte)(input_params[i1] >> ((bytes_per_long - 1) * bits_per_byte) & 0xff);
				keep_alive[i1 * bytes_per_long + 2] = (byte)(input_params[i1] >> ((bytes_per_long - 2) * bits_per_byte) & 0xff);
				keep_alive[i1 * bytes_per_long + 1] = (byte)(input_params[i1] >> ((bytes_per_long - 3) * bits_per_byte) & 0xff);
				keep_alive[i1 * bytes_per_long + 0] = (byte)(input_params[i1] >> ((bytes_per_long - 4) * bits_per_byte) & 0xff);
			}

			socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, keep_alive);
		}
	}
} 
