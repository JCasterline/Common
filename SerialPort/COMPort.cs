﻿using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPort
{
    /// <summary>
    /// Wrapper around <see cref="System.IO.Ports.SerialPort"/>.
    /// </summary>
    public class ComPort : IDisposable
    {
        private readonly System.IO.Ports.SerialPort _port;
        private readonly Action<byte> _processReceivedByte;
        private CancellationTokenSource _ctsPortQueueMonitor;

        private bool _disposed;

        /// <summary>
        /// Represents the method that will handle the port error event of a <see cref="ComPort"/> object.
        /// </summary>
        public event Action<Exception> PortError;

        private void OnPortError(Exception ex)
        {
            var handler = PortError;
            if (handler != null) handler(ex);
        }

        /// <summary>
        /// Represents the method that will handle the port connected event of a <see cref="ComPort"/> object.
        /// </summary>
        public event Action<string> PortConnected;

        private void OnPortConnected(string port)
        {
            var handler = PortConnected;
            if (handler != null) handler(port);
        }

        /// <summary>
        /// Create an instance of the ComPort.
        /// </summary>
        /// <param name="portName">The name of the serial port to connect to.</param>
        /// <param name="baudRate">Sets the serial baud rate.</param>
        /// <param name="parity">Sets the parity-checking protocol.</param>
        /// <param name="dataBits">Sets the standard length of data bits per byte.</param>
        /// <param name="stopBits">Sets the standard number of stopbits per byte.</param>
        /// <param name="processReceivedByte">Action that will process the received byte queue. If this action is not specified, the ReceivedData collection must be processed.</param>
        public ComPort(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits,
            Action<byte> processReceivedByte = null)
        {
            ReceivedData = new BlockingCollection<byte>();
            LastDataReceived = DateTime.MinValue;

            var portExists = System.IO.Ports.SerialPort.GetPortNames().Any(x => x == portName);
            if (!portExists)
            {
                OnPortError(new Exception(string.Format("COM port {0} does not exist.", portName)));
                return;
            }

            _port = new System.IO.Ports.SerialPort(portName, baudRate, parity, dataBits, stopBits);
            _port.DataReceived += _port_DataReceived;
            _port.ErrorReceived += _port_ErrorReceived;

            _processReceivedByte = processReceivedByte;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            StopCommunications();

            if (_ctsPortQueueMonitor != null)
                _ctsPortQueueMonitor.Dispose();

            _port.Dispose();

            _disposed = true;
        }

        private void _port_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            OnPortError(new Exception(e.EventType.ToString()));
        }

        private void _port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            LastDataReceived = DateTime.Now;

            var buffer = new byte[_port.BytesToRead];
            try
            {
                _port.Read(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                OnPortError(ex);
                return;
            }

            buffer.ToList().ForEach(b => ReceivedData.Add(b));
        }

        /// <summary>
        /// Writes the specified string to the serial port.
        /// </summary>
        /// <param name="text">Text to write to the serial port.</param>
        public void SendData(string text)
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name, "Cannot use a disposed object.");

            try
            {
                _port.Write(text);
            }
            catch (Exception ex)
            {
                OnPortError(ex);
            }
        }

        /// <summary>
        /// Writes the specified byte array to the serial port.
        /// </summary>
        /// <param name="bytes">The byte array that contains the data to write to the serial port.</param>
        public void SendData(byte[] bytes)
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name, "Cannot use a disposed object.");

            try
            {
                _port.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                OnPortError(ex);
            }
        }

        /// <summary>
        /// Open the port, start the disconnect timer, and if the ProcessReceivedByte action was specified, start the collection monitor.
        /// </summary>
        public void StartCommunications()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name, "Cannot use a disposed object.");

            try
            {
                if (_port.IsOpen)
                    _port.Close();

                _port.Open();

                OnPortConnected(_port.PortName);
            }
            catch (Exception ex)
            {
                OnPortError(ex);
                return;
            }

            if (_processReceivedByte != null)
                StartCollectionMonitor();
        }

        /// <summary>
        /// Stop the collection monitor if the ProcessReceivedByte action was specified, dispose of the timer, and close the port and dispose.
        /// </summary>
        public void StopCommunications()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name, "Cannot use a disposed object.");

            if (_ctsPortQueueMonitor != null)
                _ctsPortQueueMonitor.Cancel();

            try
            {
                if (_port.IsOpen)
                    _port.Close();
            }
            catch (Exception ex)
            {
                OnPortError(ex);
            }

            ReceivedData = new BlockingCollection<byte>();
        }

        private void StartCollectionMonitor()
        {
            try
            {
                _ctsPortQueueMonitor = new CancellationTokenSource();

                Task.Factory.StartNew(delegate
                {
                    while (null != _ctsPortQueueMonitor && !_ctsPortQueueMonitor.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var item = ReceivedData.Take(_ctsPortQueueMonitor.Token);
                            _processReceivedByte(item);
                        }
                        catch (OperationCanceledException)
                        {
                            //Ignore. Blocking collection throws this error when the operation is cancelled.
                        }
                        catch (Exception ex)
                        {
                            OnPortError(ex);
                        }
                    }
                }, _ctsPortQueueMonitor.Token);
            }
            catch (Exception ex)
            {
                OnPortError(ex);
            }
        }

        #region Properties
        public DateTime LastDataReceived { get; private set; }

        /// <summary>
        /// The collection holding data received on the serial port. This must be consumed if no ProcessReceivedByte action is set.
        /// </summary>
        public BlockingCollection<byte> ReceivedData { get; set; }

        #endregion
    }
}