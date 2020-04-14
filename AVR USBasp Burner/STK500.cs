using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace AVR_USBasp_Burner
{
    public class STK500 : Programmer, IDisposable
    {
        #region Data 

        readonly SerialPort _serialPort;

        int _expectedByte;
        bool _wait4response;
        int _rxByteCount;
        readonly byte[] _rxbuffer = new byte[4096];
        readonly object _rxBufferLock = new object();
        readonly AutoResetEvent _rxEvent = new AutoResetEvent(false);

        #endregion

        #region ctor

        public STK500(string portName = "COM1", int baudRate = 115200)
        {
            ResetPulseTime = 150;
            ReadWriteTimeout = 1500;
            _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
            _serialPort.DataReceived += SerialPort_DataReceived;
            _serialPort.ErrorReceived += SerialPort_ErrorReceived;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
                _serialPort.Dispose();
            _rxEvent.Dispose();
        }

        public override void Open(bool enterProgMode = true)
        {
            _serialPort.Open();

            // Reset Chip
            //_serialPort.DtrEnable = false;
            //Thread.Sleep(ResetPulseTime);
            _serialPort.DtrEnable = true;
            Thread.Sleep(ResetPulseTime);

            IsConnected = true;
        }

        public override void Close()
        {
            IsConnected = false;
            _serialPort.Close();
        }

        #endregion

        #region Read Write to SerialPort

        private byte[] ExecuteCommand(byte[] txbuf, int expectedByte)
        {
            var response = new byte[expectedByte];
            _expectedByte = expectedByte + 2; //header+footer
            _wait4response = true;
            _rxByteCount = 0;

            //Debug.WriteLine(string.Format("TX[{0:00}]: {1}", txbuf.Length, BitConverter.ToString(txbuf).Replace("-", "")));
            _serialPort.DiscardOutBuffer();
            _serialPort.DiscardInBuffer();
            _serialPort.Write(txbuf, 0, txbuf.Length);
            var resp = _rxEvent.WaitOne(ReadWriteTimeout);
            _wait4response = false;

            if (!resp && _rxByteCount == 0)
                throw new Exception("STK No Response!");
            if (_rxbuffer[0] != StkCommand.STK_INSYNC)
                throw new Exception("STK not in Sync!");
            if (_rxbuffer[expectedByte + 1] != StkCommand.STK_OK)
                throw new Exception("STK Invalid Response!");

            lock (_rxBufferLock)
            {
                Array.Copy(_rxbuffer, 1, response, 0, response.Length);
            }
            return response;
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (e.EventType == SerialData.Eof || !_wait4response) return;

            var bytesToRead = _serialPort.BytesToRead;
            lock (_rxBufferLock)
            {
                if (_rxByteCount + bytesToRead < _rxbuffer.Length)
                {
                    _serialPort.Read(_rxbuffer, _rxByteCount, bytesToRead);
                    _rxByteCount += bytesToRead;
                    //Debug.WriteLine(string.Format("RX[{0:00}]: {1}", _rxByteCount, BitConverter.ToString(_rxbuffer, 0, _rxByteCount).Replace("-","")));
                    if (_rxByteCount >= _expectedByte) _rxEvent.Set();
                }
            }
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            Debug.WriteLine("SerialPort Error: " + e.EventType);
        }

        #endregion

        #region Chip Operation

        public override void GetSync()
        {
            byte[] cmd = { StkCommand.CMD_GET_SYNC, StkCommand.CRC_EOP };
            ExecuteCommand(cmd, 0);
        }

        public override int ReadSignature()
        {
            byte[] cmd = { StkCommand.CMD_READ_SIGN, StkCommand.CRC_EOP };
            var data = ExecuteCommand(cmd, 3);
            var signature = 0;
            signature |= data[0] << 16; //msb
            signature |= data[1] << 8;
            signature |= data[2] << 0;  //lsb
            return signature;
        }

        #endregion

        #region Extended Chip Operation

        public override string GetDeviceName()
        {
            byte[] cmd = { StkCommand.CMD_GET_SIGN_ON, StkCommand.CRC_EOP };
            var data = ExecuteCommand(cmd, 7);
            return Encoding.ASCII.GetString(data);
        }

        public override int GetPageSize()
        {
            byte[] cmd1 = { StkCommand.CMD_GET_PARAMETER, StkCommand.PARAM_PAGESIZE_MSB, StkCommand.CRC_EOP };
            var data = ExecuteCommand(cmd1, 1);
            var pageSize = data[0] << 8;  //msb

            byte[] cmd2 = { StkCommand.CMD_GET_PARAMETER, StkCommand.PARAM_PAGESIZE_LSB, StkCommand.CRC_EOP };
            data = ExecuteCommand(cmd2, 1);
            pageSize |= data[0];  //lsb

            return pageSize;
        }

        public override void StartUserApp()
        {
            byte[] cmd = { StkCommand.CMD_START_USERAPP, StkCommand.CRC_EOP };
            ExecuteCommand(cmd, 0);
        }

        #endregion

        #region Read Write Page

        protected override void LoadAddress(int address)
        {
            var i = 0;
            var cmd = new byte[4];
            address >>= 1; //divide by 2 (stk500 standard)
            cmd[i++] = StkCommand.CMD_LOAD_ADDRESS;
            cmd[i++] = LSB16(address);
            cmd[i++] = MSB16(address);
            cmd[i++] = StkCommand.CRC_EOP;
            ExecuteCommand(cmd, 0);
        }

        protected override void ReadPage(MemSource source, int size, ref byte[] data)
        {
            var memType = (source == MemSource.Flash) ? 'F' : 'E';
            var i = 0;
            var cmd = new byte[5];
            cmd[i++] = StkCommand.CMD_READ_PAGE;
            cmd[i++] = MSB16(size);
            cmd[i++] = LSB16(size);
            cmd[i++] = (byte)memType;
            cmd[i++] = StkCommand.CRC_EOP;
            data = ExecuteCommand(cmd, size);
        }

        protected override void WritePage(MemSource source, int size, byte[] data)
        {
            var memType = (source == MemSource.Flash) ? 'F' : 'E';
            var i = 0;
            var cmd = new byte[5 + size];
            cmd[i++] = StkCommand.CMD_PROG_PAGE;
            cmd[i++] = MSB16(size);
            cmd[i++] = LSB16(size);
            cmd[i++] = (byte)memType;
            Array.Copy(data, 0, cmd, i, size);
            cmd[cmd.Length - 1] = StkCommand.CRC_EOP;
            ExecuteCommand(cmd, 0);
        }

        #endregion
    }

    public static class StkCommand
    {
        /* STK500 constants list, from AVRDUDE */
        public const byte STK_OK = 0x10;
        public const byte STK_FAILED = 0x11;          // Not used
        public const byte STK_UNKNOWN = 0x12;         // Not used
        public const byte STK_NODEVICE = 0x13;        // Not used
        public const byte STK_INSYNC = 0x14;          // ' '
        public const byte STK_NOSYNC = 0x15;          // Not used
        public const byte ADC_CHANNEL_ERROR = 0x16;   // Not used
        public const byte ADC_MEASURE_OK = 0x17;      // Not used
        public const byte PWM_CHANNEL_ERROR = 0x18;   // Not used
        public const byte PWM_ADJUST_OK = 0x19;       // Not used
        public const byte CRC_EOP = 0x20;             // 'SPACE'
        public const byte CMD_GET_SYNC = 0x30;        // '0'
        public const byte CMD_GET_SIGN_ON = 0x31;     // '1'
        public const byte CMD_SET_PARAMETER = 0x40;   // '@'
        public const byte CMD_GET_PARAMETER = 0x41;   // 'A'
        public const byte CMD_SET_DEVICE = 0x42;      // 'B'
        public const byte CMD_SET_DEVICE_EXT = 0x45;  // 'E'
        public const byte CMD_ENTER_PROGMODE = 0x50;  // 'P'
        public const byte CMD_LEAVE_PROGMODE = 0x51;  // 'Q'
        public const byte CMD_CHIP_ERASE = 0x52;      // 'R'
        public const byte CMD_CHECK_AUTOINC = 0x53;   // 'S'
        public const byte CMD_LOAD_ADDRESS = 0x55;    // 'U'
        public const byte CMD_UNIVERSAL = 0x56;       // 'V'
        public const byte CMD_PROG_FLASH = 0x60;      // '`'
        public const byte CMD_PROG_DATA = 0x61;       // 'a'
        public const byte CMD_PROG_FUSE = 0x62;       // 'b'
        public const byte CMD_PROG_LOCK = 0x63;       // 'c'
        public const byte CMD_PROG_PAGE = 0x64;       // 'd'
        public const byte CMD_PROG_FUSE_EXT = 0x65;   // 'e'
        public const byte CMD_READ_FLASH = 0x70;      // 'p'
        public const byte CMD_READ_DATA = 0x71;       // 'q'
        public const byte CMD_READ_FUSE = 0x72;       // 'r'
        public const byte CMD_READ_LOCK = 0x73;       // 's'
        public const byte CMD_READ_PAGE = 0x74;       // 't'
        public const byte CMD_READ_SIGN = 0x75;       // 'u'
        public const byte CMD_READ_OSCCAL = 0x76;     // 'v'
        public const byte CMD_READ_FUSE_EXT = 0x77;   // 'w'
        public const byte CMD_READ_OSCCAL_EXT = 0x78; // 'x'

        /* extended STK_GET_PARAMETER command */
        public const byte PARAM_HW_VER = 0x80;
        public const byte PARAM_SW_MAJOR = 0x81;
        public const byte PARAM_SW_MINOR = 0x82;
        public const byte PARAM_PAGESIZE_MSB = 0x83;
        public const byte PARAM_PAGESIZE_LSB = 0x84;

        /* custom added by GSM Rana */
        public const byte CMD_START_USERAPP = 0xFF;
    };

}
