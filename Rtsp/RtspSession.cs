﻿/*  
    Copyright (C) <2007-2017>  <Kay Diefenthal>

    SatIp is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    SatIp is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with SatIp.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;


namespace SatIp
{
    public class RtspSession : INotifyPropertyChanged, IDisposable
    {
        #region Private Fields
        private static readonly Regex RegexRtspSessionHeader = new Regex(@"\s*([^\s;]+)(;timeout=(\d+))?");
        private const int DefaultRtspSessionTimeout = 30;    // unit = s
        private static readonly Regex RegexDescribeResponseSignalInfo = new Regex(@";tuner=\d+,(\d+),(\d+),(\d+),", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private string _address;
        /// <summary>
        /// The current RTSP session ID. Used in the header of all RTSP messages
        /// sent to the server.
        /// </summary>
        private string _rtspSessionId;
        /// <summary>
        /// The time after which the SAT>IP server will stop streaming if it does
        /// not receive some kind of interaction.
        /// </summary>
        private int _rtspSessionTimeToLive = 0;
        private string _rtspStreamId;
        /// <summary>
        /// The port that the RTP listener thread listens to.
        /// </summary>
        private int _serverRtpPort;
        /// <summary>
        /// The port that the RTCP listener thread listens to.
        /// </summary>
        private int _serverRtcpPort;
        /// <summary>
        /// The port on which the RTP listener thread listens.
        /// </summary>
        private int _rtpPort;
        /// <summary>
        /// The port on which the RTCP listener thread listens.
        /// </summary>
        private int _rtcpPort;
        //private string _rtspStreamUrl;
        /// <summary>
        /// The Address on which the RTP RTCP listener thread listens.
        /// </summary>
        private string _destination;
        /// <summary>
        /// The Address that the RTP RTCP listener thread listens to.
        /// </summary>
        private string _source;

        private Socket _rtspSocket;
        /// <summary>
        /// A thread, used to periodically send RTSP OPTIONS to tell the SAT>IP
        /// server not to stop streaming.
        /// </summary>
        private Thread _keepAliveThread = null;
        /// <summary>
        /// An event, used to stop the streaming keep-alive thread.
        /// </summary>
        private AutoResetEvent _keepAliveThreadStopEvent = null;

        private int _rtspSequenceNum = 1;
        private bool _disposed = false;
        private RtpListener _rtpListener;
        private RtcpListener _rtcpListener;
        #endregion

        #region Constructor

        public RtspSession(string address)
        {
            //Logger.SetLogFilePath("Rtsp.log", Settings.Default.LogLevel);
            _address = address;

        }
        ~RtspSession()
        {
            Dispose(false);
        }
        #endregion

        #region Properties

        #region Rtsp
        public string RtspSessionId
        {
            get { return _rtspSessionId; }
            set { if (_rtspSessionId != value) { _rtspSessionId = value; OnPropertyChanged("RtspSessionId"); } }
        }
        public string RtspStreamId
        {
            get { return _rtspStreamId; }
            set { if (_rtspStreamId != value) { _rtspStreamId = value; OnPropertyChanged("RtspStreamId"); } }
        }


        public int RtspSessionTimeToLive
        {
            get
            {
                if (_rtspSessionTimeToLive == 0)
                    _rtspSessionTimeToLive = DefaultRtspSessionTimeout;
                return _rtspSessionTimeToLive * 1000 - 20;
            }
            set { if (_rtspSessionTimeToLive != value) { _rtspSessionTimeToLive = value; OnPropertyChanged("RtspSessionTimeToLive"); } }
        }

        #endregion

        #region Rtp Rtcp

        /// <summary>
        /// The LocalEndPoint Address
        /// </summary>
        public string Destination
        {
            get
            {
                if (string.IsNullOrEmpty(_destination))
                {
                    var result = "";
                    var host = Dns.GetHostName();
                    var hostentry = Dns.GetHostEntry(host);
                    foreach (var ip in hostentry.AddressList.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork))
                    {
                        result = ip.ToString();
                    }

                    _destination = result;
                }
                return _destination;
            }
            set
            {
                if (_destination != value)
                {
                    _destination = value;
                    OnPropertyChanged("Destination");
                }
            }
        }

        /// <summary>
        /// The RemoteEndPoint Address
        /// </summary>
        public string Source
        {
            get { return _source; }
            set
            {
                if (_source != value)
                {
                    _source = value;
                    OnPropertyChanged("Source");
                }
            }
        }

        /// <summary>
        /// The Media Data Delivery RemoteEndPoint Port if we use Unicast
        /// </summary>
        public int ServerRtpPort
        {
            get
            {
                return _serverRtpPort;
            }
            set { if (_serverRtpPort != value) { _serverRtpPort = value; OnPropertyChanged("ServerRtpPort"); } }
        }

        /// <summary>
        /// The Media Metadata Delivery RemoteEndPoint Port if we use Unicast
        /// </summary>
        public int ServerRtcpPort
        {
            get { return _serverRtcpPort; }
            set { if (_serverRtcpPort != value) { _serverRtcpPort = value; OnPropertyChanged("ServerRtcpPort"); } }
        }

        /// <summary>
        /// The Media Data Delivery RemoteEndPoint Port if we use Multicast 
        /// </summary>
        public int RtpPort
        {
            get { return _rtpPort; }
            set { if (_rtpPort != value) { _rtpPort = value; OnPropertyChanged("RtpPort"); } }
        }

        /// <summary>
        /// The Media Meta Delivery RemoteEndPoint Port if we use Multicast 
        /// </summary>
        public int RtcpPort
        {
            get { return _rtcpPort; }
            set { if (_rtcpPort != value) { _rtcpPort = value; OnPropertyChanged("RtcpPort"); } }
        }

        #endregion




        #endregion

        #region Private Methods

        private void ProcessSessionHeader(string sessionHeader, string response)
        {
            if (!string.IsNullOrEmpty(sessionHeader))
            {
                var m = RegexRtspSessionHeader.Match(sessionHeader);
                if (!m.Success)
                {
                    Logger.Error("Failed to tune, RTSP {0} response session header {1} format not recognised", response, sessionHeader);
                }
                _rtspSessionId = m.Groups[1].Captures[0].Value;
                _rtspSessionTimeToLive = m.Groups[3].Captures.Count == 1 ? int.Parse(m.Groups[3].Captures[0].Value) : DefaultRtspSessionTimeout;
            }
        }
        private void ProcessTransportHeader(string transportHeader)
        {
            if (!string.IsNullOrEmpty(transportHeader))
            {
                var transports = transportHeader.Split(',');
                foreach (var transport in transports)
                {
                    if (transport.Trim().StartsWith("RTP/AVP"))
                    {
                        var sections = transport.Split(';');
                        foreach (var section in sections)
                        {
                            var parts = section.Split('=');
                            if (parts[0].Equals("server_port"))
                            {
                                var ports = parts[1].Split('-');
                                _serverRtpPort = int.Parse(ports[0]);
                                _serverRtcpPort = int.Parse(ports[1]);
                            }
                            else if (parts[0].Equals("destination"))
                            {
                                _destination = parts[1];
                            }
                            else if (parts[0].Equals("port"))
                            {
                                var ports = parts[1].Split('-');
                                _rtpPort = int.Parse(ports[0]);
                                _rtcpPort = int.Parse(ports[1]);
                            }
                            else if (parts[0].Equals("ttl"))
                            {
                                _rtspSessionTimeToLive = int.Parse(parts[1]);
                            }
                            else if (parts[0].Equals("source"))
                            {
                                _source = parts[1];
                            }
                            else if (parts[0].Equals("client_port"))
                            {
                                var ports = parts[1].Split('-');
                                _rtpPort = int.Parse(ports[0]);
                                _rtcpPort = int.Parse(ports[1]);
                            }
                        }
                    }
                }
            }
        }
        private void Connect()
        {
            _rtspSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var ip = IPAddress.Parse(_address);
            var rtspEndpoint = new IPEndPoint(ip, 554);
            _rtspSocket.Connect(rtspEndpoint);
        }
        private void Disconnect()
        {
            if (_rtspSocket != null && _rtspSocket.Connected)
            {
                _rtspSocket.Shutdown(SocketShutdown.Both);
                _rtspSocket.Close();
            }
        }
        private void SendRequest(RtspRequest request)
        {
            if (_rtspSocket == null)
            {
                Connect();
            }
            try
            {
                request.Headers.Add("CSeq", _rtspSequenceNum.ToString());
                _rtspSequenceNum++;
                byte[] requestBytes = request.Serialise();
                if (_rtspSocket != null)
                {
                    var requestBytesCount = _rtspSocket.Send(requestBytes, requestBytes.Length, SocketFlags.None);
                    if (requestBytesCount < 1)
                    {

                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error(e.Message);
            }
        }
        private void ReceiveResponse(out RtspResponse response)
        {
            response = null;
            var responseBytesCount = 0;
            byte[] responseBytes = new byte[1024];
            try
            {
                responseBytesCount = _rtspSocket.Receive(responseBytes, responseBytes.Length, SocketFlags.None);
                response = RtspResponse.Deserialise(responseBytes, responseBytesCount);
                string contentLengthString;
                int contentLength = 0;
                if (response.Headers.TryGetValue("Content-Length", out contentLengthString))
                {
                    contentLength = int.Parse(contentLengthString);
                    if ((string.IsNullOrEmpty(response.Body) && contentLength > 0) || response.Body.Length < contentLength)
                    {
                        if (response.Body == null)
                        {
                            response.Body = string.Empty;
                        }
                        while (responseBytesCount > 0 && response.Body.Length < contentLength)
                        {
                            responseBytesCount = _rtspSocket.Receive(responseBytes, responseBytes.Length, SocketFlags.None);
                            response.Body += System.Text.Encoding.UTF8.GetString(responseBytes, 0, responseBytesCount);
                        }
                    }
                }
            }
            catch (SocketException)
            {
            }
        }
        private void StartKeepAliveThread()
        {

            if (_keepAliveThread != null && !_keepAliveThread.IsAlive)
            {
                StopKeepAliveThread();
            }

            if (_keepAliveThread == null)
            {
                Logger.Info("SAT>IP : starting new keep-alive thread");
                _keepAliveThreadStopEvent = new AutoResetEvent(false);
                _keepAliveThread = new Thread(new ThreadStart(KeepAlive));
                _keepAliveThread.Name = string.Format("SAT>IP tuner  keep-alive");
                _keepAliveThread.IsBackground = true;
                _keepAliveThread.Priority = ThreadPriority.Lowest;
                _keepAliveThread.Start();
            }
        }
        private void StopKeepAliveThread()
        {
            if (_keepAliveThread != null)
            {
                if (!_keepAliveThread.IsAlive)
                {
                    Logger.Critical("SAT>IP : aborting old keep-alive thread");
                    _keepAliveThread.Abort();
                }
                else
                {
                    _keepAliveThreadStopEvent.Set();
                    if (!_keepAliveThread.Join(RtspSessionTimeToLive))
                    {
                        Logger.Critical("SAT>IP : failed to join keep-alive thread, aborting thread");
                        _keepAliveThread.Abort();
                    }
                }
                _keepAliveThread = null;
                if (_keepAliveThreadStopEvent != null)
                {
                    _keepAliveThreadStopEvent.Close();
                    _keepAliveThreadStopEvent = null;
                }
            }
        }
        private void KeepAlive()
        {
            try
            {
                while (!_keepAliveThreadStopEvent.WaitOne(RtspSessionTimeToLive))    // -5 seconds to avoid timeout
                {
                    if ((_rtspSocket == null))
                    {
                        Connect();
                    }
                    RtspRequest request = null;
                    if (string.IsNullOrEmpty(_rtspSessionId))
                    {
                        request = new RtspRequest(RtspMethod.Options, string.Format("rtsp://{0}:{1}/", _address, 554), 1, 0);
                    }
                    else
                    {
                        request = new RtspRequest(RtspMethod.Options, string.Format("rtsp://{0}:{1}/", _address, 554), 1, 0);
                        request.Headers.Add("Session", _rtspSessionId);
                    }
                    RtspResponse response;
                    SendRequest(request);
                    ReceiveResponse(out response);
                    if (response.StatusCode != RtspStatusCode.Ok)
                    {
                        Logger.Critical("SAT>IP : keep-alive request/response failed, non-OK RTSP OPTIONS status code {0} {1}", response.StatusCode, response.ReasonPhrase);
                    }
                }
            }
            catch (ThreadAbortException)
            {
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString(), "SAT>IP : keep-alive thread exception");
                return;
            }
            Logger.Info("SAT>IP : keep-alive thread stopping");
        }

        #endregion

        #region Public Methods

        public RtspStatusCode Setup(string query, TransmissionMode transmissionmode)
        {
            RtspRequest request = null;
            RtspResponse response;
            if ((_rtspSocket == null))
            {
                Connect();
            }
            if (string.IsNullOrEmpty(_rtspSessionId))
            {
                request = new RtspRequest(RtspMethod.Setup, string.Format("rtsp://{0}:{1}/?{2}", _address, 554, query), 1, 0);
                switch (transmissionmode)
                {
                    case TransmissionMode.Multicast:
                        request.Headers.Add("Transport", string.Format("RTP/AVP;{0}", transmissionmode.ToString().ToLower()));
                        break;
                    case TransmissionMode.Unicast:
                        var activeTcpConnections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
                        var usedPorts = new HashSet<int>();
                        foreach (var connection in activeTcpConnections)
                        {
                            usedPorts.Add(connection.LocalEndPoint.Port);
                        }
                        for (var port = 40000; port <= 65534; port += 2)
                        {
                            if (!usedPorts.Contains(port) && !usedPorts.Contains(port + 1))
                            {
                                _rtpPort = port;
                                _rtcpPort = port + 1;
                                break;
                            }
                        }
                        request.Headers.Add("Transport", string.Format("RTP/AVP;{0};client_port={1}-{2}", transmissionmode.ToString().ToLower(), _rtpPort, _rtcpPort));
                        break;
                }
            }
            else
            {
                request = new RtspRequest(RtspMethod.Setup, string.Format("rtsp://{0}:{1}/?{2}", _address, 554, query), 1, 0);
                switch (transmissionmode)
                {
                    case TransmissionMode.Multicast:
                        request.Headers.Add("Session", _rtspSessionId);
                        request.Headers.Add("Transport", string.Format("RTP/AVP;{0}", transmissionmode.ToString().ToLower()));
                        break;
                    case TransmissionMode.Unicast:
                        request.Headers.Add("Session", _rtspSessionId);
                        request.Headers.Add("Transport", string.Format("RTP/AVP;{0};client_port={1}-{2}", transmissionmode.ToString().ToLower(), _rtpPort, _rtcpPort));
                        break;
                }
            }
            SendRequest(request);
            ReceiveResponse(out response);
            if (response.StatusCode == RtspStatusCode.Ok)
            {
                if (!response.Headers.TryGetValue("com.ses.streamID", out _rtspStreamId))
                {
                    Logger.Error(string.Format("Failed to tune, not able to locate Stream ID header in RTSP SETUP response"));
                }
                string sessionHeader;
                if (!response.Headers.TryGetValue("Session", out sessionHeader))
                {
                    Logger.Error(string.Format("Failed to tune, not able to locate Session header in RTSP SETUP response"));
                }
                ProcessSessionHeader(sessionHeader, "Setup");
                string transportHeader;
                if (!response.Headers.TryGetValue("Transport", out transportHeader))
                {
                    Logger.Error(string.Format("Failed to tune, not able to locate Transport header in RTSP SETUP response"));
                }
                ProcessTransportHeader(transportHeader);

                StartKeepAliveThread();
                //if (_rtpListener == null)
                //{
                //    _rtpListener = new RtpListener(_destination, _rtpPort, transmissionmode);
                //    _rtpListener.PacketReceived += new RtpListener.PacketReceivedHandler(RtpPacketReceived);
                //    _rtpListener.StartRtpListenerThread();
                //}
                if (_rtcpListener == null)
                {
                    _rtcpListener = new RtcpListener(_destination, _rtcpPort, transmissionmode);
                    _rtcpListener.PacketReceived += new RtcpListener.PacketReceivedHandler(RtcpPacketReceived);
                    _rtcpListener.StartRtcpListenerThread();
                }
            }
            return response.StatusCode;
        }

        public RtspStatusCode Play(string query)
        {
            if ((_rtspSocket == null))
            {
                Connect();
            }
            RtspResponse response;
            string data = string.Empty;
            if (string.IsNullOrEmpty(query))
            {
                data = string.Format("rtsp://{0}:{1}/stream={2}", _address,
                    554, _rtspStreamId);
            }
            else
            {
                data = string.Format("rtsp://{0}:{1}/stream={2}?{3}", _address,
                    554, _rtspStreamId, query);
            }
            var request = new RtspRequest(RtspMethod.Play, data, 1, 0);
            request.Headers.Add("Session", _rtspSessionId);
            SendRequest(request);
            ReceiveResponse(out response);
            string sessionHeader;
            if (!response.Headers.TryGetValue("Session", out sessionHeader))
            {
                Logger.Error(string.Format("Failed to tune, not able to locate Session header in RTSP Play response"));
            }
            ProcessSessionHeader(sessionHeader, "Play");
            string rtpinfoHeader;
            if (!response.Headers.TryGetValue("RTP-Info", out rtpinfoHeader))
            {
                Logger.Error(string.Format("Failed to tune, not able to locate Rtp-Info header in RTSP Play response"));
            }
            return response.StatusCode;
        }

        public RtspStatusCode Options()
        {
            if ((_rtspSocket == null))
            {
                Connect();
            }
            RtspRequest request = null;
            RtspResponse response;
            if (string.IsNullOrEmpty(_rtspSessionId))
            {
                request = new RtspRequest(RtspMethod.Options, string.Format("rtsp://{0}:{1}/", _address, 554), 1, 0);
            }
            else
            {
                request = new RtspRequest(RtspMethod.Options, string.Format("rtsp://{0}:{1}/", _address, 554), 1, 0);
                request.Headers.Add("Session", _rtspSessionId);
            }
            SendRequest(request);
            ReceiveResponse(out response);
            string sessionHeader;
            if (!response.Headers.TryGetValue("Session", out sessionHeader))
            {
                Logger.Error(string.Format("Failed to tune, not able to locate session header in RTSP Options response"));
            }
            ProcessSessionHeader(sessionHeader, "Options");
            string optionsHeader;
            if (!response.Headers.TryGetValue("Public", out optionsHeader))
            {
                Logger.Error(string.Format("Failed to tune, not able to Options header in RTSP Options response"));
            }
            return response.StatusCode;
        }

        public RtspStatusCode Describe()
        {
            if ((_rtspSocket == null))
            {
                Connect();
            }
            RtspRequest request = null;
            RtspResponse response;
            var locked = false;
            var level = 0;
            var quality = 0;
            if (string.IsNullOrEmpty(_rtspSessionId))
            {
                request = new RtspRequest(RtspMethod.Describe, string.Format("rtsp://{0}:{1}/", _address, 554), 1, 0);
                request.Headers.Add("Accept", "application/sdp");
            }
            else
            {
                request = new RtspRequest(RtspMethod.Describe, string.Format("rtsp://{0}:{1}/stream={2}", _address, 554, _rtspStreamId), 1, 0);
                request.Headers.Add("Accept", "application/sdp");
                request.Headers.Add("Session", _rtspSessionId);
            }
            SendRequest(request);
            ReceiveResponse(out response);
            string sessionHeader;
            if (!response.Headers.TryGetValue("Session", out sessionHeader))
            {
                Logger.Error(string.Format("Failed to tune, not able to locate session header in RTSP Describe response"));
            }
            ProcessSessionHeader(sessionHeader, "Describe");
            var m = RegexDescribeResponseSignalInfo.Match(response.Body);
            if (m.Success)
            {
                locked = m.Groups[2].Captures[0].Value.Equals("1");
                level = int.Parse(m.Groups[1].Captures[0].Value) * 100 / 255;    // level: 0..255 => 0..100
                quality = int.Parse(m.Groups[3].Captures[0].Value) * 100 / 15;   // quality: 0..15 => 0..100

            }
            OnSignalInfo(new SignalInfoArgs(locked, level, quality));
            /*              
                v=0
                o=- 1378633020884883 1 IN IP4 192.168.2.108
                s=SatIPServer:1 4
                t=0 0
                a=tool:idl4k
                m=video 52780 RTP/AVP 33
                c=IN IP4 0.0.0.0
                b=AS:5000
                a=control:stream=4
                a=fmtp:33 ver=1.0;tuner=1,0,0,0,12344,h,dvbs2,,off,,22000,34;pids=0,100,101,102,103,106
                =sendonly
             */
            return response.StatusCode;
        }

        public RtspStatusCode TearDown()
        {
            if ((_rtspSocket == null))
            {
                Connect();
            }
            RtspResponse response;
            var request = new RtspRequest(RtspMethod.Teardown, string.Format("rtsp://{0}:{1}/stream={2}", _address, 554, _rtspStreamId), 1, 0);
            request.Headers.Add("Session", _rtspSessionId);
            SendRequest(request);
            ReceiveResponse(out response);
            if (_rtpListener != null)
            {
                _rtpListener.Dispose();
                _rtpListener.PacketReceived -= new RtpListener.PacketReceivedHandler(RtpPacketReceived);
                _rtpListener = null;
            }
            if (_rtcpListener != null)
            {
                _rtcpListener.Dispose();
                _rtcpListener.PacketReceived -= new RtcpListener.PacketReceivedHandler(RtcpPacketReceived);
                _rtcpListener = null;
            }
            StopKeepAliveThread();
            Disconnect();
            return response.StatusCode;
        }

        #endregion

        #region Public Events

        public event PropertyChangedEventHandler PropertyChanged;

        #endregion

        #region Protected Methods

        protected void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }
        protected void RtcpPacketReceived(object sender, RtcpListener.RtcpPacketReceivedArgs e)
        {
            var locked = false;
            var level = 0;
            var quality = 0;

            if (e.Packet is RtcpAppPacket)
            {
                RtcpAppPacket apppacket = (RtcpAppPacket)e.Packet;
                var m = RegexDescribeResponseSignalInfo.Match(apppacket.Data);
                if (m.Success)
                {
                    locked = m.Groups[2].Captures[0].Value.Equals("1");
                    level = int.Parse(m.Groups[1].Captures[0].Value) * 100 / 255;    // level: 0..255 => 0..100
                    quality = int.Parse(m.Groups[3].Captures[0].Value) * 100 / 15;   // quality: 0..15 => 0..100
                }
                OnSignalInfo(new SignalInfoArgs(locked, level, quality));
            }
            else if (e.Packet is RtcpByePacket)
            {
                TearDown();
            }
        }
        protected void RtpPacketReceived(object sender, RtpListener.RtpPacketReceivedArgs e)
        {
            if ((e.Packet.HasPayload) && (e.Packet.PayloadType == 33))
            {
                OnTsData(new TsDataArgs(e.Packet.Payload));
            }
        }
        #endregion

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);//Disconnect();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    //TearDown();
                    Disconnect();
                }
            }
            _disposed = true;
        }
        protected void OnSignalInfo(SignalInfoArgs args)
        {
            if (SignalInfo != null)
            {
                SignalInfo(this, args);
            }
        }
        protected void OnTsData(TsDataArgs args)
        {
            if (TsData != null)
            {
                TsData(this, args);
            }
        }
        public delegate void SignalInfoHandler(object sender, SignalInfoArgs e);
        public delegate void TsDataHandler(object sender, TsDataArgs e);
        public event SignalInfoHandler SignalInfo;
        public event TsDataHandler TsData;
        public class SignalInfoArgs : EventArgs
        {
            public bool Locked { get; private set; }
            public int Level { get; private set; }
            public int Quality { get; private set; }
            public SignalInfoArgs(bool locked, int level, int quality)
            {
                Locked = locked;
                Level = level;
                Quality = quality;
            }
        }
        public class TsDataArgs : EventArgs
        {
            public byte[] Buffer { get; private set; }

            public TsDataArgs(byte[] buffer)
            {
                Buffer = buffer;

            }
        }
    }
}
